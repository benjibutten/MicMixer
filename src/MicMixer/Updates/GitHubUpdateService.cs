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

    public async Task LaunchInstallerAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), $"MicMixer-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        string zipPath = Path.Combine(workDirectory, "update.zip");
        string scriptPath = Path.Combine(workDirectory, "install-update.ps1");

        try
        {
            await DownloadFileAsync(update.DownloadUri, zipPath, cancellationToken);
            string checksumText = await _httpClient.GetStringAsync(update.ChecksumUri, cancellationToken);
            string expectedHash = ParseChecksum(checksumText);
            string actualHash;
            await using (var zipStream = File.OpenRead(zipPath))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(zipStream, cancellationToken));
            }

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update failed its SHA-256 integrity check.");

            await File.WriteAllTextAsync(scriptPath, UpdaterScript, cancellationToken);

            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the running executable path.");
            string installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string installParent = Directory.GetParent(installDirectory)?.FullName
                ?? throw new InvalidOperationException("Could not determine the installation parent directory.");
            bool requiresElevation = !CanWriteToDirectory(installDirectory)
                || !CanWriteToDirectory(installParent);
            string powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var startInfo = new ProcessStartInfo(powershellPath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            if (requiresElevation)
                startInfo.Verb = "runas";

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-ZipPath");
            startInfo.ArgumentList.Add(zipPath);
            startInfo.ArgumentList.Add("-ExpectedHash");
            startInfo.ArgumentList.Add(expectedHash);
            startInfo.ArgumentList.Add("-InstallDirectory");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("-ExecutablePath");
            startInfo.ArgumentList.Add(executablePath);
            Process.Start(startInfo);
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

    private async Task DownloadFileAsync(Uri uri, string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination, cancellationToken);
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

    private const string UpdaterScript = """
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$ZipPath,
    [Parameter(Mandatory=$true)][string]$ExpectedHash,
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][string]$ExecutablePath
)
$ErrorActionPreference = 'Stop'
$workDirectory = Split-Path -Parent $ZipPath
$stagingDirectory = Join-Path $workDirectory 'staging'
$installParent = Split-Path -Parent $InstallDirectory
$installLeaf = Split-Path -Leaf $InstallDirectory
$transactionId = [Guid]::NewGuid().ToString('N')
$replacementDirectory = Join-Path $installParent "$installLeaf.update-$transactionId"
$backupDirectory = Join-Path $installParent "$installLeaf.backup-$transactionId"
$originalMoved = $false
try {
    Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
    $actualHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedHash) { throw 'Update integrity check failed.' }
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $stagingDirectory -Force

    $executableName = Split-Path -Leaf $ExecutablePath
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $executableName) -PathType Leaf)) {
        throw "The update archive does not contain $executableName."
    }

    New-Item -ItemType Directory -Path $replacementDirectory | Out-Null
    Get-ChildItem -LiteralPath $InstallDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $replacementDirectory -Recurse -Force
    }
    Get-ChildItem -LiteralPath $stagingDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $replacementDirectory -Recurse -Force
    }

    Rename-Item -LiteralPath $InstallDirectory -NewName (Split-Path -Leaf $backupDirectory)
    $originalMoved = $true
    Rename-Item -LiteralPath $replacementDirectory -NewName $installLeaf
    Start-Process -FilePath $ExecutablePath
    Remove-Item -LiteralPath $backupDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $originalMoved = $false
}
catch {
    if ($originalMoved -and (Test-Path -LiteralPath $backupDirectory)) {
        Remove-Item -LiteralPath $InstallDirectory -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $InstallDirectory)) {
            Rename-Item -LiteralPath $backupDirectory -NewName $installLeaf -ErrorAction SilentlyContinue
        }
    }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("The update could not be installed.`n`n$($_.Exception.Message)", 'MicMixer Update', 'OK', 'Error') | Out-Null
}
finally {
    Start-Sleep -Milliseconds 300
    Remove-Item -LiteralPath $replacementDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
""";
}
