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

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectoryPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: LogFilePathPattern,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
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
