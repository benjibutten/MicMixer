using NAudio.Wave;

namespace MicMixer.Audio;

/// <summary>
/// Heart of the routing chain: pulls the mic and music sources exactly once per
/// block and produces two mixes from them in a single pass.
///
/// Cable mix (the Read output): mic and music each pass their own ramped gate —
/// push-to-talk for the mic, the music-route state for the music — before being
/// summed, so music can bypass push-to-talk or be cut from the cable entirely
/// (monitor-only preview) while the mic stays gated.
///
/// Secondary mix (pushed via the write callback): the same raw signals through a
/// second, independent pair of ramped gates. That per-branch gating is what makes
/// the UI's promises hold: with "ignore push-to-talk" off the secondary mic
/// follows the cable's gate, while monitor-only music still reaches the secondary
/// device (the stream hears what the streamer hears).
///
/// Upstream sources keep advancing regardless of any gate, so music never rewinds
/// while muted. Every gate uses the same ~8 ms click-free ramp, and steady gates
/// take allocation-free fast paths, so the per-block cost stays at plain
/// mix-and-copy level.
/// </summary>
internal sealed class MixFanoutSampleProvider : ISampleProvider
{
    private const float GateRampDurationSeconds = 0.008f;
    private const float ClosedGain = 0f;
    private const float OpenGain = 1f;

    private readonly ISampleProvider _mic;
    private readonly ISampleProvider? _music;
    private readonly Func<bool> _micGateOpen;
    private readonly Func<bool> _musicGateOpen;
    private readonly Func<bool> _secondaryMicOpen;
    private readonly Func<bool> _secondaryMusicOpen;
    private readonly Action<float[], int, int>? _secondaryWrite;
    private readonly Func<bool> _musicMeteringEnabled;
    private readonly Action<float, float> _onMusicLevels;
    private readonly float _gainStepPerSample;
    private float _micGain;
    private float _musicGain;
    private float _secondaryMicGain;
    private float _secondaryMusicGain;
    private float[] _musicBuffer = Array.Empty<float>();
    private float[] _secondaryBuffer = Array.Empty<float>();

    /// <param name="mic">Mic source; short reads are silence-filled.</param>
    /// <param name="music">Optional music source in the same format; short reads are silence-filled.</param>
    /// <param name="micGateOpen">Cable gate for the mic (push-to-talk).</param>
    /// <param name="musicGateOpen">Cable gate for the music (the music-route state).</param>
    /// <param name="secondaryMicOpen">Secondary gate for the mic (ignore-PTT or the cable gate).</param>
    /// <param name="secondaryMusicOpen">Secondary gate for the music (also open during monitor-only preview).</param>
    /// <param name="secondaryWrite">Receives the finished secondary mix, or null when no secondary output runs.</param>
    /// <param name="musicMeteringEnabled">Gates the per-sample music level computation.</param>
    /// <param name="onMusicLevels">Receives each measured music block's peak and RMS (pre-gate, post music volume).</param>
    public MixFanoutSampleProvider(
        ISampleProvider mic,
        ISampleProvider? music,
        Func<bool> micGateOpen,
        Func<bool> musicGateOpen,
        Func<bool> secondaryMicOpen,
        Func<bool> secondaryMusicOpen,
        Action<float[], int, int>? secondaryWrite,
        Func<bool> musicMeteringEnabled,
        Action<float, float> onMusicLevels)
    {
        _mic = mic;
        _music = music;
        _micGateOpen = micGateOpen;
        _musicGateOpen = musicGateOpen;
        _secondaryMicOpen = secondaryMicOpen;
        _secondaryMusicOpen = secondaryMusicOpen;
        _secondaryWrite = secondaryWrite;
        _musicMeteringEnabled = musicMeteringEnabled;
        _onMusicLevels = onMusicLevels;
        WaveFormat = mic.WaveFormat;
        // ~8 ms full-range ramp at the stream's interleaved sample rate, so
        // open/close never produces an audible click.
        _gainStepPerSample = OpenGain / Math.Max(GateRampDurationSeconds * WaveFormat.SampleRate * WaveFormat.Channels, OpenGain);
        _micGain = micGateOpen() ? OpenGain : ClosedGain;
        _musicGain = musicGateOpen() ? OpenGain : ClosedGain;
        if (secondaryWrite != null)
        {
            _secondaryMicGain = secondaryMicOpen() ? OpenGain : ClosedGain;
            _secondaryMusicGain = secondaryMusicOpen() ? OpenGain : ClosedGain;
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int count = buffer.Length;
        int micRead = _mic.Read(buffer);
        if (micRead < count)
        {
            buffer[micRead..].Clear();
        }

        if (_music == null)
        {
            WriteSecondary(buffer, ReadOnlySpan<float>.Empty);
            ApplyGate(buffer, ref _micGain, _micGateOpen());
            return count;
        }

        if (_musicBuffer.Length < count)
        {
            _musicBuffer = new float[count];
        }

        int musicRead = _music.Read(_musicBuffer.AsSpan(0, count));
        if (musicRead < count)
        {
            Array.Clear(_musicBuffer, musicRead, count - musicRead);
        }

        WriteSecondary(buffer, _musicBuffer.AsSpan(0, count));

        if (_musicMeteringEnabled())
        {
            MeasureMusic(_musicBuffer.AsSpan(0, count));
        }

        ApplyGate(buffer, ref _micGain, _micGateOpen());
        AddWithGate(_musicBuffer.AsSpan(0, count), buffer, ref _musicGain, _musicGateOpen());
        return count;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Builds the secondary mix from the raw signals through its own pair of gates and pushes it.</summary>
    private void WriteSecondary(ReadOnlySpan<float> micBuffer, ReadOnlySpan<float> musicBuffer)
    {
        if (_secondaryWrite == null)
        {
            return;
        }

        int count = micBuffer.Length;
        if (_secondaryBuffer.Length < count)
        {
            _secondaryBuffer = new float[count];
        }

        CopyWithGate(micBuffer, _secondaryBuffer.AsSpan(0, count), ref _secondaryMicGain, _secondaryMicOpen());

        if (!musicBuffer.IsEmpty)
        {
            AddWithGate(musicBuffer, _secondaryBuffer.AsSpan(0, count), ref _secondaryMusicGain, _secondaryMusicOpen());
        }

        // Silence is still written: the branch buffer must keep filling so the
        // secondary device's clock stays fed while everything is gated.
        _secondaryWrite(_secondaryBuffer, 0, count);
    }

    /// <summary>In-place ramped gate.</summary>
    private void ApplyGate(Span<float> buffer, ref float gain, bool isOpen)
    {
        int count = buffer.Length;
        float target = isOpen ? OpenGain : ClosedGain;

        if (gain == target)
        {
            if (target == ClosedGain)
            {
                buffer.Clear();
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            gain = Step(gain, target);
            buffer[i] *= gain;
        }
    }

    /// <summary>Ramped gate that writes source × gain into the destination (overwrites).</summary>
    private void CopyWithGate(ReadOnlySpan<float> source, Span<float> destination, ref float gain, bool isOpen)
    {
        int count = destination.Length;
        float target = isOpen ? OpenGain : ClosedGain;

        if (gain == target)
        {
            if (target == ClosedGain)
            {
                destination.Clear();
            }
            else
            {
                source.CopyTo(destination);
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            gain = Step(gain, target);
            destination[i] = source[i] * gain;
        }
    }

    /// <summary>Ramped gate that adds source × gain onto the destination.</summary>
    private void AddWithGate(ReadOnlySpan<float> source, Span<float> destination, ref float gain, bool isOpen)
    {
        int count = destination.Length;
        float target = isOpen ? OpenGain : ClosedGain;

        if (gain == target)
        {
            if (target == ClosedGain)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                destination[i] += source[i];
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            gain = Step(gain, target);
            destination[i] += source[i] * gain;
        }
    }

    private float Step(float gain, float target)
    {
        return gain < target
            ? Math.Min(target, gain + _gainStepPerSample)
            : Math.Max(target, gain - _gainStepPerSample);
    }

    private void MeasureMusic(ReadOnlySpan<float> buffer)
    {
        int count = buffer.Length;
        float max = 0f;
        double squareSum = 0d;
        for (int i = 0; i < count; i++)
        {
            float sample = buffer[i];
            float abs = Math.Abs(sample);
            if (abs > max)
            {
                max = abs;
            }

            squareSum += (double)sample * sample;
        }

        _onMusicLevels(max, (float)Math.Sqrt(squareSum / count));
    }
}
