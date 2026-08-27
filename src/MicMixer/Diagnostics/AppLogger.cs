using System.IO;
using System.Threading;
using Serilog;
using Serilog.Events;

namespace MicMixer.Diagnostics;

internal static class AppLogger
{
    private static int _initialized;

    private static readonly string LogDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "logs");

    private static readonly string LogFilePathPattern = Path.Combine(LogDirectoryPath, "micmixer-.log");

    /// <summary>
    /// Events the background sink may hold before it starts dropping. Deep enough
    /// to ride out a disk stall of several seconds' worth of trim diagnostics.
    /// </summary>
    private const int AsyncSinkQueueSize = 10_000;

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectoryPath);

        Log.Logger = new LoggerConfiguration()
            // Audio-buffer diagnostics (trim/rebuffer events) are intentionally
            // Debug-level. Keep them in the rolling log so real-world latency
            // and starvation can be evaluated before tightening any cushions.
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // Audio render/capture callbacks log from a real-time thread, and the
            // file sink writes synchronously. Hand every event to a background
            // worker instead so a slow disk can never stall a buffer refill and
            // turn into an underrun. blockWhenFull stays false: if the queue ever
            // does fill up, dropping diagnostics beats glitching the audio.
            .WriteTo.Async(
                configure: sink => sink.File(
                    path: LogFilePathPattern,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"),
                bufferSize: AsyncSinkQueueSize,
                blockWhenFull: false)
            .CreateLogger();

        string version = typeof(AppLogger).Assembly.GetName().Version?.ToString() ?? "unknown";
        Log.Information("Logger initialized. ProcessId={ProcessId} Version={Version} LogPath={LogPath}",
            Environment.ProcessId,
            version,
            LogFilePathPattern);
    }

    internal static void Shutdown()
    {
        try
        {
            Log.Information("Logger shutdown.");
            Log.CloseAndFlush();
        }
        catch
        {
            // Best effort only.
        }
    }
}
