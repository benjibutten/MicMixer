using System.Diagnostics;
using System.Globalization;
using System.Windows;
using MicMixer.Overlay;
using MicMixer.UI;
using Serilog;

namespace MicMixer;

/// <summary>
/// Stream overlay integration: a loopback-only web server that mirrors the
/// on-screen overlay into a browser source, so viewers see the mic and
/// music status even when streaming software captures only the game process. The page shows
/// exactly what <see cref="OverlayIndicatorWindow"/> would show — including
/// showing nothing while routing is stopped — but runs independently of the
/// desktop indicator, so a streamer can have either, both, or neither.
/// </summary>
public partial class MainWindow
{
    private ObsOverlayServer? _obsOverlayServer;

    /// <summary>
    /// Persistent start failure shown next to the address until a start
    /// succeeds, so the feature never looks enabled while no server runs.
    /// Retry happens on port change or by toggling the checkbox.
    /// </summary>
    private string? _obsOverlayError;

    private void OnObsOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        _settings.ObsOverlayEnabled = ObsOverlayCheck.IsChecked == true;
        ApplyObsOverlaySetting(_settings.ObsOverlayEnabled);
        SaveSettings();
    }

    private void ApplyObsOverlaySetting(bool enabled)
    {
        if (enabled)
        {
            if (_obsOverlayServer is { } running && running.Port == _settings.ObsOverlayPort)
            {
                UpdateObsOverlayStatusText();
                return;
            }

            StopObsOverlayServer();
            ObsOverlayServer? server = null;
            try
            {
                server = new ObsOverlayServer(_settings.ObsOverlayPort);
                server.ClientCountChanged += OnObsOverlayClientCountChanged;
                server.Start();
                _obsOverlayServer = server;
                _obsOverlayError = null;
                PublishObsOverlayState();
            }
            catch (Exception ex)
            {
                if (server != null)
                {
                    server.ClientCountChanged -= OnObsOverlayClientCountChanged;

                    try
                    {
                        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception disposeException)
                    {
                        Log.Warning(disposeException, "Failed to clean up the stream overlay server after startup failed.");
                    }
                }

                // Typically the port is taken by another app; keep the app
                // running and tell the user which port failed.
                Log.Error(ex, "Failed to start the stream overlay server on port {Port}.", _settings.ObsOverlayPort);
                _obsOverlayServer = null;
                _obsOverlayError =
                    $"Could not start on port {_settings.ObsOverlayPort} — the port may be in use. Change the port and press Enter.";
                StatusText.Text = $"Could not start the stream overlay on port {_settings.ObsOverlayPort}: {ex.Message}";
            }
        }
        else
        {
            StopObsOverlayServer();
            _obsOverlayError = null;
        }

        UpdateOutputMeteringEnabled();
        UpdateObsOverlayStatusText();
    }

    private void StopObsOverlayServer()
    {
        if (_obsOverlayServer is not { } server)
        {
            return;
        }

        _obsOverlayServer = null;
        server.ClientCountChanged -= OnObsOverlayClientCountChanged;
        // Disposal only aborts sockets and the listener, so the short
        // synchronous wait cannot stall the UI thread noticeably.
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Publishes the current overlay state to connected browser-source pages. Safe to call
    /// on every UI refresh: the server dedupes identical snapshots.
    /// </summary>
    private void PublishObsOverlayState()
    {
        if (_obsOverlayServer is not { } server)
        {
            return;
        }

        string mic = ComputeMicStatus() switch
        {
            MicStatus.Live => "live",
            MicStatus.Modded => "modded",
            MicStatus.Muted => "muted",
            _ => "hidden"
        };

        string music = ComputeOverlayMusicState() switch
        {
            OverlayMusicState.Sending => "sending",
            OverlayMusicState.MonitorOnly => "monitorOnly",
            OverlayMusicState.Blocked => "blocked",
            _ => "hidden"
        };

        server.PublishState(new ObsOverlayState(
            mic,
            music,
            _settings.OverlayVolumeMeterEnabled,
            _settings.MeterSensitivityDb));
    }

    private void OnObsOverlayClientCountChanged(int count)
    {
        // Raised on server threads; metering enablement and the status text
        // live on the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            UpdateOutputMeteringEnabled();
            UpdateObsOverlayStatusText();
        });
    }

    private void UpdateObsOverlayStatusText()
    {
        if (_obsOverlayServer is { } server)
        {
            ObsOverlayLinkText.Text = server.OverlayUrl;
            int clients = server.ClientCount;
            ObsOverlayClientsText.Text = clients switch
            {
                0 => " — waiting for streaming software",
                1 => " — 1 connected",
                _ => $" — {clients} connected"
            };
            ObsOverlayClientsText.Foreground = ObsOverlayStatusIdleBrush;
        }
        else
        {
            ObsOverlayLinkText.Text = string.Empty;
            ObsOverlayClientsText.Text = ObsOverlayCheck.IsChecked == true
                ? _obsOverlayError ?? string.Empty
                : "Off — the overlay is not being served.";
            ObsOverlayClientsText.Foreground = _obsOverlayError != null && ObsOverlayCheck.IsChecked == true
                ? ObsOverlayStatusErrorBrush
                : ObsOverlayStatusIdleBrush;
        }
    }

    // Matches the settings card's muted gray; the error red is StatusTheme's muted ink.
    private static readonly System.Windows.Media.Brush ObsOverlayStatusIdleBrush = CreateFrozenBrush(0x6B, 0x72, 0x80);
    private static readonly System.Windows.Media.Brush ObsOverlayStatusErrorBrush = CreateFrozenBrush(0xB9, 0x1C, 0x1C);

    private void OnObsOverlayLinkClick(object sender, RoutedEventArgs e)
    {
        if (_obsOverlayServer is not { } server)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(server.OverlayUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open the stream overlay page in a browser.");
        }
    }

    private void OnObsOverlayCopyClick(object sender, RoutedEventArgs e)
    {
        string url = _obsOverlayServer?.OverlayUrl
            ?? $"http://127.0.0.1:{_settings.ObsOverlayPort}/";
        try
        {
            System.Windows.Clipboard.SetText(url);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; not fatal.
            Log.Warning(ex, "Failed to copy the stream overlay URL to the clipboard.");
        }
    }

    private void OnObsOverlayPortBoxLostFocus(object sender, RoutedEventArgs e)
    {
        CommitObsOverlayPort();
    }

    private void OnObsOverlayPortBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitObsOverlayPort();
            e.Handled = true;
        }
    }

    private void CommitObsOverlayPort()
    {
        if (!int.TryParse(ObsOverlayPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            ObsOverlayPortBox.Text = _settings.ObsOverlayPort.ToString(CultureInfo.InvariantCulture);
            return;
        }

        port = ObsOverlayServer.ClampPort(port);
        ObsOverlayPortBox.Text = port.ToString(CultureInfo.InvariantCulture);

        if (port == _settings.ObsOverlayPort)
        {
            return;
        }

        _settings.ObsOverlayPort = port;
        if (ObsOverlayCheck.IsChecked == true)
        {
            // Restart on the new port; ApplyObsOverlaySetting stops the old
            // server because the port no longer matches.
            ApplyObsOverlaySetting(enabled: true);
        }

        SaveSettings();
    }
}
