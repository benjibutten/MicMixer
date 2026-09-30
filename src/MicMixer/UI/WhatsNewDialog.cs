using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MicMixer.Admin;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace MicMixer.UI;

/// <summary>The release notes of the running version, shown once after an update.</summary>
internal sealed class WhatsNewDialog : Window
{
    private const string ReleaseNotesResource = "MicMixer.RELEASE_NOTES.md";
    private const string ReleasesUrl = "https://github.com/benjibutten/MicMixer/releases";

    private static readonly Brush Ink = BrushFrom("#10233A");
    private static readonly Brush MutedInk = BrushFrom("#526173");
    private static readonly Brush Accent = BrushFrom("#7C3AED");

    public WhatsNewDialog(string versionText, IReadOnlyList<string> items)
    {
        Title = "What's new in MicMixer";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = BrushFrom("#F3F5F7");
        Foreground = Ink;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "What's new in MicMixer",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        content.Children.Add(new TextBlock
        {
            Text = $"You now have {versionText}.",
            Margin = new Thickness(0, 2, 0, 14),
            FontSize = 11.5,
            Foreground = MutedInk
        });

        var list = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        foreach (string item in items)
        {
            list.Children.Add(CreateItem(item));
        }

        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list
        });

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var releasesLink = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        var hyperlink = new Hyperlink(new Run("Earlier releases on GitHub"))
        {
            NavigateUri = new Uri(ReleasesUrl),
            Foreground = Accent,
            FontWeight = FontWeights.SemiBold
        };
        hyperlink.RequestNavigate += (_, e) =>
        {
            ShellLauncher.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        };
        releasesLink.Inlines.Add(hyperlink);
        footer.Children.Add(releasesLink);

        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 86,
            Padding = new Thickness(14, 6, 14, 6),
            Background = Ink,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            IsDefault = true,
            IsCancel = true
        };
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(closeButton);
        content.Children.Add(footer);

        Content = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.White,
            BorderBrush = BrushFrom("#D7DEE7"),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    /// <summary>
    /// True when the notes of <paramref name="current"/> have not been shown yet and this
    /// is an update, not a first run: a first run gets the setup guide instead. Development
    /// builds never show them.
    /// </summary>
    public static bool ShouldShow(Version? current, string? shownForVersion, bool isSetUp) =>
        current is { Major: >= 2000 }
        && isSetUp
        && (!Version.TryParse(shownForVersion, out Version? shown) || shown < current);

    /// <summary>The items of the release notes built into this version; empty when there are none.</summary>
    public static IReadOnlyList<string> ReadBuiltInItems()
    {
        using Stream? stream = typeof(WhatsNewDialog).Assembly.GetManifestResourceStream(ReleaseNotesResource);
        if (stream == null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);
        return ParseItems(reader.ReadToEnd());
    }

    /// <summary>
    /// The top-level list items of RELEASE_NOTES.md, each joined onto one line with its
    /// <c>**bold**</c> markers kept. Headings and comments are left out.
    /// </summary>
    internal static IReadOnlyList<string> ParseItems(string markdown)
    {
        var items = new List<string>();
        foreach (string rawLine in markdown.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(line[2..].Trim());
            }
            else if (items.Count > 0 && line.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0)
            {
                items[^1] += " " + line.Trim();
            }
        }

        return items;
    }

    private static TextBlock CreateItem(string item)
    {
        var text = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 19,
            Foreground = Ink
        };
        text.Inlines.Add(new Run("•  ") { Foreground = Accent });

        // Every other piece between ** markers is bold.
        string[] pieces = item.Split("**");
        for (int index = 0; index < pieces.Length; index++)
        {
            var run = new Run(pieces[index]);
            text.Inlines.Add(index % 2 == 1 ? new Bold(run) : run);
        }

        return text;
    }

    private static Brush BrushFrom(string color) =>
        (Brush)new BrushConverter().ConvertFromString(color)!;
}
