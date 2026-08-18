using System.Text.RegularExpressions;

namespace MicMixer.Music;

/// <summary>
/// Outcome of checking a pasted link. Exactly one of the two values is set:
/// <see cref="Url"/> for an accepted link, <see cref="Error"/> for a rejected one.
/// </summary>
public readonly record struct DownloadUrlCheck(string? Url, string? Error)
{
    public bool IsAllowed => Url != null;

    public static DownloadUrlCheck Allow(string url) => new(url, null);

    public static DownloadUrlCheck Reject(string error) => new(null, error);
}

/// <summary>
/// Guards the download field against links that stand for many videos rather than one.
/// A search-result, playlist, or channel link makes yt-dlp fetch every entry behind it,
/// so those are rejected, and an accepted YouTube link is rewritten to the bare video it
/// points at — dropping mix and radio parameters that would otherwise pull in extra tracks.
/// </summary>
public static partial class DownloadUrlValidator
{
    private static readonly string[] VideoPathPrefixes = ["shorts", "live", "embed", "v"];

    private static readonly string[] ChannelPathPrefixes = ["channel", "c", "user", "feed", "hashtag", "playlists"];

    public static DownloadUrlCheck Check(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return DownloadUrlCheck.Reject("Paste a valid link (https://...).");
        }

        if (!IsYouTubeHost(uri.Host))
        {
            // Other sites are still supported; the downloader caps them at one file.
            return DownloadUrlCheck.Allow(uri.AbsoluteUri);
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? videoId = ExtractVideoId(uri, segments);

        return videoId != null && VideoIdRegex().IsMatch(videoId)
            ? DownloadUrlCheck.Allow($"https://www.youtube.com/watch?v={videoId}")
            : DownloadUrlCheck.Reject(DescribeRejection(uri, segments));
    }

    /// <summary>Returns whether the URL targets YouTube or one of its supported subdomains.</summary>
    public static bool IsYouTubeUrl(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && IsYouTubeHost(uri.Host);
    }

    private static string? ExtractVideoId(Uri uri, string[] segments)
    {
        if (IsHost(uri.Host, "youtu.be"))
        {
            return segments.FirstOrDefault();
        }

        if (segments.Length == 0)
        {
            return null;
        }

        if (Matches(segments[0], "watch"))
        {
            return GetQueryValue(uri, "v");
        }

        return segments.Length >= 2 && VideoPathPrefixes.Any(prefix => Matches(segments[0], prefix))
            ? segments[1]
            : null;
    }

    private static string DescribeRejection(Uri uri, string[] segments)
    {
        string first = segments.FirstOrDefault() ?? string.Empty;

        if (Matches(first, "results"))
        {
            return "That is a YouTube search, not a video — it would download every result. "
                + "Open the track you want and paste its link.";
        }

        if (Matches(first, "playlist") || GetQueryValue(uri, "list") != null)
        {
            return "That is a playlist link, which would download every track in it. "
                + "Paste a link to a single video.";
        }

        if (first.StartsWith('@') || ChannelPathPrefixes.Any(prefix => Matches(first, prefix)))
        {
            return "That is a channel link, which would download its whole catalogue. "
                + "Paste a link to a single video.";
        }

        return "That link does not point at a single video. "
            + "Paste a link like https://www.youtube.com/watch?v=...";
    }

    private static bool IsYouTubeHost(string host)
    {
        return IsHost(host, "youtu.be")
            || IsHost(host, "youtube.com")
            || IsHost(host, "youtube-nocookie.com");
    }

    /// <summary>Matches the host itself and any subdomain of it (www, m, music).</summary>
    private static bool IsHost(string host, string domain)
    {
        return Matches(host, domain)
            || (host.Length > domain.Length
                && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - domain.Length - 1] == '.');
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator > 0 && pair.AsSpan(0, separator).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    private static bool Matches(string value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();
}
