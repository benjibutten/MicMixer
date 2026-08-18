using AwesomeAssertions;
using MicMixer.Music;
using Xunit;

namespace MicMixer.Tests;

public sealed class DownloadUrlValidatorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Rj8k5oync78")]
    [InlineData("http://youtube.com/watch?v=Rj8k5oync78")]
    [InlineData("https://m.youtube.com/watch?v=Rj8k5oync78")]
    [InlineData("https://music.youtube.com/watch?v=Rj8k5oync78")]
    [InlineData("https://youtu.be/Rj8k5oync78?t=42")]
    [InlineData("https://www.youtube.com/shorts/Rj8k5oync78")]
    [InlineData("https://www.youtube.com/live/Rj8k5oync78")]
    [InlineData("https://www.youtube-nocookie.com/embed/Rj8k5oync78")]
    [InlineData("  https://www.youtube.com/watch?v=Rj8k5oync78  ")]
    public void Check_ShouldNormalizeToTheBareVideo_WhenLinkPointsAtOneVideo(string url)
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check(url);

        result.IsAllowed.Should().BeTrue();
        result.Url.Should().Be("https://www.youtube.com/watch?v=Rj8k5oync78");
    }

    [Fact]
    public void Check_ShouldDropPlaylistParameters_WhenVideoLinkCarriesAMix()
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check(
            "https://www.youtube.com/watch?v=Rj8k5oync78&list=RDRj8k5oync78&start_radio=1&index=3");

        result.Url.Should().Be("https://www.youtube.com/watch?v=Rj8k5oync78");
    }

    [Fact]
    public void Check_ShouldReject_WhenLinkIsASearchQuery()
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check(
            "https://www.youtube.com/results?search_query=vill+ha+dig+i+m%C3%B6rkret+hos+mig");

        result.IsAllowed.Should().BeFalse();
        result.Error.Should().Contain("search");
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLabc123")]
    [InlineData("https://www.youtube.com/channel/UCabc123")]
    [InlineData("https://www.youtube.com/@somechannel")]
    [InlineData("https://www.youtube.com/user/somebody")]
    [InlineData("https://www.youtube.com/feed/trending")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/watch?v=notanid")]
    public void Check_ShouldReject_WhenYouTubeLinkIsNotASingleVideo(string url)
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check(url);

        result.IsAllowed.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a link")]
    [InlineData("ftp://example.com/song.mp3")]
    [InlineData("file:///C:/song.mp3")]
    public void Check_ShouldReject_WhenTextIsNotAnHttpUrl(string? text)
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check(text);

        result.IsAllowed.Should().BeFalse();
        result.Error.Should().Be("Paste a valid link (https://...).");
    }

    [Fact]
    public void Check_ShouldAllowUnchanged_WhenHostIsNotYouTube()
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check("https://example.com/media/song");

        result.IsAllowed.Should().BeTrue();
        result.Url.Should().Be("https://example.com/media/song");
    }

    [Fact]
    public void Check_ShouldNotTreatLookalikeHostAsYouTube_WhenDomainIsOnlyASuffixMatch()
    {
        DownloadUrlCheck result = DownloadUrlValidator.Check("https://notyoutube.com/results?search_query=x");

        result.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=Rj8k5oync78")]
    [InlineData("https://youtu.be/Rj8k5oync78")]
    [InlineData("https://music.youtube.com/watch?v=Rj8k5oync78")]
    public void IsYouTubeUrl_ShouldRecognizeYouTubeHosts(string url)
    {
        DownloadUrlValidator.IsYouTubeUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.com/media/song")]
    [InlineData("https://notyoutube.com/watch?v=Rj8k5oync78")]
    [InlineData("ftp://www.youtube.com/watch?v=Rj8k5oync78")]
    [InlineData(null)]
    public void IsYouTubeUrl_ShouldRejectOtherOrInvalidUrls(string? url)
    {
        DownloadUrlValidator.IsYouTubeUrl(url).Should().BeFalse();
    }
}
