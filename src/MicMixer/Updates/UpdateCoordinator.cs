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

        // When MicMixer was installed with Windows Package Manager (winget), let
        // winget own upgrades. Self-updating in place would swap the executable
        // without winget's knowledge, leaving `winget upgrade` to see a stale
        // version. Automatic checks are skipped entirely; a manual check still
        // reports whether a newer version exists but points to winget to install it.
        bool managedByWinget = InstallEnvironment.IsManagedByWinget;
        if (managedByWinget && !manual)
        {
            Log.Information("Skipping automatic update check; MicMixer is managed by Windows Package Manager (winget).");
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

            if (managedByWinget)
            {
                // Only manual checks reach this point for a winget-managed install.
                MessageBox.Show(
                    owner,
                    $"MicMixer {update.TagName} is available. You have {AppVersion.DisplayText}.\n\nMicMixer was installed with Windows Package Manager, so update it from a terminal:\n\n    winget upgrade --id BenjiButten.MicMixer --exact",
                    "MicMixer Update Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
