using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using MicMixer.Admin;

namespace MicMixer.Updates;

/// <param name="IsInstaller">True when <see cref="DownloadUri"/> is the installer rather than the zip.</param>
internal sealed record UpdateInfo(
    Version Version,
    string TagName,
    Uri DownloadUri,
    Uri ChecksumUri,
    Uri ReleasePageUri,
    bool IsInstaller);

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

    /// <summary>
    /// Returns the latest release when it is newer than <paramref name="currentVersion"/>,
    /// pointing at the installer when <paramref name="installedWithSetup"/> and at the zip otherwise.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(
        Version currentVersion,
        bool force,
        bool installedWithSetup,
        CancellationToken cancellationToken = default)
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

        string downloadName = installedWithSetup
            ? GetReleaseSetupName(releaseVersion!)
            : GetReleaseZipName(releaseVersion!);
        string checksumName = $"{downloadName}.sha256";
        Uri? downloadUri = null;
        Uri? checksumUri = null;

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                continue;

            if (string.Equals(name, downloadName, StringComparison.OrdinalIgnoreCase))
                downloadUri = uri;
            else if (string.Equals(name, checksumName, StringComparison.OrdinalIgnoreCase))
                checksumUri = uri;
        }

        if (downloadUri is null || checksumUri is null)
            return null;

        string pageUrl = root.GetProperty("html_url").GetString()
            ?? "https://github.com/benjibutten/MicMixer/releases/latest";
        return new UpdateInfo(releaseVersion!, tagName, downloadUri, checksumUri, new Uri(pageUrl), installedWithSetup);
    }

    /// <summary>
    /// Downloads and verifies <paramref name="update"/> and starts installing it. Returns
    /// true when MicMixer must now exit so the update can replace it, and false when the
    /// installer ended without installing, for example because Windows approval was declined.
    /// </summary>
    public async Task<bool> LaunchInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable path.");
        string installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // What this downloads is started with this process's rights. From %TEMP% while
        // MicMixer runs as administrator, any program without those rights could swap
        // it before it starts, so it goes into the install folder instead.
        string workParent = Elevation.IsElevated ? installDirectory : Path.GetTempPath();
        string workDirectory = Path.Combine(workParent, $"{UpdateInstaller.WorkDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        string downloadPath = Path.Combine(workDirectory, update.IsInstaller ? "setup.exe" : UpdateInstaller.ArchiveName);

        try
        {
            progress?.Report(new UpdateProgress("Downloading update…", 0));
            await DownloadFileAsync(update.DownloadUri, downloadPath, progress, cancellationToken);
            progress?.Report(new UpdateProgress("Verifying download…"));
            string checksumText = await _httpClient.GetStringAsync(update.ChecksumUri, cancellationToken);
            string expectedHash = ParseChecksum(checksumText);
            await using (var downloadStream = File.OpenRead(downloadPath))
            {
                await VerifySha256Async(downloadStream, expectedHash, cancellationToken);
            }

            if (!update.IsInstaller)
            {
                StartZipUpdater(executablePath, installDirectory, workDirectory, downloadPath, expectedHash, progress);
                return true;
            }

            if (await RunSetupUntilItTakesOverAsync(downloadPath, workDirectory, progress))
                return true;

            try { Directory.Delete(workDirectory, recursive: true); } catch { }
            return false;
        }
        catch
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Name of the event the installer sets once Windows has approved it, telling the
    /// MicMixer with that process id to exit. Must match PrepareToInstall in installer/MicMixer.iss.
    /// </summary>
    internal static string ExitForUpdateEventName(int processId) => $"MicMixer.ExitForUpdate.{processId}";

    // The installer asks for Windows approval itself, after it has started. MicMixer
    // keeps running until the installer signals that it got it, so a declined prompt
    // leaves MicMixer open. The installer then waits for this process to exit, replaces
    // the files, and starts MicMixer again with the rights this process had.
    private static async Task<bool> RunSetupUntilItTakesOverAsync(
        string setupPath, string workDirectory, IProgress<UpdateProgress>? progress)
    {
        using var exitRequested = EventWaitHandleAcl.Create(
            false, EventResetMode.ManualReset, ExitForUpdateEventName(Environment.ProcessId), out _, CreateExitRequestSecurity());
        var startInfo = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            WorkingDirectory = workDirectory
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add($"/WAITPID={Environment.ProcessId}");
        startInfo.ArgumentList.Add($"/UPDATECLEANUP={workDirectory}");

        Elevation.LetAdministratorsWaitForExit();

        progress?.Report(new UpdateProgress(
            Elevation.IsElevated ? "Starting installer…" : "Waiting for Windows approval…"));
        using Process setup = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the MicMixer installer.");

        var takenOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            exitRequested, (_, _) => takenOver.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            // The installer's own process stays until it has finished, so it ending first
            // means it never got as far as installing.
            return await Task.WhenAny(takenOver.Task, setup.WaitForExitAsync()) == takenOver.Task;
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    // The installer may run as another account: a standard account's UAC prompt takes an
    // administrator's password. The default permission of an event made without
    // administrator rights would keep that installer from signalling it.
    private static EventWaitHandleSecurity CreateExitRequestSecurity()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            Elevation.CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
            AccessControlType.Allow));
        return security;
    }

    private static void StartZipUpdater(
        string executablePath,
        string installDirectory,
        string workDirectory,
        string zipPath,
        string expectedHash,
        IProgress<UpdateProgress>? progress)
    {
        bool requiresElevation = !CanWriteToDirectory(installDirectory);
        string updaterPath = Path.Combine(workDirectory, "MicMixer.Update.exe");

        progress?.Report(new UpdateProgress("Preparing installer…"));
        File.Copy(executablePath, updaterPath);
        // The updater is this exe, which cannot start without the native libraries beside it.
        foreach (string library in Directory.EnumerateFiles(installDirectory, "*.dll"))
            File.Copy(library, Path.Combine(workDirectory, Path.GetFileName(library)));

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

    internal static bool TryParseVersion(string tagName, out Version? version) =>
        Version.TryParse(tagName.Trim().TrimStart('v', 'V'), out version);

    /// <summary>Must match the zip asset name in .github/workflows/release.yml. Older MicMixer versions look for it too.</summary>
    internal static string GetReleaseZipName(Version version) =>
        $"MicMixer-{version}-win-x64.zip";

    /// <summary>Must match the installer asset name in .github/workflows/release.yml.</summary>
    internal static string GetReleaseSetupName(Version version) =>
        $"MicMixer-{version}-win-x64-setup.exe";

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
