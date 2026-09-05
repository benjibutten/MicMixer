using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicMixer.Dsp;

/// <summary>Portable data model; identity is independent of the editable display name.</summary>
public sealed record VoiceProfile
{
    public required int FormatVersion { get; init; }
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required VoiceDspParameters Parameters { get; init; }
    public float? AlternateBlockMilliseconds { get; init; }

    public VoiceDspParameters Resolve(bool alternateWindow)
    {
        if (!alternateWindow) return Parameters;
        if (AlternateBlockMilliseconds is not float block)
            throw new InvalidDataException("The selected profile has no alternate analysis window. Turn off the alternate window option.");
        return Parameters with { BlockMilliseconds = block };
    }

    public void Validate()
    {
        if (FormatVersion != 1) throw new InvalidDataException("Unsupported voice profile format version.");
        if (!Guid.TryParseExact(Id, "D", out _)) throw new InvalidDataException("Profile ID must be a UUID.");
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 100)
            throw new InvalidDataException("Profile display name must contain 1–100 characters.");
        if (Parameters == null) throw new InvalidDataException("Profile DSP parameters are missing.");
        Parameters.Validate(48_000, 1);
        if (AlternateBlockMilliseconds != null) Resolve(true).Validate(48_000, 1);
    }
}

/// <summary>Call only on the control thread. Loaded records are immutable DSP snapshots.</summary>
public sealed class VoiceProfileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string DirectoryPath { get; }
    public VoiceProfileStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicMixer", "Voices");

    public static VoiceProfile ReadFile(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Voice profile must be a JSON object.");
            var parameters = document.RootElement.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals("parameters", StringComparison.OrdinalIgnoreCase)).Value;
            if (parameters.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Profile DSP parameters are missing.");
            var names = parameters.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var property in typeof(VoiceDspParameters).GetProperties().Where(p => p.GetMethod is { IsStatic: false }))
                if (!names.Contains(property.Name)) throw new InvalidDataException($"Missing DSP parameter: {property.Name}.");
            var profile = JsonSerializer.Deserialize<VoiceProfile>(json, JsonOptions)
                ?? throw new InvalidDataException("Profile is empty.");
            profile.Validate();
            return profile;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Cannot load voice profile '{Path.GetFileName(path)}': {ex.Message}", ex);
        }
    }

    public VoiceProfile Load(string? id)
    {
        if (!Guid.TryParseExact(id, "D", out _))
            throw new InvalidDataException("No valid local voice profile is selected. Select or import a profile before enabling routing.");
        var profile = ReadFile(Path.Combine(DirectoryPath, id + ".json"));
        if (profile.Id != id) throw new InvalidDataException("Voice profile ID does not match its filename.");
        return profile;
    }

    public (List<VoiceProfile> Profiles, List<string> Errors) List()
    {
        var profiles = new List<VoiceProfile>();
        var errors = new List<string>();
        if (!Directory.Exists(DirectoryPath)) return (profiles, errors);
        try
        {
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                try { profiles.Add(Load(Path.GetFileNameWithoutExtension(file))); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { errors.Add(ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { errors.Add(ex.Message); }
        profiles.Sort((a,b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName,b.DisplayName));
        return (profiles, errors);
    }

    /// <summary>Import/save-as never overwrites an existing or user-edited profile.</summary>
    public void Import(VoiceProfile profile)
    {
        profile.Validate();
        Directory.CreateDirectory(DirectoryPath);
        string destination = Path.Combine(DirectoryPath, profile.Id + ".json");
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
