using System.IO;
using Microsoft.Win32;

namespace MicMixer.Updates;

/// <summary>
/// Detects how the running MicMixer executable was installed, which decides how it
/// updates: through winget, through the MicMixer installer, or by replacing its files in place.
/// </summary>
internal static class InstallEnvironment
{
    private const string PackageIdentifier = "BenjiButten.MicMixer";

    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly Lazy<bool> ManagedByWinget = new(DetectWinget);

    /// <summary>
    /// True when MicMixer runs from a Windows Package Manager (winget) install.
    /// In that case winget owns upgrades: a self-update would swap the executable
    /// without winget's knowledge, leaving <c>winget upgrade</c> to report a stale
    /// version.
    /// </summary>
    public static bool IsManagedByWinget => ManagedByWinget.Value;

    /// <summary>
    /// True when MicMixer runs from a folder the MicMixer installer set up. Updates
    /// then run the new installer, so Apps and features keeps the right version.
    /// </summary>
    public static bool IsInstalledWithSetup =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static bool DetectWinget()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
            return false;

        // Fast path: the default winget portable roots.
        if (IsWingetPath(executablePath))
            return true;

        // Robust path: winget records every portable install in the registry
        // uninstall key, keyed by "<PackageIdentifier>_<SourceIdentifier>", with the
        // actual InstallLocation. This covers custom portablePackageUserRoot and
        // portablePackageMachineRoot settings that move installs off the default
        // roots, where the path heuristic above would miss them.
        foreach (string installLocation in EnumerateWingetInstallLocations())
        {
            if (IsExecutableWithin(executablePath, installLocation))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="executablePath"/> points inside a default
    /// Windows Package Manager (winget) install location. Exposed for testing.
    /// </summary>
    internal static bool IsWingetPath(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
            return false;

        string normalized = Path.GetFullPath(executablePath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // winget installs portable packages under ...\WinGet\Packages\ and exposes
        // them through command shims under ...\WinGet\Links\. Either location means
        // this install is managed by winget.
        return normalized.Contains(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\WinGet\Links\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="executablePath"/> lives inside the
    /// directory <paramref name="installLocation"/>. Exposed for testing.
    /// </summary>
    internal static bool IsExecutableWithin(string? executablePath, string? installLocation)
    {
        if (string.IsNullOrEmpty(executablePath) || string.IsNullOrEmpty(installLocation))
            return false;

        string executable = Path.GetFullPath(executablePath);
        string root = Path.GetFullPath(installLocation);
        if (string.Equals(executable, root, StringComparison.OrdinalIgnoreCase))
            return true;

        string rootWithSeparator = root
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return executable.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the InstallLocation of every winget registration for this package from
    /// the per-user and machine uninstall registry keys.
    /// </summary>
    private static IEnumerable<string> EnumerateWingetInstallLocations()
    {
        var locations = new List<string>();
        CollectInstallLocations(RegistryHive.CurrentUser, RegistryView.Default, locations);
        CollectInstallLocations(RegistryHive.LocalMachine, RegistryView.Registry64, locations);
        CollectInstallLocations(RegistryHive.LocalMachine, RegistryView.Registry32, locations);
        return locations;
    }

    private static void CollectInstallLocations(
        RegistryHive hive,
        RegistryView view,
        List<string> locations)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
            if (uninstallKey is null)
                return;

            string prefix = PackageIdentifier + "_";
            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                if (!subKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                using RegistryKey? entry = uninstallKey.OpenSubKey(subKeyName);
                if (entry?.GetValue("InstallLocation") is string installLocation
                    && !string.IsNullOrWhiteSpace(installLocation))
                {
                    locations.Add(installLocation);
                }
            }
        }
        catch
        {
            // Registry access is best effort; a failure just falls back to the path
            // heuristic and the normal self-update flow.
        }
    }
}
