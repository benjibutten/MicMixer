using AwesomeAssertions;
using MicMixer.Music;
using Xunit;

namespace MicMixer.Tests;

public sealed class PlaylistManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MicMixer.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SetFolders_ShouldNormalizeAndDeduplicatePaths_WhenPathsOverlap()
    {
        string first = Path.Combine(_root, "First");
        string second = Path.Combine(_root, "Second");
        var sut = new PlaylistManager();

        sut.SetFolders(new[] { $"  {first}{Path.DirectorySeparatorChar}  ", first.ToUpperInvariant(), second });

        sut.Folders.Should().Equal(Path.GetFullPath(first), Path.GetFullPath(second));
    }

    [Fact]
    public void SetFolders_ShouldFallBackToDefaultFolder_WhenNoFolderIsValid()
    {
        var sut = new PlaylistManager();

        sut.SetFolders(new[] { "", "\0" });

        sut.UsesOnlyDefaultFolder.Should().BeTrue();
        sut.Folders.Should().ContainSingle();
    }

    [Fact]
    public void RemoveFolder_ShouldKeepFolder_WhenItIsTheLastRemainingFolder()
    {
        string first = Path.Combine(_root, "First");
        string second = Path.Combine(_root, "Second");
        var sut = new PlaylistManager();
        sut.SetFolders(new[] { first, second });

        sut.RemoveFolder(first).Should().BeTrue();
        sut.RemoveFolder(second).Should().BeFalse();
        sut.Folders.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(second));
    }

    [Fact]
    public void GetTracks_ShouldMergeAndSortTracks_WhenFoldersIncludeAMissingFolder()
    {
        string first = Directory.CreateDirectory(Path.Combine(_root, "First")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "Second")).FullName;
        string missing = Path.Combine(_root, "Missing");
        File.WriteAllBytes(Path.Combine(first, "zeta.mp3"), []);
        File.WriteAllBytes(Path.Combine(second, "Alpha.MP3"), []);
        File.WriteAllBytes(Path.Combine(first, "ignored.wav"), []);
        var sut = new PlaylistManager();
        sut.SetFolders(new[] { first, missing, second });

        IReadOnlyList<TrackFile> tracks = sut.GetTracks();

        tracks.Select(track => Path.GetFileName(track.Path)).Should().Equal("Alpha.MP3", "zeta.mp3");
        tracks.Select(track => track.Folder).Should().Equal(second, first);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
