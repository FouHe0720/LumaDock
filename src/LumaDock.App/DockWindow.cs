using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.App;

public sealed class DockWindow : Window
{
    private readonly AppController controller;
    private readonly Grid body = new();
    private readonly Border tint = new() { IsHitTestVisible = false };
    private readonly DockPlate plate = new();
    private readonly DockPlate finish = new() { FinishOnly = true };
    private readonly GlassRim highlight = new() { Margin = new Thickness(1), DrawStaticEdge = false };
    private readonly LiquidGlassSurface liquid;
    private readonly StackPanel items = new();
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
    private readonly Canvas dropOverlay = new() { IsHitTestVisible = false };
    private readonly Border insertionCue = new() { CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1) };
    private sealed class Tile
    {
        public AppEntry? App; public IReadOnlyList<AppEntry> Variants = []; public required Button Button; public required FrameworkElement Visual;
        public required ScaleTransform Scale; public required TranslateTransform Move;
        public required Border Halo; public required Border Dot; public required DockReflection Reflection;
        public bool Running, Active;
        public double TargetScale = 1, TargetAlong, TargetLift, TargetHalo;
    }
    private readonly List<Tile> tiles = [];
    private readonly DispatcherTimer hover = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private long hoverTick;
    private Point lastPointer = new(double.NaN, double.NaN);
    private double bodyThickness, flight, endReserve, radius;
    private readonly DispatcherTimer hideDelay = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool tucked, dragging, rebuilding, rebuildAfterDrag, closed;
    private Point dragStart;
    private IntPtr handle;
    private HwndSource? source;
    private int generation;
    private double fullWidth, fullHeight;
    private bool Vertical => controller.State.Settings.Position is "Left" or "Right";

    public DockWindow(AppController controller)
    {
        this.controller = controller;
        liquid = new LiquidGlassSurface(this, controller);
        Title = "LumaDock"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        // Only the small Dock is per-pixel transparent: magnified icons can leave
        // the plate without painting an Acrylic rectangle in their flight area.
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = false;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        scroll.Content = items;
        body.Children.Add(liquid); body.Children.Add(tint); body.Children.Add(highlight);
        dropOverlay.Children.Add(insertionCue);
        var surface = new Grid(); surface.Children.Add(plate); surface.Children.Add(body); surface.Children.Add(finish); surface.Children.Add(scroll); surface.Children.Add(dropOverlay); Content = surface;
        body.SizeChanged += (_, _) => UpdateContour();
        hover.Tick += HoverTick;
        PreviewMouseWheel += (_, e) => { if (Vertical) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); else scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); e.Handled = true; };
        AllowDrop = true;
        PreviewDragEnter += ShowInsertion;
        PreviewDragOver += ShowInsertion;
        PreviewDrop += DropItems;
        DragLeave += (_, e) => { var p = e.GetPosition(this); if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) ClearInsertion(); };
        MouseMove += Magnify;
        MouseEnter += (_, _) => { hideDelay.Stop(); Reveal(); };
        MouseLeave += (_, _) => { lastPointer = new(double.NaN, double.NaN); highlight.ReleasePointer(); ResetMagnification(); if (controller.State.Settings.AutoHide) hideDelay.Start(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopHover(); };
        hideDelay.Tick += HideTick;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            NativeWindowHelper.ConfigureDock(handle);
            source = HwndSource.FromHwnd(handle); source?.AddHook(WndProc);
        };
        Loaded += (_, _) => { UpdateSettings(); if (controller.State.Settings.AutoHide) hideDelay.Start(); };
        ContentRendered += (_, _) => controller.RecordDockFrame();
        Closed += (_, _) => { closed = true; ClearInsertion(); StopHover(); hover.Tick -= HoverTick; highlight.StopMotion(); hideDelay.Stop(); hideDelay.Tick -= HideTick; source?.RemoveHook(WndProc); generation++; };
        ContextMenu = new ContextMenu();
        ContextMenu.Closed += (_, _) => ScheduleHide();
        AddMenu(ContextMenu, "打开应用", () => controller.ShowCenter());
        AddMenu(ContextMenu, "添加应用…", AddFile);
        AddMenu(ContextMenu, "偏好设置", () => controller.ShowCenter("Settings"));
        AddMenu(ContextMenu, "退出 LumaDock", controller.Exit);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x2E0) Dispatcher.BeginInvoke(() => { Rebuild(); liquid.Refresh(); });
        return IntPtr.Zero;
    }

    public void UpdateSettings()
    {
        var s = controller.State.Settings;
        var optical = s.LiquidGlass && !s.PowerSaver && !SystemParameters.HighContrast;
        var baseColor = ((SolidColorBrush)FindResource("OpaqueSurfaceBrush")).Color;
        var opacity = Math.Clamp((controller.State.Settings.Opacity - .02) / .98, 0, 1);
        var realtime = optical && s.LiveGlass;
        // Live mode uses one translucent plate; reduced/static modes retain a solid fallback.
        baseColor.A = realtime ? (byte)Math.Round(4 + 168 * Math.Pow(s.Opacity, 1.65)) : (byte)255;
        if (SystemParameters.HighContrast) baseColor = SystemColors.WindowColor;
        plate.Fill = new SolidColorBrush(baseColor);
        tint.Background = optical && !realtime ? new SolidColorBrush(Color.FromArgb((byte)(5 + 72 * opacity * opacity * opacity), baseColor.R, baseColor.G, baseColor.B)) : Brushes.Transparent;
        highlight.AccentColor = Theme.AccentColor;
        highlight.MotionStrength = controller.State.Settings.GlassFlowStrength;
        highlight.MotionEnabled = controller.State.Settings.GlassMotion && controller.State.Settings.LiquidGlass && !controller.ReducedMotion;
        tucked = false; scroll.Visibility = Visibility.Visible; body.Visibility = Visibility.Visible; finish.Visibility = Visibility.Visible;
        highlight.Visibility = Visibility.Visible;
        Rebuild();
        if (controller.State.Settings.AutoHide && !IsMouseOver) hideDelay.Start(); else hideDelay.Stop();
        liquid.Refresh();
    }

    public void Rebuild()
    {
        if (closed) return;
        if (dragging) { rebuildAfterDrag = true; return; }
        if (rebuilding) return;
        rebuilding = true;
        try
        {
            StopHover();
            ClearInsertion();
            var version = ++generation;
            tiles.Clear(); items.Children.Clear(); items.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;
            scroll.HorizontalScrollBarVisibility = Vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            scroll.VerticalScrollBarVisibility = Vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            var s = controller.State.Settings;
            bodyThickness = s.IconSize + (s.DockShape == "Rail" ? 36 : 42);
            flight = Math.Ceiling(s.IconSize * s.AnimationStrength * .80) + 8;
            endReserve = Math.Ceiling(s.IconSize * s.AnimationStrength * .66) + 9;
            radius = s.DockShape switch { "Rail" => Math.Min(17, 5 + s.CornerRadius * .4), "Pebble" => Math.Min(bodyThickness / 2, 17 + s.CornerRadius * .7), _ => bodyThickness / 2 };
            var launcher = AddTile(null, LaunchpadMark(s.IconSize * .69));
            launcher.Button.Click += (_, _) => controller.ShowCenter();
            var resting = (s.Position is "Bottom" or "Right" ? flight : 12) + bodyThickness / 2 - (s.Position is "Bottom" or "Right" ? 2 : -2);
            var groups = ApplicationPresentation.Group(controller.State.Apps.Where(a => !a.Hidden && a.IsAvailable));
            var groupById = groups.SelectMany(group => group.Entries.Select(app => (app.Id, group)))
                .ToDictionary(pair => pair.Id, pair => pair.group, StringComparer.OrdinalIgnoreCase);
            var displayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (controller.State.PinnedIds.Any(groupById.ContainsKey))
                items.Children.Add(new Border { Width = Vertical ? 25 : 1, Height = Vertical ? 1 : 25, Margin = Vertical ? new Thickness(resting - 12.5, 9, 0, 9) : new Thickness(9, resting - 12.5, 9, 0), VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Top, HorizontalAlignment = Vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center, Background = (Brush)FindResource("BorderBrush") });
            foreach (var id in controller.State.PinnedIds.ToArray())
            {
                if (!groupById.TryGetValue(id, out var group) || !displayed.Add(group.Representative.Id)) continue;
                // Preserve the first saved pin's launch mode, even when the catalogue
                // representative has different arguments. Other modes remain reachable.
                var app = group.Entries.First(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var image = new System.Windows.Controls.Image { Width = s.IconSize, Height = s.IconSize, Stretch = Stretch.Uniform, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var tile = AddTile(app, image); tile.Variants = group.Entries; var button = tile.Button;
                button.Click += (_, _) => controller.Launch(app);
                button.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
                button.PreviewMouseMove += (_, e) => BeginReorder(button, app, e);
                button.ContextMenu = new ContextMenu();
                button.ContextMenu.Closed += (_, _) => ScheduleHide();
                AddMenu(button.ContextMenu, "打开 / 切换到应用", () => controller.Launch(app));
                if (group.Entries.Count > 1)
                {
                    var modes = new MenuItem { Header = "启动方式" };
                    for (var index = 0; index < group.Entries.Count; index++)
                    {
                        var variant = group.Entries[index];
                        var parameters = string.IsNullOrWhiteSpace(variant.Arguments) ? "默认启动" : variant.Arguments;
                        if (parameters.Length > 64) parameters = parameters[..61] + "…";
                        var source = variant.Source switch { "StartMenu" => "开始菜单", "Desktop" => "桌面", "AppsFolder" => "Windows 注册", _ => "手动入口" };
                        var item = new MenuItem { Header = $"{index + 1}. {source} · {parameters}", ToolTip = $"{variant.LaunchPath}\n{variant.Arguments}\n{variant.WorkingDirectory}" };
                        item.Click += (_, _) => controller.LaunchNewInstance(variant); modes.Items.Add(item);
                    }
                    button.ContextMenu.Items.Add(modes);
                }
                AddMenu(button.ContextMenu, "更换图标…", () => controller.ChooseIcon(app, this));
                if (!string.IsNullOrEmpty(app.CustomIconPath)) AddMenu(button.ContextMenu, "恢复原图标", () => controller.SetCustomIcon(app, ""));
                AddMenu(button.ContextMenu, "从 Dock 移除", () => controller.Unpin(app));
                _ = LoadIcon(image, app, version);
            }
            RefreshWindows(); Reposition();
        }
        finally { rebuilding = false; }
    }

    private async Task LoadIcon(System.Windows.Controls.Image image, AppEntry app, int version)
    {
        try
        {
            var s = controller.State.Settings;
            var pixels = (int)Math.Ceiling(s.IconSize * (1 + s.AnimationStrength * .9) * VisualTreeHelper.GetDpi(this).DpiScaleX / 16) * 16;
            var icon = await controller.GetIconAsync(app, Math.Clamp(pixels, 32, 256));
            if (generation != version || closed) return;
            var vibrancy = SystemParameters.HighContrast ? 0 : s.DockIconVibrancy;
            var colored = icon == null || vibrancy <= 0 ? icon : await Task.Run(() => DockIconColor.Enhance(icon, vibrancy));
            if (generation == version && !closed) image.Source = colored;
        }
        catch (Exception ex) { Storage.Log.Write("Icon failed " + app.Name, ex); }
    }

    private static readonly ControlTemplate TileTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Grid ClipToBounds="False" Background="{TemplateBinding Background}">
            <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsPressed" Value="True"><Setter Property="Opacity" Value="0.72"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private Tile AddTile(AppEntry? app, FrameworkElement icon)
    {
        var s = controller.State.Settings;
        var cell = s.IconSize + s.Spacing + 12;
        var cross = bodyThickness + flight + 12;
        var grid = new Grid { Width = Vertical ? cross : cell, Height = Vertical ? cell : cross, ClipToBounds = false };
        var halo = new Border { CornerRadius = s.DockShape == "Rail" ? new CornerRadius(10) : new CornerRadius(13, 19, 13, 19), Background = (Brush)FindResource("AccentSoftBrush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(.7), Opacity = 0 };
        var visual = new Grid { Width = s.IconSize + 10, Height = s.IconSize + 10, RenderTransformOrigin = new Point(.5, .5) };
        visual.Children.Add(halo); visual.Children.Add(icon);
        var scale = new ScaleTransform(1, 1); var move = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(move); visual.RenderTransform = transforms;
        var insideAtStart = s.Position is "Bottom" or "Right";
        var resting = (insideAtStart ? flight : 12) + bodyThickness / 2 - (insideAtStart ? 2 : -2);
        if (Vertical) { visual.HorizontalAlignment = HorizontalAlignment.Left; visual.Margin = new Thickness(resting - visual.Width / 2, 0, 0, 0); }
        else { visual.VerticalAlignment = VerticalAlignment.Top; visual.Margin = new Thickness(0, resting - visual.Height / 2, 0, 0); }
        // A compact, high-contrast capsule stays readable over both wallpaper and
        // the mirrored icon. It is a retained shape, with no glow or idle clock.
        var markerFill = new Border { CornerRadius = new CornerRadius(2) };
        var dot = new Border
        {
            Width = Vertical ? 6 : 17, Height = Vertical ? 17 : 6,
            CornerRadius = new CornerRadius(3), Padding = new Thickness(1),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : (Brush)FindResource("OpaqueSurfaceBrush"),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : (Brush)FindResource("GlassHighlightBrush"),
            BorderThickness = new Thickness(.7), Child = markerFill, Opacity = 0, IsHitTestVisible = false
        };
        if (Vertical)
        {
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            dot.Margin = new Thickness(s.Position == "Left" ? 15 : cross - 21, 0, 0, 0);
        }
        else
        {
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, s.Position == "Top" ? 15 : cross - 21, 0, 0);
        }
        var reflection = new DockReflection();
        reflection.Configure(s.Position, cell, cross, resting, s.IconSize, icon.Width, icon.Height,
            s.IconReflection && !s.PowerSaver && !SystemParameters.HighContrast ? s.ReflectionStrength : 0);
        if (icon is System.Windows.Controls.Image image)
            reflection.SetBinding(DockReflection.SourceProperty, new Binding(nameof(System.Windows.Controls.Image.Source)) { Source = image, Mode = BindingMode.OneWay });
        grid.Children.Add(reflection); grid.Children.Add(visual); grid.Children.Add(dot);
        var name = app?.Name ?? "打开应用库 · Ctrl+Alt+Space";
        var text = new TextBlock { Text = name };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "AppNameFamily");
        text.SetResourceReference(TextBlock.FontSizeProperty, "AppNameSize");
        text.SetResourceReference(TextBlock.FontWeightProperty, "AppNameWeight");
        text.SetResourceReference(TextBlock.FontStyleProperty, "AppNameStyle");
        text.SetResourceReference(TextBlock.ForegroundProperty, "AppNameBrush");
        var button = new Button { Content = grid, Width = grid.Width, Height = grid.Height, Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Template = TileTemplate, ToolTip = new ToolTip { Content = text, Placement = s.Position switch { "Top" => PlacementMode.Bottom, "Left" => PlacementMode.Right, "Right" => PlacementMode.Left, _ => PlacementMode.Top } }, Tag = app, AllowDrop = app != null, Cursor = Cursors.Hand };
        AutomationProperties.SetName(button, app?.Name ?? "应用");
        ToolTipService.SetInitialShowDelay(button, 480); ToolTipService.SetBetweenShowDelay(button, 100);
        var tile = new Tile { App = app, Button = button, Visual = visual, Scale = scale, Move = move, Halo = halo, Dot = dot, Reflection = reflection };
        button.GotKeyboardFocus += (_, _) => FocusTile(tile);
        button.LostKeyboardFocus += (_, _) => { if (!IsMouseOver) ResetMagnification(); };
        items.Children.Add(button); tiles.Add(tile); return tile;
    }

    private FrameworkElement LaunchpadMark(double size)
    {
        var image = new System.Windows.Controls.Image { Width = size + 9, Height = size + 9, Source = BrandIcon, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
    private static readonly BitmapSource BrandIcon = LoadBrandIcon();
    private static BitmapSource LoadBrandIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/LumaDock;component/Assets/LumaDock.ico"))!.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(image => image.PixelWidth).First(); frame.Freeze(); return frame;
    }

    public void RefreshWindows()
    {
        foreach (var tile in tiles)
        {
            var running = tile.App != null && tile.Variants.Any(controller.Tracker.IsRunning);
            var active = running && tile.Variants.Any(controller.Tracker.IsActive);
            tile.Dot.Opacity = running ? 1 : 0;
            var length = active ? 24d : 17d;
            if (Vertical) tile.Dot.Height = length; else tile.Dot.Width = length;
            ((Border)tile.Dot.Child).Background = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush : (Brush)FindResource(active ? "AccentBrush" : "TextBrush");
            if (tile.Running != running || tile.Active != active)
            {
                tile.Running = running; tile.Active = active;
                AutomationProperties.SetItemStatus(tile.Button, active ? "当前窗口" : running ? "正在运行" : "未运行");
            }
        }
        if (controller.Tracker.IsForegroundFullscreenOnMonitor(controller.State.Settings.MonitorDevice))
        {
            if (IsVisible) Hide();
        }
        else if (!IsVisible) { Show(); Reposition(); }
        if (IsVisible && handle != IntPtr.Zero) NativeWindowHelper.ApplyDockLayer(handle);
    }

    public void Reposition()
    {
        if (handle == IntPtr.Zero) return;
        var monitors = MonitorService.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.DeviceName == controller.State.Settings.MonitorDevice) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
        var dpi = Native.GetDpiForWindow(handle) / 96.0; if (dpi < .5) dpi = 1;
        var s = controller.State.Settings;
        var length = tiles.Count * (s.IconSize + s.Spacing + 12) + (tiles.Count > 1 ? 19 : 0) + 28 + endReserve * 2;
        var thickness = bodyThickness + flight + 12;
        fullWidth = Vertical ? thickness : Math.Min(length, monitor.WorkWidth / dpi - 16);
        fullHeight = Vertical ? Math.Min(length, monitor.WorkHeight / dpi - 16) : thickness;
        var width = fullWidth; var height = fullHeight;
        if (tucked) { if (Vertical) width = 5; else height = 5; }
        Width = Math.Max(5, width); Height = Math.Max(5, height);
        var margin = tucked ? new Thickness(0) : s.Position switch
        {
            "Top" => new Thickness(endReserve, 12, endReserve, flight),
            "Left" => new Thickness(12, endReserve, flight, endReserve),
            "Right" => new Thickness(flight, endReserve, 12, endReserve),
            _ => new Thickness(endReserve, flight, endReserve, 12)
        };
        body.Margin = plate.Margin = finish.Margin = margin;
        items.Margin = tucked ? new Thickness(0) : Vertical ? new Thickness(0, endReserve + 14, 0, endReserve + 14) : new Thickness(endReserve + 14, 0, endReserve + 14, 0);
        foreach (var layer in new[] { plate, finish })
        {
            layer.Shape = s.DockShape; layer.Radius = radius; layer.Accent = Theme.AccentColor; layer.Dark = Theme.IsDark; layer.Vertical = Vertical; layer.OutwardEnd = s.Position is "Bottom" or "Right"; layer.InvalidateVisual();
        }
        body.Clip = DockPlate.Contour(new Size(Math.Max(0, Width - margin.Left - margin.Right), Math.Max(0, Height - margin.Top - margin.Bottom)), s.DockShape, radius);
        liquid.CornerRadiusOverride = radius;
        highlight.Radius = Math.Max(0, radius - 1);
        UpdateContour();
        var w = (int)Math.Ceiling(Width * dpi); var h = (int)Math.Ceiling(Height * dpi); var gap = tucked ? 0 : (int)(2 * dpi);
        var x = monitor.WorkLeft + (monitor.WorkWidth - w) / 2; var y = monitor.WorkTop + monitor.WorkHeight - h - gap;
        if (s.Position == "Top") y = monitor.WorkTop + gap;
        if (s.Position == "Left") { x = monitor.WorkLeft + gap; y = monitor.WorkTop + (monitor.WorkHeight - h) / 2; }
        if (s.Position == "Right") { x = monitor.WorkLeft + monitor.WorkWidth - w - gap; y = monitor.WorkTop + (monitor.WorkHeight - h) / 2; }
        NativeWindowHelper.SetPosition(handle, x, y, w, h);
        NativeWindowHelper.ApplyDockLayer(handle);
    }

    private void UpdateContour()
    {
        var shape = controller.State.Settings.DockShape;
        body.Clip = DockPlate.Contour(body.RenderSize, shape, radius);
        var size = new Size(Math.Max(0, body.ActualWidth - 2), Math.Max(0, body.ActualHeight - 2));
        highlight.SetMotionContours(DockPlate.Contour(size, shape, Math.Max(0, radius - 1), .7), DockPlate.Contour(size, shape, Math.Max(0, radius - 1), 4.5));
    }

    private void HideTick(object? sender, EventArgs e)
    {
        hideDelay.Stop();
        if (IsMouseOver || dragging || ContextMenu?.IsOpen == true || tiles.Any(t => t.Button.ContextMenu?.IsOpen == true) || !controller.State.Settings.AutoHide) return;
        tucked = true; StopHover(); highlight.StopMotion(); scroll.Visibility = Visibility.Collapsed; body.Visibility = Visibility.Collapsed; finish.Visibility = Visibility.Collapsed; Reposition();
    }
    private void ScheduleHide() { if (!closed && IsVisible && controller.State.Settings.AutoHide && !IsMouseOver) hideDelay.Start(); }
    private void Reveal()
    {
        if (!tucked) return;
        tucked = false; scroll.Visibility = Visibility.Visible; body.Visibility = Visibility.Visible; finish.Visibility = Visibility.Visible; Reposition(); liquid.Refresh();
    }

    private void Magnify(object sender, MouseEventArgs e)
    {
        if (!controller.ReducedMotion) highlight.TrackPointer(e.GetPosition(highlight));
        if (controller.ReducedMotion || tucked || dragging) return;
        var p = e.GetPosition(items);
        if (double.IsFinite(lastPointer.X) && Math.Abs(p.X - lastPointer.X) < .25 && Math.Abs(p.Y - lastPointer.Y) < .25) return;
        lastPointer = p; SetTargets(p);
    }
    private void SetTargets(Point p)
    {
        var s = controller.State.Settings; var cell = s.IconSize + s.Spacing + 12;
        foreach (var tile in tiles)
        {
            var center = tile.Button.TranslatePoint(new Point(tile.Button.ActualWidth / 2, tile.Button.ActualHeight / 2), items);
            var signed = Vertical ? center.Y - p.Y : center.X - p.X;
            var distance = Math.Min(1, Math.Abs(signed) / (cell * 1.55));
            var influence = (1 + Math.Cos(distance * Math.PI)) / 2;
            tile.TargetScale = 1 + influence * s.AnimationStrength * .9;
            tile.TargetAlong = Math.Tanh(signed / cell * 1.8) * s.IconSize * s.AnimationStrength * .26 * influence;
            tile.TargetLift = (s.IconSize * s.AnimationStrength * .2 + s.IconSize * (tile.TargetScale - 1) * .35) * influence;
            tile.TargetHalo = influence * .76;
            Panel.SetZIndex(tile.Button, (int)(influence * 100));
        }
        StartHover();
    }
    private void FocusTile(Tile tile)
    {
        if (controller.ReducedMotion) { tile.Halo.Opacity = .76; return; }
        SetTargets(tile.Button.TranslatePoint(new Point(tile.Button.ActualWidth / 2, tile.Button.ActualHeight / 2), items));
    }
    private void ResetMagnification()
    {
        foreach (var tile in tiles) { tile.TargetScale = 1; tile.TargetAlong = tile.TargetLift = tile.TargetHalo = 0; }
        if (controller.ReducedMotion) StopHover(); else StartHover();
    }
    private void StartHover()
    {
        if (hover.IsEnabled || !IsVisible) return;
        if (!tiles.Any(t => Math.Abs(t.Scale.ScaleX - t.TargetScale) > .001 || Math.Abs(t.Move.X) + Math.Abs(t.Move.Y) > .01 || Math.Abs(t.Halo.Opacity - t.TargetHalo) > .002)) return;
        hoverTick = Environment.TickCount64; hover.Start();
    }
    private void HoverTick(object? sender, EventArgs e)
    {
        if (controller.ReducedMotion || !IsVisible || tucked || dragging) { StopHover(); return; }
        var now = Environment.TickCount64; var delta = Math.Clamp(now - hoverTick, 1, 64); hoverTick = now;
        // A frame-time based response stays continuous across fast pointer reversals,
        // while its finite settle still releases the only hover timer at rest.
        var factor = 1 - Math.Exp(-delta / 50d); var moving = false;
        foreach (var tile in tiles)
        {
            var scale = Approach(tile.Scale.ScaleX, tile.TargetScale, factor, .001);
            tile.Scale.ScaleX = tile.Scale.ScaleY = scale;
            var lift = tile.TargetLift * (controller.State.Settings.Position is "Bottom" or "Right" ? -1 : 1);
            var x = Vertical ? lift : tile.TargetAlong; var y = Vertical ? tile.TargetAlong : lift;
            tile.Move.X = Approach(tile.Move.X, x, factor, .015); tile.Move.Y = Approach(tile.Move.Y, y, factor, .015);
            tile.Reflection.SetPose(scale, Vertical ? tile.Move.Y : tile.Move.X, Vertical ? tile.Move.X : tile.Move.Y);
            tile.Halo.Opacity = Approach(tile.Halo.Opacity, tile.TargetHalo, factor, .002);
            moving |= scale != tile.TargetScale || tile.Move.X != x || tile.Move.Y != y || tile.Halo.Opacity != tile.TargetHalo;
        }
        if (!moving) hover.Stop();
    }
    private static double Approach(double value, double target, double factor, double threshold) => Math.Abs(target - value) < threshold ? target : value + (target - value) * factor;
    private void StopHover()
    {
        hover.Stop(); lastPointer = new(double.NaN, double.NaN);
        foreach (var tile in tiles)
        {
            tile.Scale.ScaleX = tile.Scale.ScaleY = tile.TargetScale = 1;
            tile.Move.X = tile.Move.Y = tile.TargetAlong = tile.TargetLift = tile.TargetHalo = tile.Halo.Opacity = 0;
            tile.Reflection.SetPose(1, 0, 0);
        }
    }

    private void BeginReorder(Button button, AppEntry app, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragging) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - dragStart.X) + Math.Abs(p.Y - dragStart.Y) < 10) return;
        dragging = true; e.Handled = true; hideDelay.Stop(); StopHover();
        try
        {
            // Never yield between crossing the drag threshold and entering OLE: a quick
            // mouse release would otherwise turn a valid gesture into a cancelled drag.
            // An immutable copy also keeps a scan/settings change from altering an active transfer.
            var snapshot = new AppEntry { Id = app.Id, Name = app.Name, LaunchPath = app.LaunchPath,
                Arguments = app.Arguments, WorkingDirectory = app.WorkingDirectory, ExecutablePath = app.ExecutablePath,
                AppUserModelId = app.AppUserModelId, CustomIconPath = app.CustomIconPath };
            var data = new DockTransferData(app.Id, () => [DesktopShortcutService.PrepareTransfer(snapshot)], error =>
            { Storage.Log.Write("Shortcut transfer preparation failed", error); controller.Notify("这个应用暂时无法拖到桌面，Dock 内排序仍可使用。"); });
            if (closed || controller.IsDisposed || Mouse.LeftButton != MouseButtonState.Pressed || !IsVisible) return;
            System.Windows.DragDrop.DoDragDrop(button, data, DragDropEffects.Copy);
        }
        finally
        {
            dragging = false; ClearInsertion();
            if (rebuildAfterDrag) { rebuildAfterDrag = false; Rebuild(); }
            ScheduleHide();
        }
    }
    private string? DraggedId(IDataObject data)
    {
        foreach (var format in new[] { "LumaDock.Pin", "LumaDock.AppEntry.Id" })
            if (data.GetDataPresent(format) && data.GetData(format) is string id && controller.State.Apps.Any(a => a.Id == id)) return id;
        return null;
    }
    private bool CanAccept(IDataObject data) => DraggedId(data) != null || data.GetDataPresent(DataFormats.FileDrop);
    private int InsertionGap(Point point)
    {
        var coordinate = Vertical ? point.Y : point.X;
        var apps = tiles.Where(tile => tile.App != null).ToArray();
        for (var i = 0; i < apps.Length; i++)
        {
            var button = apps[i].Button;
            var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), this);
            if (coordinate < (Vertical ? center.Y : center.X)) return i;
        }
        return apps.Length;
    }
    private int SavedInsertionGap(int visibleGap)
    {
        var apps = tiles.Where(tile => tile.App != null).ToArray();
        if (apps.Length == 0) return controller.State.PinnedIds.Count;
        // Unavailable pins and alternate launch records stay in the saved list.
        // Resolve the visible gap against its actual saved anchor before applying
        // DockOrder, which is intentionally defined in the original list's indices.
        if (visibleGap < apps.Length)
        {
            var anchor = apps[Math.Max(0, visibleGap)].App!.Id;
            var index = controller.State.PinnedIds.FindIndex(id => id.Equals(anchor, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : controller.State.PinnedIds.Count;
        }
        var last = apps[^1].App!.Id;
        var lastIndex = controller.State.PinnedIds.FindIndex(id => id.Equals(last, StringComparison.OrdinalIgnoreCase));
        return lastIndex >= 0 ? lastIndex + 1 : controller.State.PinnedIds.Count;
    }
    private void ShowInsertion(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!CanAccept(e.Data) || (e.AllowedEffects & DragDropEffects.Copy) == 0) { e.Effects = DragDropEffects.None; ClearInsertion(); return; }
        e.Effects = DragDropEffects.Copy; Reveal(); hideDelay.Stop(); StopHover();
        ShowInsertionAt(InsertionGap(e.GetPosition(this)));
    }
    private void ShowInsertionAt(int gap)
    {
        var apps = tiles.Where(tile => tile.App != null).ToArray();
        var tile = apps.Length == 0 ? tiles[0] : apps[Math.Clamp(gap, 0, apps.Length - 1)];
        var button = tile.Button;
        var origin = button.TranslatePoint(new Point(), this);
        var along = Vertical ? origin.Y : origin.X;
        if (apps.Length == 0 || gap >= apps.Length) along += Vertical ? button.ActualHeight : button.ActualWidth;
        var cross = body.TranslatePoint(new Point(body.ActualWidth / 2, body.ActualHeight / 2), this);
        insertionCue.Background = (Brush)FindResource("AccentBrush"); insertionCue.BorderBrush = (Brush)FindResource("TextBrush");
        insertionCue.Width = Vertical ? 26 : 4; insertionCue.Height = Vertical ? 4 : 26;
        Canvas.SetLeft(insertionCue, Vertical ? cross.X - 13 : Math.Clamp(along - 2, 2, Math.Max(2, ActualWidth - 6)));
        Canvas.SetTop(insertionCue, Vertical ? Math.Clamp(along - 2, 2, Math.Max(2, ActualHeight - 6)) : cross.Y - 13);
        insertionCue.Visibility = Visibility.Visible;
    }
    private void ClearInsertion() => insertionCue.Visibility = Visibility.Collapsed;
    private async void DropItems(object sender, DragEventArgs e)
    {
        var gap = SavedInsertionGap(InsertionGap(e.GetPosition(this)));
        var id = DraggedId(e.Data);
        var paths = id == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
        e.Handled = true; ClearInsertion();
        if ((e.AllowedEffects & DragDropEffects.Copy) == 0 || (id == null && paths == null)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        try
        {
            if (id != null) controller.PinAt(controller.State.Apps.First(a => a.Id == id), gap);
            else if (paths != null) await controller.AddPathsToDockAsync(paths, gap);
        }
        catch (Exception error) { controller.Notify(error.Message); }
        ScheduleHide();
    }
    private void AddFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "添加应用到 Dock", Filter = "应用与快捷方式|*.exe;*.lnk;*.url;*.appref-ms", DereferenceLinks = false };
        if (dialog.ShowDialog() == true) { try { var app = controller.GetOrAddManual(dialog.FileName); controller.Pin(app); } catch (Exception ex) { controller.Notify(ex.Message); } }
    }
    private static void AddMenu(ContextMenu menu, string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    internal void CapturePng(string path)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static class Native { [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd); }
}
