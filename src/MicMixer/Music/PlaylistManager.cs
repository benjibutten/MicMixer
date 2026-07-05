using System.IO;

namespace MicMixer.Music;

public sealed class PlaylistManager
{
    public string MusicFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "Music");

    public IReadOnlyList<string> GetTracks()
    {
        Directory.CreateDirectory(MusicFolder);

        return Directory.EnumerateFiles(MusicFolder, "*.mp3")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
