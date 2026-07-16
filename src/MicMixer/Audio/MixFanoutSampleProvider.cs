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
        _gainStepPerSample = 1f / Math.Max(0.008f * WaveFormat.SampleRate * WaveFormat.Channels, 1f);
        _micGain = micGateOpen() ? 1f : 0f;
        _musicGain = musicGateOpen() ? 1f : 0f;
        if (secondaryWrite != null)
        {
            _secondaryMicGain = secondaryMicOpen() ? 1f : 0f;
            _secondaryMusicGain = secondaryMusicOpen() ? 1f : 0f;
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int micRead = _mic.Read(buffer, offset, count);
        if (micRead < count)
        {
            Array.Clear(buffer, offset + micRead, count - micRead);
        }

        if (_music == null)
        {
            WriteSecondary(buffer, offset, null, count);
            ApplyGate(buffer, offset, count, ref _micGain, _micGateOpen());
            return count;
        }

        if (_musicBuffer.Length < count)
        {
            _musicBuffer = new float[count];
        }

        int musicRead = _music.Read(_musicBuffer, 0, count);
        if (musicRead < count)
        {
            Array.Clear(_musicBuffer, musicRead, count - musicRead);
        }

        WriteSecondary(buffer, offset, _musicBuffer, count);

        if (_musicMeteringEnabled())
        {
            MeasureMusic(_musicBuffer, count);
        }

        ApplyGate(buffer, offset, count, ref _micGain, _micGateOpen());
        AddWithGate(_musicBuffer, buffer, offset, count, ref _musicGain, _musicGateOpen());
        return count;
    }

    /// <summary>Builds the secondary mix from the raw signals through its own pair of gates and pushes it.</summary>
    private void WriteSecondary(float[] micBuffer, int micOffset, float[]? musicBuffer, int count)
    {
        if (_secondaryWrite == null)
        {
            return;
        }

        if (_secondaryBuffer.Length < count)
        {
            _secondaryBuffer = new float[count];
        }

        CopyWithGate(micBuffer, micOffset, _secondaryBuffer, count, ref _secondaryMicGain, _secondaryMicOpen());

        if (musicBuffer != null)
        {
            AddWithGate(musicBuffer, _secondaryBuffer, 0, count, ref _secondaryMusicGain, _secondaryMusicOpen());
        }

        // Silence is still written: the branch buffer must keep filling so the
        // secondary device's clock stays fed while everything is gated.
        _secondaryWrite(_secondaryBuffer, 0, count);
    }

    /// <summary>In-place ramped gate.</summary>
    private void ApplyGate(float[] buffer, int offset, int count, ref float gain, bool isOpen)
    {
        float target = isOpen ? 1f : 0f;

        if (gain == target)
        {
            if (target == 0f)
            {
                Array.Clear(buffer, offset, count);
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            gain = Step(gain, target);
            buffer[offset + i] *= gain;
        }
    }

    /// <summary>Ramped gate that writes source × gain into the destination (overwrites).</summary>
    private void CopyWithGate(float[] source, int sourceOffset, float[] destination, int count, ref float gain, bool isOpen)
    {
        float target = isOpen ? 1f : 0f;

        if (gain == target)
        {
            if (target == 0f)
            {
                Array.Clear(destination, 0, count);
            }
            else
            {
                Array.Copy(source, sourceOffset, destination, 0, count);
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            gain = Step(gain, target);
            destination[i] = source[sourceOffset + i] * gain;
        }
    }

    /// <summary>Ramped gate that adds source × gain onto the destination.</summary>
    private void AddWithGate(float[] source, float[] destination, int destinationOffset, int count, ref float gain, bool isOpen)
    {
        float target = isOpen ? 1f : 0f;

        if (gain == target)
        {
            if (target == 0f)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                destination[destinationOffset + i] += source[i];
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            gain = Step(gain, target);
            destination[destinationOffset + i] += source[i] * gain;
        }
    }

    private float Step(float gain, float target)
    {
        return gain < target
            ? Math.Min(target, gain + _gainStepPerSample)
            : Math.Max(target, gain - _gainStepPerSample);
    }

    private void MeasureMusic(float[] buffer, int count)
    {
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
