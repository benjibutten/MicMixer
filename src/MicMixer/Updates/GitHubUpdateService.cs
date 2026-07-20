using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MicMixer.Updates;

internal sealed record UpdateInfo(
    Version Version,
    string TagName,
    Uri DownloadUri,
    Uri ChecksumUri,
    Uri ReleasePageUri);

internal sealed record UpdateProgress(string Status, double? Percentage = null);

internal sealed class GitHubUpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private readonly HttpClient _httpClient;
    private readonly string _statePath;

    public GitHubUpdateService(HttpClient? httpClient = null, string? statePath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MicMixer-Updater/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicMixer",
            "update-check.txt");
    }

    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, bool force, CancellationToken cancellationToken = default)
    {
        if (!force && !IsCheckDue())
            return null;

        using var response = await _httpClient.GetAsync(
            "https://api.github.com/repos/benjibutten/MicMixer/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        SaveCheckTime();

        JsonElement root = document.RootElement;
        string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tagName, out Version? releaseVersion) || releaseVersion <= currentVersion)
            return null;

        string zipName = GetReleaseZipName(releaseVersion!);
        string checksumName = $"{zipName}.sha256";
        Uri? zipUri = null;
        Uri? checksumUri = null;

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                continue;

            if (string.Equals(name, zipName, StringComparison.OrdinalIgnoreCase))
                zipUri = uri;
            else if (string.Equals(name, checksumName, StringComparison.OrdinalIgnoreCase))
                checksumUri = uri;
        }

        if (zipUri is null || checksumUri is null)
            return null;

        string pageUrl = root.GetProperty("html_url").GetString()
            ?? "https://github.com/benjibutten/MicMixer/releases/latest";
        return new UpdateInfo(releaseVersion!, tagName, zipUri, checksumUri, new Uri(pageUrl));
    }

    public async Task LaunchInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), $"MicMixer-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        string zipPath = Path.Combine(workDirectory, "update.zip");
        string updaterPath = Path.Combine(workDirectory, "MicMixer.Update.exe");

        try
        {
            progress?.Report(new UpdateProgress("Downloading update…", 0));
            await DownloadFileAsync(update.DownloadUri, zipPath, progress, cancellationToken);
            progress?.Report(new UpdateProgress("Verifying download…"));
            string checksumText = await _httpClient.GetStringAsync(update.ChecksumUri, cancellationToken);
            string expectedHash = ParseChecksum(checksumText);
            await using (var zipStream = File.OpenRead(zipPath))
            {
                await VerifySha256Async(zipStream, expectedHash, cancellationToken);
            }

            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the running executable path.");
            string installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            bool requiresElevation = !CanWriteToDirectory(installDirectory);

            progress?.Report(new UpdateProgress("Preparing installer…"));
            File.Copy(executablePath, updaterPath);

            var startInfo = new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDirectory
            };
            if (requiresElevation)
                startInfo.Verb = "runas";

            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add("--process-id");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--zip-path");
            startInfo.ArgumentList.Add(zipPath);
            startInfo.ArgumentList.Add("--expected-hash");
            startInfo.ArgumentList.Add(expectedHash);
            startInfo.ArgumentList.Add("--install-directory");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--executable-path");
            startInfo.ArgumentList.Add(executablePath);
            progress?.Report(new UpdateProgress(
                requiresElevation ? "Waiting for Windows approval…" : "Starting installer…"));
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the update installer.");
        }
        catch
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { }
            throw;
        }
    }

    internal static bool TryParseVersion(string tagName, out Version? version) =>
        Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out version);

    internal static string GetReleaseZipName(Version version) =>
        $"MicMixer-{version}-win-x64.zip";

    internal static string ParseChecksum(string value)
    {
        string hash = value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? string.Empty;
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The release checksum file is invalid.");
        return hash.ToUpperInvariant();
    }

    internal static async Task VerifySha256Async(
        Stream stream,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed its SHA-256 integrity check.");
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string path,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        long? totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int lastReportedPercentage = -1;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            downloadedBytes += bytesRead;
            if (totalBytes > 0)
            {
                int percentage = (int)Math.Min(100, downloadedBytes * 100 / totalBytes.Value);
                if (percentage != lastReportedPercentage)
                {
                    lastReportedPercentage = percentage;
                    progress?.Report(new UpdateProgress("Downloading update…", percentage));
                }
            }
        }
    }

    private bool IsCheckDue()
    {
        try
        {
            return !File.Exists(_statePath)
                || !DateTimeOffset.TryParse(File.ReadAllText(_statePath), out var lastCheck)
                || DateTimeOffset.UtcNow - lastCheck >= CheckInterval;
        }
        catch { return true; }
    }

    private void SaveCheckTime()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        string probe = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
        finally { try { File.Delete(probe); } catch { } }
    }

}
