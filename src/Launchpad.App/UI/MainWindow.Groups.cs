using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Launchpad.App.Apps;
using Launchpad.Core.Apps;

namespace Launchpad.App.UI;

/// <summary>One cell of the launcher grid: either a single app or a group of apps.</summary>
public sealed class GridItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public AppItem? App { get; init; }
    public AppGroup? Group { get; init; }
    public List<AppItem> Members { get; init; } = new();
    public int Launches { get; init; }
    public bool IsGroup => Group != null;
}

/// <summary>
/// Groups: the grid items, the folder tile, the live "this becomes a group" preview while dragging, and entering a group
/// (the grid simply shows the group's apps, with a back button and an editable name, so there's no separate window).
/// </summary>
public partial class MainWindow
{
    private List<GridItem> _items = new();
    private AppGroup? _openGroup;          // non-null while the group window is open
    private double _iconSize = 120;        // icon size of the current grid layout

    /// <summary>
    /// Normally: apps that aren't in a group plus one item per group, in the user's chosen order.
    /// Inside a group: just that group's apps, in the group's own order.
    /// </summary>
    private List<GridItem> BuildItems(List<AppItem> shown)
    {
        var byId = shown.ToDictionary(a => a.Id);
        int Launches(AppItem a) => _settings.Launches.GetValueOrDefault(a.Id);

        var grouped = AppGroups.GroupedAppIds(_settings.Groups);
        var items = new List<GridItem>();
        foreach (var a in shown)
            if (!grouped.Contains(a.Id))
                items.Add(new GridItem { Id = a.Id, Name = a.Name, App = a, Launches = Launches(a) });

        foreach (var g in _settings.Groups)
        {
            var members = g.AppIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (members.Count == 0) continue;
            items.Add(new GridItem { Id = g.Id, Name = g.Name, Group = g, Members = members, Launches = members.Sum(Launches) });
        }

        return AppOrdering.Apply(items, AppOrdering.Parse(_settings.AppSort), _settings.AppOrder, i => i.Id, i => i.Name, i => i.Launches);
    }

    /// <summary>During search, show matching group members as regular app tiles instead of collapsing them into folders.</summary>
    private List<GridItem> BuildStandaloneItems(IEnumerable<AppItem> apps)
    {
        var items = apps.Select(app => new GridItem
        {
            Id = app.Id,
            Name = app.Name,
            App = app,
            Launches = _settings.Launches.GetValueOrDefault(app.Id),
        }).ToList();
        return AppOrdering.Apply(items, AppOrdering.Parse(_settings.AppSort), _settings.AppOrder, i => i.Id, i => i.Name, i => i.Launches);
    }

    private Border CreateGridTile(GridItem item, double iconSize, double cellW, double cellH)
    {
        if (item.App != null)
        {
            var tile = CreateTile(item.App, iconSize, cellW, cellH, compact: false, highlight: false);
            tile.Tag = item;
            tile.Focusable = true;
            return tile;
        }
        var groupTile = CreateGroupTile(item, iconSize, cellW, cellH);
        groupTile.Focusable = true;
        return groupTile;
    }

    // ------------------------------------------------------------------ the folder icon and tile

    /// <summary>The folder "icon": a glassy rounded plate with the members' icons inside (2x2, or 3x3 beyond four).</summary>
    private static Grid BuildGroupIconArea(IReadOnlyList<AppItem> members, double iconSize)
    {
        double plateSize = iconSize * 176.0 / 224.0;     // the same footprint as an icon's tile
        var plate = new Border
        {
            Width = plateSize, Height = plateSize, CornerRadius = new CornerRadius(plateSize * 0.2237),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Color.FromArgb(0x48, 255, 255, 255), Color.FromArgb(0x20, 255, 255, 255), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)), BorderThickness = new Thickness(1),
        };

        int dim = members.Count <= 4 ? 2 : 3;
        var minis = new UniformGrid { Rows = dim, Columns = dim, Margin = new Thickness(plateSize * (dim == 2 ? 0.10 : 0.07)) };
        foreach (var m in members.Take(dim * dim))
        {
            var img = new Image
            {
                Source = m.Icon, Margin = new Thickness(plateSize * 0.005), Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(dim == 2 ? 1.12 : 1.22, dim == 2 ? 1.12 : 1.22),   // the icon canvas has padding for its shadow
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            minis.Children.Add(img);
        }
        plate.Child = minis;

        var area = new Grid
        {
            Width = iconSize, Height = iconSize, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1), IsHitTestVisible = false,
        };
        area.Children.Add(plate);
        return area;
    }

    private static Grid BuildLabel(string text, double iconSize, double cellW)
    {
        double maxText = Math.Max(40, cellW - 14);
        var label = new Grid { Margin = new Thickness(0, -iconSize * 0.07, 0, 0), IsHitTestVisible = false };
        label.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13.5, Foreground = Brushes.Black, Opacity = 0.5, MaxWidth = maxText,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1.5, 0, 0),
        });
        label.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13.5, Foreground = Brushes.White, MaxWidth = maxText,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        });
        return label;
    }

    private Border CreateGroupTile(GridItem item, double iconSize, double cellW, double cellH)
    {
        var area = BuildGroupIconArea(item.Members, iconSize);
        var scale = (ScaleTransform)area.RenderTransform;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(area);
        stack.Children.Add(BuildLabel(item.Name, iconSize, cellW));

        var tile = new Border
        {
            Width = cellW, Height = cellH, Child = stack, Tag = item, Cursor = Cursors.Hand, Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(16), RenderTransform = new TranslateTransform(),
        };
        System.Windows.Automation.AutomationProperties.SetName(tile, $"{item.Name}, group of {item.Members.Count} apps");

        tile.MouseEnter += (_, _) => { if (!_reordering) Animate(scale, ScaleTransform.ScaleXProperty, 1.07, 130, null, null, also: ScaleTransform.ScaleYProperty); };
        tile.MouseLeave += (_, _) => { if (!_reordering) Animate(scale, ScaleTransform.ScaleXProperty, 1.0, 160, null, null, also: ScaleTransform.ScaleYProperty); };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_suppressTileClick || _dragging || _reordering) { e.Handled = true; return; }
            e.Handled = true;
            EnterGroup(item.Group!);
        };
        tile.MouseRightButtonUp += (_, e) => { ShowGroupMenu(tile, item); e.Handled = true; };
        return tile;
    }

    private void ShowGroupMenu(FrameworkElement anchor, GridItem item)
    {
        var g = item.Group!;
        var menu = new ContextMenu { PlacementTarget = anchor };
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => EnterGroup(g);
        var rename = new MenuItem { Header = "Rename" };
        rename.Click += (_, _) => EnterGroup(g, rename: true);
        var ungroup = new MenuItem { Header = "Ungroup" };
        ungroup.Click += (_, _) => Ungroup(g);
        menu.Items.Add(open); menu.Items.Add(rename); menu.Items.Add(new Separator()); menu.Items.Add(ungroup);
        AddLaunchpadSettingsMenuItem(menu);
        OpenTrackedContextMenu(menu);
    }

    // ------------------------------------------------------------------ live "becomes a group" preview

    private Border? _previewTile;
    private FrameworkElement? _previewOriginalArea;
    private readonly List<(TextBlock tb, string text)> _previewOriginalLabels = new();

    /// <summary>While dragging over another app: turn the target into the group right now (the real result), so it can be judged before letting go.</summary>
    private void ShowGroupPreview(GridItem target)
    {
        if (_dragItem?.App == null || !_tileById.TryGetValue(target.Id, out var tile) || tile.Child is not StackPanel stack) return;

        _previewTile = tile;
        _previewOriginalArea = stack.Children[0] as FrameworkElement;
        var members = new List<AppItem>(target.IsGroup ? target.Members : new List<AppItem> { target.App! }) { _dragItem.App };
        var area = BuildGroupIconArea(members, _iconSize);
        stack.Children.RemoveAt(0);   // (assigning by index throws in WPF: remove, then insert)
        stack.Children.Insert(0, area);

        // the label becomes the group's name (a new group is simply called "Group")
        _previewOriginalLabels.Clear();
        if (stack.Children.Count > 1 && stack.Children[1] is Grid label)
            foreach (var tb in label.Children.OfType<TextBlock>())
            {
                _previewOriginalLabels.Add((tb, tb.Text));
                if (!target.IsGroup) tb.Text = AppGroups.DefaultName;
            }

        tile.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
        var scale = (ScaleTransform)area.RenderTransform;
        scale.ScaleX = scale.ScaleY = 0.82;
        Animate(scale, ScaleTransform.ScaleXProperty, 1.08, 200, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }, null, also: ScaleTransform.ScaleYProperty);

        if (_dragTile != null) Animate(_dragTile, OpacityProperty, 0, 110);   // the dragged icon is "inside" the group now
    }

    /// <summary>Dragged away: put the target back to being a plain icon and bring the dragged icon back.</summary>
    private void CancelGroupPreview()
    {
        if (_previewTile?.Child is StackPanel stack && _previewOriginalArea != null)
        {
            stack.Children.RemoveAt(0);
            stack.Children.Insert(0, _previewOriginalArea);
            foreach (var (tb, text) in _previewOriginalLabels) tb.Text = text;
            _previewTile.Background = Brushes.Transparent;
            if (IconScale(_previewTile) is { } s) { s.ScaleX = s.ScaleY = 1.0; }
        }
        _previewTile = null;
        _previewOriginalArea = null;
        _previewOriginalLabels.Clear();
        if (_dragTile != null) Animate(_dragTile, OpacityProperty, 0.94, 120);
    }

    // ------------------------------------------------------------------ confirming a drop

    /// <summary>The button was released over the previewed group: it stays. Create it (or add to the target group) and save.</summary>
    private void ConfirmGroup(GridItem dragged, GridItem target)
    {
        _previewTile = null;               // the grid is about to be rebuilt from the saved state
        _previewOriginalArea = null;
        if (dragged.App == null) return;   // groups never nest

        AppGroup g;
        if (target.IsGroup)
        {
            g = target.Group!;
            AppGroups.AddTo(_settings.Groups, g, dragged.Id);
        }
        else g = AppGroups.Create(_settings.Groups, target.Id, dragged.Id);

        var order = AppGroups.OrderAfterGrouping(_dragOrder.Select(i => i.Id), dragged.Id, target.Id, g.Id);
        CommitOrderIds(order);             // also saves the groups
    }

    private void Ungroup(AppGroup g)
    {
        // the members take the group's place in the order, in their group order
        var order = _settings.AppOrder.Count > 0 ? _settings.AppOrder : _items.Select(i => i.Id).ToList();
        int at = order.IndexOf(g.Id);
        if (at >= 0) { order.RemoveAt(at); order.InsertRange(at, g.AppIds); }
        _settings.AppOrder = order.Distinct().ToList();
        AppGroups.Dissolve(_settings.Groups, g.Id);
        if (_openGroup == g) ExitGroup(refresh: false);
        Task.Run(_settings.Save);
        ApplyHidden();
    }

    // ------------------------------------------------------------------ the group window: a frosted square over the blurred grid

    /// <summary>Geometry and tiles of one draggable grid. The main grid and the group window each have one; drags use whichever they started in.</summary>
    private sealed class GridCtx
    {
        public List<GridItem> Items = new();
        public Dictionary<string, Border> Tiles = new();
        public Dictionary<string, int> Orig = new();
        public int Cols = 1, TotalRows = 1;
        public double CellW, CellH, Left, Top;
        public bool Scrolls;   // the main grid scrolls under the pointer; the group window doesn't
    }

    private readonly GridCtx _main = new() { Scrolls = true };
    private GridCtx _panel = new();
    private GridCtx? _cxField;
    private GridCtx _cx { get => _cxField ?? _main; set => _cxField = value; }

    private bool _dropOut;
    private const string GroupDefaultHint = "Drag an app out of the window to take it out of the group";

    /// <summary>Opens the group window: the grid behind dims and blurs, and a frosted square holds the group's apps.</summary>
    private void EnterGroup(AppGroup g, bool rename = false)
    {
        SearchBox.Text = "";
        _openGroup = g;
        GroupNameBox.Text = g.Name;
        BuildPanel();

        GroupHost.Visibility = Visibility.Visible;
        GroupHost.Opacity = 0;
        GroupScale.ScaleX = GroupScale.ScaleY = 0.9;
        Animate(WindowDim, OpacityProperty, 1, 220);
        Animate(PagerMask, OpacityProperty, 0.55, 220);     // the grid behind recedes
        Animate(SearchHost, OpacityProperty, 0.45, 220);
        Animate(GroupHost, OpacityProperty, 1, 170);
        Animate(GroupScale, ScaleTransform.ScaleXProperty, 1, 260, new QuinticEase { EasingMode = EasingMode.EaseOut }, null, also: ScaleTransform.ScaleYProperty);

        if (rename) { GroupNameBox.Focus(); GroupNameBox.SelectAll(); }
        else Keyboard.ClearFocus();
    }

    private void ExitGroup(bool refresh = true)
    {
        if (_openGroup == null) return;
        var g = _openGroup;
        CommitGroupName(g);
        _openGroup = null;
        _cx = _main;
        SetDropOut(false);

        Animate(GroupHost, OpacityProperty, 0, 130, null, () => { if (_openGroup == null) GroupHost.Visibility = Visibility.Collapsed; });
        Animate(WindowDim, OpacityProperty, 0, 180);
        Animate(PagerMask, OpacityProperty, 1, 200);
        Animate(SearchHost, OpacityProperty, 1, 200);

        Keyboard.Focus(SearchBox);
        if (refresh) ApplyHidden();
    }

    /// <summary>(Re)builds the tiles inside the group window from the group's current members.</summary>
    private void BuildPanel()
    {
        if (_openGroup == null) return;
        var hidden = new HashSet<string>(_settings.HiddenApps);
        var members = _openGroup.AppIds.Select(id => _allApps.FirstOrDefault(a => a.Id == id))
            .Where(a => a != null && !hidden.Contains(a!.Id)).Select(a => a!).ToList();
        if (members.Count == 0) { ExitGroup(refresh: false); return; }

        double H = Math.Max(400, HomeLayer.ActualHeight);
        int n = members.Count;
        int cols = n <= 4 ? 2 : n <= 9 ? 3 : n <= 16 ? 4 : 5;
        int rows = (int)Math.Ceiling(n / (double)cols);
        // A big square window whose cells fill it, so the icons spread towards the corners instead of huddling in the middle.
        double side = Math.Clamp(cols * 200 + 90, 470, Math.Min(H * 0.86, 860));
        double availW = side - 56, availH = side - 130;       // minus side margins / the name above and the hint below
        double cw = Math.Min(availW / cols, 230), ch = Math.Min(availH / rows, 214);
        double icon = Math.Clamp(Math.Min(cw * 0.64, ch * 0.62), 54, 136);

        GroupCanvas.Children.Clear();
        GroupCanvas.Width = cols * cw;
        GroupCanvas.Height = rows * ch;
        var ctx = new GridCtx { Cols = cols, CellW = cw, CellH = ch, TotalRows = rows, Scrolls = false };
        for (int i = 0; i < n; i++)
        {
            var app = members[i];
            var item = new GridItem { Id = app.Id, Name = app.Name, App = app, Launches = _settings.Launches.GetValueOrDefault(app.Id) };
            var tile = CreateTile(app, icon, cw, ch, compact: false, highlight: false, contextMenu: ShowGroupMemberMenu);
            tile.Tag = item;
            Canvas.SetLeft(tile, (i % cols) * cw);
            Canvas.SetTop(tile, (i / cols) * ch);
            GroupCanvas.Children.Add(tile);
            ctx.Items.Add(item);
            ctx.Tiles[app.Id] = tile;
            ctx.Orig[app.Id] = i;
        }
        _panel = ctx;
        if (ReferenceEquals(_cxField, null) == false && _cxField != _main) _cxField = _panel;

        GroupHost.Width = GroupHost.Height = side;

        // Frosted glass: the window's own backdrop is a blurred copy of the grid region right behind it. Everything outside the
        // window stays sharp. The copy is larger than the window and clipped to its rounded shape so the blur has no soft edge.
        const double pad = 90;
        double hx = (HomeLayer.ActualWidth - side) / 2, hy = (HomeLayer.ActualHeight - side) / 2;
        GroupBlurHost.Clip = new RectangleGeometry(new Rect(0, 0, side, side), 46, 46);
        GroupBlurFill.Background = new VisualBrush(PagerMask)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(hx - pad, hy - pad, side + 2 * pad, side + 2 * pad),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
        };
        GroupHint.Text = GroupDefaultHint;
    }

    /// <summary>Where the group window's grid sits, in HomeLayer coordinates (needed to map the pointer to a slot).</summary>
    private void RefreshPanelOrigin()
    {
        UpdateLayout();
        var p = GroupCanvas.TranslatePoint(new Point(0, 0), HomeLayer);
        _panel.Left = p.X;
        _panel.Top = p.Y;
    }

    private Rect PanelRect()
    {
        var tl = GroupHost.TranslatePoint(new Point(0, 0), HomeLayer);
        var r = new Rect(tl, GroupHost.RenderSize);
        r.Inflate(4, 4);
        return r;
    }

    private void CommitGroupName(AppGroup g)
    {
        string name = GroupNameBox.Text.Trim();
        if (name.Length == 0) name = AppGroups.DefaultName;
        if (name == g.Name) return;
        g.Name = name;
        Task.Run(_settings.Save);
    }

    private void OnGroupNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { if (_openGroup != null) CommitGroupName(_openGroup); Keyboard.Focus(SearchBox); e.Handled = true; }
        else if (e.Key == Key.Escape) { if (_openGroup != null) GroupNameBox.Text = _openGroup.Name; Keyboard.Focus(SearchBox); e.Handled = true; }
    }

    private void OnGroupNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_openGroup != null) CommitGroupName(_openGroup);
    }

    private void ShowGroupMemberMenu(Border anchor, AppItem app)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => LaunchApp(app);
        var remove = new MenuItem { Header = "Take out of group" };
        remove.Click += (_, _) => { if (_openGroup != null) TakeOutOfGroup(app, _openGroup, null); };
        menu.Items.Add(open); menu.Items.Add(new Separator()); menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    /// <summary>Moves an app from a group back to the main grid, next to the group or at a chosen slot.</summary>
    private void TakeOutOfGroup(AppItem app, AppGroup g, int? placeAt)
    {
        var freed = AppGroups.RemoveApp(_settings.Groups, app.Id);
        bool dissolved = !_settings.Groups.Contains(g);

        var order = _settings.AppOrder.Count > 0 ? _settings.AppOrder.ToList() : _allApps.Select(a => a.Id).ToList();
        int slot = order.IndexOf(g.Id);
        order.RemoveAll(freed.Contains);
        slot = order.IndexOf(g.Id) >= 0 ? order.IndexOf(g.Id) : slot;
        if (dissolved && slot >= 0)
        {
            order.Remove(g.Id);
            order.InsertRange(Math.Min(slot, order.Count), freed);
        }
        else if (slot >= 0) order.InsertRange(Math.Min(slot + 1, order.Count), freed);
        else order.AddRange(freed);
        _settings.AppOrder = order.Distinct().ToList();
        Task.Run(_settings.Save);

        if (dissolved && _openGroup == g) ExitGroup(refresh: false);
        ApplyHidden();    // also rebuilds the group window if it's still open

        if (placeAt is int target)
        {
            // dropped onto a particular slot of the main grid
            var ids = _items.Select(i => i.Id).ToList();
            int from = ids.IndexOf(app.Id);
            if (from >= 0)
            {
                AppOrdering.Move(ids, from, Math.Clamp(target, 0, ids.Count - 1));
                CommitOrderIds(ids);
            }
        }
    }

    /// <summary>Reorder inside the group window: the group keeps its own order.</summary>
    private void CommitGroupOrder()
    {
        if (_openGroup == null) return;
        var visible = _dragOrder.Select(i => i.Id).ToList();
        _openGroup.AppIds = visible.Concat(_openGroup.AppIds.Where(id => !visible.Contains(id))).ToList();
        Task.Run(_settings.Save);
        ApplyHidden();
    }

    /// <summary>The main-grid slot under the pointer (used when an app is dragged out of the group window onto the grid).</summary>
    private int MainSlotUnderPointer()
    {
        var pg = new Point(_pointerHome.X - _main.Left, _pointerHome.Y + _scrollY - _main.Top);
        int col = Math.Clamp((int)Math.Floor(pg.X / _main.CellW), 0, _main.Cols - 1);
        int row = Math.Clamp((int)Math.Floor(pg.Y / _main.CellH), 0, _main.TotalRows - 1);
        return row * _main.Cols + col;
    }

    /// <summary>While an app is dragged beyond the group window, the window fades back to show it will be left behind.</summary>
    private void SetDropOut(bool on)
    {
        if (_dropOut == on) return;
        _dropOut = on;
        GroupPanel.Opacity = on ? 0.3 : 1;
        GroupBlurHost.Opacity = on ? 0.3 : 1;
        GroupHint.Text = on ? "Release to take it out of the group" : GroupDefaultHint;
    }
}
