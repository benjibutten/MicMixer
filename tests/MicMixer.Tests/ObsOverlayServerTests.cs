using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MicMixer.Overlay;
using Xunit;

namespace MicMixer.Tests;

public sealed class ObsOverlayServerTests
{
    [Fact]
    public async Task Server_ShouldServeTheEmbeddedOverlayPage()
    {
        await using var server = new ObsOverlayServer(GetFreePort());
        server.Start();

        using var client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync(
            server.OverlayUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.ToString());
        string page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("MicMixer Overlay", page);
        Assert.Contains("viewBox=\"0 0 116 58\"", page);

        HttpResponseMessage missing = await client.GetAsync(
            server.OverlayUrl + "does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Server_ShouldPushTheLatestStateToANewClient()
    {
        await using var server = new ObsOverlayServer(GetFreePort());
        server.Start();

        // Published before any client connects: the connect must replay it.
        server.PublishState(new ObsOverlayState("live", "sending", MeterEnabled: true, SensitivityDb: 3.5f));

        using var socket = await ConnectAsync(server);
        using JsonDocument state = await ReceiveJsonAsync(socket);

        Assert.Equal("state", state.RootElement.GetProperty("type").GetString());
        Assert.Equal("live", state.RootElement.GetProperty("mic").GetString());
        Assert.Equal("sending", state.RootElement.GetProperty("music").GetString());
        Assert.True(state.RootElement.GetProperty("meter").GetBoolean());
        Assert.Equal(3.5, state.RootElement.GetProperty("sensitivityDb").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task Server_ShouldDedupeStatesAndBroadcastLevels()
    {
        await using var server = new ObsOverlayServer(GetFreePort());
        server.Start();

        var snapshot = new ObsOverlayState("muted", "blocked", MeterEnabled: true, SensitivityDb: 0f);
        server.PublishState(snapshot);

        using var socket = await ConnectAsync(server);
        await WaitForClientAsync(server);

        // An identical snapshot must not produce a second state message, so the
        // next message after the connect replay has to be the level frame.
        server.PublishState(snapshot with { });
        server.PublishLevels(0.5f, 0.25f, 0.125f, 0.0625f);

        using JsonDocument first = await ReceiveJsonAsync(socket);
        Assert.Equal("state", first.RootElement.GetProperty("type").GetString());
        Assert.Equal("muted", first.RootElement.GetProperty("mic").GetString());

        using JsonDocument second = await ReceiveJsonAsync(socket);
        Assert.Equal("levels", second.RootElement.GetProperty("type").GetString());
        Assert.Equal(0.5, second.RootElement.GetProperty("op").GetDouble(), precision: 4);
        Assert.Equal(0.25, second.RootElement.GetProperty("or").GetDouble(), precision: 4);
        Assert.Equal(0.125, second.RootElement.GetProperty("mp").GetDouble(), precision: 4);
        Assert.Equal(0.0625, second.RootElement.GetProperty("mr").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task Server_ShouldTrackClientCountAcrossConnectAndDisconnect()
    {
        await using var server = new ObsOverlayServer(GetFreePort());
        server.Start();
        Assert.False(server.HasClients);

        var socket = await ConnectAsync(server);
        await WaitForClientAsync(server);
        Assert.True(server.HasClients);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure, null, TestContext.Current.CancellationToken);
        socket.Dispose();

        await WaitForAsync(() => !server.HasClients);
        Assert.Equal(0, server.ClientCount);
    }

    [Fact]
    public async Task Server_ShouldRejectCrossOriginWebSocketHandshakes()
    {
        await using var server = new ObsOverlayServer(GetFreePort());
        server.Start();

        // A foreign web page connecting through the user's browser carries its
        // own origin and must be refused before the socket is accepted.
        using var evil = new ClientWebSocket();
        evil.Options.SetRequestHeader("Origin", "http://evil.example");
        await Assert.ThrowsAsync<WebSocketException>(() => evil.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/ws"), TestContext.Current.CancellationToken));

        // The overlay page's own origin connects normally.
        using var own = new ClientWebSocket();
        own.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{server.Port}");
        await own.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/ws"), TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketState.Open, own.State);
    }

    private static async Task<ClientWebSocket> ConnectAsync(ObsOverlayServer server)
    {
        var socket = new ClientWebSocket();
        var uri = new Uri($"ws://127.0.0.1:{server.Port}/ws");
        await socket.ConnectAsync(uri, TestContext.Current.CancellationToken);
        return socket;
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket)
    {
        byte[] buffer = new byte[8_192];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    private static Task WaitForClientAsync(ObsOverlayServer server)
    {
        return WaitForAsync(() => server.HasClients);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the expected server condition.");
    }

    /// <summary>A port the OS considers free right now; good enough for loopback tests.</summary>
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
