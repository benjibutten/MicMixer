namespace MicMixer.Music;

/// <summary>What currently prevents or allows captured app audio to reach the virtual mic.</summary>
public enum ExternalCaptureRouteState
{
    WaitingForAudio,
    RoutingStopped,
    MonitorOnly,
    BlockedByPushToTalk,
    Sending
}

public static class ExternalCaptureRoute
{
    public static ExternalCaptureRouteState Evaluate(
        bool hasAudioSignal,
        bool isRouting,
        bool monitorOnly,
        bool musicRouteOpen)
    {
        if (!hasAudioSignal)
        {
            return ExternalCaptureRouteState.WaitingForAudio;
        }

        if (!isRouting)
        {
            return ExternalCaptureRouteState.RoutingStopped;
        }

        if (monitorOnly)
        {
            return ExternalCaptureRouteState.MonitorOnly;
        }

        return musicRouteOpen
            ? ExternalCaptureRouteState.Sending
            : ExternalCaptureRouteState.BlockedByPushToTalk;
    }
}
