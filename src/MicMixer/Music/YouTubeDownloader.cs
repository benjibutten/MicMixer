using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using Serilog;

namespace MicMixer.Music;

public readonly record struct DownloadProgress(double? Percent, string Status);

/// <summary>
/// Converts a video URL to an MP3 in the music folder by invoking yt-dlp.
/// </summary>
public sealed partial class YouTubeDownloader
{
    private readonly ToolBootstrapper _tools;

    public YouTubeDownloader(ToolBootstrapper tools)
    {
        _tools = tools;
    }

    /// <summary>
    /// Downloads and converts the given URL. Returns the path to the created MP3
    /// when yt-dlp reported it, otherwise null (caller refreshes the playlist regardless).
    /// </summary>
    public async Task<string?> DownloadAudioAsync(
        string url,
        string destinationFolder,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationFolder);

        string? resultPath = null;
        var errorOutput = new StringBuilder();

        var command = Cli.Wrap(_tools.YtDlpPath)
            // yt-dlp (Python) writes piped output in the ANSI code page by default,
            // which mangles titles like "I'm Fine" (U+2019). Force UTF-8 end to end.
            .WithEnvironmentVariables(env => env.Set("PYTHONIOENCODING", "utf-8"))
            .WithArguments(new[]
            {
                "-x",
                "--audio-format", "mp3",
                "--audio-quality", "0",
                "--ffmpeg-location", _tools.ToolsDirectory,
                "--no-playlist",
                "--newline",
                "--no-simulate",
                "--print", "after_move:filepath",
                "-o", Path.Combine(destinationFolder, "%(title)s.%(ext)s"),
                url
            })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                if (line.StartsWith("[download]", StringComparison.Ordinal))
                {
                    var match = DownloadPercentRegex().Match(line);
                    if (match.Success && double.TryParse(
                            match.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double percent))
                    {
                        progress?.Report(new DownloadProgress(percent, "Downloading..."));
                    }
                }
                else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
                {
                    progress?.Report(new DownloadProgress(null, "Converting to MP3..."));
                }
                else if (line.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(line))
                {
                    resultPath = line.Trim();
                }
            }, Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                errorOutput.AppendLine(line);
                Log.Debug("yt-dlp stderr: {Line}", line);
            }, Encoding.UTF8));

        Log.Information("Starting yt-dlp download for {Url}.", url);
        var result = await command.ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(ExtractErrorMessage(errorOutput.ToString()));
        }

        Log.Information("yt-dlp finished: {ResultPath}.", resultPath ?? "(unknown file)");
        return resultPath;
    }

    private static string ExtractErrorMessage(string stderr)
    {
        var lines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return lines.LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault()
            ?? "yt-dlp failed without an error message.";
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex DownloadPercentRegex();
}
