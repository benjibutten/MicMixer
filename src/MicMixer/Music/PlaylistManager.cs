using System.IO;

namespace MicMixer.Music;

public sealed class PlaylistManager
{
    public static string DefaultMusicFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicMixer",
        "Music");

    public string? CustomFolder { get; set; }

    public bool UsesCustomFolder => !string.IsNullOrWhiteSpace(CustomFolder);

    public string MusicFolder => UsesCustomFolder ? CustomFolder! : DefaultMusicFolder;

    public IReadOnlyList<string> GetTracks()
    {
        Directory.CreateDirectory(MusicFolder);

        return Directory.EnumerateFiles(MusicFolder, "*.mp3")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
