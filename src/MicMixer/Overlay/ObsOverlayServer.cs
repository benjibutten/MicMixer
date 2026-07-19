using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Serilog;

namespace MicMixer.Overlay;

/// <summary>
/// Everything the stream overlay page needs to draw, except the live audio levels.
/// The string fields use the wire vocabulary of the page: mic is one of
/// "hidden" / "live" / "modded" / "muted" and music is one of "hidden" /
/// "sending" / "monitorOnly" / "blocked". Record equality is the dedupe key,
/// so publishing the same snapshot repeatedly costs nothing.
/// </summary>
internal sealed record ObsOverlayState(string Mic, string Music, bool MeterEnabled, float SensitivityDb);

/// <summary>
/// Tiny loopback-only web server that mirrors the on-screen overlay into a browser source.
/// Serves one embedded HTML page (added to streaming software as a browser source) and pushes
/// state changes plus throttled level frames over a WebSocket, so viewers see
/// the same status cluster even when streaming software captures only the game process.
///
/// Bound strictly to 127.0.0.1 and carries only status strings and level
/// numbers — no audio, no video, no control surface. Slow or stalled clients
/// never block the callers: <see cref="PublishState"/> and
/// <see cref="PublishLevels"/> only swap byte payloads into per-client slots
/// (latest wins) and each client drains its slots at its own pace. The page is
/// the mirror of <see cref="OverlayIndicatorWindow"/> — when that overlay would
/// be hidden, the browser page renders nothing.
/// </summary>
internal sealed class ObsOverlayServer : IAsyncDisposable
{
    public const int DefaultPort = 4573;
    public const int MinimumPort = 1024;
    public const int MaximumPort = 65535;

    private static readonly TimeSpan WebSocketKeepAlive = TimeSpan.FromSeconds(15);
    private static byte[]? _pageBytes;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _clientsLock = new();
    private readonly List<OverlayClient> _clients = new();
    private Task? _acceptTask;
    private byte[]? _stateSnapshot;
    private ObsOverlayState? _lastState;
    private int _clientCount;

    public ObsOverlayServer(int port)
    {
        Port = ClampPort(port);
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }

    public string OverlayUrl => $"http://127.0.0.1:{Port}/";

    public bool HasClients => Volatile.Read(ref _clientCount) > 0;

    public int ClientCount => Volatile.Read(ref _clientCount);

    /// <summary>Raised from server threads whenever a browser connects or disconnects.</summary>
    public event Action<int>? ClientCountChanged;

    public static int ClampPort(int port) => Math.Clamp(port, MinimumPort, MaximumPort);

    /// <summary>Starts listening. Throws (e.g. port already in use) so the caller can surface the error.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        Log.Information("Stream overlay server listening on {OverlayUrl}.", OverlayUrl);
    }

    /// <summary>
    /// Publishes a new overlay state to all connected pages. No-op when the
    /// state equals the previously published one, so callers can publish on
    /// every UI refresh without flooding the sockets. UI-thread only.
    /// </summary>
    public void PublishState(ObsOverlayState state)
    {
        if (state == _lastState)
        {
            return;
        }

        _lastState = state;
        string json =
            $"{{\"type\":\"state\",\"mic\":\"{state.Mic}\",\"music\":\"{state.Music}\"," +
            $"\"meter\":{(state.MeterEnabled ? "true" : "false")}," +
            $"\"sensitivityDb\":{state.SensitivityDb.ToString("0.##", CultureInfo.InvariantCulture)}}}";
        byte[] payload = Encoding.UTF8.GetBytes(json);

        OverlayClient[] targets;
        lock (_clientsLock)
        {
            // The snapshot is what a page connecting later receives first, so
            // it must be updated under the same lock that admits new clients.
            _stateSnapshot = payload;
            targets = _clients.ToArray();
        }

        foreach (OverlayClient client in targets)
        {
            client.Offer(payload, isState: true);
        }
    }

    /// <summary>
    /// Publishes one level frame (same peak/RMS contract as the on-screen
    /// overlay's feed tick). Cheap no-op without connected clients; a page that
    /// lags simply skips frames because only the latest frame is retained.
    /// </summary>
    public void PublishLevels(float outputPeak, float outputRms, float musicPeak, float musicRms)
    {
        if (!HasClients)
        {
            return;
        }

        string json =
            $"{{\"type\":\"levels\",\"op\":{Compact(outputPeak)},\"or\":{Compact(outputRms)}," +
            $"\"mp\":{Compact(musicPeak)},\"mr\":{Compact(musicRms)}}}";
        byte[] payload = Encoding.UTF8.GetBytes(json);

        OverlayClient[] targets;
        lock (_clientsLock)
        {
            targets = _clients.ToArray();
        }

        foreach (OverlayClient client in targets)
        {
            client.Offer(payload, isState: false);
        }
    }

    /// <summary>Four decimals keep level frames short; the meters cannot resolve finer anyway.</summary>
    private static string Compact(float value)
    {
        return Math.Clamp(value, 0f, 4f).ToString("0.####", CultureInfo.InvariantCulture);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Stream overlay server failed to accept a request.");
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";

            if (context.Request.IsWebSocketRequest && string.Equals(path, "/ws", StringComparison.OrdinalIgnoreCase))
            {
                // Browsers exempt WebSockets from the same-origin policy, so
                // without this check any web page could read the status feed
                // through the user's browser despite the loopback binding.
                // Browser handshakes always carry an Origin header; only our
                // own page's origin may pass. Non-browser clients send no
                // Origin and are unaffected.
                string? origin = context.Request.Headers["Origin"];
                if (origin != null && !string.Equals(
                        origin.TrimEnd('/'), $"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Stream overlay rejected a cross-origin WebSocket from {Origin}.", origin);
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    context.Response.Close();
                    return;
                }

                HttpListenerWebSocketContext webSocketContext =
                    await context.AcceptWebSocketAsync(subProtocol: null, WebSocketKeepAlive).ConfigureAwait(false);
                await RunClientAsync(webSocketContext.WebSocket, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && (path == "/" || string.Equals(path, "/overlay", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] page = LoadPage();
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                // The page is versioned with the app itself; never let the browser source cache a stale copy.
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentLength64 = page.Length;
                await context.Response.OutputStream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Stream overlay request handling ended.");
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // The response may already be gone; nothing further to clean up.
            }
        }
    }

    private async Task RunClientAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var client = new OverlayClient(socket);
        byte[]? snapshot;
        int count;
        lock (_clientsLock)
        {
            _clients.Add(client);
            snapshot = _stateSnapshot;
            // Published under the same lock that changes the list: written
            // outside, an older count could overwrite a newer one when
            // connects and disconnects overlap (e.g. a browser-source reload), leaving
            // HasClients permanently wrong and the metering stuck off.
            count = _clients.Count;
            Volatile.Write(ref _clientCount, count);
        }

        ClientCountChanged?.Invoke(count);
        Log.Information("Stream overlay page connected ({ClientCount} total).", count);

        try
        {
            // A page must never start blank waiting for the next state change.
            if (snapshot != null)
            {
                client.Offer(snapshot, isState: true);
            }

            await client.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
                count = _clients.Count;
                Volatile.Write(ref _clientCount, count);
            }

            ClientCountChanged?.Invoke(count);
            socket.Dispose();
            Log.Information("Stream overlay page disconnected ({ClientCount} total).", count);
        }
    }

    private static byte[] LoadPage()
    {
        return _pageBytes ??= ReadEmbeddedPage();
    }

    private static byte[] ReadEmbeddedPage()
    {
        Assembly assembly = typeof(ObsOverlayServer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("MicMixer.Overlay.ObsOverlay.html")
            ?? throw new InvalidOperationException("Embedded stream overlay page is missing from the build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        try
        {
            _listener.Stop();
        }
        catch
        {
            // Stop may throw if the listener never started; shutdown continues.
        }

        OverlayClient[] clients;
        lock (_clientsLock)
        {
            clients = _clients.ToArray();
            _clients.Clear();
        }

        foreach (OverlayClient client in clients)
        {
            client.Abort();
        }

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener.Close();
        _shutdown.Dispose();
    }

    /// <summary>
    /// One connected overlay page. Outgoing data lives in two latest-wins
    /// slots — one for state (must eventually arrive) and one for level frames
    /// (only the newest matters) — drained by a single send loop, so a slow
    /// client coalesces naturally instead of building a queue.
    /// </summary>
    private sealed class OverlayClient
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly object _sync = new();
        private readonly CancellationTokenSource _closed = new();
        private byte[]? _pendingState;
        private byte[]? _pendingLevels;

        public OverlayClient(WebSocket socket)
        {
            _socket = socket;
        }

        public void Offer(byte[] payload, bool isState)
        {
            lock (_sync)
            {
                if (isState)
                {
                    _pendingState = payload;
                }
                else
                {
                    _pendingLevels = payload;
                }
            }

            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // The send loop is already signaled and will pick up both slots.
            }
        }

        public void Abort()
        {
            _closed.Cancel();
            try
            {
                _socket.Abort();
            }
            catch (ObjectDisposedException)
            {
                // The session's own teardown disposed the socket first; the
                // client is already on its way out.
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            Task receiveTask = ReceiveUntilClosedAsync(session);

            try
            {
                while (!session.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    await _wake.WaitAsync(session.Token).ConfigureAwait(false);

                    byte[]? state;
                    byte[]? levels;
                    lock (_sync)
                    {
                        state = _pendingState;
                        levels = _pendingLevels;
                        _pendingState = null;
                        _pendingLevels = null;
                    }

                    if (state != null)
                    {
                        await SendAsync(state, session.Token).ConfigureAwait(false);
                    }

                    if (levels != null)
                    {
                        await SendAsync(levels, session.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
                // The page went away mid-send; the session simply ends.
            }
            finally
            {
                session.Cancel();
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (WebSocketException)
                {
                }

                // Answer the page's close frame so the handshake completes
                // instead of the socket being torn down mid-close. Safe here:
                // both the send loop and the receive loop have ended.
                if (_socket.State == WebSocketState.CloseReceived)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await _socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (WebSocketException)
                    {
                    }
                }

                // _closed and _wake are deliberately never disposed: the
                // server's DisposeAsync (Abort) and in-flight publishes
                // (Offer) may still hold this client and would race an
                // ObjectDisposedException here — which StopObsOverlayServer's
                // synchronous wait would surface on the UI thread. Neither
                // object owns anything beyond memory in this usage (no
                // AvailableWaitHandle, no linked registrations), so the GC
                // reclaims them safely.
            }
        }

        private Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            return _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        /// <summary>
        /// The page never sends application data; this loop exists to detect the
        /// close handshake (and to satisfy keep-alive), ending the session.
        /// </summary>
        private async Task ReceiveUntilClosedAsync(CancellationTokenSource session)
        {
            byte[] buffer = new byte[512];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result =
                        await _socket.ReceiveAsync(buffer, session.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                session.Cancel();
            }
        }
    }
}
