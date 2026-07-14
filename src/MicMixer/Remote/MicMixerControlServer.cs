using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Serilog;

namespace MicMixer.Remote;

internal sealed class MicMixerControlServer : IAsyncDisposable
{
    private static readonly string[] Capabilities =
    [
        "state", "library", "transport", "seek", "volume", "queue", "delayedStart", "singleTrack",
        "sourceMode", "download", "folders"
    ];

    private readonly IMicMixerControlHost _host;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public MicMixerControlServer(IMicMixerControlHost host, string? pipeName = null)
    {
        _host = host;
        _pipeName = pipeName ?? MicMixerControlProtocol.PipeName;
    }

    public void Start()
    {
        _serverTask ??= Task.Run(() => RunAsync(_shutdown.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                Log.Information("StreamDecky control client connected to MicMixer.");
                await RunSessionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "MicMixer control pipe session ended.");
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MicMixer control pipe failed; accepting a new client.");
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSessionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var outgoing = Channel.CreateBounded<ControlEnvelope>(new BoundedChannelOptions(32)
        {
            // Responses must never be dropped. State publishing uses TryWrite and
            // simply coalesces naturally while a slow client drains the channel.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var lineReader = new BoundedLineReader(reader, MicMixerControlProtocol.MaximumMessageCharacters);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        Task writeTask = WriteLoopAsync(writer, outgoing.Reader, sessionCancellation.Token);
        Task stateTask = PublishStateLoopAsync(outgoing.Writer, sessionCancellation.Token);

        try
        {
            while (!sessionCancellation.IsCancellationRequested && pipe.IsConnected)
            {
                BoundedLine line = await lineReader.ReadAsync(sessionCancellation.Token).ConfigureAwait(false);
                if (line.EndOfStream)
                {
                    break;
                }

                ControlEnvelope response = line.TooLarge
                    ? Failure(null, "message_too_large", "Control message exceeded the allowed size.")
                    : await HandleLineAsync(line.Value!, sessionCancellation.Token).ConfigureAwait(false);
                await outgoing.Writer.WriteAsync(response, sessionCancellation.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            outgoing.Writer.TryComplete();
            await IgnoreCancellationAsync(writeTask).ConfigureAwait(false);
            await IgnoreCancellationAsync(stateTask).ConfigureAwait(false);
            Log.Information("StreamDecky control client disconnected from MicMixer.");
        }
    }

    private async Task<ControlEnvelope> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        ControlRequest? request;
        try
        {
            request = System.Text.Json.JsonSerializer.Deserialize<ControlRequest>(
                line,
                MicMixerControlProtocol.JsonOptions);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Failure(null, "invalid_json", ex.Message);
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Command))
        {
            return Failure(request?.Id, "invalid_request", "A request id and command are required.");
        }

        if (request.ProtocolVersion != MicMixerControlProtocol.Version)
        {
            return Failure(request.Id, "unsupported_protocol",
                $"MicMixer supports control protocol {MicMixerControlProtocol.Version}.");
        }

        if (string.Equals(request.Command, "hello", StringComparison.OrdinalIgnoreCase))
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            return Success(request.Id, new MicMixerHello(
                "MicMixer", version, MicMixerControlProtocol.Version, Capabilities));
        }

        try
        {
            ControlResult result = await _host.HandleControlRequestAsync(request, cancellationToken).ConfigureAwait(false);
            return result.Success
                ? Success(request.Id, result.Data)
                : new ControlEnvelope("response", request.Id, false, Error: result.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MicMixer control command {Command} failed.", request.Command);
            return Failure(request.Id, "command_failed", ex.Message);
        }
    }

    private async Task PublishStateLoopAsync(ChannelWriter<ControlEnvelope> writer, CancellationToken cancellationToken)
    {
        string? previousJson = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            MusicControlState state = await _host.GetControlStateAsync(cancellationToken).ConfigureAwait(false);
            string json = System.Text.Json.JsonSerializer.Serialize(state, MicMixerControlProtocol.JsonOptions);
            if (string.Equals(json, previousJson, StringComparison.Ordinal))
            {
                continue;
            }

            if (writer.TryWrite(new ControlEnvelope("event", Data: new { name = "stateChanged", state })))
            {
                // Only mark the snapshot as published after it has entered the
                // channel. If the channel is full, retry the latest state on the
                // next tick instead of permanently considering it delivered.
                previousJson = json;
            }
        }
    }

    private static async Task WriteLoopAsync(
        StreamWriter writer,
        ChannelReader<ControlEnvelope> reader,
        CancellationToken cancellationToken)
    {
        await foreach (ControlEnvelope message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            string json = System.Text.Json.JsonSerializer.Serialize(message, MicMixerControlProtocol.JsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ControlEnvelope Success(string id, object? data) =>
        new("response", id, true, data);

    private static ControlEnvelope Failure(string? id, string code, string message) =>
        new("response", id, false, Error: new ControlError(code, message));

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private readonly record struct BoundedLine(string? Value, bool TooLarge, bool EndOfStream);

    /// <summary>
    /// Reads newline-delimited messages without ever retaining more than the
    /// configured maximum. Oversized input is drained through its newline so the
    /// following request can still be processed on the same connection.
    /// </summary>
    private sealed class BoundedLineReader
    {
        private const int BufferSize = 4_096;

        private readonly StreamReader _reader;
        private readonly int _maximumCharacters;
        private readonly char[] _buffer = new char[BufferSize];
        private int _bufferOffset;
        private int _bufferCount;

        public BoundedLineReader(StreamReader reader, int maximumCharacters)
        {
            _reader = reader;
            _maximumCharacters = maximumCharacters;
        }

        public async ValueTask<BoundedLine> ReadAsync(CancellationToken cancellationToken)
        {
            var value = new StringBuilder(Math.Min(_maximumCharacters, BufferSize));
            bool tooLarge = false;
            bool hasCharacters = false;

            while (true)
            {
                if (_bufferOffset >= _bufferCount)
                {
                    _bufferCount = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _bufferOffset = 0;
                    if (_bufferCount == 0)
                    {
                        return hasCharacters
                            ? Complete(value, tooLarge)
                            : new BoundedLine(null, false, true);
                    }
                }

                int newline = Array.IndexOf(_buffer, '\n', _bufferOffset, _bufferCount - _bufferOffset);
                int segmentEnd = newline >= 0 ? newline : _bufferCount;
                int segmentLength = segmentEnd - _bufferOffset;
                hasCharacters |= segmentLength > 0;

                if (!tooLarge && value.Length + segmentLength <= _maximumCharacters)
                {
                    value.Append(_buffer, _bufferOffset, segmentLength);
                }
                else if (segmentLength > 0)
                {
                    tooLarge = true;
                    value.Clear();
                }

                _bufferOffset = newline >= 0 ? newline + 1 : segmentEnd;
                if (newline >= 0)
                {
                    return Complete(value, tooLarge);
                }
            }
        }

        private static BoundedLine Complete(StringBuilder value, bool tooLarge)
        {
            if (tooLarge)
            {
                return new BoundedLine(null, true, false);
            }

            if (value.Length > 0 && value[^1] == '\r')
            {
                value.Length--;
            }

            return new BoundedLine(value.ToString(), false, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask != null)
        {
            await IgnoreCancellationAsync(_serverTask).ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }
}
