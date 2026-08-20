using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Serilog;

namespace MicMixer.Music;

/// <summary>
/// Downloads yt-dlp and ffmpeg into a local tools folder on first use and, for
/// YouTube downloads, adds a JavaScript runtime so the user does not have to
/// install anything themselves.
///
/// Versions are pinned and downloads are verified against known SHA-256 hashes,
/// so the app behaves deterministically and a tampered download is rejected.
/// To upgrade a tool: update the version/URL/hash constants below — the version
/// marker files make existing installs re-download automatically.
/// </summary>
public sealed class ToolBootstrapper
{
    private const string YtDlpVersion = "2026.08.19";
    private const string YtDlpDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/" + YtDlpVersion + "/yt-dlp.exe";
    private const string YtDlpSha256 = "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a";

    private const string FfmpegVersion = "autobuild-2026-07-01-16-32";
    private const string FfmpegArchiveName = "ffmpeg-N-125385-ge2e889d9da-win64-gpl.zip";
    private const string FfmpegDownloadUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/" + FfmpegVersion + "/" + FfmpegArchiveName;
    private const string FfmpegArchiveSha256 = "aa8bd4e8365f673a3d4194dc51cb69e85365fcbaaed9bb497ca24a006573df3f";

    // YouTube hides its media URLs behind a JavaScript challenge. yt-dlp can only solve
    // that challenge when a JavaScript runtime is installed, and an unsolved challenge
    // makes the transfer fail with HTTP 403. Deno is the runtime yt-dlp enables by default.
    private const string DenoVersion = "v2.9.5";
    private const string DenoArchiveName = "deno-x86_64-pc-windows-msvc.zip";
    private const string DenoDownloadUrl = "https://github.com/denoland/deno/releases/download/" + DenoVersion + "/" + DenoArchiveName;
    private const string DenoArchiveSha256 = "171efab55ac6b9881fd53ee4c20f8bf3bb1340ffc618483746909014db12216a";

    private static readonly HttpClient Http = CreateHttpClient();

    public string ToolsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "tools");

    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");

    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");

    public string DenoPath => Path.Combine(ToolsDirectory, "deno.exe");

    private string YtDlpVersionMarkerPath => Path.Combine(ToolsDirectory, "yt-dlp.version");

    private string FfmpegVersionMarkerPath => Path.Combine(ToolsDirectory, "ffmpeg.version");

    private string DenoVersionMarkerPath => Path.Combine(ToolsDirectory, "deno.version");

    public bool IsReady => AreToolsReady(requireJavaScriptRuntime: true);

    public async Task EnsureToolsAsync(
        IProgress<string>? status,
        CancellationToken cancellationToken,
        bool requireJavaScriptRuntime = true)
    {
        if (AreToolsReady(requireJavaScriptRuntime))
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
            await InstallArchivedToolAsync(
                "ffmpeg",
                FfmpegVersion,
                FfmpegDownloadUrl,
                FfmpegArchiveSha256,
                FfmpegVersionMarkerPath,
                "Downloading ffmpeg (~160 MB, one-time download)...",
                requiredExecutable: "ffmpeg.exe",
                optionalExecutables: ["ffprobe.exe"],
                status,
                cancellationToken);
        }

        if (requireJavaScriptRuntime
            && !IsToolInstalled(DenoPath, DenoVersionMarkerPath, DenoVersion))
        {
            await InstallArchivedToolAsync(
                "deno",
                DenoVersion,
                DenoDownloadUrl,
                DenoArchiveSha256,
                DenoVersionMarkerPath,
                "Downloading the JavaScript runtime (~40 MB, one-time download)...",
                requiredExecutable: "deno.exe",
                optionalExecutables: [],
                status,
                cancellationToken);
        }
    }

    private bool AreToolsReady(bool requireJavaScriptRuntime)
    {
        return IsToolInstalled(YtDlpPath, YtDlpVersionMarkerPath, YtDlpVersion)
            && IsToolInstalled(FfmpegPath, FfmpegVersionMarkerPath, FfmpegVersion)
            && (!requireJavaScriptRuntime
                || IsToolInstalled(DenoPath, DenoVersionMarkerPath, DenoVersion));
    }

    private async Task InstallArchivedToolAsync(
        string toolName,
        string version,
        string downloadUrl,
        string archiveSha256,
        string versionMarkerPath,
        string downloadStatus,
        string requiredExecutable,
        string[] optionalExecutables,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        Log.Information("Downloading {Tool} {Version} to {Path}.", toolName, version, ToolsDirectory);
        status?.Report(downloadStatus);
        string zipPath = Path.Combine(ToolsDirectory, toolName + ".zip.tmp");

        try
        {
            await DownloadVerifiedFileAsync(downloadUrl, zipPath, archiveSha256, cancellationToken);
            status?.Report($"Extracting {toolName}...");
            ExtractExecutables(zipPath, requiredExecutable, optionalExecutables);
            File.WriteAllText(versionMarkerPath, version);
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

    private void ExtractExecutables(string zipPath, string requiredExecutable, string[] optionalExecutables)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (string executableName in optionalExecutables.Prepend(requiredExecutable))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals(executableName, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                if (executableName == requiredExecutable)
                {
                    throw new InvalidOperationException($"{requiredExecutable} was not found in the downloaded archive.");
                }

                continue;
            }

            entry.ExtractToFile(Path.Combine(ToolsDirectory, executableName), overwrite: true);
        }
    }
}
