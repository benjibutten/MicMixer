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
        // The persisted preference may outlive the profile that supplied the alternate.
        // In that case the profile's base window is the only valid resolution.
        if (!alternateWindow || AlternateBlockMilliseconds is not float block)
            return Parameters;
        if (Parameters.PitchEngine == PitchEngine.TimeDomain)
            throw new InvalidDataException("Time-domain profiles cannot define an alternate Signalsmith window.");
        return Parameters with { BlockMilliseconds = block };
    }

    public void Validate()
    {
        if (FormatVersion is not (1 or 2)) throw new InvalidDataException("Unsupported voice profile format version.");
        if (!Guid.TryParseExact(Id, "D", out _)) throw new InvalidDataException("Profile ID must be a UUID.");
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 100)
            throw new InvalidDataException("Profile display name must contain 1–100 characters.");
        if (Parameters == null) throw new InvalidDataException("Profile DSP parameters are missing.");
        if (Parameters.PitchEngine == PitchEngine.TimeDomain && FormatVersion != 2)
            throw new InvalidDataException("Time-domain profiles require format version 2.");
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
            var versionElement = document.RootElement.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals("formatVersion", StringComparison.OrdinalIgnoreCase)).Value;
            if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version))
                throw new InvalidDataException("Profile format version is missing or invalid.");
            foreach (var property in typeof(VoiceDspParameters).GetProperties().Where(p => p.GetMethod is { IsStatic: false }))
            {
                bool legacyOptional = version == 1 && property.Name is nameof(VoiceDspParameters.PitchEngine)
                    or nameof(VoiceDspParameters.TimeDomainWindowMilliseconds) or nameof(VoiceDspParameters.TimeDomainSearchMilliseconds);
                if (!legacyOptional && !names.Contains(property.Name)) throw new InvalidDataException($"Missing DSP parameter: {property.Name}.");
            }
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
        if (BuiltInVoiceProfiles.Find(id) is { } builtIn) return builtIn;
        var profile = ReadFile(FilePath(id));
        if (profile.Id != id) throw new InvalidDataException("Voice profile ID does not match its filename.");
        return profile;
    }

    /// <summary>Rejects ids that are not UUIDs, so an id can never escape the store directory.</summary>
    private string FilePath(string? id) => Guid.TryParseExact(id, "D", out _)
        ? Path.Combine(DirectoryPath, id + ".json")
        : throw new InvalidDataException("No valid local voice profile is selected. Select or import a profile before enabling routing.");

    public (List<VoiceProfile> Profiles, List<string> Errors) List()
    {
        var profiles = new List<VoiceProfile>(BuiltInVoiceProfiles.All);
        var errors = new List<string>();
        if (!Directory.Exists(DirectoryPath)) return (profiles, errors);
        try
        {
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                if (BuiltInVoiceProfiles.Find(Path.GetFileNameWithoutExtension(file)) != null)
                {
                    errors.Add($"'{Path.GetFileName(file)}' uses a reserved built-in ID. Save a copy with a new ID.");
                    continue;
                }
                try { profiles.Add(Load(Path.GetFileNameWithoutExtension(file))); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { errors.Add(ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { errors.Add(ex.Message); }
        profiles.Sort((a,b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayName,b.DisplayName));
        return (profiles, errors);
    }

    /// <summary>Import/save-as never overwrites an existing or user-edited profile.</summary>
    public void Import(VoiceProfile profile) => Write(profile, overwrite: false);

    /// <summary>Saves edits back to a profile the user already owns.</summary>
    public void Replace(VoiceProfile profile) => Write(profile, overwrite: true);

    public void Delete(string? id)
    {
        RequireEditable(id);
        File.Delete(FilePath(id));
    }

    private void Write(VoiceProfile profile, bool overwrite)
    {
        profile.Validate();
        RequireEditable(profile.Id);
        Directory.CreateDirectory(DirectoryPath);
        string destination = FilePath(profile.Id);
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequireEditable(string? id)
    {
        if (BuiltInVoiceProfiles.Find(id) != null)
            throw new InvalidDataException("Built-in voices cannot be overwritten. Save a copy with a new ID.");
    }
}
