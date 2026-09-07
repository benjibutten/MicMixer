using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;

namespace MicMixer.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;

    public SettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicMixer",
            "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_settingsPath);
            // Legacy names are read only. The local binding contains no DSP defaults.
            var root = JsonNode.Parse(json)!.AsObject();
            if (root["ModifiedVoiceMode"]?.ToString() == "BuiltInGirly")
                root["ModifiedVoiceMode"] = "LocalProfile";
            if (!root.ContainsKey("ProcessedVoiceVolume") && root.ContainsKey("GirlyOutputVolume"))
                root["ProcessedVoiceVolume"] = root["GirlyOutputVolume"]?.DeepClone();
            if (!root.ContainsKey("LongerAnalysisWindow") && root.ContainsKey("SmootherGirlyProcessing"))
                root["LongerAnalysisWindow"] = root["SmootherGirlyProcessing"]?.DeepClone();
            json = root.ToJsonString();
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();

            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.ModifiedVoiceMode), out _)
                || !Enum.IsDefined(settings.ModifiedVoiceMode))
            {
                settings.ModifiedVoiceMode = settings.SkipModdedMic || string.IsNullOrWhiteSpace(settings.ModdedInputDeviceId)
                    ? ModifiedVoiceMode.None
                    : ModifiedVoiceMode.ExternalMicrophone;
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedVoiceProfileId))
            {
                string binding = Path.Combine(Path.GetDirectoryName(_settingsPath)!, "legacy-voice-profile.json");
                if (File.Exists(binding))
                {
                    try
                    {
                        using var map = JsonDocument.Parse(File.ReadAllText(binding));
                        settings.SelectedVoiceProfileId = map.RootElement.GetProperty("profileId").GetString();
                    }
                    catch (Exception ex) { Log.Warning(ex, "Cannot read local voice migration binding"); }
                }
            }
            settings.ProcessedVoiceVolume = float.IsFinite(settings.ProcessedVoiceVolume)
                ? Math.Clamp(settings.ProcessedVoiceVolume, 0f, 1f) : 1f;
            settings.SkipModdedMic = settings.ModifiedVoiceMode == ModifiedVoiceMode.None;
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {SettingsPath}; using defaults.", _settingsPath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.SkipModdedMic = settings.ModifiedVoiceMode == ModifiedVoiceMode.None;
        string directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);

        string tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(tempPath, _settingsPath, overwrite: true);
    }
}
