using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launchpad.Core.Apps;

namespace Launchpad.App.UI;

/// <summary>
/// Drag-to-rearrange for the app grid. Press an icon and move: it lifts at once and follows the pointer, the others slide
/// aside, and the grid auto-scrolls at the edges. Hover over the middle of another icon for a moment and a group is offered
/// (the target lights up and the dragged icon shrinks into it) - nothing is created until the button is released, and moving
/// away cancels it. Dragging empty space scrolls the grid as before.
/// </summary>
public partial class MainWindow
{
    private const double DragSlop = 6;         // DIPs of movement before a pressed icon starts to drag
    private const int GroupDwellMs = 40;       // over the middle of another icon: the group shows at once (this only filters out a pointer merely passing through)
    private const int SlowReorderDwellMs = 200;
    private const double FastDragSpeed = 700;  // DIPs per second; faster movement reorders immediately

    // grid geometry, set by RebuildPages
    private double _cellW;
    private double _gridLeft, _gridTop;
    private int _totalRows = 1;
    private Dictionary<string, Border> _tileById => _main.Tiles;       // the main grid's tiles (RebuildPages fills these)
    private Dictionary<string, int> _origIndexById => _main.Orig;

    // pressed icon waiting to see whether it's a click or a drag
    private bool _pressed;
    private Border? _holdTile;
    private Point _holdStart;

    // active rearrangement
    private bool _reordering;
    private Border? _dragTile;
    private GridItem? _dragItem;
    private int _dragOrigIndex, _dragIndex;
    private List<GridItem> _dragOrder = new();
    private Point _dragGrab;                  // pointer offset inside the picked-up tile
    private Point _pointerHome;               // pointer in HomeLayer coordinates
    private Point _lastPointerSample;
    private long _lastPointerSampleAt;
    private double _pointerSpeed;
    private readonly Dictionary<string, Vector> _tileShift = new();
    private DispatcherTimer? _edgeTimer;
    private bool _selfTestDrag;               // scripted drags have no physical button held

    // hover bookkeeping: which slot the pointer is over, since when, and the pending group (if any)
    private int _hoverIdx = -1;
    private long _hoverSince;
    private int _hoverReorderDwellMs;
    private bool _hoverCentre;
    private GridItem? _groupTarget;

    private Point SlotPos(int i) => new((i % _cx.Cols) * _cx.CellW, (i / _cx.Cols) * _cx.CellH);

    /// <summary>HomeLayer coordinates to coordinates inside the scrolled grid.</summary>
    private Point ToGrid(Point home) => new(home.X - _cx.Left, home.Y + (_cx.Scrolls ? _scrollY : 0) - _cx.Top);

    private static ScaleTransform? IconScale(Border tile) =>
        (tile.Child as StackPanel)?.Children[0] is FrameworkElement { RenderTransform: ScaleTransform s } ? s : null;

    private static double MsSince(long stamp) => (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;

    private void ResetPointerSpeed(Point pointer)
    {
        _lastPointerSample = pointer;
        _lastPointerSampleAt = Stopwatch.GetTimestamp();
        _pointerSpeed = 0;
    }

    private void SamplePointerSpeed(Point pointer)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPointerSampleAt != 0)
        {
            double seconds = (now - _lastPointerSampleAt) / (double)Stopwatch.Frequency;
            if (seconds > 0) _pointerSpeed = (pointer - _lastPointerSample).Length / seconds;
        }
        _lastPointerSample = pointer;
        _lastPointerSampleAt = now;
    }

    // ------------------------------------------------------------------ pressing

    private void BeginHold(Border tile, Point pointer)
    {
        _holdTile = tile;
        _holdStart = _pointerHome = pointer;
    }

    private void CancelHold() => _holdTile = null;

    // ------------------------------------------------------------------ picking up, moving, dropping

    private void StartReorder()
    {
        if (_holdTile?.Tag is not GridItem item || !_cx.Tiles.ContainsKey(item.Id)) { _holdTile = null; return; }

        Diag.Log($"reorder: start '{item.Name}' slot {_cx.Orig[item.Id]} pointer={_pointerHome}");
        _reordering = true;
        _dragTile = _holdTile;
        _holdTile = null;
        _dragItem = item;
        _dragOrder = _cx.Items.ToList();
        _dragOrigIndex = _dragIndex = _cx.Orig[item.Id];
        _hoverIdx = -1;
        _groupTarget = null;
        _tileShift.Clear();
        foreach (var id in _cx.Tiles.Keys) _tileShift[id] = default;

        var pg = ToGrid(_pointerHome);
        var slot = SlotPos(_dragOrigIndex);
        _dragGrab = new Point(Math.Clamp(pg.X - slot.X, 0, _cx.CellW), Math.Clamp(pg.Y - slot.Y, 0, _cx.CellH));

        // lift: bigger, on top
        System.Windows.Controls.Panel.SetZIndex(_dragTile, 100);
        if (IconScale(_dragTile) is { } s)
            Animate(s, ScaleTransform.ScaleXProperty, 1.16, 140, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }, null, also: ScaleTransform.ScaleYProperty);
        _dragTile.Opacity = 0.94;

        HomeLayer.CaptureMouse();
        _edgeTimer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeTimer.Tick -= OnEdgeTimer;
        _edgeTimer.Tick += OnEdgeTimer;
        _edgeTimer.Start();
        UpdateReorder();
    }

    private void UpdateReorder()
    {
        if (!_reordering || _dragTile == null || _dragItem == null) return;

        var pg = ToGrid(_pointerHome);
        var origin = SlotPos(_dragOrigIndex);

        // the held icon follows the pointer exactly (no easing: it should feel attached)
        var tt = (TranslateTransform)_dragTile.RenderTransform;
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        tt.X = pg.X - _dragGrab.X - origin.X;
        tt.Y = pg.Y - _dragGrab.Y - origin.Y;

        EvaluateHover();
    }

    /// <summary>The slot under the pointer, and whether the pointer is over the middle of the icon sitting there.</summary>
    private (int idx, bool centre) SlotUnderPointer()
    {
        var pg = ToGrid(_pointerHome);
        int col = Math.Clamp((int)Math.Floor(pg.X / _cx.CellW), 0, _cx.Cols - 1);
        int row = Math.Clamp((int)Math.Floor(pg.Y / _cx.CellH), 0, _cx.TotalRows - 1);
        int idx = Math.Clamp(row * _cx.Cols + col, 0, _dragOrder.Count - 1);

        var slot = SlotPos(idx);
        double dx = pg.X - (slot.X + _cx.CellW / 2), dy = pg.Y - (slot.Y + _cx.CellH * 0.42);   // the icon sits a little above the cell's centre
        bool centre = Math.Abs(dx) < _cx.CellW * 0.26 && Math.Abs(dy) < _cx.CellH * 0.28;
        return (idx, centre);
    }

    /// <summary>
    /// Decides what hovering means: over the middle of another app briefly offers a group; entering another slot
    /// immediately moves the others aside. Called on every pointer move and on the 16 ms tick.
    /// </summary>
    private void EvaluateHover()
    {
        if (!_reordering || _dragItem == null) return;

        // inside the group window, carrying an icon beyond its edge means "take it out"
        if (ReferenceEquals(_cx, _panel) && _openGroup != null)
        {
            bool outside = !PanelRect().Contains(_pointerHome);
            SetDropOut(outside);
            if (outside) { SetGroupTarget(null); return; }
        }

        var (idx, centre) = SlotUnderPointer();
        if (idx != _hoverIdx || centre != _hoverCentre)
        {
            _hoverIdx = idx; _hoverCentre = centre; _hoverSince = Stopwatch.GetTimestamp();
            _hoverReorderDwellMs = _pointerSpeed >= FastDragSpeed ? 0 : SlowReorderDwellMs;
            SetGroupTarget(null);
        }
        if (idx == _dragIndex) return;

        var occupant = _dragOrder[idx];
        bool canGroup = centre && !_dragItem.IsGroup && ReferenceEquals(_cx, _main);   // groups can't nest, and you don't group inside a group
        if (canGroup)
        {
            if (MsSince(_hoverSince) >= GroupDwellMs) SetGroupTarget(occupant);
            return;                                      // while in the middle, don't shuffle the target out from under the pointer
        }
        if (MsSince(_hoverSince) >= _hoverReorderDwellMs) MoveHeldTo(idx);
    }

    private void MoveHeldTo(int idx)
    {
        if (idx == _dragIndex) return;
        AppOrdering.Move(_dragOrder, _dragIndex, idx);
        _dragIndex = idx;
        ReflowOthers();
    }

    /// <summary>Shows (or clears) the live group preview: the target icon turns into the group straight away.</summary>
    private void SetGroupTarget(GridItem? target)
    {
        if (ReferenceEquals(target, _groupTarget)) return;
        if (_groupTarget != null) CancelGroupPreview();
        _groupTarget = target;
        if (target != null) ShowGroupPreview(target);
    }

    /// <summary>Slides every other icon to the slot it now occupies.</summary>
    private void ReflowOthers()
    {
        for (int i = 0; i < _dragOrder.Count; i++)
        {
            var item = _dragOrder[i];
            if (ReferenceEquals(item, _dragItem) || !_cx.Tiles.TryGetValue(item.Id, out var tile)) continue;

            var shift = SlotPos(i) - SlotPos(_cx.Orig[item.Id]);
            if (_tileShift.TryGetValue(item.Id, out var current) && current == shift) continue;
            _tileShift[item.Id] = shift;

            var t = (TranslateTransform)tile.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Animate(t, TranslateTransform.XProperty, shift.X, 220, ease);
            Animate(t, TranslateTransform.YProperty, shift.Y, 220, ease);
        }
    }

    /// <summary>16 ms tick while dragging: dwell timing, plus scrolling when the pointer is near the top or bottom edge.</summary>
    private void OnEdgeTimer(object? sender, EventArgs e)
    {
        if (!_reordering) return;
        EvaluateHover();
        if (!_cx.Scrolls || _maxScroll <= 1) return;

        const double zone = 90, maxSpeed = 22;
        double y = _pointerHome.Y, speed = 0;
        if (y < zone) speed = -(zone - Math.Max(0, y)) / zone * maxSpeed;
        else if (y > _viewH - zone) speed = (Math.Min(_viewH, y) - (_viewH - zone)) / zone * maxSpeed;
        if (speed == 0) return;

        double next = Math.Clamp(_scrollY + speed, 0, _maxScroll);
        if (Math.Abs(next - _scrollY) < 0.01) return;
        _scrollTarget = _scrollY = next;
        ApplyScroll();
        UpdateReorder();   // the pointer is now over different content
    }

    private void FinishReorder()
    {
        if (!_reordering || _dragTile == null || _dragItem == null) { _reordering = false; return; }

        _reordering = false;
        _edgeTimer?.Stop();
        HomeLayer.ReleaseMouseCapture();

        var tile = _dragTile;
        var dragged = _dragItem;
        var target = _groupTarget;
        _dragTile = null;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var t = (TranslateTransform)tile.RenderTransform;

        if (_dropOut && dragged.App != null && _openGroup != null)
        {
            // released beyond the group window: the app leaves the group and lands where it was dropped on the grid
            var group = _openGroup;
            int slot = MainSlotUnderPointer();
            SetDropOut(false);
            ExitGroup(refresh: false);
            TakeOutOfGroup(dragged.App, group, slot);
            return;
        }

        if (target != null)
        {
            // released over the live preview: it stays. The grid is rebuilt from the saved state.
            Diag.Log($"reorder: group '{dragged.Name}' -> '{target.Name}'");
            _groupTarget = null;
            ConfirmGroup(dragged, target);
            return;
        }

        // released on a slot the others hadn't made room for yet: do it now
        var (idx, _) = SlotUnderPointer();
        if (idx != _dragIndex) MoveHeldTo(idx);
        SetGroupTarget(null);

        Diag.Log($"reorder: finish from {_dragOrigIndex} to {_dragIndex}");
        bool changed = _dragIndex != _dragOrigIndex;
        var land = SlotPos(_dragIndex) - SlotPos(_dragOrigIndex);

        if (IconScale(tile) is { } s)
            Animate(s, ScaleTransform.ScaleXProperty, 1.0, 180, ease, null, also: ScaleTransform.ScaleYProperty);
        tile.Opacity = 1;
        Animate(t, TranslateTransform.YProperty, land.Y, 180, ease);
        Animate(t, TranslateTransform.XProperty, land.X, 180, ease, () =>
        {
            System.Windows.Controls.Panel.SetZIndex(tile, 0);
            if (changed) CommitOrder();
        });
    }

    /// <summary>Stores the new order and switches the grid to "Custom".</summary>
    private void CommitOrder()
    {
        if (_openGroup != null) CommitGroupOrder();     // inside a group the group keeps its own order
        else CommitOrderIds(_dragOrder.Select(a => a.Id));
    }

    private void CommitOrderIds(IEnumerable<string> visibleIdsInOrder)
    {
        var previous = _settings.AppOrder.Count > 0 ? _settings.AppOrder : _allApps.Select(a => a.Id).ToList();
        _settings.AppOrder = AppOrdering.Commit(visibleIdsInOrder, previous);
        _settings.AppSort = AppOrdering.ToSetting(AppArrangement.Custom);
        Task.Run(_settings.Save);
        ApplyHidden();          // rebuilds the grid in the stored order (identical to what's on screen, so nothing jumps)
        SyncArrangeChips();
    }

    /// <summary>A reorder in progress is dropped where it is (used when the launcher closes mid-drag).</summary>
    private void AbortReorder()
    {
        CancelHold();
        if (_reordering) FinishReorder();
    }

    // ------------------------------------------------------------------ "arrange" setting

    private void OnArrangeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        var mode = AppOrdering.Parse(tag);

        if (mode == AppArrangement.Custom && _settings.AppOrder.Count == 0)
            _settings.AppOrder = _cx.Items.Select(a => a.Id).Concat(_allApps.Select(a => a.Id)).Distinct().ToList();   // start from what's on screen

        _settings.AppSort = AppOrdering.ToSetting(mode);
        Task.Run(_settings.Save);
        ApplyHidden();
    }

    private void SyncArrangeChips()
    {
        var mode = AppOrdering.Parse(_settings.AppSort);
        foreach (var child in ArrangeChips.Children)
            if (child is RadioButton rb) rb.IsChecked = AppOrdering.Parse((string)rb.Tag) == mode;
    }

    // ------------------------------------------------------------------ developer self-test

    /// <summary>
    /// Scripted drags with no real mouse: (1) picks up the second icon and drops it on the sixth slot, (2) picks up the first
    /// icon and holds it over the middle of the fourth to offer a group, captures that frame, then drops. Writes the group-offer
    /// frame to <paramref name="pngPath"/>. Run with <c>--reorder-test out.png</c> and a LAUNCHPAD_DATA folder (it changes the
    /// saved order and groups).
    /// </summary>
    internal async Task<string> SelfTestReorderAsync(string pngPath, int width, int height)
    {
        Width = width; Height = height;
        Cloak(true);
        ShowActivated = false;
        Show();
        await Task.Delay(900);
        UpdateLayout();
        if (_cx.Items.Count < 8) return "not enough apps";

        string Names() => string.Join(", ", _cx.Items.Take(8).Select(a => a.Name));
        Point Center(int slot, double yFactor = 0.5) { var p = SlotPos(slot); return new Point(_cx.Left + p.X + _cx.CellW / 2, _cx.Top + p.Y + _cx.CellH * yFactor - _scrollY); }
        var report = new System.Text.StringBuilder();

        // ---- 1. plain reorder
        _selfTestDrag = true;
        string before = Names();
        var picked = _cx.Items[1];
        _pressed = true; _pointerHome = Center(1);
        _holdTile = _cx.Tiles[picked.Id];
        StartReorder();
        await Task.Delay(200);
        _pointerHome = Center(5, 0.9);       // low in the slot: the edge, not the middle
        UpdateReorder();
        await Task.Delay(500);
        _pressed = false;
        FinishReorder();
        await Task.Delay(700);
        report.AppendLine($"reorder: before: {before}\nreorder: after:  {Names()}\nreorder: '{picked.Name}' now at index {_cx.Items.FindIndex(i => i.Id == picked.Id)} (sort={_settings.AppSort})");

        // ---- 2. group: hold over the middle of another icon, capture the offer, drop
        var dragged = _cx.Items[0];
        var target = _cx.Items[3];
        _pressed = true; _pointerHome = Center(0);
        _holdTile = _cx.Tiles[dragged.Id];
        StartReorder();
        await Task.Delay(200);
        _pointerHome = Center(3, 0.42);      // dead centre of the icon
        UpdateReorder();
        await Task.Delay(120);
        report.AppendLine($"group: 120 ms over the middle of '{_groupTarget?.Name ?? "none"}': preview shows at once (target tile now {(_previewTile != null ? "a group preview" : "unchanged")})");
        await Task.Delay(500);
        report.AppendLine($"group: after 620 ms pending={_groupTarget?.Name ?? "none"}");

        var root = (FrameworkElement)Content;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(pngPath)) enc.Save(fs);

        // moving away must cancel it
        _pointerHome = Center(3, 0.98);
        UpdateReorder();
        await Task.Delay(100);
        report.AppendLine($"group: after moving to the edge pending={_groupTarget?.Name ?? "none"} (expected none)");
        _pointerHome = Center(3, 0.42);
        UpdateReorder();
        await Task.Delay(600);

        _pressed = false;
        FinishReorder();
        await Task.Delay(900);
        var g = _settings.Groups.FirstOrDefault();
        report.AppendLine($"group: dropped '{dragged.Name}' on '{target.Name}' -> groups={_settings.Groups.Count}" +
            (g == null ? "" : $" [{g.Name}: {string.Join(", ", g.AppIds.Select(id => _allApps.FirstOrDefault(a => a.Id == id)?.Name))}]"));
        report.AppendLine($"grid now: {Names()}");

        // ---- 3. open the group window, then drag an app out of it
        if (g != null)
        {
            EnterGroup(g);
            await Task.Delay(600);
            report.AppendLine($"group window: open={_openGroup != null}; it shows {_panel.Items.Count} apps ({string.Join(", ", _panel.Items.Select(i => i.Name))}); the main grid behind it still has {_main.Items.Count} items");
            var rtb2 = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb2.Render((FrameworkElement)Content);
            var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb2));
            using (var fs2 = File.Create(pngPath + ".group.png")) enc2.Save(fs2);

            var member = _panel.Items[0];
            _cx = _panel;
            RefreshPanelOrigin();
            _pressed = true;
            _pointerHome = new Point(_panel.Left + _panel.CellW / 2, _panel.Top + _panel.CellH / 2);
            _holdTile = _panel.Tiles[member.Id];
            StartReorder();
            await Task.Delay(150);
            _pointerHome = new Point(300, 330);          // well outside the window, over the grid
            UpdateReorder();
            await Task.Delay(300);
            report.AppendLine($"drag '{member.Name}' beyond the window: dropOut={_dropOut} (window fades back)");
            _pressed = false;
            FinishReorder();
            await Task.Delay(900);
            report.AppendLine($"after drop: groups={_settings.Groups.Count}, window open={_openGroup != null}, grid: {Names()}");
        }
        return report.ToString();
    }
}
