using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Serilog;

namespace MicMixer.Music;

/// <summary>
/// Downloads yt-dlp and ffmpeg into a local tools folder on first use, so the
/// user does not have to install anything themselves.
///
/// Versions are pinned and downloads are verified against known SHA-256 hashes,
/// so the app behaves deterministically and a tampered download is rejected.
/// To upgrade a tool: update the version/URL/hash constants below — the version
/// marker files make existing installs re-download automatically.
/// </summary>
public sealed class ToolBootstrapper
{
    private const string YtDlpVersion = "2026.07.04";
    private const string YtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/" + YtDlpVersion + "/yt-dlp.exe";
    private const string YtDlpSha256 = "52fe3c26dcf71fbdc85b528589020bb0b8e383155cfa81b64dd447bbe35e24b8";

    private const string FfmpegVersion = "autobuild-2026-07-01-16-32";
    private const string FfmpegArchiveName = "ffmpeg-N-125385-ge2e889d9da-win64-gpl.zip";
    private const string FfmpegDownloadUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/" + FfmpegVersion + "/" + FfmpegArchiveName;
    private const string FfmpegArchiveSha256 = "aa8bd4e8365f673a3d4194dc51cb69e85365fcbaaed9bb497ca24a006573df3f";

    private static readonly HttpClient Http = CreateHttpClient();

    public string ToolsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "tools");

    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");

    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");

    private string YtDlpVersionMarkerPath => Path.Combine(ToolsDirectory, "yt-dlp.version");

    private string FfmpegVersionMarkerPath => Path.Combine(ToolsDirectory, "ffmpeg.version");

    public bool IsReady => IsToolInstalled(YtDlpPath, YtDlpVersionMarkerPath, YtDlpVersion)
        && IsToolInstalled(FfmpegPath, FfmpegVersionMarkerPath, FfmpegVersion);

    public async Task EnsureToolsAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return;
        }

        Directory.CreateDirectory(ToolsDirectory);

        if (!IsToolInstalled(YtDlpPath, YtDlpVersionMarkerPath, YtDlpVersion))
        {
            Log.Information("Downloading yt-dlp {Version} to {Path}.", YtDlpVersion, YtDlpPath);
            status?.Report($"Downloading yt-dlp {YtDlpVersion}...");
            await DownloadVerifiedFileAsync(YtDlpDownloadUrl, YtDlpPath, YtDlpSha256, cancellationToken);
            File.WriteAllText(YtDlpVersionMarkerPath, YtDlpVersion);
        }

        if (!IsToolInstalled(FfmpegPath, FfmpegVersionMarkerPath, FfmpegVersion))
        {
            Log.Information("Downloading ffmpeg {Version} to {Path}.", FfmpegVersion, FfmpegPath);
            status?.Report("Downloading ffmpeg (~160 MB, one-time download)...");
            string zipPath = Path.Combine(ToolsDirectory, "ffmpeg.zip.tmp");

            try
            {
                await DownloadVerifiedFileAsync(FfmpegDownloadUrl, zipPath, FfmpegArchiveSha256, cancellationToken);
                status?.Report("Extracting ffmpeg...");
                ExtractFfmpeg(zipPath);
                File.WriteAllText(FfmpegVersionMarkerPath, FfmpegVersion);
            }
            finally
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // Leftover temp file is harmless.
                }
            }
        }
    }

    private static bool IsToolInstalled(string executablePath, string markerPath, string expectedVersion)
    {
        if (!File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            return File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == expectedVersion;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MicMixer/1.0");
        return client;
    }

    private static async Task DownloadVerifiedFileAsync(
        string url,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        string tempPath = destination + ".download";

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var target = File.Create(tempPath))
        {
            await response.Content.CopyToAsync(target, cancellationToken);
        }

        string actualSha256;
        await using (var readStream = File.OpenRead(tempPath))
        {
            actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(readStream, cancellationToken));
        }

        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            Log.Error(
                "Checksum mismatch for {Url}: expected {Expected}, got {Actual}.",
                url, expectedSha256, actualSha256);
            throw new InvalidOperationException(
                "The download could not be verified (checksum mismatch). Please try again later.");
        }

        File.Move(tempPath, destination, overwrite: true);
    }

    private void ExtractFfmpeg(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (string executableName in new[] { "ffmpeg.exe", "ffprobe.exe" })
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals(executableName, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                if (executableName == "ffmpeg.exe")
                {
                    throw new InvalidOperationException("ffmpeg.exe was not found in the downloaded archive.");
                }

                continue;
            }

            entry.ExtractToFile(Path.Combine(ToolsDirectory, executableName), overwrite: true);
        }
    }
}
