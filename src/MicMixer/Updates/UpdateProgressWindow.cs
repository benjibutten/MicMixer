using System.Windows;
using System.Windows.Controls;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace MicMixer.Updates;

internal sealed class UpdateProgressWindow : Window, IProgress<UpdateProgress>
{
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;

    public UpdateProgressWindow(Window? owner)
    {
        Title = "MicMixer Update";
        if (owner is not null)
            Owner = owner;
        Width = 420;
        Height = 145;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _statusText = new TextBlock
        {
            Text = "Preparing update…",
            Margin = new Thickness(20, 18, 20, 10)
        };
        _progressBar = new ProgressBar
        {
            Height = 10,
            Margin = new Thickness(20, 0, 20, 20),
            IsIndeterminate = true
        };
        Content = new StackPanel { Children = { _statusText, _progressBar } };
    }

    public void Report(UpdateProgress value)
    {
        _statusText.Text = value.Status;
        _progressBar.IsIndeterminate = value.Percentage is null;
        if (value.Percentage is double percentage)
            _progressBar.Value = percentage;
    }
}
