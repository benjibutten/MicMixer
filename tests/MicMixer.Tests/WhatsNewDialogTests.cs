using MicMixer.UI;
using Xunit;

namespace MicMixer.Tests;

public sealed class WhatsNewDialogTests
{
    [Fact]
    public void ParseItems_JoinsWrappedItemsAndSkipsHeadingsAndComments()
    {
        const string markdown = "# What's changed\r\n\r\n<!-- a note -->\r\n\r\n- **First.** It wraps\r\n  onto a second line.\r\n- Second.\r\n";

        IReadOnlyList<string> items = WhatsNewDialog.ParseItems(markdown);

        Assert.Equal(["**First.** It wraps onto a second line.", "Second."], items);
    }

    [Fact]
    public void The_built_in_release_notes_have_items()
    {
        Assert.NotEmpty(WhatsNewDialog.ReadBuiltInItems());
    }

    [Theory]
    [InlineData("2026.9.1.0", "2026.9.2.0", true, true)]
    [InlineData(null, "2026.9.2.0", true, true)]
    [InlineData("2026.9.2.0", "2026.9.2.0", true, false)]
    [InlineData(null, "2026.9.2.0", false, false)]
    [InlineData(null, "1.0.0.0", true, false)]
    public void ShouldShow_OnlyAfterAnUpdateOfARelease(string? shownFor, string current, bool isSetUp, bool expected)
    {
        Assert.Equal(expected, WhatsNewDialog.ShouldShow(Version.Parse(current), shownFor, isSetUp));
    }
}
