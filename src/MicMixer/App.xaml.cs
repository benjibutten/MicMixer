using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MicMixer.Admin;
using MicMixer.Audio;
using MicMixer.Diagnostics;
using MicMixer.Music;
using MicMixer.Remote;
using MicMixer.Settings;
using MicMixer.Updates;
using Serilog;

namespace MicMixer;

public partial class App : System.Windows.Application
{
    private const string AppUserModelId = "BenjiButten.MicMixer";
    private const string MutexName = "MicMixer_SingleInstance_B7E3A1F0";
    private const string EventName = "MicMixer_ShowExisting_B7E3A1F0";

    /// <summary>Starts MicMixer hidden in the tray, as Windows does at sign-in.</summary>
    internal const string MinimizedArgument = "--minimized";

    /// <summary>Turns on "Run as administrator" and exits; the installer passes it when that box is ticked.</summary>
    internal const string RunAsAdministratorArgument = "--run-as-administrator";

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
    private MainWindow? _mainWindow;

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

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (Elevation.IsElevated)
        {
            Elevation.RefuseUntrustedJunctions();
        }

        if (UpdateInstaller.IsCleanupMode(e.Args))
        {
            base.OnStartup(e);
            await UpdateInstaller.RunCleanupAsync(e.Args);
            Shutdown();
            return;
        }
        if (UpdateInstaller.IsUpdateMode(e.Args))
        {
            base.OnStartup(e);
            await UpdateInstaller.RunAsync(e.Args);
            Shutdown();
            return;
        }
        if (e.Args.Contains(RunAsAdministratorArgument, StringComparer.OrdinalIgnoreCase))
        {
            base.OnStartup(e);
            try
            {
                AppLogger.Initialize();
                TurnOnRunAsAdministrator();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not turn on Run as administrator.");
            }
            Shutdown();
            return;
        }
        if (VirtualCableNamer.IsNameMode(e.Args))
        {
            base.OnStartup(e);
            try
            {
                AppLogger.Initialize();
                await Task.Run(VirtualCableNamer.Run);
            }
            catch (Exception ex)
            {
                // The installer carries on either way; the cable keeps VB-CABLE's names.
                Log.Warning(ex, "Could not rename the VB-CABLE ends.");
            }
            Shutdown();
            return;
        }
        UpdateInstaller.ScheduleCleanup(e.Args);
        Elevation.WaitForPreviousInstance(e.Args);

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

        bool createdNew;
        try
        {
            _instanceMutex = MutexAcl.Create(true, MutexName, out createdNew, CreateMutexSecurity());
        }
        catch (UnauthorizedAccessException)
        {
            // Held by a MicMixer running as administrator that does not grant this
            // account access. It is still running, so this launch must not become a
            // second instance.
            Log.Warning("Another MicMixer is running as administrator and cannot be reached; exiting.");
            Shutdown();
            return;
        }

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

        var settingsStore = new SettingsStore();
        bool handedOver = false;
        if (ShouldRestartAsAdministrator(settingsStore))
        {
            try
            {
                handedOver = Elevation.TryStartElevatedCopy();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not restart MicMixer as administrator; running without administrator rights.");
            }
        }

        if (handedOver)
        {
            Log.Information("Handing over to a copy of MicMixer running as administrator.");
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, EventName, out _, CreateEventSecurity());
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
        var playlist = new PlaylistManager();
        var mainWindow = new MainWindow(session, _router, _music, settingsStore, playlist);
        _mainWindow = mainWindow;
        StartupTrace("MainWindow created");
        _controlServer = new MicMixerControlServer(mainWindow);
        _controlServer.Start();
        StartupTrace("Control server started");
        if (StartHiddenInTray)
        {
            CheckForUpdatesWhenShown(mainWindow);
            mainWindow.StartHiddenInTray();
            StartupTrace("MainWindow started hidden in tray");
        }
        else
        {
            mainWindow.Show();
            StartupTrace("MainWindow shown");
            _ = CheckForUpdatesAfterStartupAsync(mainWindow);
        }
    }

    private static async Task CheckForUpdatesAfterStartupAsync(Window owner)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (owner.Dispatcher.HasShutdownStarted)
            return;

        if (!owner.IsVisible)
        {
            CheckForUpdatesWhenShown(owner);
            return;
        }

        await UpdateCoordinator.CheckAsync(owner, manual: false);
    }

    private static void CheckForUpdatesWhenShown(Window owner)
    {
        DependencyPropertyChangedEventHandler? visibilityChanged = null;
        visibilityChanged = (_, _) =>
        {
            if (!owner.IsVisible)
                return;

            owner.IsVisibleChanged -= visibilityChanged;
            _ = CheckForUpdatesAfterStartupAsync(owner);
        };
        owner.IsVisibleChanged += visibilityChanged;
    }

    internal static bool HasStartHiddenInTrayArgument(IEnumerable<string> args) =>
        args.Contains(MinimizedArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this launch should hand over to a copy running as administrator.
    /// Never at sign-in: a UAC prompt there would stand between the user and the desktop.
    /// </summary>
    private static bool ShouldRestartAsAdministrator(SettingsStore settingsStore) =>
        !Elevation.IsElevated && Elevation.CanRunAsAdministrator && !StartHiddenInTray && !StartupBenchmarkMode
        && settingsStore.Load().RunAsAdministrator;

    private static void TurnOnRunAsAdministrator()
    {
        var settingsStore = new SettingsStore();
        AppSettings settings = settingsStore.Load();
        if (settings.RunAsAdministrator)
        {
            return;
        }

        settings.RunAsAdministrator = true;
        settingsStore.Save(settings);
    }

    // The instance that creates these may run as administrator, and then Windows
    // gives its named objects a default permission that shuts every process without
    // administrator rights out, including a second MicMixer launched from the Start menu.
    private static MutexSecurity CreateMutexSecurity()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(Elevation.CurrentUser(), MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static EventWaitHandleSecurity CreateEventSecurity()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(Elevation.CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return security;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;

        _mainWindow?.ReleaseHeldKey();

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
        _mainWindow?.ReleaseHeldKey();
        args.Handled = true;
        ShowUnhandledErrorDialog(args.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        _mainWindow?.ReleaseHeldKey();

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
            string message = "An internal error occurred and was logged. MicMixer will continue running.";
            message += $"{Environment.NewLine}{Environment.NewLine}Latest error: {exception.Message}";

            if (suppressedSinceLastDialog > 0)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}{suppressedSinceLastDialog} similar errors were suppressed to avoid a flood of pop-ups.";
            }

            System.Windows.MessageBox.Show(message, "MicMixer — Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

