using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace MicMixer;

public partial class App : System.Windows.Application
{
    private const string AppUserModelId = "BenjiButten.MicMixer";
    private const string MutexName = "MicMixer_SingleInstance_B7E3A1F0";
    private const string EventName = "MicMixer_ShowExisting_B7E3A1F0";

    internal static readonly Stopwatch StartupStopwatch = Stopwatch.StartNew();
    private static readonly object StartupLogSync = new();
    private static readonly string StartupLogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "startup-timeline.log");

    internal static bool StartupBenchmarkMode { get; private set; }

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Thread? _listenerThread;

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
        StartupBenchmarkMode = e.Args.Any(arg =>
            string.Equals(arg, "--startup-benchmark", StringComparison.OrdinalIgnoreCase));

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
        catch
        {
            // Non-fatal if unavailable.
        }

        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show its window.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(EventName);
                signal.Set();
            }
            catch
            {
                // If the event doesn't exist yet the other instance will surface on its own.
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

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "MicMixer — Fel", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        StartupTrace("Pre-MainWindow");
        var mainWindow = new MainWindow();
        StartupTrace("MainWindow created");
        mainWindow.Show();
        StartupTrace("MainWindow shown");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Set();   // unblock listener thread so it exits
        _showEvent?.Dispose();

        if (_instanceMutex != null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

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

            // If the app is shutting down, stop listening.
            if (_showEvent == null || Environment.HasShutdownStarted)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                var window = MainWindow;
                if (window == null) return;

                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            });
        }
    }
}

