using AwesomeAssertions;
using MicMixer.Settings;
using Xunit;

namespace MicMixer.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MicMixer.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_ShouldRoundTripRoutingAndMusicSettings_WhenValuesAreConfigured()
    {
        string path = Path.Combine(_root, "nested", "settings.json");
        var sut = new SettingsStore(path);
        var expected = new AppSettings
        {
            PushToTalkMode = true,
            OverlayIndicatorEnabled = true,
            MusicFolderPaths = [@"C:\Music A", @"D:\Music B"],
            DownloadFolderPath = @"D:\Music B",
            MusicVolume = 0.75f,
            SecondaryOutputEnabled = true,
            SecondaryOutputDeviceId = "secondary-device-id",
            SecondaryOutputVolume = 0.6f,
            SecondaryOutputIgnorePushToTalk = false
        };

        sut.Save(expected);
        AppSettings actual = sut.Load();

        actual.PushToTalkMode.Should().BeTrue();
        actual.OverlayIndicatorEnabled.Should().BeTrue();
        actual.MusicFolderPaths.Should().Equal(expected.MusicFolderPaths!);
        actual.DownloadFolderPath.Should().Be(expected.DownloadFolderPath);
        actual.MusicVolume.Should().Be(expected.MusicVolume);
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
