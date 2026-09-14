using AwesomeAssertions;
using MicMixer.Audio;
using Xunit;

namespace MicMixer.Tests;

public sealed class AudioDevicesTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE-A Input (VB-Audio Cable A)", true)]
    [InlineData("Line 1 (Virtual Audio Cable)", true)]
    [InlineData("Voicemeeter Input (VB-Audio Voicemeeter VAIO)", true)]
    [InlineData("Speakers (Realtek(R) Audio)", false)]
    [InlineData("Microphone (Voicemod Virtual Audio Device (WDM))", false)]
    public void LooksLikeVirtualCable_ShouldRecognizeCommonCableDrivers(string name, bool expected)
    {
        AudioDevices.LooksLikeVirtualCable(new AudioDeviceOption("id", name)).Should().Be(expected);
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", "CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("Line 1 (Virtual Audio Cable)", "Line 1 (Virtual Audio Cable)")]
    public void FindRecordingEnd_ShouldPairThePlaybackEndWithItsMicrophone(string playback, string expected)
    {
        var inputs = new List<AudioDeviceOption>
        {
            new("mic", "Microphone (Realtek(R) Audio)"),
            new("vb", "CABLE Output (VB-Audio Virtual Cable)"),
            new("vac", "Line 1 (Virtual Audio Cable)")
        };

        AudioDevices.FindRecordingEnd(new AudioDeviceOption("out", playback), inputs)!.FriendlyName.Should().Be(expected);
    }

    [Fact]
    public void SelectInput_ShouldNotGuessTheCableAsAMicrophone_WhenAnotherDeviceExists()
    {
        var inputs = new List<AudioDeviceOption>
        {
            new("cable", "CABLE Output (VB-Audio Virtual Cable)"),
            new("mic", "Microphone (Realtek(R) Audio)"),
            new("headset", "Headset Microphone (USB Audio)")
        };

        AudioDevices.SelectInput(inputs, null, AudioDevices.LooksLikeNormalMic)!.Id.Should().Be("mic");
        AudioDevices.SelectInput(inputs, null, AudioDevices.LooksLikeVoiceModDevice, excludedId: "mic")!.Id.Should().Be("headset");
    }
}
