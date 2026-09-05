using System.IO;
using MicMixer.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicMixer.Audio;

/// <summary>Private, bounded, in-memory audition. Never connected to the routing graph.</summary>
internal sealed class VoicePreview : IDisposable
{
    private WasapiRecorder? _recorder;
    private WasapiPlayer? _player;
    private MMDevice? _inputDevice, _outputDevice;
    private readonly MMDeviceEnumerator _devices = new();
    private readonly MemoryStream _recording = new();
    private TaskCompletionSource<float[]>? _completion;
    private PreviewSamples? _playingSamples;
    private Exception? _playbackError;

    public PlaybackState PlaybackState => _player?.PlaybackState ?? PlaybackState.Stopped;
    public Exception? PlaybackError => Volatile.Read(ref _playbackError);
    public void Pause() => _player?.Pause();
    public void Resume() => _player?.Play();
    public void SetLoop(bool loop) => _playingSamples?.SetLoop(loop);

    public void SetVolume(float volume) => _playingSamples?.SetVolume(volume);

    public Task<float[]> RecordAsync(string deviceId)
    {
        StopPlayback();
        _recording.SetLength(0);
        _inputDevice = _devices.GetDevice(deviceId);
        _recorder = new WasapiRecorderBuilder().WithDevice(_inputDevice).WithSharedMode().WithEventSync().Build();
        _recording.Capacity = checked(15 * _recorder.WaveFormat.AverageBytesPerSecond);
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += OnStopped;
        _recorder.StartRecording();
        return _completion.Task;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _recorder!.WaveFormat;
        int remaining = (int)(15L * format.AverageBytesPerSecond - _recording.Length);
        int count = Math.Min(buffer.Length, remaining);
        count -= count % format.BlockAlign;
        if (count > 0) _recording.Write(buffer[..count]);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        try
        {
            if (e.Exception != null) throw e.Exception;
            using var stream = new RawSourceWaveStream(new MemoryStream(_recording.ToArray()), _recorder!.WaveFormat);
            var source = FormatNormalizer.Normalize(stream.ToSampleProvider(), 48_000, 1);
            var samples = new float[48_000 * 15];
            int length = 0, read;
            while (length < samples.Length && (read = source.Read(samples.AsSpan(length))) > 0) length += read;
            _completion?.TrySetResult(samples[..length]);
        }
        catch (Exception ex) { _completion?.TrySetException(ex); }
    }

    public void StopRecording()
    {
        try { _recorder?.StopRecording(); }
        catch (Exception ex) { _completion?.TrySetException(ex); }
    }

    public void ReleaseRecording()
    {
        if (_recorder != null)
        {
            _recorder.DataAvailable -= OnData;
            _recorder.RecordingStopped -= OnStopped;
            try { if (_recorder.CaptureState == CaptureState.Capturing) _recorder.StopRecording(); }
            catch { /* A disconnected microphone still needs disposal. */ }
            _recorder.Dispose();
            _recorder = null;
        }
        _inputDevice?.Dispose();
        _inputDevice = null;
    }

    public static float[] Render(float[] raw, VoiceDspParameters parameters, CancellationToken cancellationToken = default)
    {
        using var processor = new ProfileVoiceProcessor(48_000, 1, parameters);
        var output = new float[raw.Length + processor.LatencySamples];
        for (int offset = 0; offset < raw.Length; offset += 480)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(480, raw.Length - offset);
            processor.Process(raw.AsSpan(offset, count), output.AsSpan(offset, count));
        }
        processor.Flush(output.AsSpan(raw.Length));
        return output.AsSpan(processor.LatencySamples, raw.Length).ToArray();
    }

    public void Play(float[] samples, string deviceId, float volume, bool loop)
    {
        StopPlayback();
        try
        {
            _playbackError = null;
            _outputDevice = _devices.GetDevice(deviceId);
            _player = new WasapiPlayerBuilder().WithDevice(_outputDevice).WithSharedMode().WithEventSync().Build();
            _player.PlaybackStopped += OnPlaybackStopped;
            _playingSamples = new PreviewSamples(samples, volume, loop);
            var source = FormatNormalizer.Normalize(_playingSamples, _player.DeviceMixFormat);
            _player.Init(new SampleToTargetWaveProvider(source, _player.DeviceMixFormat, padSilence: false));
            _player.Play();
        }
        catch { StopPlayback(); throw; }
    }

    public void StopPlayback()
    {
        if (_player != null)
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            _player.Dispose();
        }
        _player = null;
        _playingSamples = null;
        _outputDevice?.Dispose();
        _outputDevice = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => Volatile.Write(ref _playbackError, e.Exception);

    public void Dispose()
    {
        StopPlayback();
        ReleaseRecording();
        _completion?.TrySetCanceled();
        _recording.Dispose();
        _devices.Dispose();
    }

    internal sealed class PreviewSamples(float[] samples, float volume, bool loop) : ISampleProvider
    {
        private int _position;
        private float _volume = volume;
        private bool _loop = loop;
        public void SetVolume(float value) => Volatile.Write(ref _volume, value);
        public void SetLoop(bool value) => Volatile.Write(ref _loop, value);
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer)
        {
            int written = 0;
            while (written < buffer.Length && samples.Length > 0)
            {
                if (_position == samples.Length) { if (!Volatile.Read(ref _loop)) break; _position = 0; }
                buffer[written++] = Math.Clamp(samples[_position++] * Volatile.Read(ref _volume), -1f, 1f);
            }
            return written;
        }
    }
}
