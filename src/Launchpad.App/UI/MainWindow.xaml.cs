using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Launchpad.App.Apps;
using Launchpad.App.Native;
using Launchpad.App.Services;
using Launchpad.Core.Apps;

namespace Launchpad.App.UI;

public partial class MainWindow : Window
{
    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameEx(int nameFormat, System.Text.StringBuilder userName, ref int size);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo10
    {
        public IntPtr Name, Comment, UserComment, FullName;
    }

    private readonly AppSettings _settings;

    private IntPtr _hwnd;
    private List<AppItem> _allApps = new();
    private List<AppItem> _visibleApps = new();
    private readonly List<Button> _applicationPickerButtons = new();
    private int _applicationPickerSelection = -1;

    private bool _isOpen;
    private DateTime _shownAt;
    private bool _suppressDeactivate;
    private bool _dockMenuOpen;

    // pager
    private int _cols = 1, _rows = 1;
    private bool _mouseDown, _dragging;
    private bool _suppressTileClick;
    private bool _dismissGroupClick;
    private Point _dragStart;
    private int _focusedSearchItem = -1;

    // hot key capture
    private bool _capturingHotkey;

    /// <summary>Asks the host to rebind the global shortcut; returns false if the combination is unusable.</summary>
    public Func<string, bool>? ChangeHotkey { get; set; }
    public Action? RebuildIndexRequested { get; set; }
    public Action? RefreshAppsRequested { get; set; }
    public Action<bool>? IndexToggleRequested { get; set; }
    public Action<string>? AddItemRequested { get; set; }
    public Action<AppItem>? AddInstalledApplicationRequested { get; set; }

    /// <summary>Turns the Windows-key takeover on or off; returns false if the hook couldn't be installed.</summary>
    public Func<bool, bool>? WinKeyToggleRequested { get; set; }

    public MainWindow(AppSettings settings, IndexService index)
    {
        _settings = settings;
        InitializeComponent();
        LoadWindowsProfile();

        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) =>
        {
            if (_isOpen && !_suppressDeactivate && !_dockMenuOpen && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 300) HideLaunchpad();
        };
        Activated += (_, _) => { if (_isOpen && SettingsOverlay.Visibility != Visibility.Visible) SearchBox.Focus(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonUp += OnWindowMouseUp;
        PreviewMouseRightButtonUp += OnWindowMouseRightUp;
    }

    // ================================================================== window plumbing

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int doNotRound = 1, none = unchecked((int)0xFFFFFFFE);
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, 4);
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref none, 4);

        // layered, so the whole overlay can fade; excluded from capture so we can show first and photograph the desktop afterwards
        var ex = WindowStyles.GetWindowLongPtr(_hwnd, WindowStyles.GWL_EXSTYLE).ToInt64();
        WindowStyles.SetWindowLongPtr(_hwnd, WindowStyles.GWL_EXSTYLE, new IntPtr(ex | WindowStyles.WS_EX_LAYERED));
        ApplyAlpha(1);
        if (!CaptureBeforeShow) WindowStyles.SetWindowDisplayAffinity(_hwnd, WindowStyles.WDA_EXCLUDEFROMCAPTURE);
    }

    private void Cloak(bool on)
    {
        int v = on ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_hwnd, 13 /* DWMWA_CLOAK */, ref v, 4);
    }

    private bool _warming;

    /// <summary>
    /// Runs one complete show/hide cycle while cloaked (invisible, no focus change) so JIT, layout, fonts and the
    /// composition surface are all warm before the first real hot key press.
    /// </summary>
    public void WarmUp()
    {
        _warming = true;
        // Cloak needs a real HWND. Creating it here ensures the warm-up show below stays invisible;
        // otherwise Cloak(true) runs against the zero handle and the window flashes on startup.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Cloak(true);
        ShowActivated = false;
        ShowLaunchpad();
        ShowActivated = true;
        EventHandler? onFrame = null;
        int frames = 0;
        onFrame = (_, _) =>
        {
            if (++frames < 12) return;   // let the show animations play out unseen
            CompositionTarget.Rendering -= onFrame;
            HideLaunchpad();
        };
        CompositionTarget.Rendering += onFrame;
    }

    private void PlaceOnMonitor(NativeMethods.Rect r)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, NativeMethods.SWP_NOACTIVATE);
        // Re-assert topmost only after the window is visible. Clearing stale topmost state
        // before placement lets Windows keep transient layered notifications above it.
        if (IsVisible) NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, NativeMethods.SWP_NOACTIVATE);
    }

    public bool IsOpen => _isOpen;

    public void Toggle()
    {
        if (_isOpen) HideLaunchpad(); else ShowLaunchpad();
    }

    private int _showId;

    // Whole-window fade (a layered window, so the overlay can fade over the live desktop without AllowsTransparency).
    public static readonly DependencyProperty WindowAlphaProperty = DependencyProperty.Register(
        nameof(WindowAlpha), typeof(double), typeof(MainWindow),
        new PropertyMetadata(1.0, (d, e) => ((MainWindow)d).ApplyAlpha((double)e.NewValue)));

    public double WindowAlpha { get => (double)GetValue(WindowAlphaProperty); set => SetValue(WindowAlphaProperty, value); }

    private void ApplyAlpha(double a)
    {
        if (_hwnd != IntPtr.Zero)
            WindowStyles.SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Clamp((int)(a * 255 + 0.5), 0, 255), WindowStyles.LWA_ALPHA);
    }

    /// <summary>
    /// With diagnostics on, the window stays visible to screen capture (so tests can screenshot it) and the backdrop is grabbed
    /// before showing. Normally the window is excluded from capture, which lets us show first and grab the desktop afterwards.
    /// </summary>
    private static readonly bool CaptureBeforeShow = Environment.GetEnvironmentVariable("LAUNCHPAD_DEBUG") == "1";

    public void ShowLaunchpad()
    {
        if (_isOpen) { NativeMethods.ForceForeground(_hwnd); return; }
        _isOpen = true;
        _shownAt = DateTime.UtcNow;
        int showId = ++_showId;
        var sw = Stopwatch.StartNew();
        EventHandler? onFrame = null;
        onFrame = (_, _) => { CompositionTarget.Rendering -= onFrame; Diag.Log($"show: first frame after {sw.Elapsed.TotalMilliseconds:0.0} ms"); };
        CompositionTarget.Rendering += onFrame;

        var r = NativeMethods.GetMonitorRectAtCursor();
        PlaceOnMonitor(r);
        ResetUi();

        bool fresh = !IsVisible;
        if (fresh)
        {
            Backdrop.Opacity = 0;
            Backdrop.Source = null;
            if (CaptureBeforeShow)
            {
                Backdrop.Source = BackdropCapture.Capture(r);
                Backdrop.Opacity = 1;
                Diag.Log($"backdrop capture+blur {sw.Elapsed.TotalMilliseconds:0.0} ms");
            }
        }

        Stage.BeginAnimation(OpacityProperty, null);
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Stage.Opacity = 0;
        StageScale.ScaleX = StageScale.ScaleY = 1.06;
        BeginAnimation(WindowAlphaProperty, null);
        WindowAlpha = fresh ? 0 : WindowAlpha;

        if (!_warming) Cloak(false);
        Show();
        PlaceOnMonitor(r);   // second pass: a monitor with different DPI makes WPF resize us on the first
        if (!_warming)
        {
            NativeMethods.ForceForeground(_hwnd);
            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
        Animate(this, WindowAlphaProperty, 1, 130, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(Stage, OpacityProperty, 1, 170, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(StageScale, ScaleTransform.ScaleXProperty, 1, 260, new QuinticEase { EasingMode = EasingMode.EaseOut });
        Animate(StageScale, ScaleTransform.ScaleYProperty, 1, 260, new QuinticEase { EasingMode = EasingMode.EaseOut });

        if (fresh && !CaptureBeforeShow)
        {
            // The window is excluded from capture, so this sees the desktop as it was; the frosted backdrop fades in when ready.
            Task.Run(() =>
            {
                var cap = Stopwatch.StartNew();
                var bmp = BackdropCapture.Capture(r);
                Diag.Log($"backdrop capture+blur (async) {cap.Elapsed.TotalMilliseconds:0.0} ms");
                return bmp;
            }).ContinueWith(t =>
            {
                if (showId != _showId || !_isOpen || t.Result == null) return;
                Backdrop.Source = t.Result;
                Animate(Backdrop, OpacityProperty, 1, 200, new CubicEase { EasingMode = EasingMode.EaseOut });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    public void HideLaunchpad()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _capturingHotkey = false;
        AbortReorder();
        Animate(StageScale, ScaleTransform.ScaleXProperty, 1.04, 140, new CubicEase { EasingMode = EasingMode.EaseIn });
        Animate(StageScale, ScaleTransform.ScaleYProperty, 1.04, 140, new CubicEase { EasingMode = EasingMode.EaseIn });
        Animate(Stage, OpacityProperty, 0, 130, new CubicEase { EasingMode = EasingMode.EaseIn });
        Animate(this, WindowAlphaProperty, 0, 150, new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
        {
            if (_isOpen) return;
            Hide();
            Backdrop.Source = null;
            WindowAlpha = 1;
            if (_warming) { _warming = false; Cloak(false); }
        });
    }

    private void ResetUi()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        if (_openGroup != null)
        {
            ExitGroup();
            GroupHost.Visibility = Visibility.Collapsed;   // no fade: the launcher is being reset for a fresh open
            WindowDim.BeginAnimation(OpacityProperty, null); WindowDim.Opacity = 0;
            PagerMask.BeginAnimation(OpacityProperty, null); PagerMask.Opacity = 1;
            SearchHost.BeginAnimation(OpacityProperty, null); SearchHost.Opacity = 1;
            PagerMask.Effect = null;
        }
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        _scrollTarget = _scrollY = 0;
        ApplyScroll(showThumb: false);
    }

    // ================================================================== apps & pager

    public void SetApps(List<AppItem> apps)
    {
        _allApps = apps;
        ApplyHidden();
        if (ApplicationPicker.Visibility == Visibility.Visible) UpdateApplicationPickerResults();
    }

    public void ShowApplicationPicker()
    {
        ApplicationPickerSearch.Text = "";
        ApplicationPicker.Visibility = Visibility.Visible;
        UpdateApplicationPickerResults();
        ApplicationPickerSearch.Focus();
        Keyboard.Focus(ApplicationPickerSearch);
    }

    public void CloseApplicationPicker()
    {
        ApplicationPicker.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
    }

    private void OnApplicationPickerSearchChanged(object sender, TextChangedEventArgs e) => UpdateApplicationPickerResults();

    private void UpdateApplicationPickerResults()
    {
        if (ApplicationPickerResults == null) return;
        ApplicationPickerResults.Children.Clear();
        _applicationPickerButtons.Clear();
        _applicationPickerSelection = -1;
        string query = ApplicationPickerSearch.Text.Trim();
        if (query.Length == 0)
        {
            ApplicationPickerResults.Children.Add(new TextBlock { Text = "Type an app name to search installed applications", Foreground = (Brush)FindResource("SubtleTextBrush"), FontSize = 12.5, Margin = new Thickness(4, 4, 4, 10), TextWrapping = TextWrapping.Wrap });
            return;
        }

        var matches = AppSearch.Search(_allApps, query, limit: 40);
        if (matches.Count == 0)
        {
            ApplicationPickerResults.Children.Add(new TextBlock { Text = "No installed applications found", Foreground = (Brush)FindResource("SubtleTextBrush"), FontSize = 12.5, Margin = new Thickness(4, 4, 4, 10) });
            return;
        }

        foreach (var app in matches)
        {
            var button = new Button
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 7, 10, 7), Cursor = Cursors.Hand,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Image { Source = app.Icon, Width = 34, Height = 34, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0) },
                        new TextBlock { Text = app.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                },
                Tag = app,
            };
            button.MouseEnter += (_, _) => SetApplicationPickerSelection(_applicationPickerButtons.IndexOf(button));
            button.Click += (_, _) => SelectApplicationPickerItem(_applicationPickerButtons.IndexOf(button));
            _applicationPickerButtons.Add(button);
            ApplicationPickerResults.Children.Add(button);
        }
        SetApplicationPickerSelection(0);
    }

    private void SetApplicationPickerSelection(int index)
    {
        if (_applicationPickerButtons.Count == 0) { _applicationPickerSelection = -1; return; }
        _applicationPickerSelection = Math.Clamp(index, 0, _applicationPickerButtons.Count - 1);
        for (int i = 0; i < _applicationPickerButtons.Count; i++)
            _applicationPickerButtons[i].Background = i == _applicationPickerSelection
                ? new SolidColorBrush(Color.FromArgb(0x35, 255, 255, 255))
                : Brushes.Transparent;
        _applicationPickerButtons[_applicationPickerSelection].BringIntoView();
    }

    private void SelectApplicationPickerItem(int index)
    {
        if (index < 0 || index >= _applicationPickerButtons.Count) return;
        if (_applicationPickerButtons[index].Tag is AppItem selected) AddInstalledApplicationRequested?.Invoke(selected);
        CloseApplicationPicker();
    }

    private bool NavigateApplicationPicker(int delta)
    {
        if (_applicationPickerButtons.Count == 0) return false;
        int current = _applicationPickerSelection < 0 ? 0 : _applicationPickerSelection;
        SetApplicationPickerSelection(Math.Clamp(current + delta, 0, _applicationPickerButtons.Count - 1));
        return true;
    }

    private void OnApplicationPickerBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ApplicationPicker)) { CloseApplicationPicker(); e.Handled = true; }
    }

    private void ApplyHidden()
    {
        var hidden = new HashSet<string>(_settings.HiddenApps);
        _visibleApps = _allApps.Where(a => !hidden.Contains(a.Id) && (_settings.ShowAllApps || !a.IsUtility)).ToList();
        _items = BuildItems(_visibleApps);   // apps and groups, in the chosen order
        EmptyAppsText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildPages(resetScrollForSearch: false);
        if (_openGroup != null)
        {
            if (!_settings.Groups.Contains(_openGroup)) ExitGroup(refresh: false);
            else BuildPanel();
        }
        ApplySearchFilter();
    }

    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs e) => RebuildPages();

    private void RebuildPages(bool resetScrollForSearch = true)
    {
        double w = HomeLayer.ActualWidth, h = HomeLayer.ActualHeight;
        if (w < 200 || h < 200) return;

        double areaW = Math.Min(w - 140, 1560);
        double areaH = h - 24;
        _cols = Math.Clamp((int)(areaW / 176), 3, 9);
        _rows = Math.Clamp((int)(areaH / 166), 2, 6);   // rows that fit on screen at once; the rest scroll
        double cellW = areaW / _cols;
        double cellH = Math.Min(areaH / _rows, 188);
        double iconSize = Math.Min(Math.Min(cellW * 0.80, cellH * 0.74), 136);

        int totalRows = Math.Max(1, (int)Math.Ceiling(_items.Count / (double)_cols));
        double gridH = totalRows * cellH;
        // Keep search results anchored below the search box; otherwise retain the launcher's short-list centering.
        double padTop = SearchBox.Text.Length > 0 ? 52 : Math.Max(52, (h - gridH) / 2);
        const double padBottom = 64;
        _cellH = cellH;
        _viewH = h;
        _contentH = padTop + gridH + padBottom;
        _maxScroll = Math.Max(0, _contentH - h);

        // tiles are placed absolutely on a Canvas so they can be slid around smoothly while rearranging
        // The app icons are already cached as frozen bitmaps on disk. Cache their composed tile visuals too,
        // so scrolling translates one rasterized surface instead of rerendering every icon and label each frame.
        var grid = new Canvas
        {
            Width = cellW * _cols,
            Height = gridH,
            CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = true },
        };
        _tileById.Clear();
        _origIndexById.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var app = _items[i];
            var tile = CreateGridTile(app, iconSize, cellW, cellH);
            Canvas.SetLeft(tile, (i % _cols) * cellW);
            Canvas.SetTop(tile, (i / _cols) * cellH);
            grid.Children.Add(tile);
            _tileById[app.Id] = tile;
            _origIndexById[app.Id] = i;
        }
        _cellW = cellW;
        _iconSize = iconSize;
        _totalRows = totalRows;
        // a group with only a few apps is centred horizontally instead of hugging the left edge
        _gridLeft = (w - cellW * _cols) / 2;
        _gridTop = padTop;
        _main.Items = _items; _main.Cols = _cols; _main.CellW = cellW; _main.CellH = cellH; _main.TotalRows = totalRows; _main.Left = _gridLeft; _main.Top = _gridTop;

        Pager.Children.Clear();
        Canvas.SetLeft(grid, _gridLeft);   // a Canvas, so the tall grid isn't layout-clipped to the viewport
        Canvas.SetTop(grid, padTop);
        Pager.Children.Add(grid);

        // soft fade where tiles slide under the top / bottom edge. Absolute mapping, because a relative one would
        // stretch over the whole (much taller) scrolled content instead of the viewport.
        double fade = 44;
        var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, fade / h));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade / h));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        mask.Freeze();
        PagerMask.OpacityMask = mask;

        if (resetScrollForSearch && SearchBox.Text.Length > 0) _scrollTarget = _scrollY = 0;
        else
        {
            _scrollTarget = Math.Clamp(_scrollTarget, 0, _maxScroll);
            _scrollY = Math.Clamp(_scrollY, 0, _maxScroll);
        }
        ApplyScroll(showThumb: false);
        Diag.Log($"RebuildPages w={w:0} h={h:0} cols={_cols} rowsVisible={_rows} totalRows={totalRows} cell={cellW:0}x{cellH:0} icon={iconSize:0} items={_items.Count} content={_contentH:0} max={_maxScroll:0}");
    }

    private Border CreateTile(AppItem app, double iconSize, double cellW, double cellH, bool compact, bool highlight, Action<Border, AppItem>? contextMenu = null)
    {
        var scale = new ScaleTransform(1, 1);
        var img = new Image
        {
            Source = app.Icon, Width = iconSize, Height = iconSize,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        double fontSize = compact ? 12 : 13.5;
        double maxText = Math.Max(40, cellW - 14);
        var label = new Grid { Margin = new Thickness(0, -iconSize * 0.07, 0, 0), IsHitTestVisible = false };
        if (!compact)
            label.Children.Add(new TextBlock
            {
                Text = app.Name, FontSize = fontSize, Foreground = Brushes.Black, Opacity = 0.5, MaxWidth = maxText,
                TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1.5, 0, 0),
            });
        label.Children.Add(new TextBlock
        {
            Text = app.Name, FontSize = fontSize, Foreground = Brushes.White, MaxWidth = maxText,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(img);
        stack.Children.Add(label);

        var tile = new Border
        {
            Width = cellW, Height = cellH, Child = stack, Tag = app, Cursor = Cursors.Hand,
            Background = highlight ? new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(16),
            ToolTip = null,
            RenderTransform = new TranslateTransform(),   // slides the tile while icons are being rearranged
        };
        AutomationProperties.SetName(tile, app.Name);

        tile.MouseEnter += (_, _) => Animate(scale, ScaleTransform.ScaleXProperty, 1.07, 130, null, null, also: ScaleTransform.ScaleYProperty);
        tile.MouseLeave += (_, _) => Animate(scale, ScaleTransform.ScaleXProperty, 1.0, 160, null, null, also: ScaleTransform.ScaleYProperty);
        tile.PreviewMouseLeftButtonDown += (_, _) => Animate(scale, ScaleTransform.ScaleXProperty, 0.93, 80, null, null, also: ScaleTransform.ScaleYProperty);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_suppressTileClick || _dragging || _reordering) { e.Handled = true; return; }
            e.Handled = true;
            LaunchApp(app);
        };
        tile.MouseRightButtonUp += (_, e) => { if (contextMenu != null) contextMenu(tile, app); else ShowAppMenu(tile, app); e.Handled = true; };
        return tile;
    }

    // ---- smooth vertical scrolling

    private double _scrollY, _scrollTarget, _maxScroll, _cellH = 180, _viewH, _contentH;
    private bool _scrollLoop;
    private long _scrollBegan;
    private int _scrollFrames;
    private long _scrollStamp;
    private double _dragStartScroll, _dragLastY, _dragVelocity;
    private long _dragStamp;
    private System.Windows.Threading.DispatcherTimer? _thumbTimer;

    private void ScrollBy(double delta) => ScrollTo(_scrollTarget + delta);

    /// <summary>Scrolls (eased) towards <paramref name="y"/> DIPs from the top; <paramref name="instant"/> jumps there.</summary>
    private void ScrollTo(double y, bool instant = false)
    {
        _scrollTarget = Math.Clamp(y, 0, _maxScroll);
        if (instant) { _scrollY = _scrollTarget; ApplyScroll(); return; }
        if (_scrollLoop) return;
        _scrollLoop = true;
        _scrollStamp = _scrollBegan = Stopwatch.GetTimestamp();
        _scrollFrames = 0;
        CompositionTarget.Rendering += OnScrollFrame;
    }

    /// <summary>Runs once per rendered frame while a scroll is in flight: a frame-rate independent exponential ease-out.</summary>
    private void OnScrollFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _scrollStamp) / (double)Stopwatch.Frequency, 0.001, 0.05);
        _scrollStamp = now;
        _scrollFrames++;
        if (_dragging) return;

        double diff = _scrollTarget - _scrollY;
        if (Math.Abs(diff) < 0.3)
        {
            _scrollY = _scrollTarget;
            _scrollLoop = false;
            CompositionTarget.Rendering -= OnScrollFrame;
            Diag.Log($"scroll settled at {_scrollY:0}/{_maxScroll:0} after {_scrollFrames} frames in {(now - _scrollBegan) * 1000.0 / Stopwatch.Frequency:0} ms");
        }
        else _scrollY += diff * (1 - Math.Exp(-dt * 22));
        ApplyScroll();
    }

    private void ApplyScroll(bool showThumb = true)
    {
        PagerShift.Y = -_scrollY;
        if (showThumb) UpdateThumb();
    }

    private void UpdateThumb()
    {
        if (_maxScroll <= 1) { ScrollThumb.Opacity = 0; return; }
        double track = _viewH - 24;
        double thumbH = Math.Max(40, track * _viewH / _contentH);
        double top = 12 + (_scrollY / _maxScroll) * (track - thumbH);
        ScrollThumb.Height = thumbH;
        ScrollThumb.Margin = new Thickness(0, top, 12, 0);
        ScrollThumb.BeginAnimation(OpacityProperty, null);
        ScrollThumb.Opacity = 0.75;

        _thumbTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _thumbTimer.Tick -= OnThumbTimer;
        _thumbTimer.Tick += OnThumbTimer;
        _thumbTimer.Stop();
        _thumbTimer.Start();
    }

    private void OnThumbTimer(object? sender, EventArgs e)
    {
        _thumbTimer?.Stop();
        Animate(ScrollThumb, OpacityProperty, 0, 350);
    }

    // ---- scroll input: wheel / touchpad / drag

    private void OnPagerWheel(object sender, MouseWheelEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        e.Handled = true;
        if (_openGroup != null) return;   // the group window is modal

        // A mouse wheel sends one 120-unit event per notch: that should be about one row. A touchpad sends a stream of small
        // (or rapid-fire) events for one gesture, so it gets a much gentler factor, or a flick would fly through the whole list.
        long now = Stopwatch.GetTimestamp();
        double sinceLast = (now - _lastWheelStamp) * 1000.0 / Stopwatch.Frequency;
        _lastWheelStamp = now;
        bool touchpad = Math.Abs(e.Delta) % 120 != 0 || Math.Abs(e.Delta) < 120 || sinceLast < 60;

        double step = touchpad
            ? Math.Clamp(-e.Delta * 0.42, -48, 48)                       // roughly finger distance, capped per event
            : -e.Delta * (_cellH / 120.0) * 0.95;                        // a notch is about one row
        ScrollBy(step);
    }

    private long _lastWheelStamp;

    private void OnPagerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SearchBox.Text.Length > 0) SearchBox.Focus();
        if (_openGroup != null)
        {
            if (!IsInside(e.OriginalSource as DependencyObject, GroupHost))
            {
                _dismissGroupClick = true;
                e.Handled = true;
                return;
            }
            _cx = _panel;
            RefreshPanelOrigin();
        }
        else _cx = _main;
        _pressed = true;
        _mouseDown = _openGroup == null && _maxScroll > 1;
        _dragging = false;
        _suppressTileClick = false;
        _dragStart = e.GetPosition(HomeLayer);
        _pointerHome = _dragStart;
        ResetPointerSpeed(_dragStart);

        // pressing an icon starts the hold timer: keep still long enough and it lifts so it can be rearranged
        if (FindAncestor<Border>(e.OriginalSource as DependencyObject, b => b.Tag is AppItem or GridItem) is { } tile)
            BeginHold(tile, _dragStart);
    }

    private void OnPagerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        if (e.LeftButton != MouseButtonState.Pressed && !_selfTestDrag)
        {
            _pressed = _mouseDown = _dragging = false;
            AbortReorder();
            return;
        }

        var pos = e.GetPosition(HomeLayer);
        SamplePointerSpeed(pos);
        _pointerHome = pos;
        if (_reordering) { UpdateReorder(); return; }

        // an icon was pressed and the pointer moved: pick it up straight away (no hold needed)
        if (_holdTile != null && (Math.Abs(pos.X - _holdStart.X) > DragSlop || Math.Abs(pos.Y - _holdStart.Y) > DragSlop))
        {
            _suppressTileClick = true;
            StartReorder();
            if (_reordering) { UpdateReorder(); return; }
        }

        // dragging empty space scrolls; a pressed icon never does
        if (_holdTile != null) return;

        if (!_mouseDown) return;
        double y = pos.Y;
        if (!_dragging && Math.Abs(y - _dragStart.Y) > 8)
        {
            CancelHold();
            _dragging = true;
            _suppressTileClick = true;
            HomeLayer.CaptureMouse();
            _dragStart.Y = y;                 // start from here so the content doesn't jump by the dead zone
            _dragStartScroll = _scrollY;
            _dragLastY = y;
            _dragStamp = Stopwatch.GetTimestamp();
            _dragVelocity = 0;
        }
        if (!_dragging) return;

        long now = Stopwatch.GetTimestamp();
        double dt = (now - _dragStamp) / (double)Stopwatch.Frequency;
        if (dt > 0.004)
        {
            double v = (_dragLastY - y) / dt;               // DIPs per second, positive = scrolling down
            _dragVelocity = _dragVelocity * 0.6 + v * 0.4;
            _dragLastY = y; _dragStamp = now;
        }
        _scrollTarget = _scrollY = Math.Clamp(_dragStartScroll - (y - _dragStart.Y), 0, _maxScroll);
        ApplyScroll();
    }

    private void OnPagerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dismissGroupClick)
        {
            _dismissGroupClick = false;
            ExitGroup();
            e.Handled = true;
            return;
        }

        bool wasDragging = _dragging, wasReordering = _reordering;
        _pressed = _mouseDown = _dragging = false;
        CancelHold();

        if (wasReordering)
        {
            _suppressTileClick = true;
            e.Handled = true;      // dropping an icon must not also launch it
            FinishReorder();
            return;
        }
        if (wasDragging)
        {
            _suppressTileClick = true;
            HomeLayer.ReleaseMouseCapture();
            e.Handled = true;
            // let it coast a little in the direction of the flick
            if ((Stopwatch.GetTimestamp() - _dragStamp) / (double)Stopwatch.Frequency < 0.12)
                ScrollTo(_scrollY + _dragVelocity * 0.25);
            return;
        }
        // a plain click on empty space dismisses the launcher
        if (_openGroup == null
            && FindAncestor<Border>(e.OriginalSource as DependencyObject, b => b.Tag is AppItem or GridItem) == null)
            HideLaunchpad();
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        var src = e.OriginalSource as DependencyObject;
        if (_openGroup != null)
        {
            if (IsInside(src, GroupHost)) return;
            if (!IsInside(src, HomeLayer))
            {
                ExitGroup();
                e.Handled = true;
            }
            return;
        }
        if (IsInside(src, ApplicationPicker) || IsInside(src, HomeLayer) || IsInside(src, SearchHost) || IsInside(src, Dock)) return;
        HideLaunchpad();
    }

    private void OnWindowMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isOpen || SettingsOverlay.Visibility == Visibility.Visible || ApplicationPicker.Visibility == Visibility.Visible) return;
        var source = e.OriginalSource as DependencyObject;
        // App and group tiles keep their existing actions and receive a settings entry in those menus.
        if (FindAncestor<Border>(source, border => border.Tag is AppItem or GridItem) != null) return;
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var add = new MenuItem { Header = "Add item" };
        AddItemOption(add, "Application…", "application");
        AddItemOption(add, "Folder…", "folder");
        AddItemOption(add, "File…", "file");
        menu.Items.Add(add);
        AddLaunchpadSettingsMenuItem(menu);
        OpenTrackedContextMenu(menu);
        e.Handled = true;
    }

    private void AddItemOption(MenuItem parent, string label, string kind)
    {
        var option = new MenuItem { Header = label };
        option.Click += (_, _) => AddItemRequested?.Invoke(kind);
        parent.Items.Add(option);
    }

    private void OnExplorerClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); HideLaunchpad(); }
        catch (Exception) { }
    }

    private void OnDocumentsClick(object sender, RoutedEventArgs e) => OpenUserFolder(Environment.SpecialFolder.MyDocuments);

    private void OnDownloadsClick(object sender, RoutedEventArgs e) => OpenUri("shell:Downloads");

    private void OpenUserFolder(Environment.SpecialFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(folder);
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            HideLaunchpad();
        }
        catch (Exception) { }
    }

    private void OnWindowsSettingsClick(object sender, RoutedEventArgs e) => OpenUri("ms-settings:");

    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        var menu = CreateDockMenu();
        menu.Items.Add(new MenuItem { Header = "Account settings" });
        ((MenuItem)menu.Items[0]).Click += (_, _) => OpenUri("ms-settings:yourinfo");
        var lockItem = new MenuItem { Header = "Lock" };
        lockItem.Click += (_, _) => RunSystemCommand("rundll32.exe", "user32.dll,LockWorkStation", "lock");
        menu.Items.Add(lockItem);
        var signOutItem = new MenuItem { Header = "Sign out" };
        signOutItem.Click += (_, _) => ConfirmSystemAction("Sign out", "Sign out of your Windows account?", "shutdown.exe", "/l");
        menu.Items.Add(signOutItem);
        OpenDockMenu(AccountButton, menu);
    }

    private void OnPowerClick(object sender, RoutedEventArgs e)
    {
        var menu = CreateDockMenu();
        var sleepItem = new MenuItem { Header = "Sleep" };
        sleepItem.Click += (_, _) => RunSystemCommand("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0", "sleep");
        menu.Items.Add(sleepItem);
        var shutdownItem = new MenuItem { Header = "Shut down" };
        shutdownItem.Click += (_, _) => ConfirmSystemAction("Shut down", "Shut down this PC?", "shutdown.exe", "/s /t 0");
        menu.Items.Add(shutdownItem);
        var restartItem = new MenuItem { Header = "Restart" };
        restartItem.Click += (_, _) => ConfirmSystemAction("Restart", "Restart this PC?", "shutdown.exe", "/r /t 0");
        menu.Items.Add(restartItem);
        OpenDockMenu(PowerButton, menu);
    }

    private static ContextMenu CreateDockMenu() => new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false };

    private static void OpenDockMenu(FrameworkElement anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        if (Window.GetWindow(anchor) is MainWindow window) window.OpenTrackedContextMenu(menu);
        else menu.IsOpen = true;
    }

    private void OpenTrackedContextMenu(ContextMenu menu)
    {
        _dockMenuOpen = true;
        menu.Closed += (_, _) => _dockMenuOpen = false;
        menu.Opened += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                NativeMethods.SetAccent(source.Handle, NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND, 0xD91C1C1C);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        menu.IsOpen = true;
    }

    private void AddLaunchpadSettingsMenuItem(ContextMenu menu)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Launchpad settings" };
        settings.Click += (_, _) => OnSettingsClick(this, new RoutedEventArgs());
        menu.Items.Add(settings);
    }

    private void LoadWindowsProfile()
    {
        string displayName = GetWindowsFullName() ?? Environment.UserName;
        try
        {
            int length = 0;
            GetUserNameEx(3 /* NameDisplay */, new System.Text.StringBuilder(1), ref length);
            if (length > 1)
            {
                var name = new System.Text.StringBuilder(length);
                if (GetUserNameEx(3, name, ref length) && !string.IsNullOrWhiteSpace(name.ToString())) displayName = name.ToString();
            }
        }
        catch (Exception) { }

        AccountName.Text = displayName;
        AccountName.ToolTip = displayName;
        AccountInitial.Text = displayName.Trim().FirstOrDefault(char.IsLetterOrDigit).ToString().ToUpperInvariant();
        string? photoPath = FindAccountPhoto();
        if (photoPath == null) return;
        try
        {
            using var stream = File.OpenRead(photoPath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource image = decoder.Frames[0];
            image.Freeze();
            AccountPhoto.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            AccountPhoto.Visibility = Visibility.Visible;
            AccountInitial.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { }
    }

    private static string? FindAccountPhoto()
    {
        try
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            if (sid.Length > 0)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
                foreach (string imageKey in new[] { "Image448", "Image424", "Image208", "Image96", "Image64" })
                    if (key?.GetValue(imageKey) is string imagePath && File.Exists(imagePath)) return imagePath;
            }
        }
        catch (Exception) { }

        // Newer Microsoft-account profile photos can be held in CloudExperienceHost's account cache.
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages",
            "Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy", "AC", "TokenBroker", "Accounts");
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => Path.GetFileName(path).Contains("tbacctpic", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path.EndsWith("1080x1080", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => path.EndsWith("424x424", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => path.EndsWith("208x208", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    private static string? GetWindowsFullName()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NetUserGetInfo(null, Environment.UserName, 10, out buffer) != 0 || buffer == IntPtr.Zero) return null;
            var info = Marshal.PtrToStructure<UserInfo10>(buffer);
            string? fullName = Marshal.PtrToStringUni(info.FullName);
            return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
        }
        catch (Exception) { return null; }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }

    private void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); HideLaunchpad(); }
        catch (Exception) { }
    }

    private void RunSystemCommand(string fileName, string arguments, string action)
    {
        try { Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true }); HideLaunchpad(); }
        catch (Exception) { MessageBox.Show(this, $"Windows couldn't {action}.", "Launchpad", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ConfirmSystemAction(string title, string message, string fileName, string arguments)
    {
        if (MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            RunSystemCommand(fileName, arguments, title.ToLowerInvariant());
    }

    // ================================================================== launching

    private void LaunchApp(AppItem app)
    {
        AppCatalog.Launch(app);
        _settings.Launches[app.Id] = _settings.Launches.GetValueOrDefault(app.Id) + 1;
        Task.Run(_settings.Save);
        HideLaunchpad();
    }

    private void ShowAppMenu(FrameworkElement anchor, AppItem app)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => LaunchApp(app);
        menu.Items.Add(open);

        if (Path.IsPathRooted(app.Target) && File.Exists(app.Target))
        {
            var loc = new MenuItem { Header = "Show in folder" };
            loc.Click += (_, _) => { Reveal(app.Target); HideLaunchpad(); };
            menu.Items.Add(loc);
        }
        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "Hide from Launchpad" };
        hide.Click += (_, _) =>
        {
            if (!_settings.HiddenApps.Contains(app.Id)) _settings.HiddenApps.Add(app.Id);
            Task.Run(_settings.Save);
            ApplyHidden();
        };
        menu.Items.Add(hide);
        AddLaunchpadSettingsMenuItem(menu);
        menu.PlacementTarget = anchor;
        OpenTrackedContextMenu(menu);
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ================================================================== search

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string text = SearchBox.Text;
        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplySearchFilter(resetScroll: true);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void ApplySearchFilter(bool resetScroll = false)
    {
        string[] words = SearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string name) => words.All(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        var matchingApps = _visibleApps.Where(app => Matches(app.Name)).ToList();
        foreach (var group in _settings.Groups.Where(group => Matches(group.Name)))
            matchingApps.AddRange(_visibleApps.Where(app => group.AppIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase)));
        _items = BuildItems(matchingApps.DistinctBy(app => app.Id).ToList());
        if (_openGroup != null)
            _items = _items.Where(item => item.App != null && _openGroup.AppIds.Contains(item.App.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        _focusedSearchItem = -1;
        foreach (var tile in _tileById.Values) tile.Background = Brushes.Transparent;
        if (resetScroll) _scrollTarget = _scrollY = 0;
        EmptyAppsText.Text = words.Length == 0 ? "Loading apps…" : "No matching apps";
        EmptyAppsText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildPages(resetScrollForSearch: resetScroll);
        if (words.Length > 0 && _items.Count > 0)
        {
            _focusedSearchItem = 0;
            HighlightSearchItem(0);
        }
    }

    // ================================================================== keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey) return;   // handled by the hotkey button

        if (ApplicationPicker.Visibility == Visibility.Visible)
        {
            switch (e.Key)
            {
                case Key.Escape: CloseApplicationPicker(); e.Handled = true; break;
                case Key.Down: NavigateApplicationPicker(1); e.Handled = true; break;
                case Key.Up: NavigateApplicationPicker(-1); e.Handled = true; break;
                case Key.Home: SetApplicationPickerSelection(0); e.Handled = true; break;
                case Key.End: SetApplicationPickerSelection(_applicationPickerButtons.Count - 1); e.Handled = true; break;
                case Key.Enter:
                    if (_applicationPickerSelection >= 0) SelectApplicationPickerItem(_applicationPickerSelection);
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (GroupNameBox.IsKeyboardFocused) return;   // typing a group name: the box's own handler deals with Enter / Esc

        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else if (_openGroup != null) ExitGroup();
                else HideLaunchpad();
                e.Handled = true;
                break;

            case Key.Enter:
                if (SearchBox.Text.Length > 0) ActivateSearchItem();
                e.Handled = true;
                break;

            case Key.Down when SearchBox.Text.Length > 0: NavigateSearchItems(0, 1); e.Handled = true; break;
            case Key.Up when SearchBox.Text.Length > 0: NavigateSearchItems(0, -1); e.Handled = true; break;
            case Key.Right when SearchBox.Text.Length > 0: NavigateSearchItems(1, 0); e.Handled = true; break;
            case Key.Left when SearchBox.Text.Length > 0: NavigateSearchItems(-1, 0); e.Handled = true; break;
            case Key.Down: ScrollBy(_cellH); e.Handled = true; break;
            case Key.Up: ScrollBy(-_cellH); e.Handled = true; break;
            case Key.PageDown: ScrollBy(_viewH - _cellH * 0.5); e.Handled = true; break;
            case Key.PageUp: ScrollBy(-(_viewH - _cellH * 0.5)); e.Handled = true; break;
            case Key.Home: ScrollTo(0); e.Handled = true; break;
            case Key.End: ScrollTo(_maxScroll); e.Handled = true; break;

            case Key.OemComma when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): OnSettingsClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private void NavigateSearchItems(int dx, int dy)
    {
        if (_items.Count == 0) return;
        int target;
        if (_focusedSearchItem < 0) target = 0;
        else
        {
            int col = _focusedSearchItem % _cols;
            int row = _focusedSearchItem / _cols;
            int targetCol = col + dx;
            int targetRow = row + dy;
            if (targetCol < 0 || targetCol >= _cols || targetRow < 0) target = _focusedSearchItem;
            else
            {
                target = targetRow * _cols + targetCol;
                if (target >= _items.Count) target = _focusedSearchItem;
            }
        }

        _focusedSearchItem = target;
        HighlightSearchItem(target);
    }

    private void HighlightSearchItem(int index)
    {
        foreach (var candidate in _tileById.Values) candidate.Background = Brushes.Transparent;
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        if (_tileById.TryGetValue(item.Id, out var tile))
        {
            tile.Background = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255));
            tile.Focusable = true;
            double tileY = Canvas.GetTop(tile) + _gridTop;
            if (tileY < _scrollY + 24) ScrollTo(tileY - 24);
            else if (tileY + _cellH > _scrollY + _viewH - 24) ScrollTo(tileY + _cellH - _viewH + 24);
        }
    }

    private void ActivateSearchItem()
    {
        if (_items.Count == 0) return;
        if (_focusedSearchItem < 0) _focusedSearchItem = 0;
        var item = _items[_focusedSearchItem];
        if (item.App != null) LaunchApp(item.App);
        else if (item.Group != null) EnterGroup(item.Group);
    }

    // ================================================================== settings overlay

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        StartupSwitch.IsChecked = LoginStartup.IsEnabled();
        IndexSwitch.IsChecked = _settings.IndexFiles;
        WinKeySwitch.IsChecked = _settings.WinKeyOpensLaunchpad;
        UtilitiesSwitch.IsChecked = _settings.ShowAllApps;
        SyncArrangeChips();
        int utilities = _allApps.Count(a => a.IsUtility);
        UtilitiesInfo.Text = utilities == 0 ? "Admin tools and helpers are kept out of the grid."
            : $"{utilities} admin tools and helpers are kept out of the grid.";
        HotkeyButton.Content = _settings.Hotkey;
        UnhideButton.Visibility = _settings.HiddenApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        IndexSettingsInfo.Text = _settings.IndexFiles ? "File search index is enabled" : "File search index is disabled";
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettings()
    {
        _capturingHotkey = false;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
    }

    private void OnSettingsBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsInside(e.OriginalSource as DependencyObject, SettingsPanel)) { CloseSettings(); e.Handled = true; }
    }

    private void OnStartupToggle(object sender, RoutedEventArgs e)
    {
        bool on = StartupSwitch.IsChecked == true;
        try { LoginStartup.SetEnabled(on); _settings.LaunchAtLogin = on; _settings.Save(); }
        catch (Exception) { StartupSwitch.IsChecked = !on; }
    }

    private void OnUtilitiesToggle(object sender, RoutedEventArgs e)
    {
        _settings.ShowAllApps = UtilitiesSwitch.IsChecked == true;
        _settings.Save();
        ApplyHidden();
    }

    private void OnWinKeyToggle(object sender, RoutedEventArgs e)
    {
        bool on = WinKeySwitch.IsChecked == true;
        if (WinKeyToggleRequested?.Invoke(on) == false) { WinKeySwitch.IsChecked = false; on = false; }
        _settings.WinKeyOpensLaunchpad = on;
        _settings.Save();
    }

    private void OnIndexToggle(object sender, RoutedEventArgs e)
    {
        bool on = IndexSwitch.IsChecked == true;
        _settings.IndexFiles = on;
        _settings.Save();
        IndexToggleRequested?.Invoke(on);
    }

    private void OnRebuildClick(object sender, RoutedEventArgs e) { RebuildIndexRequested?.Invoke(); CloseSettings(); }
    private void OnRefreshAppsClick(object sender, RoutedEventArgs e) { RefreshAppsRequested?.Invoke(); CloseSettings(); }

    private void OnUnhideClick(object sender, RoutedEventArgs e)
    {
        _settings.HiddenApps.Clear();
        _settings.Save();
        ApplyHidden();
        UnhideButton.Visibility = Visibility.Collapsed;
    }

    private void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyButton.Content = "Press new shortcut…";
        HotkeyButton.Focus();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;
        if (key == Key.Escape) { _capturingHotkey = false; HotkeyButton.Content = _settings.Hotkey; return; }

        var mods = Keyboard.Modifiers;
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (mods == ModifierKeys.None && !isFunctionKey) { HotkeyButton.Content = "Include Ctrl, Alt, Shift or Win"; return; }

        string spec = HotkeyHost.Format(mods, key);
        _capturingHotkey = false;
        if (ChangeHotkey?.Invoke(spec) == true)
        {
            _settings.Hotkey = spec;
            _settings.Save();
            HotkeyButton.Content = spec;
        }
        else
        {
            HotkeyButton.Content = "In use — try another";
            Dispatcher.InvokeAsync(async () => { await Task.Delay(1400); HotkeyButton.Content = _settings.Hotkey; });
        }
    }

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyButton.Content = _settings.Hotkey;
    }

    // ================================================================== off-screen rendering (visual tests)

    /// <summary>Lays the window out at a fixed size and writes it to a PNG over a synthetic backdrop. Nothing is shown on screen.</summary>
    internal bool RenderOpenGroup { get; set; }

    public async Task RenderToPngAsync(string path, int width, int height, string? query, string? filter, int page, bool settings)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        root.UpdateLayout();

        if (page > 0) ScrollTo(page * _cellH * _rows, instant: true);   // --page=N scrolls N screens down
        // Search is always limited to the existing Launchpad items; filter is retained for command compatibility.
        if (query != null) SearchBox.Text = query;
        if (settings) OnSettingsClick(this, new RoutedEventArgs());
        if (RenderOpenGroup && _settings.Groups.Count > 0) EnterGroup(_settings.Groups[0]);
        root.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        await Task.Delay(300);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var back = new DrawingVisual();
        using (var dc = back.RenderOpen())
        {
            var g = new LinearGradientBrush(Color.FromRgb(0x5B, 0x3E, 0x7A), Color.FromRgb(0x1F, 0x5C, 0x8A), 35);
            g.GradientStops.Insert(1, new GradientStop(Color.FromRgb(0xC2, 0x5E, 0x6E), 0.45));
            dc.DrawRectangle(g, null, new Rect(0, 0, width, height));
            // soft blobs, like a blurred wallpaper
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 255, 190, 90)), null, new Point(width * 0.2, height * 0.8), width * 0.25, height * 0.3);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(110, 90, 200, 255)), null, new Point(width * 0.85, height * 0.2), width * 0.2, height * 0.3);
        }
        rtb.Render(back);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ================================================================== helpers

    private static T? FindAncestor<T>(DependencyObject? d, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t && (predicate == null || predicate(t))) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>Animates a double property from its current value; the end value is applied permanently.</summary>
    private static void Animate(DependencyObject target, DependencyProperty dp, double to, int ms,
        IEasingFunction? easing = null, Action? done = null, DependencyProperty? also = null)
    {
        if (target is not IAnimatable anim) return;
        double from = (double)target.GetValue(dp);
        var props = also == null ? new[] { dp } : new[] { dp, also };
        foreach (var p in props)
        {
            target.SetValue(p, to);
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            if (done != null && p == dp) a.Completed += (_, _) => done();
            anim.BeginAnimation(p, a);
        }
    }
}
