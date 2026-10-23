using Launchpad.App;

namespace Launchpad.Tests;

public class StartupSettingsTests
{
    [Fact]
    public void Startup_defaults_on_migrates_existing_settings_and_preserves_opt_out()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Launchpad-startup-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("LAUNCHPAD_DATA");
        Environment.SetEnvironmentVariable("LAUNCHPAD_DATA", folder);
        try
        {
            Assert.True(AppSettings.Load().LaunchAtLogin);
            File.WriteAllText(AppPaths.SettingsFile, "{\"LaunchAtLogin\":false,\"Hotkey\":\"Alt+L\"}");
            var settings = AppSettings.Load();
            Assert.True(settings.LaunchAtLogin);
            Assert.True(settings.StartupConfigured);
            Assert.Equal("Alt+L", settings.Hotkey);
            settings.LaunchAtLogin = false;
            settings.Save();
            Assert.False(AppSettings.Load().LaunchAtLogin);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAUNCHPAD_DATA", previous);
            Directory.Delete(folder, recursive: true);
        }
    }
}
