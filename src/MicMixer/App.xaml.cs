using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MicMixer.Audio;
using MicMixer.Diagnostics;
using MicMixer.Music;
using MicMixer.Remote;
using MicMixer.Settings;
using Serilog;

namespace MicMixer;

public partial class App : System.Windows.Application
{
    private const string AppUserModelId = "BenjiButten.MicMixer";
    private const string MutexName = "MicMixer_SingleInstance_B7E3A1F0";
    private const string EventName = "MicMixer_ShowExisting_B7E3A1F0";

    internal static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();
    private static readonly object StartupLogSync = new();
    private static readonly object UnhandledDialogSync = new();
    private static readonly TimeSpan UnhandledDialogCooldown = TimeSpan.FromMinutes(2);
    private static readonly string StartupLogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "startup-timeline.log");
    private static DateTime _nextUnhandledDialogUtc = DateTime.MinValue;
    private static int _suppressedUnhandledDialogs;

    internal static bool StartupBenchmarkMode { get; private set; }

    internal static bool StartHiddenInTray { get; private set; }

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _listenerThread;
    private MicMixerControlServer? _controlServer;
    private AudioRouter? _router;
    private MusicPlaybackEngine? _music;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    internal static void StartupTrace(string stage)
    {
        long elapsedMs = StartupStopwatch.ElapsedMilliseconds;
        Trace.WriteLine($"[MicMixer] {stage}: {elapsedMs} ms");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogFilePath)!);

            lock (StartupLogSync)
            {
                File.AppendAllText(
                    StartupLogFilePath,
                    $"{DateTime.UtcNow:O}\t{Environment.ProcessId}\t{elapsedMs}\t{stage}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best effort logging only.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            AppLogger.Initialize();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MicMixer] Logger initialization failed: {ex.Message}");
        }

        StartupBenchmarkMode = e.Args.Any(arg =>
            string.Equals(arg, "--startup-benchmark", StringComparison.OrdinalIgnoreCase));
        StartHiddenInTray = HasStartHiddenInTrayArgument(e.Args);

        Log.Information("MicMixer startup begin. BenchmarkMode={BenchmarkMode}", StartupBenchmarkMode);
        StartupTrace("OnStartup begin");
        if (StartupBenchmarkMode)
        {
            StartupTrace("Startup benchmark mode enabled");
        }

        // A stable AppUserModelID helps Windows keep shell-level preferences tied to this app.
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set AppUserModelID.");
        }

        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Windows startup must not unexpectedly surface an instance that is
            // already running. An ordinary second launch still activates it.
            if (!StartHiddenInTray)
            {
                Log.Information("Second app instance detected; signaling existing instance.");
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(EventName);
                    signal.Set();
                }
                catch
                {
                    // If the event doesn't exist yet the other instance will surface on its own.
                }
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _listenerThread = new Thread(ListenForActivationSignal)
        {
            IsBackground = true,
            Name = "SingleInstanceListener"
        };
        _listenerThread.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;

        StartupTrace("Pre-MainWindow");
        var session = new MusicSession();
        _router = new AudioRouter();
        _music = new MusicPlaybackEngine();
        var settingsStore = new SettingsStore();
        var playlist = new PlaylistManager();
        var mainWindow = new MainWindow(session, _router, _music, settingsStore, playlist);
        StartupTrace("MainWindow created");
        _controlServer = new MicMixerControlServer(mainWindow);
        _controlServer.Start();
        StartupTrace("Control server started");
        if (StartHiddenInTray)
        {
            mainWindow.StartHiddenInTray();
            StartupTrace("MainWindow started hidden in tray");
        }
        else
        {
            mainWindow.Show();
            StartupTrace("MainWindow shown");
        }
    }

    internal static bool HasStartHiddenInTrayArgument(IEnumerable<string> args) =>
        args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;

        if (_controlServer != null)
        {
            _controlServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _controlServer = null;
        }

        _router?.Dispose();
        _router = null;
        _music?.Dispose();
        _music = null;

        _showEvent?.Set();   // unblock listener thread so it exits
        _showEvent?.Dispose();

        if (_instanceMutex != null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        Log.Information("MicMixer exiting.");
        AppLogger.Shutdown();

        base.OnExit(e);
    }

    private void ListenForActivationSignal()
    {
        while (_showEvent != null)
        {
            try
            {
                _showEvent.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Single-instance listener loop failed while waiting for activation signal.");
                return;
            }

            // If the app is shutting down, stop listening.
            if (_showEvent == null || Environment.HasShutdownStarted)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow window)
                {
                    window.ShowAndActivate();
                }
            });
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Error(args.Exception, "Unhandled UI exception.");
        args.Handled = true;
        ShowUnhandledErrorDialog(args.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", args.IsTerminating);
            return;
        }

        Log.Fatal(
            "Unhandled AppDomain exception with non-exception payload. PayloadType={PayloadType} IsTerminating={IsTerminating}",
            args.ExceptionObject?.GetType().FullName ?? "null",
            args.IsTerminating);
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Log.Error(args.Exception, "Unobserved task exception.");
        args.SetObserved();
    }

    private static void ShowUnhandledErrorDialog(Exception exception)
    {
        int suppressedSinceLastDialog;

        lock (UnhandledDialogSync)
        {
            var now = DateTime.UtcNow;

            if (now < _nextUnhandledDialogUtc)
            {
                _suppressedUnhandledDialogs++;
                return;
            }

            suppressedSinceLastDialog = _suppressedUnhandledDialogs;
            _suppressedUnhandledDialogs = 0;
            _nextUnhandledDialogUtc = now + UnhandledDialogCooldown;
        }

        try
        {
            string message = "Ett internt fel inträffade och loggades. MicMixer fortsätter köra.";
            message += $"{Environment.NewLine}{Environment.NewLine}Senaste fel: {exception.Message}";

            if (suppressedSinceLastDialog > 0)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}{suppressedSinceLastDialog} liknande fel undertrycktes för att undvika popup-storm.";
            }

            System.Windows.MessageBox.Show(message, "MicMixer — Fel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Best effort only.
        }

        if (suppressedSinceLastDialog > 0)
        {
            Log.Warning("Suppressed {SuppressedCount} repeated UI exception dialogs.", suppressedSinceLastDialog);
        }
    }
}

