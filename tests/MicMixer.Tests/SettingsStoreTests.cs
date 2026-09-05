using AwesomeAssertions;
using MicMixer.Settings;
using Xunit;

namespace MicMixer.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MicMixer.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{}", 1f)]
    [InlineData("{\"ProcessedVoiceVolume\":-1}", 0f)]
    [InlineData("{\"ProcessedVoiceVolume\":3}", 1f)]
    public void Load_ShouldDefaultAndClampProcessedVoiceVolume(string json, float expected)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, json);
        new SettingsStore(path).Load().ProcessedVoiceVolume.Should().Be(expected);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripRoutingAndMusicSettings_WhenValuesAreConfigured()
    {
        string path = Path.Combine(_root, "nested", "settings.json");
        var sut = new SettingsStore(path);
        var expected = new AppSettings
        {
            StartWithWindows = true,
            PushToTalkMode = true,
            OverlayIndicatorEnabled = true,
            MusicFolderPaths = [@"C:\Music A", @"D:\Music B"],
            DownloadFolderPath = @"D:\Music B",
            MusicVolume = 0.75f,
            ProcessedVoiceVolume = 0.63f,
            SecondaryOutputEnabled = true,
            SecondaryOutputDeviceId = "secondary-device-id",
            SecondaryOutputVolume = 0.6f,
            SecondaryOutputIgnorePushToTalk = false
        };

        sut.Save(expected);
        AppSettings actual = sut.Load();

        actual.StartWithWindows.Should().BeTrue();
        actual.PushToTalkMode.Should().BeTrue();
        actual.OverlayIndicatorEnabled.Should().BeTrue();
        actual.MusicFolderPaths.Should().Equal(expected.MusicFolderPaths!);
        actual.DownloadFolderPath.Should().Be(expected.DownloadFolderPath);
        actual.MusicVolume.Should().Be(expected.MusicVolume);
        actual.ProcessedVoiceVolume.Should().Be(expected.ProcessedVoiceVolume);
        actual.SecondaryOutputEnabled.Should().BeTrue();
        actual.SecondaryOutputDeviceId.Should().Be(expected.SecondaryOutputDeviceId);
        actual.SecondaryOutputVolume.Should().Be(expected.SecondaryOutputVolume);
        actual.SecondaryOutputIgnorePushToTalk.Should().BeFalse();
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Load_ShouldReturnSafeDefaults_WhenJsonIsCorrupt()
    {
        string path = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{ not-json");

        var sut = new SettingsStore(path);

        AppSettings settings = sut.Load();

        settings.MonitorEnabled.Should().BeTrue();
        settings.MusicVolume.Should().Be(0.5f);
        settings.MonitorVolume.Should().Be(0.5f);
        settings.SecondaryOutputEnabled.Should().BeFalse();
        settings.SecondaryOutputVolume.Should().Be(1.0f);
        settings.SecondaryOutputIgnorePushToTalk.Should().BeTrue();
    }

    [Fact]
    public void SavingAnUnrelatedStateChange_ShouldPreserveUnavailableDevicePreferences()
    {
        string path = Path.Combine(_root, "settings.json");
        var sut = new SettingsStore(path);
        sut.Save(new AppSettings
        {
            NormalInputDeviceId = "normal-mic",
            ModdedInputDeviceId = "modded-mic",
            OutputDeviceId = "virtual-cable",
            MusicMonitorDeviceId = "unplugged-monitor",
            SecondaryOutputDeviceId = "unplugged-secondary"
        });

        AppSettings state = sut.Load();
        state.MusicVolume = 0.8f;
        sut.Save(state);

        AppSettings reloaded = sut.Load();
        reloaded.NormalInputDeviceId.Should().Be("normal-mic");
        reloaded.ModdedInputDeviceId.Should().Be("modded-mic");
        reloaded.OutputDeviceId.Should().Be("virtual-cable");
        reloaded.MusicMonitorDeviceId.Should().Be("unplugged-monitor");
        reloaded.SecondaryOutputDeviceId.Should().Be("unplugged-secondary");
        reloaded.MusicVolume.Should().Be(0.8f);
    }

    [Theory]
    [InlineData(ModifiedVoiceMode.None)]
    [InlineData(ModifiedVoiceMode.ExternalMicrophone)]
    [InlineData(ModifiedVoiceMode.LocalProfile)]
    public void SaveAndLoad_ShouldRoundTripModifiedVoiceModes(ModifiedVoiceMode mode)
    {
        string path = Path.Combine(_root, $"{mode}.json");
        var sut = new SettingsStore(path);

        sut.Save(new AppSettings
        {
            ModifiedVoiceMode = mode,
            ModdedInputDeviceId = "legacy-external-device",
            LongerAnalysisWindow = true
        });

        AppSettings reloaded = sut.Load();
        reloaded.ModifiedVoiceMode.Should().Be(mode);
        reloaded.ModdedInputDeviceId.Should().Be("legacy-external-device");
        reloaded.LongerAnalysisWindow.Should().BeTrue();
        reloaded.SkipModdedMic.Should().Be(mode == ModifiedVoiceMode.None);
    }

    [Fact]
    public void Load_ShouldMigrateLegacyExternalModdedDevice()
    {
        string path = Path.Combine(_root, "legacy-external.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, """
            {
              "ModdedInputDeviceId": "voicemod-device-id",
              "SkipModdedMic": false
            }
            """);

        AppSettings reloaded = new SettingsStore(path).Load();

        reloaded.ModifiedVoiceMode.Should().Be(ModifiedVoiceMode.ExternalMicrophone);
        reloaded.ModdedInputDeviceId.Should().Be("voicemod-device-id");
    }

    [Fact]
    public void Load_ShouldMigrateLegacyNoModdedMicSelection()
    {
        string path = Path.Combine(_root, "legacy-none.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, """
            {
              "ModdedInputDeviceId": "previous-device-id",
              "SkipModdedMic": true
            }
            """);

        AppSettings reloaded = new SettingsStore(path).Load();

        reloaded.ModifiedVoiceMode.Should().Be(ModifiedVoiceMode.None);
        reloaded.ModdedInputDeviceId.Should().Be("previous-device-id");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
