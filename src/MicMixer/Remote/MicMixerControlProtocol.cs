using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicMixer.Remote;

internal static class MicMixerControlProtocol
{
    public const int Version = 1;
    public const string PipeName = "MicMixer.Control.v1";
    public const int MaximumMessageCharacters = 1_048_576;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed record ControlRequest(
    string Id,
    string Command,
    JsonElement? Payload = null,
    int ProtocolVersion = MicMixerControlProtocol.Version);

internal sealed record ControlEnvelope(
    string Type,
    string? Id = null,
    bool? Success = null,
    object? Data = null,
    ControlError? Error = null,
    int ProtocolVersion = MicMixerControlProtocol.Version);

internal sealed record ControlError(string Code, string Message);

internal sealed record ControlResult(bool Success, object? Data = null, ControlError? Error = null)
{
    public static ControlResult Ok(object? data = null) => new(true, data);

    public static ControlResult Fail(string code, string message) =>
        new(false, Error: new ControlError(code, message));
}

internal sealed record MicMixerHello(
    string Product,
    string Version,
    int ProtocolVersion,
    IReadOnlyList<string> Capabilities);

internal sealed record MusicControlState(
    string PlaybackState,
    bool IsExternalMode,
    string? CurrentTrackId,
    string? CurrentTrackName,
    double PositionSeconds,
    double DurationSeconds,
    double MusicVolume,
    double MonitorVolume,
    bool IsRouting,
    bool HasMonitorOutput,
    bool CanStartPlayback,
    string? PlaybackBlockedReason,
    bool IsDelayedStartActive,
    int DelayedStartSeconds,
    int DelayedStartRemainingSeconds,
    string SingleTrackMode,
    string LibraryVersion,
    string QueueVersion,
    IReadOnlyList<RemoteMusicFolder> Folders,
    IReadOnlyList<RemoteQueueItem> Queue,
    bool IsDownloading,
    double? DownloadPercent,
    string DownloadStatus,
    string StatusText,
    bool VolumesLinked = false,
    bool MusicIgnoresPushToTalk = false,
    bool MusicMonitorOnly = false);

internal sealed record RemoteTrack(
    string Id,
    string Name,
    string FolderId,
    string FolderName,
    string FolderPath,
    bool IsPlaying,
    IReadOnlyList<int> QueuePositions);

internal sealed record RemoteTrackPage(
    int Offset,
    int Limit,
    int Total,
    string LibraryVersion,
    IReadOnlyList<RemoteTrack> Tracks);

internal sealed record RemoteQueueItem(int Index, string TrackId, string Name);

internal sealed record RemoteMusicFolder(
    string Id,
    string Name,
    string Path,
    bool IsDefault,
    bool IsPreferredDownloadFolder,
    bool IsEffectiveDownloadFolder);

internal static class RemoteId
{
    public static string FromPath(string path)
    {
        string normalized = Path.GetFullPath(path).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    public static string VersionForPaths(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant());
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 8));
    }
}

internal interface IMicMixerControlHost
{
    Task<ControlResult> HandleControlRequestAsync(ControlRequest request, CancellationToken cancellationToken);

    Task<MusicControlState> GetControlStateAsync(CancellationToken cancellationToken);
}
