using System.Windows;

namespace MicMixer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "MicMixer — Fel", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}

