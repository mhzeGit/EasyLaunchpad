using System.Windows;
using EasyTools.Updating;

namespace Launchpad.App.UI;

public partial class MainWindow
{
    readonly AppUpdater _updater = new("mhzeGit/EasyLaunchpad", "EasyLaunchpad", "Launchpad.exe");
    UpdateRelease? _availableUpdate;
    bool _updateBusy;

    public void OpenUpdateSettings()
    {
        OnSettingsClick(this, new RoutedEventArgs());
        _ = CheckOrInstallUpdateAsync(false);
    }

    async void OnUpdateClick(object sender, RoutedEventArgs e) => await CheckOrInstallUpdateAsync(_availableUpdate is not null);

    async Task CheckOrInstallUpdateAsync(bool install)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        _suppressDeactivate = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Please wait…";
        UpdateStatus.Text = install ? "Preparing update…" : "Checking GitHub…";
        try
        {
            if (install && _availableUpdate is { } release)
            {
                await _updater.InstallAsync(release, new Progress<string>(text => UpdateStatus.Text = text));
                ((App)Application.Current).Quit();
                return;
            }
            _availableUpdate = await _updater.CheckAsync();
            UpdateStatus.Text = _availableUpdate is { } found
                ? $"A new build ({found.Version}) is available. Update will restart Launchpad in the background."
                : "You're up to date.";
        }
        catch (Exception error)
        {
            _availableUpdate = null;
            UpdateStatus.Text = "Update failed: " + error.Message;
        }
        finally
        {
            _updateBusy = false;
            _suppressDeactivate = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = _availableUpdate is null ? "Check for updates" : "Update & restart";
        }
    }
}
