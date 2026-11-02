using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.App.Center;
using LumaDock.Windows;

namespace LumaDock.App;

/// <summary>One set of native subscriptions for the controller lifetime, independent of Dock rebuilds.</summary>
internal sealed class DockSystemContext : IDisposable
{
    public SystemStatusService Status { get; } = new();
    public AudioEndpointService Audio { get; } = new();
    public BrightnessService Brightness { get; } = new();
    public InputLanguageService Input { get; } = new();
    public void Dispose() { Input.Dispose(); Brightness.Dispose(); Audio.Dispose(); Status.Dispose(); }
}

/// <summary>Compact system region. Timers run only for a visible clock or a pending user edit.</summary>
public sealed class DockSystemStrip : Grid, IDisposable
{
    private readonly AppController controller;
    private DockSystemContext services => controller.SystemContext;
    private readonly bool vertical;
    private readonly TextBlock mode, profile, network, sound, battery, time, date;
    private readonly Button modeButton, profileButton, combinedButton;
    private readonly DispatcherTimer clock;
    private Flyout? panel;
    private bool disposed, visible = true, initialized;
    private InputTarget inputTarget;
    private IntPtr previousForeground;
    private uint previousPid;
    public bool IsInteracting => panel?.IsVisible == true;
    public event EventHandler? InteractionChanged;
    public double AlongLength { get; }

    public DockSystemStrip(AppController controller, bool vertical)
    {
        this.controller = controller; this.vertical = vertical;
        var s = controller.State.Settings;
        var icon = s.DockStatusIconSize; var font = s.DockStatusFontSize;
        var weight = FontWeight.FromOpenTypeWeight(s.DockStatusFontWeight);
        var cross = s.IconSize + (s.DockShape == "Rail" ? 36 : 42) + Math.Ceiling(s.IconSize * s.AnimationStrength * .76) + 20;
        var body = s.IconSize + (s.DockShape == "Rail" ? 36 : 42);
        var resting = (s.Position is "Bottom" or "Right" ? cross - body - 12 : 12) + body / 2 - (s.Position is "Bottom" or "Right" ? 2 : -2);
        mode = Text("—", font); profile = Text("—", font);
        network = Glyph("\uE839", icon); sound = Glyph("\uE767", icon); battery = Text("—", font - 1);
        time = Text("", font + 1); date = Text("", Math.Max(9, font - 2));
        foreach (var label in new[] { mode, profile, network, sound, battery, time, date }) label.FontWeight = weight;
        var tray = MakeButton("后台应用", Glyph("\uE70D", icon - 2), async () =>
        {
            try { ClosePanel(); var result = await TrayIntegration.OpenOverflowAsync(); Report(result); }
            catch (Exception error) { Storage.Log.Write("Tray entry failed", error); controller.Notify("后台应用入口暂时不可用，请从原生任务栏打开。"); }
        });
        modeButton = MakeButton("切换中英文输入模式", mode, async () =>
        {
            try { services.Input.Refresh(); var target = services.Input.CaptureTarget(); Report(await services.Input.ToggleModeAsync(target)); }
            catch (Exception e) { Storage.Log.Write("Input mode change failed", e); controller.Notify("当前输入法未接受切换，请使用它的原生入口。"); }
        });
        profileButton = MakeButton("键盘布局", profile, () => OpenPanel("Keyboard"));
        var combined = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var label in new[] { network, sound, Glyph("\uE83F", icon), battery }) { label.FontWeight = weight; label.Margin = new Thickness(4, 0, 4, 0); combined.Children.Add(label); }
        combinedButton = MakeButton("网络、音量与电源", combined, () => OpenPanel("Quick"));
        var clockFace = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; clockFace.Children.Add(time); clockFace.Children.Add(date);
        var clockButton = MakeButton("日期与日历", clockFace, () => OpenPanel("Calendar"));
        var row = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
        foreach (var b in new[] { tray, modeButton, profileButton, combinedButton, clockButton }) row.Children.Add(b);
        if (vertical)
        {
            AlongLength = 208; Width = cross; Height = AlongLength;
            row.Width = Math.Max(64, body - 8); row.Margin = new Thickness(resting - row.Width / 2, 0, 0, 0); row.HorizontalAlignment = HorizontalAlignment.Left;
            combined.Children.Clear();
            combinedButton.Content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { network, sound } };
        }
        else
        {
            AlongLength = 308 + Math.Max(0, icon - 16) * 4 + Math.Max(0, font - 12) * 10;
            Width = AlongLength; Height = cross; row.Height = 42; row.Margin = new Thickness(7, resting - 21, 6, 0); row.VerticalAlignment = VerticalAlignment.Top;
            tray.Width = modeButton.Width = profileButton.Width = 30 + Math.Max(0, icon - 16);
            combinedButton.Width = 132 + Math.Max(0, font - 12) * 4; clockButton.Width = 87 + Math.Max(0, font - 12) * 6;
        }
        Children.Add(row);
        AutomationProperties.SetName(this, "Dock 系统区");
        clock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher); clock.Tick += ClockTick;
        Loaded += LoadedStrip;
    }
    private void LoadedStrip(object sender, RoutedEventArgs e) { UpdateClock(); ScheduleClock(); Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Initialize)); }
    private void Initialize()
    {
        if (disposed || initialized) return; initialized = true;
        services.Status.Changed += Changed; services.Audio.Changed += Changed; services.Input.Changed += Changed; services.Brightness.Changed += Changed;
        UpdateLabels(); _ = RefreshInput();
    }
    private async Task RefreshInput() { try { await services.Input.RefreshNativeIndicatorAsync(); } catch (Exception e) { Storage.Log.Write("Input indicator read failed", e); } }
    public void SetVisible(bool value)
    {
        visible = value; clock.Stop();
        if (!value) ClosePanel(); else { UpdateClock(); ScheduleClock(); }
    }
    private void ScheduleClock()
    {
        clock.Stop(); if (disposed || !visible || !IsLoaded) return;
        var now = DateTime.Now; clock.Interval = TimeSpan.FromMilliseconds(Math.Max(100, 60020 - now.Second * 1000 - now.Millisecond)); clock.Start();
    }
    private void ClockTick(object? sender, EventArgs e) { clock.Stop(); if (disposed || !visible) return; UpdateClock(); if (initialized) services.Status.RefreshPower(); panel?.Refresh(); ScheduleClock(); }
    private void UpdateClock() { time.Text = DateTime.Now.ToString("HH:mm"); date.Text = DateTime.Now.ToString("yyyy/M/d"); }
    private void Changed(object? sender, EventArgs e) { if (!disposed) { UpdateLabels(); panel?.Refresh(); } }
    private void UpdateLabels()
    {
        var s = services.Status.Current; var a = services.Audio.Current; var i = services.Input.Current;
        mode.Text = i.ModeText; modeButton.IsEnabled = i.CanToggleMode; modeButton.ToolTip = i.ModeAvailable ? "单击切换中英文输入" : "当前输入法未提供可控制的中英文状态";
        profile.Text = i.ProfileText; profileButton.ToolTip = i.ProfileName + " · 选择键盘布局";
        network.Text = s.Network.Connected ? "\uE839" : "\uE871";
        sound.Text = !a.Available ? "\uE7F4" : a.Muted ? "\uE74F" : "\uE767";
        battery.Text = s.Battery.HasBattery ? (s.Battery.IsCharging == true ? "+ " : "") + (s.Battery.Percent is {} p ? p + "%" : "—") : "电源";
        combinedButton.ToolTip = s.Network.Text + "\n" + (a.Available ? a.DeviceName + " · " + Math.Round(a.Volume * 100) + "%" : "未检测到播放设备") + "\n" + s.Battery.Text;
        UpdateClock();
    }
    private void OpenPanel(string kind)
    {
        if (disposed) return;
        Initialize();
        if (panel != null && panel.Kind == kind) { ClosePanel(); return; }
        ClosePanel(); previousForeground = GetForegroundWindow(); GetWindowThreadProcessId(previousForeground, out previousPid);
        inputTarget = services.Input.CaptureTarget(); services.Input.Refresh(); services.Status.Refresh(); services.Audio.Refresh();
        var created = new Flyout(this, kind); panel = created;
        created.Owner = Window.GetWindow(this);
        created.Closed += (_, _) => { if (ReferenceEquals(panel, created)) panel = null; InteractionChanged?.Invoke(this, EventArgs.Empty); };
        created.ContentRendered += (_, _) => PositionPanel(created);
        created.Show(); PositionPanel(created); InteractionChanged?.Invoke(this, EventArgs.Empty);
        if (kind == "Quick") _ = RefreshBrightness();
    }
    private async Task RefreshBrightness() { try { await services.Brightness.RefreshAsync(); } catch (Exception e) { Storage.Log.Write("Brightness read failed", e); } }
    private void PositionPanel(Window target)
    {
        var ms = MonitorService.GetMonitors(); var m = ms.FirstOrDefault(x => x.DeviceName == controller.State.Settings.MonitorDevice) ?? ms.FirstOrDefault(x => x.IsPrimary) ?? ms.FirstOrDefault();
        if (m == null) return;
        var dpi = VisualTreeHelper.GetDpi(this); var p = PointToScreen(new Point());
        var x = p.X / dpi.DpiScaleX + ActualWidth - target.ActualWidth; var y = p.Y / dpi.DpiScaleY - target.ActualHeight + 7;
        if (controller.State.Settings.Position == "Top") y = p.Y / dpi.DpiScaleY + ActualHeight - 7;
        if (vertical) { x = p.X / dpi.DpiScaleX + (controller.State.Settings.Position == "Left" ? ActualWidth - 7 : -target.ActualWidth + 7); y = p.Y / dpi.DpiScaleY + ActualHeight / 2 - target.ActualHeight / 2; }
        var left = m.WorkLeft / dpi.DpiScaleX; var top = m.WorkTop / dpi.DpiScaleY;
        target.MaxHeight = Math.Max(180, m.WorkHeight / dpi.DpiScaleY - 24);
        target.Left = Math.Clamp(x, left + 12, Math.Max(left + 12, left + m.WorkWidth / dpi.DpiScaleX - target.ActualWidth - 12));
        target.Top = Math.Clamp(y, top + 12, Math.Max(top + 12, top + m.WorkHeight / dpi.DpiScaleY - target.ActualHeight - 12));
    }
    public void ClosePanel() { var current = panel; panel = null; current?.Close(); }
    private void Report(SystemActionResult result) { if (!result.Succeeded) controller.Notify(result.Error ?? "Windows 未接受此操作。"); }
    private void Run(SystemAction action)
    {
        var hwnd = previousForeground; var pid = previousPid; ClosePanel();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (controller.IsDisposed) return; GetWindowThreadProcessId(hwnd, out var livePid);
            if (hwnd != IntPtr.Zero && IsWindow(hwnd) && pid == livePid) SetForegroundWindow(hwnd);
            Report(SystemActions.Execute(action));
        }));
    }
    private void Settings(SystemSettingsPage page) { ClosePanel(); Report(SystemActions.OpenSettings(page)); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; ClosePanel(); clock.Stop(); clock.Tick -= ClockTick; Loaded -= LoadedStrip;
        if (initialized) { services.Status.Changed -= Changed; services.Audio.Changed -= Changed; services.Input.Changed -= Changed; services.Brightness.Changed -= Changed; }
    }
    private static TextBlock Text(string value, double size = 12) { var t = new TextBlock { Text = value, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }; t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); t.SetResourceReference(TextBlock.FontFamilyProperty, "UiTextFamily"); return t; }
    private static TextBlock Glyph(string value, double size = 18) { var t = Text(value, size); t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); return t; }
    private static readonly ControlTemplate ButtonFace = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Face" CornerRadius="12" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
          <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Background" Value="{DynamicResource AccentSoftBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Face" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Face" Property="BorderThickness" Value="1"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter Property="Opacity" Value=".65"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".4"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private static Button MakeButton(string name, object face, Action action)
    {
        var button = new Button { Content = face, ToolTip = name, Height = 38, Padding = new Thickness(5), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Template = ButtonFace, Cursor = Cursors.Hand };
        AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);

    private sealed class Flyout : Window
    {
        private readonly DockSystemStrip strip;
        private readonly StackPanel body = new();
        private readonly TextBlock volumeText = Text(""), brightnessText = Text(""), footer = Text("");
        private readonly Slider volume = Slider("系统音量"), brightness = Slider("屏幕亮度");
        private readonly DispatcherTimer volumeEdit = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private readonly DispatcherTimer brightnessEdit = new() { Interval = TimeSpan.FromMilliseconds(160) };
        private bool updating, closing, brightnessWriting;
        private double desiredBrightness;
        private int brightnessVersion;
        private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private readonly TextBlock monthText = Text("", 15);
        private readonly UniformGrid days = new() { Columns = 7 };
        private TextBlock? calendarTitle;
        private DateTime calendarDay = DateTime.Today;
        public string Kind { get; }
        public Flyout(DockSystemStrip strip, string kind)
        {
            this.strip = strip; Kind = kind; Title = "LumaDock · " + (kind == "Quick" ? "快捷控制" : kind == "Keyboard" ? "键盘布局" : "日历");
            Width = 392; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            SetResourceReference(FontFamilyProperty, "UiTextFamily"); SetResourceReference(ForegroundProperty, "TextBrush");
            WindowBackdrop.Attach(this, () => Theme.IsDark, () => strip.controller.State.Settings.PowerSaver || !strip.controller.State.Settings.LiquidGlass,
                () => strip.controller.State.Settings.LiveGlass && strip.controller.State.Settings.LiveGlassStyle == "Clear");
            var shell = new Border { CornerRadius = new CornerRadius(24), Padding = new Thickness(20), BorderThickness = new Thickness(.8) };
            shell.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Theme.IsDark ? Color.FromArgb(176, 20, 29, 42) : Color.FromArgb(180, 244, 249, 255));
            shell.SetResourceReference(Border.BorderBrushProperty, "GlassHighlightBrush");
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var close = MakeButton("关闭", Glyph("\uE8BB", 12), Close); close.Width = close.Height = 28; DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
            var title = Text(kind == "Quick" ? "快捷控制" : kind == "Keyboard" ? "键盘布局" : DateTime.Today.ToString("M月d日 dddd"), 19); title.FontWeight = FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Left; heading.Children.Add(title); body.Children.Add(heading);
            if (kind == "Calendar") calendarTitle = title;
            if (kind == "Quick") BuildQuick(); else if (kind == "Keyboard") BuildKeyboard(); else BuildCalendar();
            ApplyType(body, strip.controller.State.Settings.DockStatusFontSize / 12, FontWeight.FromOpenTypeWeight(strip.controller.State.Settings.DockStatusFontWeight), strip.controller.State.Settings.DockStatusIconSize / 16);
            shell.Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Content = shell;
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Deactivated += (_, _) => { if (!closing) Close(); };
            Closing += (_, _) => closing = true;
            Closed += (_, _) => { closing = true; if (volumeEdit.IsEnabled) strip.Report(strip.services.Audio.SetVolume(volume.Value / 100)); if (brightnessEdit.IsEnabled) _ = WriteBrightness(); volumeEdit.Stop(); brightnessEdit.Stop(); };
            volumeEdit.Tick += (_, _) => { volumeEdit.Stop(); if (!closing) { var result = strip.services.Audio.SetVolume(volume.Value / 100); strip.Report(result); Refresh(); } };
            brightnessEdit.Tick += (_, _) => { brightnessEdit.Stop(); if (!closing) _ = WriteBrightness(); };
            volume.ValueChanged += (_, _) => { if (!updating && volume.IsEnabled) { volumeText.Text = "音量 " + Math.Round(volume.Value) + "%"; volumeEdit.Stop(); volumeEdit.Start(); } };
            brightness.ValueChanged += (_, _) => { if (!updating && brightness.IsEnabled) { desiredBrightness = brightness.Value / 100; brightnessVersion++; brightnessText.Text = "亮度 " + Math.Round(brightness.Value) + "%"; brightnessEdit.Stop(); brightnessEdit.Start(); } };
            Refresh();
        }
        private async Task WriteBrightness()
        {
            if (brightnessWriting) return; brightnessWriting = true;
            try
            {
                int sent;
                do { brightnessEdit.Stop(); sent = brightnessVersion; var result = await strip.services.Brightness.SetBrightnessAsync(desiredBrightness); if (!strip.controller.IsDisposed) strip.Report(result); }
                while (sent != brightnessVersion && !strip.controller.IsDisposed && !strip.disposed);
            }
            catch (Exception e) { Storage.Log.Write("Brightness change failed", e); }
            finally { brightnessWriting = false; if (!closing) Refresh(); }
        }
        private static void ApplyType(DependencyObject root, double textScale, FontWeight weight, double iconScale)
        {
            if (root is TextBlock text) { text.FontWeight = weight; text.FontSize *= text.FontFamily.Source.StartsWith("Segoe Fluent") ? iconScale : textScale; }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) ApplyType(VisualTreeHelper.GetChild(root, index), textScale, weight, iconScale);
            // Before first layout the visual tree can be empty. Follow logical content instead.
            if (VisualTreeHelper.GetChildrenCount(root) == 0 && root is Panel p) foreach (UIElement child in p.Children) ApplyType(child, textScale, weight, iconScale);
            else if (VisualTreeHelper.GetChildrenCount(root) == 0 && root is ContentControl c && c.Content is DependencyObject d) ApplyType(d, textScale, weight, iconScale);
            else if (VisualTreeHelper.GetChildrenCount(root) == 0 && root is Decorator decorator && decorator.Child != null) ApplyType(decorator.Child, textScale, weight, iconScale);
        }
        private void BuildQuick()
        {
            var quick = new UniformGrid { Columns = 3, Margin = new Thickness(-4, -4, -4, 12) };
            quick.Children.Add(Tile("网络", "\uE701", () => strip.Settings(SystemSettingsPage.Network)));
            quick.Children.Add(Tile("蓝牙", "\uE702", () => strip.Settings(SystemSettingsPage.Bluetooth)));
            quick.Children.Add(Tile("快捷设置", "\uE713", () => strip.Run(SystemAction.QuickSettings)));
            body.Children.Add(quick);
            var sliders = new StackPanel();
            sliders.Children.Add(SliderLine("\uE706", brightnessText, brightness, () => strip.Settings(SystemSettingsPage.Display)));
            sliders.Children.Add(SliderLine("\uE767", volumeText, volume, () => { strip.Report(strip.services.Audio.SetMuted(!strip.services.Audio.Current.Muted)); Refresh(); }));
            body.Children.Add(Card(sliders));
            var actions = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 12, -4, 8) };
            actions.Children.Add(Tile("开始", "\uE8A5", () => strip.Run(SystemAction.StartMenu)));
            actions.Children.Add(Tile("搜索", "\uE721", () => strip.Run(SystemAction.Search)));
            actions.Children.Add(Tile("任务视图", "\uE7C4", () => strip.Run(SystemAction.TaskView)));
            actions.Children.Add(Tile("显示桌面", "\uE8EF", () => strip.Run(SystemAction.ShowDesktop)));
            actions.Children.Add(Tile("通知", "\uE91C", () => strip.Run(SystemAction.NotificationCenter)));
            actions.Children.Add(Tile("任务管理器", "\uE9D9", () => strip.Run(SystemAction.TaskManager)));
            body.Children.Add(actions);
            footer.TextWrapping = TextWrapping.Wrap; footer.HorizontalAlignment = HorizontalAlignment.Left; footer.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var foot = new DockPanel(); var settings = MakeButton("Windows 设置", Glyph("\uE713", 17), () => strip.Settings(SystemSettingsPage.System)); settings.Width = 36; DockPanel.SetDock(settings, Dock.Right); foot.Children.Add(settings); foot.Children.Add(footer); body.Children.Add(foot);
        }
        private FrameworkElement SliderLine(string glyph, TextBlock label, Slider slider, Action action)
        {
            var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var button = MakeButton(glyph == "\uE767" ? "静音 / 恢复音量" : "显示设置", Glyph(glyph, 20), action); grid.Children.Add(button);
            var stack = new StackPanel(); label.HorizontalAlignment = HorizontalAlignment.Left; label.FontSize = 11; stack.Children.Add(label); stack.Children.Add(slider); Grid.SetColumn(stack, 1); grid.Children.Add(stack); return grid;
        }
        private static Border Card(UIElement child) { var border = new Border { Child = child, CornerRadius = new CornerRadius(18), Padding = new Thickness(12), BorderThickness = new Thickness(.6) }; border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return border; }
        private static Button Tile(string name, string glyph, Action action)
        {
            var face = new StackPanel(); face.Children.Add(Glyph(glyph, 20)); var label = Text(name, 11); label.Margin = new Thickness(0, 8, 0, 0); face.Children.Add(label);
            var b = MakeButton(name, face, action); b.Height = 68; b.Margin = new Thickness(4); b.SetResourceReference(BackgroundProperty, "PanelBrush"); b.SetResourceReference(BorderBrushProperty, "BorderBrush"); b.BorderThickness = new Thickness(.5); return b;
        }
        private static Slider Slider(string name) { var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5, Height = 25, Template = CenterStyles.SliderTemplate }; AutomationProperties.SetName(slider, name); return slider; }
        private void BuildKeyboard()
        {
            foreach (var item in strip.services.Input.EnumerateProfiles())
            {
                var label = Text(item.Name, 13); label.HorizontalAlignment = HorizontalAlignment.Left;
                var b = MakeButton(item.Name, label, () => { var target = strip.inputTarget; Close(); strip.Report(strip.services.Input.ActivateProfile(item, target)); });
                b.Height = 48; b.Margin = new Thickness(0, 3, 0, 3); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.SetResourceReference(BackgroundProperty, "PanelBrush"); body.Children.Add(b);
            }
            if (body.Children.Count == 1) body.Children.Add(Text("未读取到键盘布局，可在 Windows 设置中查看。"));
            body.Children.Add(MakeButton("更多键盘设置", Text("更多键盘设置", 11), () => strip.Settings(SystemSettingsPage.Keyboard)));
        }
        private void BuildCalendar()
        {
            var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var next = MakeButton("下个月", Glyph("\uE76C", 12), () => { month = month.AddMonths(1); DrawMonth(); }); next.Width = 34; DockPanel.SetDock(next, Dock.Right); nav.Children.Add(next);
            var back = MakeButton("上个月", Glyph("\uE76B", 12), () => { month = month.AddMonths(-1); DrawMonth(); }); back.Width = 34; DockPanel.SetDock(back, Dock.Left); nav.Children.Add(back); nav.Children.Add(monthText); body.Children.Add(nav); body.Children.Add(days);
            body.Children.Add(MakeButton("今天", Text("回到今天", 11), () => { month = new(DateTime.Today.Year, DateTime.Today.Month, 1); DrawMonth(); }));
            var row = new UniformGrid { Columns = 2 }; row.Children.Add(MakeButton("通知中心", Text("通知中心", 12), () => strip.Run(SystemAction.NotificationCenter))); row.Children.Add(MakeButton("日期与时间设置", Text("日期与时间", 12), () => strip.Settings(SystemSettingsPage.DateAndTime))); body.Children.Add(row); DrawMonth();
        }
        private void DrawMonth()
        {
            monthText.Text = month.ToString("yyyy年 M月"); days.Children.Clear();
            foreach (var name in new[] { "一", "二", "三", "四", "五", "六", "日" }) { var t = Text(name, 11); t.Height = 30; t.Opacity = .6; days.Children.Add(t); }
            var start = month.AddDays(-((int)month.DayOfWeek + 6) % 7);
            for (var index = 0; index < 42; index++)
            {
                var day = start.AddDays(index); var t = Text(day.Day.ToString(), 12); t.Opacity = day.Month == month.Month ? 1 : .35;
                var face = new Border { Child = t, Height = 34, Margin = new Thickness(2), CornerRadius = new CornerRadius(11) };
                if (day == DateTime.Today) { face.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); t.FontWeight = FontWeights.Bold; }
                days.Children.Add(face);
            }
            if (IsLoaded) ApplyType(days, strip.controller.State.Settings.DockStatusFontSize / 12, FontWeight.FromOpenTypeWeight(strip.controller.State.Settings.DockStatusFontWeight), 1);
        }
        public void Refresh()
        {
            if (closing) return;
            if (Kind == "Calendar" && calendarDay != DateTime.Today) { calendarDay = DateTime.Today; if (calendarTitle != null) calendarTitle.Text = calendarDay.ToString("M月d日 dddd"); DrawMonth(); }
            if (Kind != "Quick") return; updating = true;
            try
            {
                var audio = strip.services.Audio.Current; var light = strip.services.Brightness.Current;
                volume.IsEnabled = audio.Available; brightness.IsEnabled = light.Available;
                if (!volumeEdit.IsEnabled) volume.Value = audio.Volume * 100;
                if (!brightnessEdit.IsEnabled && !brightnessWriting) brightness.Value = light.Percent;
                volumeText.Text = audio.Available ? (audio.Muted ? "已静音 · " : "音量 ") + Math.Round(volume.Value) + "%" : "无可用播放设备";
                volume.ToolTip = audio.DeviceName;
                brightnessText.Text = light.Available ? "内置屏幕亮度 " + Math.Round(brightness.Value) + "%" : "此显示器未提供亮度接口"; brightness.ToolTip = light.Reason;
                footer.Text = strip.services.Status.Current.Battery.Text;
            }
            finally { updating = false; }
        }
    }
}
