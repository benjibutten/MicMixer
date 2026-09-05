using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MicMixer.Dsp;
using MicMixer.Settings;
using Xunit;

namespace MicMixer.Tests;

public sealed class VoiceProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MicMixer-profile-tests", Guid.NewGuid().ToString());
    private VoiceProfile Example() => new() { FormatVersion = 1, Id = Guid.NewGuid().ToString(), DisplayName = "Synthetic voice",
        Parameters = new() { PitchSemitones = 3, LowMidGainDb = -2, CompressorRatio = 4 }, AlternateBlockMilliseconds = 60 };

    [Fact]
    public void ImportRoundTrip_PreservesEveryParameter_AndNeverOverwrites()
    {
        var store = new VoiceProfileStore(_directory); var profile = Example();
        store.Import(profile); store.Load(profile.Id).Should().Be(profile);
        Action duplicate = () => store.Import(profile with { DisplayName = "Edited" });
        duplicate.Should().Throw<IOException>();
        store.Load(profile.Id).Should().Be(profile);
        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void CleanInstallation_HasNoInventedVoice_AndFailsExplicitlyIfSelected()
    {
        var store = new VoiceProfileStore(_directory);
        store.List().Profiles.Should().BeEmpty(); store.List().Errors.Should().BeEmpty();
        Action missingSelection = () => store.Load(null);
        missingSelection.Should().Throw<InvalidDataException>();
        Action missingFile = () => store.Load(Guid.NewGuid().ToString());
        missingFile.Should().Throw<InvalidDataException>();
        new SettingsStore(Path.Combine(_directory,"settings.json")).Load().ModifiedVoiceMode.Should().Be(ModifiedVoiceMode.None);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("invalid")]
    public void UnsafeIdentity_IsRejected(string id)
    {
        Action action = () => new VoiceProfileStore(_directory).Import(Example() with { Id = id });
        action.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missingParameter")]
    [InlineData("unknownParameter")]
    [InlineData("invalidParameter")]
    [InlineData("identityMismatch")]
    [InlineData("emptyName")]
    [InlineData("nullParameters")]
    [InlineData("invalidAlternate")]
    public void InvalidProfile_IsReported_NotSilentlyReplaced(string kind)
    {
        var store = new VoiceProfileStore(_directory); var profile = Example(); store.Import(profile);
        var node = JsonSerializer.SerializeToNode(profile, VoiceProfileStore.JsonOptions)!;
        switch (kind)
        {
            case "version": node["formatVersion"] = 99; break;
            case "missingParameter": node["parameters"]!.AsObject().Remove("pitchSemitones"); break;
            case "unknownParameter": node["parameters"]!["mystery"] = 1; break;
            case "invalidParameter": node["parameters"]!["compressorRatio"] = 0; break;
            case "identityMismatch": node["id"] = Guid.NewGuid().ToString(); break;
            case "emptyName": node["displayName"] = " "; break;
            case "nullParameters": node["parameters"] = null; break;
            case "invalidAlternate": node["alternateBlockMilliseconds"] = 0; break;
        }
        File.WriteAllText(Path.Combine(_directory,profile.Id+".json"),node.ToJsonString());
        Action action = () => store.Load(profile.Id); action.Should().Throw<InvalidDataException>();
        store.List().Profiles.Should().BeEmpty(); store.List().Errors.Should().ContainSingle();
    }

    [Fact]
    public void WindowResolution_PreservesProfile_AndRequiresExplicitAlternate()
    {
        var profile = Example();
        profile.Resolve(false).Should().Be(profile.Parameters);
        profile.Resolve(true).Should().Be(profile.Parameters with { BlockMilliseconds = 60 });
        Action absent = () => (profile with { AlternateBlockMilliseconds = null }).Resolve(true);
        absent.Should().Throw<InvalidDataException>();
        profile.Parameters.BlockMilliseconds.Should().Be(40);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{broken")]
    public void MalformedFile_IsListedAsError(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory,Guid.NewGuid()+".json"),json);
        var result = new VoiceProfileStore(_directory).List();
        result.Profiles.Should().BeEmpty(); result.Errors.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyMigration_IsIdempotent_AndPreservesSelectionVolumeWindow(bool longer)
    {
        Directory.CreateDirectory(_directory);
        string id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_directory,"legacy-voice-profile.json"),JsonSerializer.Serialize(new {profileId=id}));
        string path = Path.Combine(_directory,"settings.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new { ModifiedVoiceMode="BuiltInGirly", GirlyOutputVolume=0.42f, SmootherGirlyProcessing=longer }));
        var store = new SettingsStore(path); var settings = store.Load();
        settings.ModifiedVoiceMode.Should().Be(ModifiedVoiceMode.LocalProfile);
        settings.SelectedVoiceProfileId.Should().Be(id); settings.ProcessedVoiceVolume.Should().Be(0.42f);
        settings.LongerAnalysisWindow.Should().Be(longer); store.Save(settings);
        File.WriteAllText(Path.Combine(_directory,"legacy-voice-profile.json"),"{\"profileId\":\"different\"}");
        var again = store.Load(); again.SelectedVoiceProfileId.Should().Be(id);
        again.ProcessedVoiceVolume.Should().Be(0.42f); again.LongerAnalysisWindow.Should().Be(longer);
        File.ReadAllText(path).Should().NotContain("Girly");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidMigrationBinding_DoesNotSwitchToAnotherVoice(bool invalid)
    {
        Directory.CreateDirectory(_directory);
        if (invalid) File.WriteAllText(Path.Combine(_directory,"legacy-voice-profile.json"),"bad json");
        string path = Path.Combine(_directory,"settings.json");
        File.WriteAllText(path,"{\"ModifiedVoiceMode\":\"BuiltInGirly\",\"GirlyOutputVolume\":0.7,\"SmootherGirlyProcessing\":true}");
        var settings = new SettingsStore(path).Load();
        settings.ModifiedVoiceMode.Should().Be(ModifiedVoiceMode.LocalProfile);
        settings.SelectedVoiceProfileId.Should().BeNull(); settings.ProcessedVoiceVolume.Should().Be(0.7f);
        settings.LongerAnalysisWindow.Should().BeTrue();
    }

    [Fact]
    public void GenericSettingsWinOverLegacyFields()
    {
        Directory.CreateDirectory(_directory); string path = Path.Combine(_directory,"settings.json");
        File.WriteAllText(path,"{\"ProcessedVoiceVolume\":0.2,\"GirlyOutputVolume\":0.9,\"LongerAnalysisWindow\":false,\"SmootherGirlyProcessing\":true}");
        var settings = new SettingsStore(path).Load(); settings.ProcessedVoiceVolume.Should().Be(0.2f);
        settings.LongerAnalysisWindow.Should().BeFalse();
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-30f)]
    [InlineData(30f)]
    public void InvalidPitch_IsRejectedBeforeProcessing(float pitch)
    {
        Action action = () => (Example() with { Parameters = new() { PitchSemitones = pitch } }).Validate();
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
