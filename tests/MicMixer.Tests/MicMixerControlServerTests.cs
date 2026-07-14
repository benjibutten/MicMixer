using System.IO.Pipes;
using System.Text.Json;
using MicMixer.Remote;
using Xunit;

namespace MicMixer.Tests;

public sealed class MicMixerControlServerTests
{
    [Fact]
    public async Task Server_ShouldHandshakeAndDispatchRequests()
    {
        string pipeName = $"MicMixer.Tests.{Guid.NewGuid():N}";
        var host = new FakeControlHost();
        await using var server = new MicMixerControlServer(host, pipeName);
        server.Start();

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5_000, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("{\"id\":\"hello-1\",\"command\":\"hello\",\"protocolVersion\":1}");
        using JsonDocument hello = await ReadResponseAsync(reader, "hello-1");
        Assert.True(hello.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("MicMixer", hello.RootElement.GetProperty("data").GetProperty("product").GetString());

        await writer.WriteLineAsync("{\"id\":\"state-1\",\"command\":\"getState\",\"protocolVersion\":1}");
        using JsonDocument response = await ReadResponseAsync(reader, "state-1");
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Stopped", response.RootElement.GetProperty("data").GetProperty("playbackState").GetString());
        Assert.Equal(
            @"C:\Music",
            response.RootElement.GetProperty("data").GetProperty("folders")[0].GetProperty("path").GetString());
        Assert.Equal("getState", host.LastCommand);
    }

    [Fact]
    public void RemoteId_ShouldBeStableAndHideTheOriginalPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "Music", "Example.mp3");

        string first = RemoteId.FromPath(path);
        string second = RemoteId.FromPath(path.ToLowerInvariant());

        Assert.Equal(first, second);
        Assert.DoesNotContain("Example", first, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public async Task Server_ShouldRejectOversizedLineAndContinueTheSession()
    {
        string pipeName = $"MicMixer.Tests.{Guid.NewGuid():N}";
        await using var server = new MicMixerControlServer(new FakeControlHost(), pipeName);
        server.Start();

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5_000, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(new string('x', MicMixerControlProtocol.MaximumMessageCharacters + 1));
        using (JsonDocument oversized = JsonDocument.Parse(
            await reader.ReadLineAsync(TestContext.Current.CancellationToken)
                ?? throw new IOException("Control server disconnected before responding.")))
        {
            Assert.False(oversized.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                "message_too_large",
                oversized.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await writer.WriteLineAsync("{\"id\":\"hello-after-large\",\"command\":\"hello\",\"protocolVersion\":1}");
        using JsonDocument hello = await ReadResponseAsync(reader, "hello-after-large");
        Assert.True(hello.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Server_ShouldRetryWhenPipeNameIsTemporarilyUnavailable()
    {
        string pipeName = $"MicMixer.Tests.{Guid.NewGuid():N}";
        var blocker = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await using var server = new MicMixerControlServer(new FakeControlHost(), pipeName);
        server.Start();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await blocker.DisposeAsync();

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5_000, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("{\"id\":\"hello-after-retry\",\"command\":\"hello\",\"protocolVersion\":1}");
        using JsonDocument hello = await ReadResponseAsync(reader, "hello-after-retry");
        Assert.True(hello.RootElement.GetProperty("success").GetBoolean());
    }

    private static async Task<JsonDocument> ReadResponseAsync(StreamReader reader, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            string line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("Control server disconnected before responding.");
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out JsonElement responseId)
                && string.Equals(responseId.GetString(), id, StringComparison.Ordinal))
            {
                return document;
            }

            document.Dispose();
        }
    }

    private sealed class FakeControlHost : IMicMixerControlHost
    {
        public string? LastCommand { get; private set; }

        public Task<ControlResult> HandleControlRequestAsync(
            ControlRequest request,
            CancellationToken cancellationToken)
        {
            LastCommand = request.Command;
            return Task.FromResult(ControlResult.Ok(CreateState()));
        }

        public Task<MusicControlState> GetControlStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CreateState());

        private static MusicControlState CreateState() => new(
            "Stopped",
            false,
            null,
            null,
            0,
            0,
            0.5,
            0.5,
            false,
            false,
            false,
            "no_playback_clock",
            false,
            3,
            0,
            "Off",
            "library",
            "queue",
            [new RemoteMusicFolder("folder-1", "Music", @"C:\Music", false, true, true)],
            [],
            false,
            null,
            string.Empty,
            "Idle");
    }
}
