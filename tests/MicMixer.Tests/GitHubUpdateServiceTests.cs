using MicMixer.Updates;
using Xunit;

namespace MicMixer.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("v2026.7.12", 2026, 7, 12)]
    [InlineData("2025.11.3", 2025, 11, 3)]
    public void TryParseVersion_ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateService.TryParseVersion(tag, out Version? version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void GetReleaseZipName_IncludesVersion()
    {
        string result = GitHubUpdateService.GetReleaseZipName(new Version(2026, 7, 12));

        Assert.Equal("MicMixer-2026.7.12-win-x64.zip", result);
    }

    [Fact]
    public void ParseChecksum_AcceptsStandardSha256File()
    {
        string hash = new('a', 64);

        string result = GitHubUpdateService.ParseChecksum($"{hash}  MicMixer-2026.7.12-win-x64.zip");

        Assert.Equal(hash.ToUpperInvariant(), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")]
    public void ParseChecksum_RejectsInvalidContent(string value)
    {
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ParseChecksum(value));
    }

    [Fact]
    public void InstallFiles_UpdatesReleaseFilesAndPreservesOtherFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"MicMixer-update-test-{Guid.NewGuid():N}");
        string staging = Path.Combine(root, "staging");
        string install = Path.Combine(root, "install");
        string backup = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(staging, "MicMixer.exe"), "new version");
            File.WriteAllText(Path.Combine(staging, "NOTICE.txt"), "new notice");
            File.WriteAllText(Path.Combine(install, "MicMixer.exe"), "old version");
            File.WriteAllText(Path.Combine(install, "user-file.txt"), "keep me");

            UpdateInstaller.InstallFiles(staging, install, backup);

            Assert.Equal("new version", File.ReadAllText(Path.Combine(install, "MicMixer.exe")));
            Assert.Equal("new notice", File.ReadAllText(Path.Combine(install, "NOTICE.txt")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "user-file.txt")));
            Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "MicMixer.exe")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
