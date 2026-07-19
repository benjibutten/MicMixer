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
}
