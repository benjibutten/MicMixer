using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace MicMixer.Diagnostics;

/// <summary>
/// Records the recording end of the cable, which is exactly what the game hears as
/// the mic, to one WAV file per hour in %LocalAppData%\MicMixer\recordings, and
/// measures its level. The oldest recordings are deleted whenever a file starts so
/// that all of them together stay under <see cref="MaxTotalBytes"/>.
/// </summary>
internal sealed class CableRecorder : IDisposable
{
    // Mono 16-bit is what the game's voice activation analyses; about 345 MB per hour.
    private static readonly WaveFormat FileFormat = new(48000, 16, 1);
    private static readonly TimeSpan FileLength = TimeSpan.FromHours(1);

    // About 29 hours of recording.
    private const long MaxTotalBytes = 10L * 1024 * 1024 * 1024;

    public static readonly string RecordingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "recordings");

    // Separate locks: the UI thread takes the peak every 50 ms and must not wait
    // for a slow disk write.
    private readonly object _fileSync = new();
    private readonly object _peakSync = new();
    private readonly WasapiRecorder _capture;
    private WaveFileWriter? _writer;
    private long _samplesInFile;
    private long _samplesSinceHeaderUpdate;
    private int _discontinuities;
    private string? _writeFailure;
    private bool _disposed;
    private float _peakSinceTake;
    private volatile bool _captureRunning = true;

    private CableRecorder(WasapiRecorder capture)
    {
        _capture = capture;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// Starts recording <paramref name="deviceId"/>. Returns null, after logging why,
    /// when the device cannot be recorded; the rest of the app is unaffected.
    /// </summary>
    public static CableRecorder? TryStart(string deviceId)
    {
        WasapiRecorder? capture = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            capture = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithFormat(FileFormat)
                .Build();
            var recorder = new CableRecorder(capture);
            capture.StartRecording();
            Log.Information("Cable recording started. Device={Device} Folder={Folder}", capture.DeviceFriendlyName, RecordingDirectory);
            return recorder;
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            Log.Warning(ex, "Cable recording could not start.");
            return null;
        }
    }

    /// <summary>
    /// The highest level captured since the previous call, 0 to 1, or null once the
    /// capture has stopped and nothing is measured any more. Measuring continues
    /// when writing the file fails.
    /// </summary>
    public float? TakePeak()
    {
        if (!_captureRunning)
        {
            return null;
        }

        lock (_peakSync)
        {
            float peak = _peakSinceTake;
            _peakSinceTake = 0f;
            return peak;
        }
    }

    /// <summary>The current file and the position in it, for finding a moment in the recording.</summary>
    public string DescribePosition()
    {
        lock (_fileSync)
        {
            string position = _writeFailure != null ? $"not written ({_writeFailure})"
                : _writer == null ? "no recording file yet"
                : $"{Path.GetFileName(_writer.Filename)} at {FormatPosition(_samplesInFile)}";
            return _captureRunning ? position : $"stopped, last {position}";
        }
    }

    public void Dispose()
    {
        lock (_fileSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try
        {
            // Stops the capture and waits for its thread, so no callback runs afterwards.
            _capture.Dispose();
        }
        finally
        {
            lock (_fileSync)
            {
                TryCloseFile();
            }
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        MeasurePeak(buffer);
        lock (_fileSync)
        {
            if (_disposed || _writeFailure != null)
            {
                return;
            }

            try
            {
                Write(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk must not end the measuring; only the file stops.
                Log.Warning(ex, "Cable recording stopped writing; the level is still measured.");
                _writeFailure = ex.Message;
                TryCloseFile();
                return;
            }

            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
            {
                _discontinuities++;
            }
        }
    }

    private void MeasurePeak(ReadOnlySpan<byte> buffer)
    {
        float peak = 0f;
        foreach (short sample in MemoryMarshal.Cast<byte, short>(buffer))
        {
            peak = Math.Max(peak, Math.Abs(sample / 32768f));
        }

        lock (_peakSync)
        {
            _peakSinceTake = Math.Max(_peakSinceTake, peak);
        }
    }

    private void Write(ReadOnlySpan<byte> buffer)
    {
        if (_writer == null || _samplesInFile >= FileLength.TotalSeconds * FileFormat.SampleRate)
        {
            CloseFile();
            OpenFile();
        }

        _writer!.Write(buffer);
        int sampleCount = buffer.Length / FileFormat.BlockAlign;
        _samplesInFile += sampleCount;
        _samplesSinceHeaderUpdate += sampleCount;
        // Keeps the file playable up to the last few seconds if the app is killed.
        if (_samplesSinceHeaderUpdate >= 5 * FileFormat.SampleRate)
        {
            _writer.Flush();
            _samplesSinceHeaderUpdate = 0;
        }
    }

    private void OpenFile()
    {
        Directory.CreateDirectory(RecordingDirectory);
        DeleteOldestRecordings();
        DateTime start = DateTime.Now;
        string path = Path.Combine(RecordingDirectory, $"cable-{start:yyyyMMdd-HHmmss.fff}.wav");
        _writer = new WaveFileWriter(path, FileFormat);
        _samplesInFile = 0;
        _samplesSinceHeaderUpdate = 0;
        _discontinuities = 0;
        Log.Information("Cable recording file {File} starts at {Start:HH:mm:ss.fff}.", Path.GetFileName(path), start);
    }

    private void CloseFile()
    {
        if (_writer is not { } writer)
        {
            return;
        }

        _writer = null;
        // Each gap shifts the rest of the file earlier than the clock, so positions
        // after one are approximate.
        Log.Information(
            "Cable recording file {File} closed after {Length}, gaps in the capture: {Discontinuities}.",
            Path.GetFileName(writer.Filename), FormatPosition(_samplesInFile), _discontinuities);
        writer.Dispose();
    }

    private void TryCloseFile()
    {
        try
        {
            CloseFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Closing the cable recording file failed; it is playable up to its last header update.");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _captureRunning = false;
        Log.Warning(e.Exception, "Cable recording stopped by itself; from here on the cable is neither recorded nor measured.");
    }

    private static string FormatPosition(long samples)
    {
        var position = TimeSpan.FromSeconds((double)samples / FileFormat.SampleRate);
        return position.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static void DeleteOldestRecordings()
    {
        try
        {
            long total = 0;
            foreach (var file in new DirectoryInfo(RecordingDirectory).GetFiles("cable-*.wav")
                         .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                total += file.Length;
                if (total > MaxTotalBytes - FileFormat.AverageBytesPerSecond * (long)FileLength.TotalSeconds)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Deleting old cable recordings failed.");
        }
    }
}
