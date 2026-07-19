using System.Windows.Controls;

namespace MicMixer.UI;

/// <summary>
/// Scrollable host for the routing surface. It deliberately remains a custom
/// control (rather than opening a second XAML namescope) while MainWindow's
/// legacy named controls are incrementally moved behind panel state/render APIs.
/// </summary>
public sealed class RoutingPanel : ScrollViewer
{
    public RoutingPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
    }
}
