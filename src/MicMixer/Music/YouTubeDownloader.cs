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
    /// <summary>
    /// YouTube player clients to try, in order. The audio URLs handed out for the
    /// default clients are rejected with HTTP 403 once the transfer starts, while the
    /// embedded-web client still serves usable ones. Videos that disallow embedding
    /// (age-restricted ones, for example) fail on that client instead, so yt-dlp's own
    /// defaults remain as a second attempt.
    /// </summary>
    private static readonly string?[] PlayerClients = ["web_embedded", null];

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
        Log.Information("Starting yt-dlp download for {Url}.", url);

        string? lastError = null;
        bool isYouTubeUrl = DownloadUrlValidator.IsYouTubeUrl(url);
        string?[] playerClients = isYouTubeUrl ? PlayerClients : [null];

        foreach (string? playerClient in playerClients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (lastError != null)
            {
                progress?.Report(new DownloadProgress(null, "Retrying..."));
            }

            (string? resultPath, string? error) = await RunYtDlpAsync(
                url, destinationFolder, playerClient, progress, cancellationToken);

            if (error == null)
            {
                Log.Information("yt-dlp finished: {ResultPath}.", resultPath ?? "(unknown file)");
                return resultPath;
            }

            Log.Warning(
                "yt-dlp failed using player client {PlayerClient}: {Error}",
                playerClient ?? "(yt-dlp default)",
                error);
            lastError = error;
        }

        throw new InvalidOperationException(lastError ?? "yt-dlp failed without an error message.");
    }

    /// <summary>
    /// Runs yt-dlp once. Returns the created file (when yt-dlp reported one) and the
    /// error message, which is null exactly when the run succeeded.
    /// </summary>
    private async Task<(string? ResultPath, string? Error)> RunYtDlpAsync(
        string url,
        string destinationFolder,
        string? playerClient,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? resultPath = null;
        var errorOutput = new StringBuilder();
        bool requiresJavaScriptRuntime = DownloadUrlValidator.IsYouTubeUrl(url);

        var arguments = new List<string>
        {
            "-x",
            "--audio-format", "mp3",
            "--audio-quality", "0",
            "--ffmpeg-location", _tools.ToolsDirectory,
            "--no-playlist",
            // Last line of defence: even if a link slips past DownloadUrlValidator and
            // resolves to a playlist, only its first entry is ever fetched.
            "--playlist-items", "1",
            "--newline",
            "--no-simulate",
            "--print", "after_move:filepath",
            "-o", Path.Combine(destinationFolder, "%(title)s.%(ext)s")
        };

        if (requiresJavaScriptRuntime)
        {
            // Solving YouTube's "n" challenge needs a JavaScript runtime; without one
            // the media URLs stay scrambled and the transfer fails with HTTP 403.
            arguments.Add("--js-runtimes");
            arguments.Add("deno:" + _tools.DenoPath);
        }

        if (playerClient != null)
        {
            arguments.Add("--extractor-args");
            arguments.Add("youtube:player_client=" + playerClient);
        }

        arguments.Add(url);

        var command = Cli.Wrap(_tools.YtDlpPath)
            // yt-dlp (Python) writes piped output in the ANSI code page by default,
            // which mangles titles like "I'm Fine" (U+2019). Force UTF-8 end to end.
            .WithEnvironmentVariables(env => env.Set("PYTHONIOENCODING", "utf-8"))
            .WithArguments(arguments)
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

        var result = await command.ExecuteAsync(cancellationToken);

        return result.ExitCode == 0
            ? (resultPath, null)
            : (null, ExtractErrorMessage(errorOutput.ToString()));
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
