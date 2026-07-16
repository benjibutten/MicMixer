using System.Diagnostics;
using System.Globalization;
using System.Windows;
using MicMixer.Overlay;
using MicMixer.UI;
using Serilog;

namespace MicMixer;

/// <summary>
/// OBS overlay integration: a loopback-only web server that mirrors the
/// on-screen overlay into an OBS Browser source, so viewers see the mic and
/// music status even when OBS captures only the game process. The page shows
/// exactly what <see cref="OverlayIndicatorWindow"/> would show — including
/// showing nothing while routing is stopped — but runs independently of the
/// desktop indicator, so a streamer can have either, both, or neither.
/// </summary>
public partial class MainWindow
{
    private ObsOverlayServer? _obsOverlayServer;
    private int _obsOverlayPort = ObsOverlayServer.DefaultPort;

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

        ApplyObsOverlaySetting(ObsOverlayCheck.IsChecked == true);
        SaveSettings();
    }

    private void ApplyObsOverlaySetting(bool enabled)
    {
        if (enabled)
        {
            if (_obsOverlayServer is { } running && running.Port == _obsOverlayPort)
            {
                UpdateObsOverlayStatusText();
                return;
            }

            StopObsOverlayServer();
            try
            {
                var server = new ObsOverlayServer(_obsOverlayPort);
                server.ClientCountChanged += OnObsOverlayClientCountChanged;
                server.Start();
                _obsOverlayServer = server;
                _obsOverlayError = null;
                PublishObsOverlayState();
            }
            catch (Exception ex)
            {
                // Typically the port is taken by another app; keep the app
                // running and tell the user which port failed.
                Log.Error(ex, "Failed to start the OBS overlay server on port {Port}.", _obsOverlayPort);
                _obsOverlayServer = null;
                _obsOverlayError =
                    $"Kunde inte starta på port {_obsOverlayPort} — porten kan vara upptagen. Byt port och tryck Enter.";
                StatusText.Text = $"Kunde inte starta OBS-overlayn på port {_obsOverlayPort}: {ex.Message}";
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
    /// Publishes the current overlay state to connected OBS pages. Safe to call
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
            OverlayVolumeMeterCheck.IsChecked == true,
            (float)MeterSensitivitySlider.Value));
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
                0 => " — väntar på OBS",
                1 => " — 1 ansluten",
                _ => $" — {clients} anslutna"
            };
            ObsOverlayClientsText.Foreground = ObsOverlayStatusIdleBrush;
        }
        else
        {
            ObsOverlayLinkText.Text = string.Empty;
            ObsOverlayClientsText.Text = ObsOverlayCheck.IsChecked == true
                ? _obsOverlayError ?? string.Empty
                : "Av — overlayn serveras inte.";
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
            Log.Warning(ex, "Failed to open the OBS overlay page in a browser.");
        }
    }

    private void OnObsOverlayCopyClick(object sender, RoutedEventArgs e)
    {
        string url = _obsOverlayServer?.OverlayUrl
            ?? $"http://127.0.0.1:{_obsOverlayPort}/";
        try
        {
            System.Windows.Clipboard.SetText(url);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; not fatal.
            Log.Warning(ex, "Failed to copy the OBS overlay URL to the clipboard.");
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
            ObsOverlayPortBox.Text = _obsOverlayPort.ToString(CultureInfo.InvariantCulture);
            return;
        }

        port = ObsOverlayServer.ClampPort(port);
        ObsOverlayPortBox.Text = port.ToString(CultureInfo.InvariantCulture);

        if (port == _obsOverlayPort)
        {
            return;
        }

        _obsOverlayPort = port;
        if (ObsOverlayCheck.IsChecked == true)
        {
            // Restart on the new port; ApplyObsOverlaySetting stops the old
            // server because the port no longer matches.
            ApplyObsOverlaySetting(enabled: true);
        }

        SaveSettings();
    }
}
