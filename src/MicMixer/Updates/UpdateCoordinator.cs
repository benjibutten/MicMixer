using System.Windows;
using MicMixer.Diagnostics;
using Serilog;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MicMixer.Updates;

internal static class UpdateCoordinator
{
    private static readonly GitHubUpdateService Service = new();
    private static int _checkInProgress;

    public static async Task CheckAsync(Window owner, bool manual)
    {
        Version? currentVersion = AppVersion.Current;
        if (currentVersion is null || currentVersion.Major < 2000)
        {
            if (manual)
                MessageBox.Show(owner, "Update checks are only available in release builds.", "MicMixer Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Interlocked.Exchange(ref _checkInProgress, 1) != 0)
            return;

        try
        {
            UpdateInfo? update = await Service.CheckAsync(currentVersion, manual);
            if (update is null)
            {
                if (manual)
                    MessageBox.Show(owner, "You already have the latest version.", "MicMixer Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                owner,
                $"MicMixer {update.TagName} is available. You have {AppVersion.DisplayText}.\n\nDownload and install it now? MicMixer will close and restart automatically.\n\nWindows may request UAC approval or show a security warning when the updated app restarts, especially for unsigned or newly published builds.",
                "MicMixer Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
                return;

            var progressWindow = new UpdateProgressWindow(owner);
            owner.IsEnabled = false;
            progressWindow.Show();
            try
            {
                var progress = new Progress<UpdateProgress>(progressWindow.Report);
                await Service.LaunchInstallerAsync(update, progress);
                if (Application.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.ExitForUpdate();
                else
                    Application.Current.Shutdown();
            }
            finally
            {
                progressWindow.Close();
                owner.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check or installation preparation failed.");
            if (manual)
                MessageBox.Show(owner, $"Could not check for or prepare the update.\n\n{ex.Message}", "MicMixer Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _checkInProgress, 0);
        }
    }
}
