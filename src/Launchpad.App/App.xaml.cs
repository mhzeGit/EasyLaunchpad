using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Launchpad.App.Apps;
using Launchpad.App.Services;
using Launchpad.App.UI;
using WinForms = System.Windows.Forms;

namespace Launchpad.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private AppSettings _settings = new();
    private HotkeyHost? _hotkey;
    private IndexService? _index;
    private AppCatalog? _catalog;
    private MainWindow? _window;
    private WinKeyHook? _winKey;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _startupItem;
    private List<AppItem> _apps = new();
    private readonly List<string> _pendingAddPaths = new();
    private bool _appsLoaded;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { LogCrash(ex.Exception); ex.Handled = true; };
        if (e.Args.Contains("--render-test")) { RenderTest(e.Args); return; }
        if (e.Args.Contains("--reorder-test")) { ReorderTest(e.Args); return; }
        if (e.Args.Contains("--dump-icon")) { DumpIcon(e.Args); return; }
        string? addPath = ArgumentValue(e.Args, "--add-to-launchpad");
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            try
            {
                if (addPath != null) _single.SignalExistingAdd(addPath);
                else _single.SignalExisting(toggle: e.Args.Contains("--toggle"));
            }
            catch (Exception ex) { LogCrash(ex); }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { LogCrash(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash(ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { LogCrash(ex.Exception); ex.SetObserved(); };
        SessionEnding += (_, _) => Quit();

        _single.AddRequested += path => Dispatcher.BeginInvoke(() => AddToLaunchpad(path));
        _single.StartAddListener();
        ExplorerIntegration.Register();

        bool background = e.Args.Contains("--background") || addPath != null;
        _settings = AppSettings.Load();
        _catalog = new AppCatalog();
        _index = new IndexService(_settings);

        _window = new MainWindow(_settings, _index)
        {
            ChangeHotkey = spec =>
            {
                if (_hotkey!.Register(spec)) return true;
                _hotkey.Register(_settings.Hotkey);   // put the old one back
                return false;
            },
            RebuildIndexRequested = () => _ = _index.RebuildAsync(),
            RefreshAppsRequested = () => _ = RefreshAppsAsync(rebuildIcons: true),
            WinKeyToggleRequested = on => SetWinKey(on),
            IndexToggleRequested = on =>
            {
                if (on) _ = _index.StartAsync();
                else Task.Run(_index.Stop);
            },
            AddItemRequested = AddItemFromPicker,
            AddInstalledApplicationRequested = AddInstalledApplication,
        };
        _window.WarmUp();

        _hotkey = new HotkeyHost();
        _hotkey.Pressed += () => _window.Toggle();
        BindHotkey();
        if (_settings.WinKeyOpensLaunchpad) SetWinKey(true);

        _single.ShowRequested += () => Dispatcher.BeginInvoke(() => _window.ShowLaunchpad());
        _single.ToggleRequested += () => Dispatcher.BeginInvoke(() => _window.Toggle());
        BuildTray();

        // Paint instantly from the cache, then let the shell tell us what changed.
        Task.Run(() => _catalog.LoadCached(_settings.CustomApps.ToArray())).ContinueWith(t =>
        {
            _apps = t.Result;
            _appsLoaded = true;
            _window.SetApps(_apps);
            if (addPath != null) AddToLaunchpad(addPath);
            foreach (string pending in _pendingAddPaths.ToArray()) AddToLaunchpad(pending);
            _pendingAddPaths.Clear();
            _ = RefreshAppsAsync(rebuildIcons: false);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        _ = _index.StartAsync();

        if (!background) Dispatcher.InvokeAsync(() => _window.ShowLaunchpad(), DispatcherPriority.ApplicationIdle);
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void AddToLaunchpad(string path)
    {
        if (!_appsLoaded) { _pendingAddPaths.Add(path); return; }
        if (_catalog == null || _window == null) return;
        if (!_catalog.TryCreateCustom(path, out var entry, out var item))
        {
            Balloon($"Couldn't add '{Path.GetFileName(path)}' to Launchpad.");
            return;
        }
        _settings.HiddenApps.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (!_settings.CustomApps.Any(a => string.Equals(a.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
            _settings.CustomApps.Add(entry);
        _settings.Save();
        _apps.RemoveAll(a => a.Id == item.Id);
        _apps.Add(item);
        _window.SetApps(_apps);
        _ = RefreshAppsAsync(rebuildIcons: false);
        Balloon($"Added '{item.Name}' to Launchpad.");
    }

    private void AddItemFromPicker(string kind)
    {
        if (_window == null || _catalog == null) return;
        if (kind == "application")
        {
            _window.ShowApplicationPicker();
            return;
        }
        string? path = null;
        try
        {
            if (kind == "file")
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Add file to Launchpad",
                    Filter = "All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                };
                if (dialog.ShowDialog(_window) == true) path = dialog.FileName;
            }
            else if (kind == "folder")
            {
                using var dialog = new WinForms.FolderBrowserDialog { Description = "Choose a folder to add to Launchpad", UseDescriptionForTitle = true };
                if (dialog.ShowDialog() == WinForms.DialogResult.OK) path = dialog.SelectedPath;
            }
        }
        catch (Exception ex) { LogCrash(ex); }

        if (path == null) return;
        AddToLaunchpad(path);
        _window.ShowLaunchpad();
    }

    private void AddInstalledApplication(AppItem app)
    {
        if (_window == null) return;
        _settings.HiddenApps.RemoveAll(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase));
        var entry = _settings.CustomApps.FirstOrDefault(existing => string.Equals(existing.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new CustomAppEntry { Id = app.Id, Name = app.Name, Path = app.Target, IsShellApplication = app.IsShellApplication || !app.IsCustom };
            _settings.CustomApps.Add(entry);
        }
        _settings.Save();
        var pinned = new AppItem(app.Id, app.Name, app.Target, isCustom: true, isShellApplication: entry.IsShellApplication) { Icon = app.Icon };
        _apps.RemoveAll(existing => existing.Id == app.Id);
        _apps.Add(pinned);
        _window.SetApps(_apps);
    }

    /// <summary>Developer aid: <c>Launchpad.exe --render-test out.png [--query=text] [--filter=Images] [--page=1] [--settings] [--size=1920x1080]</c></summary>
    private async void RenderTest(string[] args)
    {
        string Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
        string? Opt(string name) { string v = Arg(name); return v.Length > 0 ? v : null; }
        string path = args[Array.IndexOf(args, "--render-test") + 1];
        var size = (Arg("--size") is { Length: > 0 } sz ? sz : "1920x1080").Split('x');
        try
        {
            var settings = AppSettings.Load();
            var index = new IndexService(settings);
            if (Opt("--scan") is { } scanRoot) index.ScanForTest(scanRoot); else index.LoadSnapshotOnly();
            var window = new MainWindow(settings, index);
            window.SetApps(new AppCatalog().LoadCached());
            window.RenderOpenGroup = args.Contains("--open-group");
            int.TryParse(Arg("--page"), out int page);
            await window.RenderToPngAsync(path, int.Parse(size[0]), int.Parse(size[1]),
                Opt("--query"), Opt("--filter"), page, args.Contains("--settings"));
        }
        catch (Exception ex) { LogCrash(ex); }
        Shutdown();
    }

    /// <summary>Installs or removes the "lone Windows key opens Launchpad" hook.</summary>
    private bool SetWinKey(bool on)
    {
        if (!on) { _winKey?.Stop(); return true; }
        if (_winKey == null)
        {
            _winKey = new WinKeyHook();
            _winKey.Tapped += () => Dispatcher.BeginInvoke(() => _window?.Toggle());   // the hook callback must return immediately
        }
        if (_winKey.Start()) return true;
        Balloon("Couldn't hook the Windows key on this system.");
        return false;
    }

    private static readonly string[] HotkeyFallbacks = { "Ctrl+Alt+L", "Ctrl+Alt+Space", "Ctrl+Alt+Shift+Space", "Ctrl+Win+Space", "Ctrl+Alt+F4" };

    /// <summary>Registers the configured shortcut; if another app owns it, quietly picks the first free alternative and says so.</summary>
    private void BindHotkey()
    {
        if (_hotkey!.Register(_settings.Hotkey)) return;
        string taken = _settings.Hotkey;
        foreach (string candidate in HotkeyFallbacks)
        {
            if (candidate == taken || !_hotkey.Register(candidate)) continue;
            _settings.Hotkey = candidate;
            _settings.Save();
            Dispatcher.InvokeAsync(() => Balloon($"{taken} is already used by another app, so Launchpad uses {candidate}. You can change it in Settings."));
            return;
        }
        Dispatcher.InvokeAsync(() => Balloon($"Couldn't register a shortcut ({taken} is taken). Pick one in Settings, or run Launchpad.exe --toggle from another tool."));
    }

    /// <summary>Developer aid (needs LAUNCHPAD_DATA): scripted drag-to-rearrange; see <see cref="MainWindow.SelfTestReorderAsync"/>.</summary>
    private async void ReorderTest(string[] args)
    {
        string path = args[Array.IndexOf(args, "--reorder-test") + 1];
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAUNCHPAD_DATA")))
                throw new InvalidOperationException("--reorder-test changes the saved order; set LAUNCHPAD_DATA to a scratch folder first.");
            var settings = AppSettings.Load();
            var window = new MainWindow(settings, new IndexService(settings));
            window.SetApps(new AppCatalog().LoadCached());
            File.WriteAllText(path + ".txt", await window.SelfTestReorderAsync(path, 1920, 1080));
        }
        catch (Exception ex) { File.WriteAllText(path + ".txt", ex.ToString()); }
        Shutdown();
    }

    /// <summary>Developer aid: <c>--dump-icon "App name" out.png</c> writes the raw bitmap Windows returns for an app (before styling).</summary>
    private void DumpIcon(string[] args)
    {
        int i = Array.IndexOf(args, "--dump-icon");
        string name = args[i + 1], path = args[i + 2];
        try
        {
            var app = new AppCatalog().LoadCached().First(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string target = JsonCache(app);
            if (Native.ShellIcons.TryGetBitmap(@"shell:AppsFolder\" + target, 256, out var bgra, out int w, out int h))
            {
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(path);
                enc.Save(fs);
            }
        }
        catch (Exception ex) { LogCrash(ex); }
        Shutdown();
    }

    private static string JsonCache(AppItem app) => app.Target;

    private async Task RefreshAppsAsync(bool rebuildIcons)
    {
        try
        {
            var fresh = await _catalog!.RefreshAsync(_apps, rebuildIcons);
            bool same = !rebuildIcons && fresh.Count == _apps.Count && fresh.Zip(_apps).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.IsUtility == p.Second.IsUtility);
            if (same) return;
            _apps = fresh;
            _window?.SetApps(fresh);
        }
        catch (Exception ex) { LogCrash(ex); }
    }

    // ================================================================== tray

    private void BuildTray()
    {
        var icon = LoadTrayIcon();
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };

        menu.Items.Add("Open Launchpad", null, (_, _) => _window?.ShowLaunchpad());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _startupItem = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = LoginStartup.IsEnabled(), CheckOnClick = true };
        _startupItem.CheckedChanged += (_, _) =>
        {
            try { LoginStartup.SetEnabled(_startupItem.Checked); _settings.LaunchAtLogin = _startupItem.Checked; _settings.Save(); }
            catch (Exception) { }
        };
        menu.Items.Add(_startupItem);
        menu.Items.Add("Rebuild file index", null, (_, _) => _ = _index!.RebuildAsync());
        menu.Items.Add("Refresh apps", null, (_, _) => _ = RefreshAppsAsync(rebuildIcons: true));
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Opening += (_, _) => _startupItem.Checked = LoginStartup.IsEnabled();

        _tray = new WinForms.NotifyIcon { Icon = icon, Text = "Launchpad", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _window?.Toggle(); };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/launchpad.ico"));
            if (res != null) return new System.Drawing.Icon(res.Stream);
        }
        catch (Exception) { }
        return System.Drawing.SystemIcons.Application;
    }

    private void Balloon(string text)
    {
        _tray?.ShowBalloonTip(5000, "Launchpad", text, WinForms.ToolTipIcon.Warning);
    }

    // ================================================================== lifecycle

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _winKey?.Dispose();
            _hotkey?.Dispose();
            _index?.Dispose();
            _settings.Save();
            _single?.Dispose();
        }
        finally { Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_quitting) { _quitting = true; _hotkey?.Dispose(); _index?.Dispose(); _single?.Dispose(); }
        base.OnExit(e);
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "errors.log"), $"[{DateTime.Now:s}] {ex}\n\n"); }
        catch (Exception) { }
    }
}
