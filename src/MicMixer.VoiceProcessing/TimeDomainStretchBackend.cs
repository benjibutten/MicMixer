using System.Numerics;

namespace MicMixer.Dsp;

/// <summary>
/// Bounded streaming resampling + waveform-similarity overlap-add. All storage and
/// FIR coefficients are prepared at construction; Process/Flush allocate nothing.
/// Absolute positions prevent accumulated clock drift. A single search offset is
/// shared by all channels to preserve the stereo image.
/// </summary>
internal sealed class TimeDomainStretchBackend : IPitchBackend
{
    private readonly int _channels, _window, _hop, _search, _up, _down, _halfFilter, _ringMask;
    private readonly double _speed;
    private readonly float[] _input, _filter, _ready;
    private readonly double[] _weights, _prefixEnergy;
    private readonly double[][] _candidates, _reference, _tail;
    private long _inputFrames, _outputFrames;
    private long _endInputFrames = long.MaxValue;
    private bool _disposed, _flushing;

    public TimeDomainStretchBackend(int sampleRate, int channels, VoiceDspParameters preset)
    {
        preset.Validate(sampleRate, channels);
        _channels = channels;
        _window = (int)Math.Round(sampleRate * preset.TimeDomainWindowMilliseconds / 1000d);
        _window += _window % 2;
        _hop = _window / 2;
        _search = (int)Math.Round(sampleRate * preset.TimeDomainSearchMilliseconds / 1000d);
        (_up, _down) = RationalApproximation(Math.Pow(2, -preset.PitchSemitones / 12d));
        _speed = _down / (double)_up;
        _halfFilter = _up == _down ? 0 : 10 * Math.Max(_up, _down);
        _filter = CreateFilter(_up, _down, _halfFilter);
        // Worst-case chosen window, plus centered FIR support and integer rounding.
        LatencySamples = (int)Math.Ceiling((_window + _search) * _speed + _halfFilter / (double)_up) + 4;
        int capacity = 1;
        int needed = (int)Math.Ceiling((2 * _window + 4 * _search) * _speed + 2d * _halfFilter / _up) + 128;
        while (capacity < needed) capacity *= 2;
        _ringMask = capacity - 1;
        _input = new float[capacity * channels];
        _ready = new float[_hop * channels];
        _weights = new double[_window];
        for (int i = 0; i < _window; i++)
            _weights[i] = Math.Pow(Math.Sin(Math.PI * (i + .5) / _window), 2);
        _candidates = new double[channels][];
        _reference = new double[channels][];
        _tail = new double[channels][];
        for (int c = 0; c < channels; c++)
        {
            _candidates[c] = new double[_window + 2 * _search + 1];
            _reference[c] = new double[_hop];
            _tail[c] = new double[_hop];
        }
        _prefixEnergy = new double[_window + 2 * _search + 2];
    }

    public int LatencySamples { get; }
    public int InputLatencySamples => LatencySamples;
    public int OutputLatencySamples => 0;
    public int BlockSamples => _window;
    public int IntervalSamples => _hop;

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_flushing) throw new InvalidOperationException("Reset the processor after flushing before accepting new input.");
        if (input.Length != output.Length || input.Length % _channels != 0)
            throw new ArgumentException("Input and output must have the same complete interleaved frames.");
        if (input.Overlaps(output)) throw new ArgumentException("Separate input and output buffers are required.");
        for (int offset = 0; offset < input.Length; offset += _channels)
        {
            int write = (int)(_inputFrames & _ringMask) * _channels;
            input.Slice(offset, _channels).CopyTo(_input.AsSpan(write, _channels));
            _inputFrames++;
            EmitFrame(output.Slice(offset, _channels));
        }
    }

    public void Flush(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (output.Length % _channels != 0) throw new ArgumentException("Complete interleaved frames are required.");
        if (!_flushing) { _endInputFrames = _inputFrames; _flushing = true; }
        for (int offset = 0; offset < output.Length; offset += _channels)
        {
            _input.AsSpan((int)(_inputFrames & _ringMask) * _channels, _channels).Clear();
            _inputFrames++;
            EmitFrame(output.Slice(offset, _channels));
        }
    }

    private void EmitFrame(Span<float> output)
    {
        long aligned = _outputFrames++ - LatencySamples;
        if (aligned < 0) { output.Clear(); return; }
        int cursor = (int)(aligned % _hop);
        if (cursor == 0) BuildSegment(aligned);
        _ready.AsSpan(cursor * _channels, _channels).CopyTo(output);
    }

    private void BuildSegment(long position)
    {
        long nominal = (long)Math.Round(position / _speed);
        long first = Math.Max(0, nominal - _search);
        int count = checked((int)(nominal + _search - first + 1));
        int available = count - 1 + _window;
        double referenceEnergy = 0;
        for (int c = 0; c < _channels; c++)
        {
            for (int i = 0; i < available; i++) _candidates[c][i] = Resample(first + i, c);
            for (int i = 0; i < _hop; i++)
            {
                double v = _tail[c][i] / Math.Max(_weights[_hop + i], 1e-12);
                _reference[c][i] = v;
                referenceEnergy += v * v;
            }
        }
        int chosen = (int)(nominal - first);
        if (position > 0 && Math.Sqrt(referenceEnergy / (_hop * _channels)) > 1e-6)
        {
            _prefixEnergy[0] = 0;
            for (int i = 0; i < count - 1 + _hop; i++)
            {
                double e = 0;
                for (int c = 0; c < _channels; c++) e += _candidates[c][i] * _candidates[c][i];
                _prefixEnergy[i + 1] = _prefixEnergy[i] + e;
            }
            double best = double.NegativeInfinity;
            for (int offset = 0; offset < count; offset++)
            {
                double dot = 0;
                for (int c = 0; c < _channels; c++) dot += Dot(_candidates[c].AsSpan(offset, _hop), _reference[c]);
                double energy = Math.Max(_prefixEnergy[offset + _hop] - _prefixEnergy[offset], 1e-20);
                double displacement = (first + offset - nominal) / (double)Math.Max(_search, 1);
                double score = dot / Math.Sqrt(energy * Math.Max(referenceEnergy, 1e-20)) - .015 * displacement * displacement;
                if (score > best) { best = score; chosen = offset; }
            }
        }
        for (int c = 0; c < _channels; c++)
        {
            for (int i = 0; i < _hop; i++)
            {
                double weight = _weights[i] + (position == 0 ? 0 : _weights[_hop + i]);
                double sample = (_tail[c][i] + _candidates[c][chosen + i] * _weights[i]) / Math.Max(weight, 1e-12);
                _ready[i * _channels + c] = (float)sample;
                _tail[c][i] = _candidates[c][chosen + _hop + i] * _weights[_hop + i];
            }
        }
    }

    private float Resample(long index, int channel)
    {
        if (_endInputFrames != long.MaxValue && index >= (_endInputFrames * _up + _down - 1) / _down) return 0;
        if (_up == _down) return Input(index, channel);
        long center = index * _down;
        long first = Math.Max(0, CeilingDivide(center - _halfFilter, _up));
        long last = (center + _halfFilter) / _up;
        float value = 0;
        for (long frame = first; frame <= last; frame++)
            value += Input(frame, channel) * _filter[checked((int)(_halfFilter + center - frame * _up))];
        return value;
    }

    private float Input(long frame, int channel)
    {
        if (frame < 0 || frame >= _endInputFrames) return 0;
        if (frame >= _inputFrames || frame < _inputFrames - (_ringMask + 1L))
            throw new InvalidOperationException("Time-domain input history/lookahead invariant failed.");
        return _input[(int)(frame & _ringMask) * _channels + channel];
    }

    private static long CeilingDivide(long value, int divisor) => value >= 0 ? (value + divisor - 1) / divisor : value / divisor;

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var sum = Vector<double>.Zero;
        int i = 0, width = Vector<double>.Count;
        for (; i <= a.Length - width; i += width) sum += new Vector<double>(a.Slice(i, width)) * new Vector<double>(b.Slice(i, width));
        double result = Vector.Sum(sum);
        for (; i < a.Length; i++) result += a[i] * b[i];
        return result;
    }

    private static (int up, int down) RationalApproximation(double ratio)
    {
        double best = double.PositiveInfinity;
        int up = 1, down = 1;
        for (int denominator = 1; denominator <= 4096; denominator++)
        {
            int numerator = Math.Max(1, (int)Math.Round(ratio * denominator));
            double error = Math.Abs(numerator / (double)denominator - ratio);
            if (error < best) { best = error; up = numerator; down = denominator; }
        }
        return (up, down);
    }

    private static float[] CreateFilter(int up, int down, int half)
    {
        if (half == 0) return [1];
        var coefficients = new double[2 * half + 1];
        double scale = Math.Max(up, down), sum = 0, i0 = BesselI0(5);
        for (int i = 0; i < coefficients.Length; i++)
        {
            double offset = i - half, x = offset / scale;
            double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
            double kaiser = BesselI0(5 * Math.Sqrt(Math.Max(0, 1 - offset * offset / (half * (double)half)))) / i0;
            coefficients[i] = sinc * kaiser / scale;
            sum += coefficients[i];
        }
        var result = new float[coefficients.Length];
        for (int i = 0; i < result.Length; i++) result[i] = (float)(coefficients[i] / sum) * up;
        return result;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, quarter = x * x / 4;
        for (int k = 1; k < 40; k++) { term *= quarter / (k * k); sum += term; if (term < sum * 1e-17) break; }
        return sum;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_input); Array.Clear(_ready); Array.Clear(_prefixEnergy);
        for (int c = 0; c < _channels; c++) { Array.Clear(_tail[c]); Array.Clear(_reference[c]); Array.Clear(_candidates[c]); }
        _inputFrames = _outputFrames = 0; _endInputFrames = long.MaxValue; _flushing = false;
    }

    public void Dispose() => _disposed = true;
}
