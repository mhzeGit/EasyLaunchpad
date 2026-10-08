using System.Text.Json;
using Launchpad.App.Apps;

namespace Launchpad.App;

public static class AppPaths
{
    /// <summary>the LocalAppDataaunchpad folder, or the folder in LAUNCHPAD_DATA (development: lets a test copy run beside the real one).</summary>
    public static string DataDir { get; } = EnsureDir(Environment.GetEnvironmentVariable("LAUNCHPAD_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Launchpad"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string IndexFile => Path.Combine(DataDir, "index.bin");

    private static string EnsureDir(string p) { Directory.CreateDirectory(p); return p; }
}

public sealed class AppSettings
{
    /// <summary>Global shortcut, e.g. "Ctrl+Alt+L", "Win+Shift+L", "F4".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+L";

    public bool LaunchAtLogin { get; set; }

    /// <summary>Folders / drives to index. Empty means every fixed drive.</summary>
    public List<string> IndexedRoots { get; set; } = new();

    /// <summary>Folders skipped (with everything below them).</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    public bool IndexFiles { get; set; } = true;

    /// <summary>Opt-in: a lone tap of the Windows key opens Launchpad instead of the Start menu (Win+other shortcuts are untouched).</summary>
    public bool WinKeyOpensLaunchpad { get; set; }

    /// <summary>Ids of apps the user hid from the grid.</summary>
    public List<string> HiddenApps { get; set; } = new();

    /// <summary>How the grid is ordered: "name" (A-Z), "mostused", or "custom" (the order the user dragged icons into).</summary>
    public string AppSort { get; set; } = "name";

    /// <summary>Folders of apps the user made by dropping one icon on another.</summary>
    public List<Launchpad.Core.Apps.AppGroup> Groups { get; set; } = new();

    /// <summary>Files, shortcuts, and folders the user added from Explorer.</summary>
    public List<CustomAppEntry> CustomApps { get; set; } = new();

    /// <summary>App ids in the user's own order (used when <see cref="AppSort"/> is "custom").</summary>
    public List<string> AppOrder { get; set; } = new();

    /// <summary>Show utilities (admin tools, documentation links, developer prompts...) in the grid too. They are always searchable.</summary>
    public bool ShowAllApps { get; set; }

    public Dictionary<string, int> Launches { get; set; } = new();


    // ------------------------------------------------------------------ persistence

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _saveLock = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception) { /* corrupt file: start over with defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
            catch (Exception) { }
        }
    }
}

/// <summary>Opt-in diagnostics: set LAUNCHPAD_DEBUG=1 and read %LOCALAPPDATA%\Launchpad\debug.log.</summary>
public static class Diag
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("LAUNCHPAD_DEBUG") == "1" || Environment.GetEnvironmentVariable("LAUNCHPAD_TRACE") == "1";

    public static void Log(string message)
    {
        if (!Enabled) return;
        try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}\n"); }
        catch (Exception) { }
    }
}
