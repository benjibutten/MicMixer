using Microsoft.Win32;

namespace MicMixer.UI;

/// <summary>
/// Windows 11 keeps a new tray icon in the overflow menu until the user moves it
/// to the taskbar. This makes that choice on the user's behalf.
/// </summary>
internal static class TrayIconPromotion
{
    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";
    private const string PromotedValue = "IsPromoted";

    /// <summary>
    /// Shows the tray icon of <paramref name="executablePath"/> on the taskbar. Does
    /// nothing when the user has already placed the icon in Windows settings, when
    /// Windows has not recorded the icon yet, or on Windows 10.
    /// </summary>
    public static void PromoteUnlessUserChose(string executablePath)
    {
        using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey);
        if (settings == null)
        {
            return;
        }

        foreach (string iconKeyName in settings.GetSubKeyNames())
        {
            using RegistryKey? icon = settings.OpenSubKey(iconKeyName);
            // Windows writes the value only once the user picks a side, so a missing
            // value is the one state that is safe to overwrite.
            if (icon == null
                || icon.GetValue(PromotedValue) != null
                || !IsSameExecutable(icon.GetValue("ExecutablePath") as string, executablePath))
            {
                continue;
            }

            using RegistryKey? writableIcon = settings.OpenSubKey(iconKeyName, writable: true);
            writableIcon?.SetValue(PromotedValue, 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>True when <paramref name="recordedPath"/>, as Windows stores it, points at <paramref name="executablePath"/>.</summary>
    internal static bool IsSameExecutable(string? recordedPath, string executablePath)
    {
        if (string.IsNullOrEmpty(recordedPath))
        {
            return false;
        }

        // A path under a known folder such as Program Files is recorded as
        // "{folder GUID}\rest of the path".
        int guidEnd = recordedPath.IndexOf('}');
        if (recordedPath.StartsWith('{') && guidEnd > 0)
        {
            return executablePath.EndsWith(recordedPath[(guidEnd + 1)..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(recordedPath, executablePath, StringComparison.OrdinalIgnoreCase);
    }
}
