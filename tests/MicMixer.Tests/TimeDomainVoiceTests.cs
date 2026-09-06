using System.Text.Json;
using System.Text.Json.Nodes;
using MicMixer.Dsp;
using Xunit;

namespace MicMixer.Tests;

public sealed class TimeDomainVoiceTests
{
    private static VoiceDspParameters Parameters(float pitch = 2) => new()
    {
        PitchEngine = PitchEngine.TimeDomain, PitchSemitones = pitch,
        TimeDomainWindowMilliseconds = 24, TimeDomainSearchMilliseconds = 8,
        HighPassHz = 20, CompressorRatio = 1
    };

    private static float[] SpeechLike(int rate, double seconds, int channels = 1)
    {
        var data = new float[(int)(rate * seconds) * channels];
        for (int i = 0; i < data.Length / channels; i++)
        {
            double t = i / (double)rate;
            double gate = t % .7 < .12 ? 0 : .6 + .4 * Math.Sin(2 * Math.PI * 3.1 * t);
            double phase = 2 * Math.PI * (130 * t + 2 * Math.Sin(2 * Math.PI * 1.3 * t));
            float value = (float)(gate * (.15 * Math.Sin(phase) + .07 * Math.Sin(2 * phase) + .025 * Math.Sin(7 * phase)));
            for (int c = 0; c < channels; c++) data[i * channels + c] = c % 2 == 0 ? value : -value;
        }
        return data;
    }

    private static float[] Render(float[] input, VoiceDspParameters p, int rate, int channels, int[] chunks)
    {
        using var processor = new ProfileVoiceProcessor(rate, channels, p);
        var output = new float[input.Length + processor.LatencySamples * channels];
        int offset = 0, index = 0;
        while (offset < input.Length)
        {
            int count = Math.Min(chunks[index++ % chunks.Length] * channels, input.Length - offset);
            processor.Process(input.AsSpan(offset, count), output.AsSpan(offset, count));
            offset += count;
        }
        processor.Flush(output.AsSpan(input.Length));
        return output[(processor.LatencySamples * channels)..];
    }

    [Theory]
    [InlineData(48000, 1)]
    [InlineData(44100, 2)]
    public void ArbitraryCallbackBoundaries_DoNotChangeTheWaveform(int rate, int channels)
    {
        var raw = SpeechLike(rate, 3, channels);
        var continuous = Render(raw, Parameters(), rate, channels, [raw.Length / channels]);
        var jittered = Render(raw, Parameters(), rate, channels, [1, 127, 480, 13, 1024, 31]);
        Assert.Equal(continuous, jittered);
        Assert.All(jittered, x => Assert.True(float.IsFinite(x)));
        Assert.Contains(jittered, x => Math.Abs(x) > .05);
        if (channels == 2)
            for (int i = 0; i < jittered.Length; i += 2) Assert.Equal(jittered[i], -jittered[i + 1]);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void ReportedDelayAlignsZeroPitchImpulse_AndStaysBelow50ms(int rate)
    {
        var p = Parameters(0);
        using var processor = new ProfileVoiceProcessor(rate, 1, p);
        Assert.InRange(processor.LatencySamples * 1000d / rate, 1, 50);
        var raw = new float[rate]; raw[rate / 3] = .2f;
        var output = Render(raw, p, rate, 1, [17, 480, 41]);
        int peak = Array.IndexOf(output, output.Max());
        Assert.Equal(rate / 3, peak);
        Assert.InRange(output[peak], .19f, .21f);
    }

    [Theory]
    [InlineData(-5f)]
    [InlineData(2f)]
    [InlineData(5f)]
    public void SteadyToneMovesToTheRequestedPitch(float shift)
    {
        const int rate = 48000;
        var raw = Enumerable.Range(0, rate * 2).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 170 * i / rate))).ToArray();
        var y = Render(raw, Parameters(shift), rate, 1, [480]);
        double expected = 170 * Math.Pow(2, shift / 12d);
        int low = (int)(rate / (expected + 8)), high = (int)(rate / (expected - 8));
        double best = double.NegativeInfinity; int lagBest = 0;
        for (int lag = low; lag <= high; lag++)
        {
            double sum = 0, aa = 0, bb = 0;
            for (int i = rate / 2; i < rate * 3 / 2; i++) { double a = y[i], b = y[i + lag]; sum += a * b; aa += a * a; bb += b * b; }
            double corr = sum / Math.Sqrt(aa * bb);
            if (corr > best) { best = corr; lagBest = lag; }
        }
        Assert.InRange(rate / (double)lagBest, expected - 1.5, expected + 1.5);
    }

    [Fact]
    public void FlushRendersTail_ResetRestoresState_AndCallbacksAllocateNothing()
    {
        const int rate = 48000;
        using var processor = new ProfileVoiceProcessor(rate, 1, Parameters());
        var raw = SpeechLike(rate, .9);
        var first = new float[raw.Length + processor.LatencySamples];
        var again = new float[first.Length];
        // Warm JIT/native math before measuring the processing loop.
        processor.Process(raw, first.AsSpan(0, raw.Length)); processor.Flush(first.AsSpan(raw.Length)); processor.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int offset = 0; offset < raw.Length; offset += 480)
        {
            int n = Math.Min(480, raw.Length - offset);
            processor.Process(raw.AsSpan(offset, n), again.AsSpan(offset, n));
        }
        processor.Flush(again.AsSpan(raw.Length));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(first, again);
        Assert.Contains(again[^processor.LatencySamples..], x => Math.Abs(x) > .01);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(1000)]
    public void VeryShortInputCanBeFlushed(int frames)
    {
        var raw = new float[frames]; raw[^1] = .1f;
        var y = Render(raw, Parameters(), 48000, 1, [1]);
        Assert.Equal(frames, y.Length); Assert.All(y, x => Assert.True(float.IsFinite(x)));
    }

    [Fact]
    public void Version1FilesKeepLegacyDefaults_Version2RoundTripsEngineAndOptions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MicMixer-domain-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var profile = new VoiceProfile { FormatVersion = 1, Id = Guid.NewGuid().ToString(), DisplayName = "Synthetic", Parameters = new() };
            var node = JsonSerializer.SerializeToNode(profile, VoiceProfileStore.JsonOptions)!;
            foreach (string field in new[] { "pitchEngine", "timeDomainWindowMilliseconds", "timeDomainSearchMilliseconds" }) node["parameters"]!.AsObject().Remove(field);
            string old = Path.Combine(dir, profile.Id + ".json"); File.WriteAllText(old, node.ToJsonString());
            Assert.Equal(PitchEngine.Signalsmith, VoiceProfileStore.ReadFile(old).Parameters.PitchEngine);
            var modern = profile with { FormatVersion = 2, Id = Guid.NewGuid().ToString(), Parameters = Parameters() };
            var store = new VoiceProfileStore(dir); store.Import(modern); Assert.Equal(modern, store.Load(modern.Id));
            Assert.Throws<InvalidDataException>(() => (modern with { FormatVersion = 1 }).Validate());
            Assert.Throws<InvalidDataException>(() => (modern with { AlternateBlockMilliseconds = 50 }).Validate());
            Assert.Throws<ArgumentException>(() => (modern.Parameters with { FormantSemitones = 1 }).Validate(48000, 1));
            var bad = JsonSerializer.SerializeToNode(modern, VoiceProfileStore.JsonOptions)!;
            bad["parameters"]!.AsObject().Remove("pitchEngine"); File.WriteAllText(old, bad.ToJsonString());
            Assert.Throws<InvalidDataException>(() => VoiceProfileStore.ReadFile(old));
            bad.AsObject().Remove("formatVersion"); File.WriteAllText(old, bad.ToJsonString());
            Assert.Throws<InvalidDataException>(() => VoiceProfileStore.ReadFile(old));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
