using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MicMixer.Diagnostics;

/// <summary>
/// Reads the voice chat settings FiveM keeps in fivem.cfg, for the log only.
/// The game shows the sensitivity as an unlabeled slider and the input device
/// as a list index, so a user cannot report either reliably; the file has both.
/// </summary>
internal static class FiveMVoiceSettings
{
    // FiveM persists the game's profile settings as "profile_<camelCaseName>"
    // convars, one seta line each, CRLF line endings.
    private static readonly Regex ProfileLine = new(
        """^[ \t]*seta?[ \t]+"?profile_(?<key>voice\w+)"?[ \t]+"?(?<value>-?\d+)"?[ \t]*\r?$""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // The device index in fivem.cfg is resolved inside the game process against
    // a device order this process cannot reproduce (it differs per process on
    // the same machine). FiveM logs the device it actually opened, so that line
    // is the fact and the index is only reported raw.
    private static readonly Regex CaptureDeviceLine = new(
        @"Returning device (?<name>.+?) for GUID \{[0-9A-Fa-f-]+\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static string Describe()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CitizenFX", "fivem.cfg");

        string text;
        DateTime writtenAt;
        try
        {
            if (!File.Exists(path))
            {
                return "fivem.cfg not found";
            }

            text = File.ReadAllText(path);
            writtenAt = File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"fivem.cfg unreadable: {ex.Message}";
        }

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (Match match in ProfileLine.Matches(text))
            {
                if (int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    values[match.Groups["key"].Value] = value;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return "fivem.cfg could not be parsed";
        }

        var summary = new StringBuilder();
        summary.Append("chat mode ").Append(values.TryGetValue("voiceChatMode", out int mode)
            ? mode == 1 ? "push-to-talk (1)" : $"voice activation ({mode})"
            : "not set");
        summary.Append(", sensitivity ").Append(values.TryGetValue("voiceMicSensitivity", out int sensitivity)
            ? sensitivity.ToString(CultureInfo.InvariantCulture)
            : "not set");
        summary.Append(", input device index ").Append(values.TryGetValue("voiceInputDevice", out int deviceIndex)
            ? deviceIndex.ToString(CultureInfo.InvariantCulture)
            : "not set");
        summary.Append(", voice enabled ").Append(values.TryGetValue("voiceEnable", out int enabled) ? enabled != 0 : "not set");
        summary.Append(", talk enabled ").Append(values.TryGetValue("voiceTalkEnabled", out int talk) ? talk != 0 : "not set");
        summary.Append(", cfg written ").Append(writtenAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        summary.Append("; capture device per FiveM log: ").Append(DescribeCaptureDeviceFromLog());
        return summary.ToString();
    }

    private static string DescribeCaptureDeviceFromLog()
    {
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.app", "logs");

        FileInfo? newest;
        try
        {
            newest = new DirectoryInfo(logDirectory).Exists
                ? new DirectoryInfo(logDirectory).GetFiles("CitizenFX_log_*.log").MaxBy(file => file.LastWriteTimeUtc)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"log unreadable: {ex.Message}";
        }

        if (newest == null)
        {
            return "no FiveM log found";
        }

        string? lastDevice = null;
        try
        {
            // The game keeps the file open, so share the read.
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (CaptureDeviceLine.Match(line) is { Success: true } match)
                {
                    lastDevice = match.Groups["name"].Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return $"log unreadable: {ex.Message}";
        }

        string logStamp = newest.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return lastDevice == null
            ? $"no capture device line in {newest.Name} (written {logStamp})"
            : $"{lastDevice} ({newest.Name}, written {logStamp})";
    }
}
