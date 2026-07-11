using System.IO;
using Serilog;

namespace MicMixer.Music;

/// <summary>A playable file together with the music folder it was found in.</summary>
public sealed record TrackFile(string Path, string Folder);

public sealed class PlaylistManager
{
    public static string DefaultMusicFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "Music");

    private readonly List<string> _folders = new() { DefaultMusicFolder };

    /// <summary>Active music folders in user order. Always contains at least one entry.</summary>
    public IReadOnlyList<string> Folders => _folders;

    public bool UsesOnlyDefaultFolder => _folders.Count == 1 && IsDefaultFolder(_folders[0]);

    public static bool IsDefaultFolder(string folder)
    {
        return string.Equals(TryNormalizeFolder(folder), DefaultMusicFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces the folder list. Null, empty, or all-invalid input falls back to the default folder.</summary>
    public void SetFolders(IEnumerable<string>? folders)
    {
        var normalized = new List<string>();

        foreach (string folder in folders ?? Enumerable.Empty<string>())
        {
            if (TryNormalizeFolder(folder) is string candidate
                && !normalized.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(candidate);
            }
        }

        _folders.Clear();
        _folders.AddRange(normalized.Count > 0 ? normalized : new[] { DefaultMusicFolder });
    }

    /// <summary>Adds a folder to the list. Returns false when it is already present or invalid.</summary>
    public bool AddFolder(string folder)
    {
        if (TryNormalizeFolder(folder) is not string normalized
            || _folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _folders.Add(normalized);
        return true;
    }

    /// <summary>Removes a folder. The last remaining folder cannot be removed.</summary>
    public bool RemoveFolder(string folder)
    {
        if (_folders.Count <= 1)
        {
            return false;
        }

        int index = _folders.FindIndex(existing =>
            string.Equals(existing, TryNormalizeFolder(folder), StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return false;
        }

        _folders.RemoveAt(index);
        return true;
    }

    public IReadOnlyList<TrackFile> GetTracks()
    {
        var tracks = new List<TrackFile>();

        foreach (string folder in _folders)
        {
            try
            {
                if (IsDefaultFolder(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                else if (!Directory.Exists(folder))
                {
                    // A user folder may live on a disconnected drive; skip quietly
                    // instead of failing the whole playlist.
                    continue;
                }

                tracks.AddRange(Directory.EnumerateFiles(folder, "*.mp3")
                    .Select(path => new TrackFile(path, folder)));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read music folder {Folder}.", folder);
            }
        }

        return tracks
            .OrderBy(track => Path.GetFileName(track.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryNormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        }
        catch
        {
            return null;
        }
    }
}
