using MicMixer.Audio;
using MicMixer.Dsp;
using Xunit;

namespace MicMixer.Tests;

public sealed class VoicePreviewTests
{
    [Fact]
    public void FinitePreview_SignalsEndOfFile_WhileLiveAdapterKeepsItsClock()
    {
        var samples = new VoicePreview.PreviewSamples([.2f, .4f, .6f], 1, false);
        var finite = new SampleToTargetWaveProvider(samples, samples.WaveFormat, padSilence: false);
        var output = new byte[32];
        Assert.Equal(12, finite.Read(output));
        Assert.Equal(0, finite.Read(output));
        var live = new SampleToTargetWaveProvider(samples, samples.WaveFormat);
        Assert.Equal(output.Length, live.Read(output));
        Assert.All(output, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void SamplePlayback_KeepsPosition_AndAppliesLoopAndVolumeChanges()
    {
        var source = new VoicePreview.PreviewSamples([.2f, .4f, .6f], 1, false);
        var output = new float[2];
        Assert.Equal(2, source.Read(output));
        Assert.Equal(new[] { .2f, .4f }, output);
        // A paused player does not request samples: the next read must continue.
        source.SetVolume(.5f);
        Assert.Equal(1, source.Read(output));
        Assert.Equal(.3f, output[0]);
        Assert.Equal(0, source.Read(output));
        source.SetLoop(true);
        Assert.Equal(2, source.Read(output));
        Assert.Equal(new[] { .1f, .2f }, output);
        source.SetLoop(false);
        Assert.Equal(1, source.Read(output));
        Assert.Equal(0, source.Read(output));
    }

    [Fact]
    public void ReRenderingWhilePlaying_SwapsTheAudio_ButKeepsThePlayhead()
    {
        var source = new VoicePreview.PreviewSamples([.1f, .2f, .3f, .4f], 1, false);
        var output = new float[2];
        Assert.Equal(2, source.Read(output));
        Assert.Equal(new[] { .1f, .2f }, output);
        // A slider moved mid-playback re-renders the same take; playback continues
        // from where it was rather than restarting.
        source.Replace([.5f, .6f, .7f, .8f]);
        Assert.Equal(2, source.Read(output));
        Assert.Equal(new[] { .7f, .8f }, output);
        Assert.Equal(0, source.Read(output));
    }

    [Theory]
    [InlineData(BuiltInVoiceProfiles.FeminineId, 130, 173.53)]
    [InlineData(BuiltInVoiceProfiles.MasculineId, 210, 157.32)]
    public void StarterProfiles_RenderFiniteAudio_AndMovePitchInTheIntendedDirection(string id, double inputHz, double expectedHz)
    {
        var raw = Enumerable.Range(0, 48000).Select(i => .2f * (float)Math.Sin(2 * Math.PI * inputHz * i / 48000)).ToArray();
        var rendered = VoicePreview.Render(raw, BuiltInVoiceProfiles.Find(id)!.Parameters, TestContext.Current.CancellationToken);
        Assert.Equal(raw.Length, rendered.Length);
        Assert.All(rendered, sample => Assert.True(float.IsFinite(sample)));
        Assert.Contains(rendered, sample => Math.Abs(sample) > .01f);
        // Autocorrelation tolerates small extra zero crossings from the effects.
        // Search 120–240 Hz, avoiding octave aliases for these two synthetic tones.
        int bestLag = 0;
        double bestCorrelation = double.NegativeInfinity;
        for (int lag = 200; lag <= 400; lag++)
        {
            double correlation = 0, energyA = 0, energyB = 0;
            for (int i = 12000; i < 36000; i++)
            {
                double a = rendered[i], b = rendered[i + lag];
                correlation += a * b; energyA += a * a; energyB += b * b;
            }
            correlation /= Math.Sqrt(energyA * energyB);
            if (correlation > bestCorrelation) { bestCorrelation = correlation; bestLag = lag; }
        }
        Assert.InRange(48000d / bestLag, expectedHz - 4, expectedHz + 4);
    }

    [Fact]
    public void Render_FlushesAndAlignsToOriginalTimeline_WithoutChangingRaw()
    {
        var raw = Enumerable.Range(0, 4800).Select(i => .2f * MathF.Sin(2 * MathF.PI * 180 * i / 48000)).ToArray();
        var original = raw.ToArray();
        var parameters = new VoiceDspParameters { PitchSemitones = 3, FormantSemitones = -2 };
        var actual = VoicePreview.Render(raw, parameters, TestContext.Current.CancellationToken);
        using var processor = new ProfileVoiceProcessor(48000, 1, parameters);
        var expected = new float[raw.Length + processor.LatencySamples];
        processor.Process(raw, expected.AsSpan(0, raw.Length));
        processor.Flush(expected.AsSpan(raw.Length));
        Assert.Equal(raw.Length, actual.Length);
        Assert.Equal(original, raw);
        Assert.Equal(expected[processor.LatencySamples..], actual);
        Assert.All(actual, sample => Assert.True(float.IsFinite(sample)));
        Assert.Contains(actual, sample => Math.Abs(sample) > .01f);
    }

    [Fact]
    public void Render_RespondsToCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => VoicePreview.Render(new float[4800], VoiceDspParameters.Initial, cancellation.Token));
    }
}
