using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDrop = System.Windows.DragDrop;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace LumaDock.App.Center;

/// <summary>The management surface. Closing normally hides this window, keeping the resident dock alive.</summary>
public sealed partial class ControlCenterWindow : Window
{
    private const string AppDragFormat = "LumaDock.AppEntry.Id";
    private static readonly Lazy<BitmapSource> BrandArtwork = new(() =>
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/LumaDock;component/Assets/LumaDock.ico"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First(); image.Freeze(); return image;
    });
    private readonly AppController _controller;
    private readonly Grid _frame = new();
    private readonly StackPanel _sidebar = new();
    private readonly Grid _body = new();
    private readonly Border _shell = new();
    private readonly GlassRim _glassRim = new();
    private readonly LiquidGlassSurface _liquid;
    private static readonly Lazy<Choice[]> InstalledNameFonts = new(() =>
    {
        try
        {
            var language = XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
            return new[] { new Choice("", "界面默认") }.Concat(Fonts.SystemFontFamilies
                .Select(family => new Choice(family.Source, family.FamilyNames.TryGetValue(language, out var name) ? name : family.Source))
                .GroupBy(font => font.Value, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .OrderBy(font => font.Label, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        }
        catch { return [new Choice("", "界面默认")]; }
    });
    private TextBlock? _fontPreview;
    private TextBlock? _fontPreviewInfo;
    private StackPanel? _settingsRoot;
    private StackPanel? _settingsGroup;
    private readonly Dictionary<string, TextBlock> _categoryCounts = new(StringComparer.Ordinal);
    private IReadOnlyList<ApplicationDisplayGroup> _applicationGroups = [];
    private Dictionary<string, IReadOnlyList<AppEntry>> _variantsById = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _applicationOrder = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _desktopEntryIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _importCancellation;
    private bool _importing;
    private double _settingsScrollOffset;
    private TextBlock _title = new();
    private TextBlock _subtitle = new();
    private readonly TextBlock _status = new();
    private StackPanel _tools = new() { Orientation = Orientation.Horizontal };
    private WrapPanel _categories = new() { Margin = new Thickness(0, 0, 0, 18) };
    private string _section;
    private string _category = "全部应用";
    private string _query = "";
    private int _page;
    private bool _refreshQueued;
    private bool _savingSettings;
    private bool _disposed;
    private bool _reallyClose;
    private bool _resizeQueued;
    private bool _hiding;
    private bool _exitQueued;
    private int _visibilityEpoch;
    private Point _dragStart;
    public double LastOpenMilliseconds { get; private set; }
    public string CurrentSection => _section;

    public ControlCenterWindow(AppController controller, string section = "Launchpad", DesktopFileService? desktopFiles = null)
    {
        _controller = controller;
        _desktopFiles = desktopFiles ?? new DesktopFileService();
        _liquid = new LiquidGlassSurface(this, controller);
        _section = NormalizeSection(section);
        Title = "LumaDock";
        Icon = BrandArtwork.Value;
        Width = 1120; Height = 760; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(FontFamilyProperty, "UiTextFamily");
        SetResourceReference(FontWeightProperty, "UiTextWeight");
        SetResourceReference(FontStyleProperty, "UiTextStyle");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(ForegroundProperty, "TextBrush");
        Resources[typeof(ComboBox)] = CenterStyles.ComboStyle();
        WindowBackdrop.Attach(this, () => Theme.IsDark, () => _controller.State.Settings.PowerSaver,
            () => _controller.State.Settings.LiveGlass && _controller.State.Settings.LiquidGlass && _controller.State.Settings.LiveGlassStyle == "Clear");
        BuildChrome();
        _controller.Changed += ControllerChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { HideSmoothly(); e.Handled = true; } };
        PreviewKeyDown += DesktopKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => DisposeSubscriptions();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Render(); AnimateOpen(); } else StopDesktopWatching(); };
        SizeChanged += (_, _) => QueueGridResize();
        PreviewMouseMove += (_, e) => { if (!_controller.ReducedMotion) _glassRim.TrackPointer(e.GetPosition(_glassRim)); };
        MouseLeave += (_, _) => _glassRim.ReleasePointer();
        Deactivated += (_, _) => _glassRim.StopMotion();
        Activated += (_, _) => _controller.RefreshSystemFont();
        ContentRendered += (_, _) => _controller.RecordLaunchpadFrame();
        Loaded += (_, _) => UpdateSurfaceTint();
        Render();
    }

    private static string NormalizeSection(string section) => section.ToLowerInvariant() switch
    { "settings" or "设置" or "偏好设置" => "Settings", "about" or "关于" => "About", "desktop" or "桌面集" => "Desktop", _ => "Launchpad" };

    public void Navigate(string section)
    {
        CancelPendingHide();
        _section = NormalizeSection(section); _page = 0;
        if (_section is "Launchpad" or "Desktop") _category = "全部应用";
        if (!IsVisible) Show();
        Render(); Activate();
    }

    public void ClosePermanently() { CancelPendingHide(); _reallyClose = true; Close(); }

    private void CancelPendingHide()
    {
        _visibilityEpoch++;
        _hiding = false;
        _shell.BeginAnimation(OpacityProperty, null);
    }

    public void HideSmoothly()
    {
        if (_hiding || !IsVisible) return;
        if (_controller.ReducedMotion) { Hide(); return; }
        _hiding = true;
        var epoch = ++_visibilityEpoch;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (_disposed || epoch != _visibilityEpoch) return; _hiding = false; Hide(); };
        _shell.BeginAnimation(OpacityProperty, fade);
    }

    private void BuildChrome()
    {
        _shell.CornerRadius = new CornerRadius(24); _shell.BorderThickness = new Thickness(.8);
        _shell.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _shell.SetResourceReference(Border.BorderBrushProperty, "GlassHighlightBrush");
        var layers = new Grid();
        var reflection = new Border
        {
            CornerRadius = new CornerRadius(24), IsHitTestVisible = false,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(22, 255, 255, 255), 0),
                new(Color.FromArgb(0, 255, 255, 255), .4)
            }, new Point(0, 0), new Point(.4, 1))
        };
        layers.Children.Add(_liquid); layers.Children.Add(reflection); layers.Children.Add(_frame); layers.Children.Add(_glassRim);
        _shell.Child = layers; Content = _shell;
        _frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
        _frame.RowDefinitions.Add(new RowDefinition());
        _frame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        _frame.ColumnDefinitions.Add(new ColumnDefinition());
        var header = new Grid { Margin = new Thickness(24, 0, 16, 0), Background = Brushes.Transparent };
        Grid.SetColumnSpan(header, 2); _frame.Children.Add(header);
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var logo = new Image { Width = 29, Height = 29, Source = Icon, Margin = new Thickness(0, 0, 10, 0) };
        brand.Children.Add(logo); brand.Children.Add(Text("LumaDock", 15, FontWeights.SemiBold));
        header.Children.Add(brand);
        var chrome = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        chrome.Children.Add(ChromeButton("\uE921", "最小化", () => WindowState = WindowState.Minimized));
        chrome.Children.Add(ChromeButton("\uE8BB", "关闭", () => Close())); header.Children.Add(chrome);
        var sideBorder = new Border { BorderThickness = new Thickness(0, .5, .5, 0), Padding = new Thickness(10, 21, 10, 22), CornerRadius = new CornerRadius(0, 0, 0, 24) };
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); sideBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");
        Grid.SetRow(sideBorder, 1); _frame.Children.Add(sideBorder);
        var sideGrid = new Grid(); sideGrid.RowDefinitions.Add(new RowDefinition()); sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideBorder.Child = sideGrid;
        var sidebarScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        sidebarScroll.Resources[typeof(ScrollBar)] = CenterStyles.ScrollBarStyle(); sideGrid.Children.Add(sidebarScroll);
        var footer = new StackPanel(); Grid.SetRow(footer, 1); sideGrid.Children.Add(footer);
        var shortcut = new StackPanel(); shortcut.Children.Add(Text("快速打开应用库", 11, FontWeights.Medium));
        var keys = Text("Ctrl + Alt + Space", 10, FontWeights.Normal, true); keys.Margin = new Thickness(0, 4, 0, 0); shortcut.Children.Add(keys);
        var shortcutCard = ActionButton("快速打开应用库 · Ctrl + Alt + Space", () => Navigate("Launchpad")); shortcutCard.Content = shortcut;
        shortcutCard.ToolTip = "按 Ctrl + Alt + Space，可从其他应用快速打开应用库"; shortcutCard.Tag = "QuickLaunchHint";
        shortcutCard.Padding = new Thickness(9, 8, 9, 8); shortcutCard.Margin = new Thickness(7, 0, 7, 11); shortcutCard.HorizontalContentAlignment = HorizontalAlignment.Left;
        shortcutCard.SetResourceReference(BackgroundProperty, "CardBrush"); footer.Children.Add(shortcutCard);
        var version = Text("v" + (typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "0.10.0"), 10, FontWeights.Normal, true); version.Margin = new Thickness(16, 0, 0, 0); footer.Children.Add(version);
        _body.Margin = new Thickness(38, 24, 38, 24);
        Grid.SetRow(_body, 1); Grid.SetColumn(_body, 1); _frame.Children.Add(_body);
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new RowDefinition());
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    private void ControllerChanged(object? sender, EventArgs e)
    {
        if (_savingSettings || _refreshQueued || !IsVisible || _disposed) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = false; if (!_disposed && IsVisible && !_savingSettings) Render(); });
    }

    private void Render()
    {
        UpdateDesktopWatching();
        if (_category != "全部应用" && !_controller.State.Categories.Contains(_category)) _category = "全部应用";
        if (_section == "Launchpad") RebuildApplicationGroups();
        _fontPreview = null; _fontPreviewInfo = null;
        _settingsRoot = null; _settingsGroup = null;
        _categoryCounts.Clear();
        WindowBackdrop.Refresh(this, Theme.IsDark, _controller.State.Settings.PowerSaver);
        UpdateSurfaceTint();
        _shell.CornerRadius = new CornerRadius(_controller.State.Settings.CornerRadius);
        _glassRim.Radius = _controller.State.Settings.CornerRadius;
        UpdateGlassMotion();
        _liquid.Refresh();
        BuildSidebar(); _body.Children.Clear();
        _title = new(); _subtitle = new() { Visibility = Visibility.Collapsed };
        _tools = new() { Orientation = Orientation.Horizontal };
        _categories = new() { Margin = new Thickness(0, 0, 0, 10) };
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 15) };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel(); _title.FontSize = 28; _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.SetResourceReference(TextBlock.FontWeightProperty, "UiEmphasisWeight"); _title.SetResourceReference(TextBlock.FontFamilyProperty, "UiTextFamily");
        _title.Tag = "PageHeading";
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); _subtitle.FontSize = 12;
        words.Children.Add(_title); words.Children.Add(_subtitle); heading.Children.Add(words);
        var toolRail = new Border { CornerRadius = new CornerRadius(10, 19, 10, 19), Padding = new Thickness(3), Margin = new Thickness(18, 0, 0, 0), BorderThickness = new Thickness(.6), VerticalAlignment = VerticalAlignment.Center, Child = _tools };
        toolRail.SetResourceReference(Border.BackgroundProperty, "CardBrush"); toolRail.SetResourceReference(Border.BorderBrushProperty, "GlassHighlightBrush");
        Grid.SetColumn(toolRail, 1); heading.Children.Add(toolRail); _body.Children.Add(heading);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); _status.FontSize = 10.5; _status.Margin = new Thickness(0, 15, 0, 0); Grid.SetRow(_status, 3); _body.Children.Add(_status);
        switch (_section)
        {
            case "Settings": BuildSettings(); break;
            case "About": BuildAbout(); break;
            case "Desktop": BuildDesktop(); break;
            default: BuildApps(false); break;
        }
        toolRail.Visibility = _tools.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildSidebar()
    {
        _sidebar.Children.Clear();
        AddNavCaption("工作区");
        AddNav("Launchpad", "\uE71D", "应用库"); AddNav("Desktop", "\uE8A5", "桌面集");
        if (_controller.State.Categories.Count > 0) AddNavCaption("栏目");
        foreach (var category in _controller.State.Categories)
        {
            var captured = category;
            var button = AddNav("Launchpad", "\uE8B7", captured, captured);
            button.ContextMenu = CategoryMenu(captured); EnableCategoryDrop(button, captured);
        }
        var add = ActionButton("新建栏目", CreateCategory); add.Margin = new Thickness(7, 2, 7, 0);
        add.HorizontalAlignment = HorizontalAlignment.Stretch; _sidebar.Children.Add(add);
        var divider = new Border { Height = .5, Margin = new Thickness(16, 16, 16, 19) }; divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); _sidebar.Children.Add(divider);
        AddNavCaption("选项");
        AddNav("Settings", "\uE713", "偏好设置"); AddNav("About", "\uE946", "关于");
    }

    private void AddNavCaption(string label)
    {
        var caption = Text(label, 10, FontWeights.Medium, true); caption.Margin = new Thickness(16, 0, 0, 12);
        caption.SetResourceReference(TextBlock.ForegroundProperty, "CaptionBrush"); _sidebar.Children.Add(caption);
    }

    private Button AddNav(string section, string glyph, string label, string? category = null)
    {
        var button = ActionButton(label, () => { _section = section; if (section is "Launchpad" or "Desktop") _category = category ?? "全部应用"; _page = 0; Render(); });
        var selected = _section == section && (section != "Launchpad" || _category == (category ?? "全部应用"));
        var content = new Grid { Width = 128 }; content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) }); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); content.ColumnDefinitions.Add(new ColumnDefinition());
        var spine = new Border { Width = 2.5, Height = 18, CornerRadius = new CornerRadius(1.25), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = selected ? Visibility.Visible : Visibility.Hidden };
        spine.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); content.Children.Add(spine);
        var icon = Glyph(glyph, 14); icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.SetResourceReference(TextBlock.ForegroundProperty, selected ? "AccentBrush" : "MutedBrush");
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8, 12, 8, 12), HorizontalAlignment = HorizontalAlignment.Left, Child = icon };
        badge.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoftBrush" : "CardBrush"); Grid.SetColumn(badge, 1); content.Children.Add(badge);
        var name = Text(label, 13, selected ? FontWeights.SemiBold : FontWeights.Normal); name.Tag = "NavigationLabel"; name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(name, 2); content.Children.Add(name); button.Content = content;
        button.Tag = category == null ? "Navigation:" + section : "CategoryNavigation:" + category;
        if (category != null) button.ToolTip = category + "\n右键可重命名或删除栏目";
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Padding = new Thickness(9, 9, 10, 9); button.Margin = new Thickness(0, 0, 0, 8); button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent;
        ApplyButtonTemplate(button, new CornerRadius(11, 20, 20, 11), navigation: true);
        if (selected) { button.SetResourceReference(BackgroundProperty, "SelectionBrush"); button.SetResourceReference(BorderBrushProperty, "GlassHighlightBrush"); }
        _sidebar.Children.Add(button);
        return button;
    }

    private void BuildApps(bool desktop)
    {
        _title.Text = _category == "全部应用" ? desktop ? "桌面集" : "应用库" : _category;
        _title.ToolTip = _title.Text;
        _subtitle.Text = "";
        var add = ActionButton(_importing ? "正在添加…" : "添加应用", AddApplication, true); add.IsEnabled = !_importing; add.Tag = "AddApplication"; _tools.Children.Add(add);
        if (_importing) _tools.Children.Add(ActionButton("取消", () => _importCancellation?.Cancel()));
        if (desktop) _tools.Children.Add(ActionButton("桌面图标", ToggleDesktopIcons));
        _tools.Children.Add(ActionButton("刷新", async () => { try { _status.Text = "正在扫描应用…"; await _controller.RefreshAppsAsync(); Render(); } catch (Exception ex) { ShowError(ex); } }));
        var filters = new StackPanel(); Grid.SetRow(filters, 1); _body.Children.Add(filters);
        var searchBox = new Border { CornerRadius = new CornerRadius(12, 21, 12, 21), BorderThickness = new Thickness(.8), Padding = new Thickness(15, 10, 12, 10), Margin = new Thickness(0, 0, 0, 12) };
        searchBox.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); searchBox.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var searchGrid = new Grid(); searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) }); searchGrid.ColumnDefinitions.Add(new ColumnDefinition()); searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glass = Glyph("\uE721", 16); glass.VerticalAlignment = VerticalAlignment.Center; glass.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); searchGrid.Children.Add(glass);
        // Search is a fresh control on each navigation, retaining the query without retaining old event handlers.
        var search = new TextBox { Text = _query, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 14, ToolTip = "名称或路径", VerticalContentAlignment = VerticalAlignment.Center };
        search.SetResourceReference(ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索应用");
        var field = new Grid(); Grid.SetColumn(field, 1); field.Children.Add(search);
        var placeholder = Text("搜索应用", 14, FontWeights.Normal, true); placeholder.IsHitTestVisible = false; placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.Visibility = string.IsNullOrEmpty(_query) ? Visibility.Visible : Visibility.Collapsed; field.Children.Add(placeholder); field.Children.Add(StaticCaret.Create(search));
        var clear = ChromeButton("\uE711", "清空搜索", () => search.Clear()); clear.Width = 23; clear.Height = 23; clear.Margin = new Thickness(5, -3, 0, -3); clear.Visibility = string.IsNullOrEmpty(_query) ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(clear, 2); searchGrid.Children.Add(clear);
        search.TextChanged += (_, _) =>
        {
            _query = search.Text; _page = 0;
            clear.Visibility = string.IsNullOrEmpty(_query) ? Visibility.Collapsed : Visibility.Visible;
            placeholder.Visibility = string.IsNullOrEmpty(_query) ? Visibility.Visible : Visibility.Collapsed;
            RenderAppGrid(desktop);
        };
        searchGrid.Children.Add(field); searchBox.Child = searchGrid; filters.Children.Add(searchBox);
        foreach (var category in new[] { "全部应用" }.Concat(_controller.State.Categories).Distinct())
        {
            var captured = category; var chip = ActionButton(category == "全部应用" ? "全部" : category, () => { _category = captured; _page = 0; Render(); });
            var label = category == "全部应用" ? "全部" : category;
            var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
            chipContent.Children.Add(Text(label, 11));
            var count = Text("", 9, FontWeights.Medium, true); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(7, 0, 0, 0); count.MinWidth = 8;
            _categoryCounts[category] = count; chipContent.Children.Add(count); chip.Content = chipContent;
            System.Windows.Automation.AutomationProperties.SetName(chip, label);
            chip.Tag = category; chip.Padding = new Thickness(9, 7, 9, 7); chip.Margin = new Thickness(0, 0, 6, 7); chip.FontSize = 11;
            ApplyButtonTemplate(chip, new CornerRadius(8, 15, 8, 15));
            if (category != "全部应用")
            {
                EnableCategoryDrop(chip, captured); chip.ContextMenu = CategoryMenu(captured);
                chip.ToolTip = "右键可重命名或删除栏目";
            }
            _categories.Children.Add(chip);
        }
        var newGroup = ActionButton("新建栏目", CreateCategory); newGroup.FontSize = 11; newGroup.Padding = new Thickness(10, 7, 10, 7);
        var createContent = new StackPanel { Orientation = Orientation.Horizontal }; var plus = Glyph("\uE710", 10); plus.Margin = new Thickness(0, 0, 6, 0); createContent.Children.Add(plus); createContent.Children.Add(Text("栏目", 11)); newGroup.Content = createContent;
        System.Windows.Automation.AutomationProperties.SetName(newGroup, "新建栏目");
        ApplyButtonTemplate(newGroup, new CornerRadius(8, 15, 8, 15)); _categories.Children.Add(newGroup); filters.Children.Add(_categories); UpdateCategoryStyles();
        RenderAppGrid(desktop);
    }

    private void UpdateCategoryStyles()
    {
        var source = _applicationGroups.Select(group => group.Entries.Where(app => MatchesSearch(app) && (_section != "Desktop" || _desktopEntryIds.Contains(app.Id))).ToArray()).Where(group => group.Length > 0).ToArray();
        foreach (var button in _categories.Children.OfType<Button>())
        {
            if (button.Tag is not string category) continue;
            var selected = category == _category;
            button.SetResourceReference(BackgroundProperty, selected ? "SelectionBrush" : "CardBrush");
            button.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
            button.SetResourceReference(FontWeightProperty, selected ? "UiEmphasisWeight" : "UiTextWeight");
            if (button.Content is StackPanel labelStack && labelStack.Children.OfType<TextBlock>().FirstOrDefault() is { } label) label.SetResourceReference(TextBlock.FontWeightProperty, selected ? "UiEmphasisWeight" : "UiTextWeight");
            var count = category == "全部应用" ? source.Length : source.Count(group => group.Any(app => app.Category == category));
            if (_categoryCounts.TryGetValue(category, out var countText)) { countText.Text = count.ToString(); countText.SetResourceReference(TextBlock.ForegroundProperty, selected ? "TextBrush" : "CaptionBrush"); }
            System.Windows.Automation.AutomationProperties.SetHelpText(button, $"{count} 个应用");
        }
    }

    private IEnumerable<AppEntry> FilteredApps(bool desktop)
    {
        return _applicationGroups.Select(group => group.Entries.FirstOrDefault(app => MatchesSearch(app) && (!desktop || _desktopEntryIds.Contains(app.Id)) && (_category == "全部应用" || app.Category == _category)))
            .OfType<AppEntry>().OrderBy(app => _applicationOrder.GetValueOrDefault(app.Id, int.MaxValue));
    }

    private bool MatchesSearch(AppEntry app) => !app.Hidden && app.IsAvailable && (string.IsNullOrWhiteSpace(_query)
        || app.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase) || app.LaunchPath.Contains(_query, StringComparison.OrdinalIgnoreCase));

    private void RebuildApplicationGroups()
    {
        _desktopEntryIds = _controller.State.Apps.Where(app => app.DesktopPaths.Any(File.Exists)
            || (app.Source.Equals("Desktop", StringComparison.OrdinalIgnoreCase) && File.Exists(app.LaunchPath))).Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _applicationGroups = ApplicationPresentation.Group(_controller.State.Apps);
        _variantsById = _applicationGroups.SelectMany(group => group.Entries.Select(entry => (entry.Id, group.Entries)))
            .ToDictionary(pair => pair.Id, pair => pair.Entries, StringComparer.OrdinalIgnoreCase);
        _applicationOrder = _controller.State.Apps.Select((entry, index) => (entry.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<AppEntry> Variants(AppEntry app) => _variantsById.TryGetValue(app.Id, out var variants) ? variants : [app];

    private void AssignColumn(AppEntry app, string category)
    {
        foreach (var variant in Variants(app)) Catalog.AssignCategory(variant, category);
        _controller.Save(); Render();
    }

    private void RemoveApplication(AppEntry app)
    {
        foreach (var variant in Variants(app)) { variant.Hidden = true; _controller.State.PinnedIds.Remove(variant.Id); }
        _controller.Save(); Render();
    }

    private void RenderAppGrid(bool desktop)
    {
        foreach (var child in _body.Children.Cast<UIElement>().Where(x => Grid.GetRow(x) == 2).ToList()) _body.Children.Remove(child);
        var apps = FilteredApps(desktop).ToList();
        var availableWidth = _body.ActualWidth > 0 ? _body.ActualWidth : Width - 266;
        var availableHeight = _body.ActualHeight > 0 ? _body.ActualHeight : Height - 114;
        double headings = 0;
        foreach (var header in _body.Children.Cast<UIElement>().Where(x => Grid.GetRow(x) < 2)) { header.Measure(new System.Windows.Size(availableWidth, double.PositiveInfinity)); headings += header.DesiredSize.Height; }
        // Measure the actual headers and footer, rather than reserving a second
        // estimated footer inside the already measured body. Pager has a fixed 44 DIP slot.
        _status.Measure(new System.Windows.Size(availableWidth, double.PositiveInfinity));
        var font = _controller.NameFont;
        var family = new FontFamily(font.FamilyName);
        var gridWidth = Math.Min(1080, Math.Max(300, availableWidth - 5));
        var gridHeight = Math.Max(0, availableHeight - headings - _status.DesiredSize.Height);
        var metrics = AppGridLayout.Calculate(gridWidth, gridHeight - 44, _controller.State.Settings.Columns, font.SizeDip, family.LineSpacing);
        if (apps.Count <= metrics.Columns * metrics.Rows) metrics = AppGridLayout.Calculate(gridWidth, gridHeight, _controller.State.Settings.Columns, font.SizeDip, family.LineSpacing);
        var columns = metrics.Columns; var rows = metrics.Rows;
        var pageSize = columns * rows;
        var pages = Math.Max(1, (int)Math.Ceiling(apps.Count / (double)pageSize)); _page = Math.Clamp(_page, 0, pages - 1);
        var host = new Grid(); host.RowDefinitions.Add(new RowDefinition()); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(host, 2); _body.Children.Add(host);
        var grid = new UniformGrid { Columns = columns, Rows = 0, VerticalAlignment = VerticalAlignment.Top, Width = gridWidth, HorizontalAlignment = HorizontalAlignment.Center };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = grid, Padding = new Thickness(0, 0, 5, 0) }; host.Children.Add(scroll);
        foreach (var app in apps.Skip(_page * pageSize).Take(pageSize)) grid.Children.Add(CreateAppTile(app, desktop, metrics));
        if (apps.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = Glyph("\uE721", 27); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 16); icon.SetResourceReference(ForegroundProperty, "MutedBrush"); empty.Children.Add(icon);
            empty.Children.Add(Text(string.IsNullOrWhiteSpace(_query) ? "暂无应用" : "未找到应用", 17, FontWeights.Medium)); host.Children.Add(empty);
        }
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 34, Margin = new Thickness(0, 10, 0, 0), Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed };
        Grid.SetRow(pager, 1); host.Children.Add(pager);
        if (pages > 1)
        {
            var prev = ActionButton("‹", () => { _page--; RenderAppGrid(desktop); }); prev.Height = 34; prev.IsEnabled = _page > 0; pager.Children.Add(prev);
            var number = Text($"{_page + 1} / {pages}", 11, FontWeights.Normal, true); number.Margin = new Thickness(16, 0, 16, 0); number.VerticalAlignment = VerticalAlignment.Center; pager.Children.Add(number);
            var next = ActionButton("›", () => { _page++; RenderAppGrid(desktop); }); next.Height = 34; next.IsEnabled = _page < pages - 1; pager.Children.Add(next);
        }
        var pins = _controller.State.PinnedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pinnedApplications = _applicationGroups.Count(group => group.Entries.Any(entry => !entry.Hidden && entry.IsAvailable && pins.Contains(entry.Id)));
        _status.Text = $"{apps.Count} 个{(desktop ? "桌面应用" : "应用")}  ·  已固定 {pinnedApplications}";
        UpdateCategoryStyles();
    }

    private FrameworkElement CreateAppTile(AppEntry app, bool desktop, AppGridMetrics metrics)
    {
        var variants = Variants(app);
        var launchModes = variants.Where(variant => !variant.Hidden && variant.IsAvailable).ToArray();
        var vm = new AppTileViewModel(app);
        var tile = new AppTileButton { Margin = new Thickness(5), Padding = new Thickness(7, 6, 7, 4), Height = metrics.TileHeight, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, DataContext = vm, ToolTip = app.Name };
        if (_controller.State.Apps.Any(other => !other.Hidden && other.IsAvailable && other.Id != app.Id && string.Equals(other.Name, app.Name, StringComparison.CurrentCultureIgnoreCase)))
        {
            static string Brief(string value) => value.Length <= 360 ? value : value[..180] + "…" + value[^179..];
            var target = string.IsNullOrEmpty(app.ExecutablePath) ? app.LaunchPath : app.ExecutablePath;
            var details = app.Name + "\n位置：" + Brief(target);
            if (!string.IsNullOrWhiteSpace(app.Arguments)) details += "\n参数：" + Brief(app.Arguments);
            if (launchModes.Length > 1) details += $"\n{launchModes.Length} 种启动方式，右键选择";
            var explanation = Text(details, 12); explanation.MaxWidth = 420; explanation.TextWrapping = TextWrapping.Wrap;
            tile.ToolTip = new System.Windows.Controls.ToolTip { Content = explanation, MaxWidth = 460 };
            System.Windows.Automation.AutomationProperties.SetHelpText(tile, "同名的独立启动入口。" + details);
        }
        ApplyButtonTemplate(tile, new CornerRadius(14, 23, 14, 23), tile: true);
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var image = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); image.SetBinding(Image.SourceProperty, new Binding(nameof(AppTileViewModel.Icon)));
        // A missing Shell icon stays empty: no invented application artwork.
        content.Children.Add(image);
        var name = Text(app.Name); name.Tag = "ApplicationName";
        name.SetResourceReference(TextBlock.FontFamilyProperty, "AppNameFamily"); name.SetResourceReference(TextBlock.FontSizeProperty, "AppNameSize");
        name.SetResourceReference(TextBlock.FontWeightProperty, "AppNameWeight"); name.SetResourceReference(TextBlock.FontStyleProperty, "AppNameStyle");
        name.SetResourceReference(TextBlock.TextDecorationsProperty, "AppNameDecorations");
        name.SetResourceReference(TextBlock.ForegroundProperty, "AppNameBrush");
        name.TextAlignment = TextAlignment.Center; name.TextWrapping = TextWrapping.Wrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.LineHeight = metrics.LineHeight; name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        name.Height = metrics.NameHeight; name.MaxHeight = metrics.NameHeight; name.Margin = new Thickness(0, 10, 0, 0); content.Children.Add(name);
        var dot = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(0, 3, 0, 0), Opacity = variants.Any(variant => _controller.State.PinnedIds.Contains(variant.Id)) ? 1 : 0 }; dot.SetResourceReference(Shape.FillProperty, "AccentBrush"); content.Children.Add(dot);
        tile.Content = content; tile.Click += (_, _) => _controller.Launch(app);
        var lift = new TranslateTransform(); content.RenderTransform = lift;
        void Lift(double y)
        {
            if (_controller.ReducedMotion) return;
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
        tile.MouseEnter += (_, _) => Lift(-2); tile.MouseLeave += (_, _) => Lift(0);
        tile.Loaded += async (_, _) => await vm.LoadIconAsync(_controller, Math.Clamp(Math.Max(80, (int)Math.Ceiling(48 * VisualTreeHelper.GetDpi(image).DpiScaleX / 16) * 16), 32, 256));
        tile.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) =>
        {
            var point = e.GetPosition(tile);
            if (e.LeftButton == MouseButtonState.Pressed && ((point - _dragStart).Length > 7))
            { DragDrop.DoDragDrop(tile, new DataObject(AppDragFormat, app.Id), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); e.Handled = true; }
        };
        tile.AllowDrop = true; tile.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(AppDragFormat) ? LocalDragEffect(e) : DragDropEffects.None; e.Handled = true; };
        tile.Drop += (_, e) =>
        {
            if (GetDragged(e) is { } moved && !variants.Any(variant => variant.Id == moved.Id))
            {
                var list = _controller.State.Apps;
                var moving = Variants(moved).OrderBy(variant => _applicationOrder.GetValueOrDefault(variant.Id, int.MaxValue)).ToArray();
                foreach (var variant in moving) list.Remove(variant);
                // Reordering the unified list never silently changes a manual column.
                if (_category != "全部应用") foreach (var variant in moving) Catalog.AssignCategory(variant, _category);
                list.InsertRange(Math.Max(0, list.IndexOf(app)), moving); _controller.Save(); Render();
            }
            e.Handled = true;
        };
        var menu = new ContextMenu { Padding = new Thickness(5), BorderThickness = new Thickness(.8) };
        menu.SetResourceReference(Control.BackgroundProperty, "PopupBrush"); menu.SetResourceReference(Control.ForegroundProperty, "TextBrush"); menu.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        menu.Items.Add(Menu("打开应用", () => _controller.Launch(app)));
        if (launchModes.Length > 1)
        {
            var modes = new MenuItem { Header = "启动方式" };
            for (var index = 0; index < launchModes.Length; index++)
            {
                var variant = launchModes[index];
                var source = variant.Source switch { "StartMenu" => "开始菜单", "Desktop" => "桌面", "AppsFolder" => "系统注册", "Manual" => "手动入口", _ => "启动入口" };
                var parameters = string.IsNullOrWhiteSpace(variant.Arguments) ? "默认参数" : variant.Arguments.Length <= 80 ? variant.Arguments : variant.Arguments[..60] + "…" + variant.Arguments[^19..];
                var mode = Menu($"{index + 1}. {source} · {parameters}", () => _controller.LaunchNewInstance(variant));
                mode.Tag = variant;
                var explanation = Text("位置：" + variant.LaunchPath + (string.IsNullOrEmpty(variant.Arguments) ? "" : "\n参数：" + variant.Arguments)
                    + (string.IsNullOrEmpty(variant.WorkingDirectory) ? "" : "\n工作目录：" + variant.WorkingDirectory), 12);
                explanation.TextWrapping = TextWrapping.Wrap; explanation.MaxWidth = 420; explanation.MaxHeight = 240;
                mode.ToolTip = new System.Windows.Controls.ToolTip { Content = explanation, MaxWidth = 460 };
                modes.Items.Add(mode);
            }
            menu.Items.Add(modes);
            System.Windows.Automation.AutomationProperties.SetHelpText(tile, $"{launchModes.Length} 种启动方式，可在右键菜单中选择。原始参数保留。");
        }
        var pinned = variants.Any(variant => _controller.State.PinnedIds.Contains(variant.Id));
        menu.Items.Add(Menu(pinned ? "从 Dock 移除" : "固定到 Dock", () =>
        {
            if (!pinned) _controller.Pin(app);
            else { foreach (var variant in variants) _controller.State.PinnedIds.Remove(variant.Id); _controller.Save(); }
        }));
        var groups = new MenuItem { Header = "放入栏目" };
        groups.Items.Add(Menu("不放入栏目", () => AssignColumn(app, "")));
        foreach (var category in _controller.State.Categories) { var captured = category; groups.Items.Add(Menu(category, () => AssignColumn(app, captured))); }
        menu.Items.Add(groups);
        menu.Items.Add(Menu("更换图标…", () => _controller.ChooseIcon(app, this)));
        var restoreIcon = Menu("恢复原图标", () => _controller.SetCustomIcon(app, "")); restoreIcon.IsEnabled = !string.IsNullOrEmpty(app.CustomIconPath); menu.Items.Add(restoreIcon);
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("删除管理记录…", () =>
        {
            if (MessageBox.Show(this, $"从 LumaDock 隐藏“{app.Name}”？\n应用、桌面快捷方式和原始文件都将保留。可在数据管理中恢复隐藏记录。", "删除管理记录", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            { RemoveApplication(app); }
        }));
        tile.ContextMenu = menu; return tile;
    }

    private AppEntry? GetDragged(System.Windows.DragEventArgs e) => e.Data.GetDataPresent(AppDragFormat) ? _controller.State.Apps.FirstOrDefault(a => a.Id == (string?)e.Data.GetData(AppDragFormat)) : null;
    private static DragDropEffects LocalDragEffect(System.Windows.DragEventArgs e) => (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : e.AllowedEffects & DragDropEffects.Copy;

    private async void AddApplication()
    {
        if (_importing) return;
        try
        {
            var picker = new OpenFileDialog { Title = "添加应用", Filter = "应用与快捷方式|*.exe;*.lnk;*.url;*.appref-ms|所有文件|*.*", CheckFileExists = true, Multiselect = true, DereferenceLinks = false };
            if (picker.ShowDialog(this) != true) return;
            await ImportApplicationPathsAsync(picker.FileNames);
        }
        catch (Exception error) { ShowError(error); }
    }

    // One path for the file picker and tests; only the picker itself needs modal UI.
    private async Task ImportApplicationPathsAsync(IEnumerable<string> paths)
    {
        if (_importing) return;
        _importing = true; _importCancellation = new CancellationTokenSource(); Render(); _status.Text = "正在读取应用信息…";
        try
        {
            var result = await _controller.ImportApplicationsAsync(paths, _importCancellation.Token);
            if (_disposed) return;
            if (result.Cancelled) { Render(); _status.Text = "已取消添加"; return; }
            if (result.Busy) { _status.Text = "已有添加任务，请稍候"; return; }
            if (result.Entries.Count > 0)
            {
                _section = "Launchpad"; _category = "全部应用"; _query = ""; _page = 0; Render();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                if (_disposed) return;
                // Show the first imported logical app even when its existing record
                // is far beyond the first page. Filters never hide the result.
                var first = result.Entries[0].Id;
                var index = FilteredApps(false).ToList().FindIndex(app => app.Id == first || Variants(app).Any(variant => variant.Id == first));
                var rendered = _body.Children.OfType<Grid>().Where(child => Grid.GetRow(child) == 2).SelectMany(child => child.Children.OfType<ScrollViewer>()).Select(scroll => scroll.Content).OfType<UniformGrid>().FirstOrDefault();
                var pageSize = rendered?.Children.Count ?? 0;
                if (index >= 0 && pageSize > 0) { _page = index / pageSize; RenderAppGrid(false); }
            }
            else Render();
            _status.Text = $"已加入 {result.Entries.Count} 个应用" + (result.Failures.Count > 0 ? $"，{result.Failures.Count} 个未能加入" : "");
            if (result.Failures.Count > 0) _controller.Notify(string.Join("\n", result.Failures.Take(3).Select(failure => failure.Name + "：" + failure.Reason)));
        }
        finally
        {
            _importing = false; _importCancellation?.Dispose(); _importCancellation = null;
            foreach (var button in _tools.Children.OfType<Button>()) if (Equals(button.Tag, "AddApplication")) { button.IsEnabled = true; button.Content = "添加应用"; System.Windows.Automation.AutomationProperties.SetName(button, "添加应用"); }
            foreach (var button in _tools.Children.OfType<Button>().Where(button => Equals(System.Windows.Automation.AutomationProperties.GetName(button), "取消")).ToArray()) _tools.Children.Remove(button);
        }
    }

    private void CreateCategory()
    {
        var name = AskText("新建栏目", "栏目名称", "", "创建");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!TryCreateCategory(name)) _controller.Notify("请使用 1–40 个字符，并避开已有名称。列名“全部”由应用保留。");
    }

    private bool TryCreateCategory(string name)
    {
        if (!Catalog.CreateCategory(_controller.State, name)) return false;
        _section = "Launchpad"; _category = name.Trim(); _page = 0; _controller.Save(); Render(); return true;
    }

    private void RenameCategory(string category)
    {
        var name = AskText("重命名栏目", "栏目名称", category);
        if (name == null || string.Equals(name.Trim(), category, StringComparison.Ordinal)) return;
        if (!TryRenameCategory(category, name)) _controller.Notify("请使用 1–40 个字符，并避开已有名称。列名“全部”由应用保留。");
    }

    private bool TryRenameCategory(string category, string name)
    {
        if (!Catalog.RenameCategory(_controller.State, category, name)) return false;
        if (_category == category) _category = name.Trim();
        _controller.Save(); Render(); return true;
    }

    private void DeleteCategory(string category)
    {
        if (!Catalog.DeleteCategory(_controller.State, category)) return;
        if (_category == category) _category = "全部应用";
        _page = 0; _controller.Save(); Render();
    }

    private ContextMenu CategoryMenu(string category)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Menu("重命名栏目…", () => RenameCategory(category)));
        var remove = Menu("删除栏目", () => DeleteCategory(category));
        remove.ToolTip = "应用仍保留在“全部”中，不改变原文件或 Dock 固定状态。";
        menu.Items.Add(remove); return menu;
    }

    private void EnableCategoryDrop(Button button, string category)
    {
        button.AllowDrop = true;
        button.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(AppDragFormat) ? LocalDragEffect(e) : DragDropEffects.None; e.Handled = true; };
        button.Drop += (_, e) =>
        {
            if (GetDragged(e) is { } app) AssignColumn(app, category);
            e.Handled = true;
        };
    }

    private void ToggleDesktopIcons()
    {
        try
        {
            var visible = DesktopVisibility.IsVisible;
            if (MessageBox.Show(this, visible ? "隐藏 Windows 桌面图标？这会改变 Explorer 的桌面显示设置。\n可再次点击此按钮显示，或在桌面右键 → 查看 → 显示桌面图标恢复。" : "显示 Windows 桌面图标？这会改变 Explorer 的桌面显示设置。", "更改桌面图标显示", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            DesktopVisibility.SetVisible(!visible); _status.Text = visible ? "已隐藏桌面图标。可用本按钮或桌面右键菜单恢复。" : "已显示桌面图标。";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BuildSettings()
    {
        _title.Text = "偏好设置"; _subtitle.Text = ""; _status.Text = "自动保存";
        _fontPreview = null; _fontPreviewInfo = null;
        var previousOffset = _settingsScrollOffset;
        var stack = new StackPanel(); var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); _body.Children.Add(scroll);
        scroll.Resources[typeof(ScrollBar)] = CenterStyles.ScrollBarStyle();
        _settingsRoot = stack; _settingsGroup = null;
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(previousOffset);
        scroll.ScrollChanged += (_, e) => _settingsScrollOffset = e.VerticalOffset;
        var settings = _controller.State.Settings;
        var materialHeading = Section(stack, "材质与颜色", "");
        AccentPaletteRow(stack);
        ToggleRow(stack, "液态玻璃", "玻璃边缘与透景材质", settings.LiquidGlass, value => settings.LiquidGlass = value);
        ToggleRow(stack, "动态透景", "系统实时合成；关闭后使用静态折射", settings.LiveGlass, value => { settings.LiveGlass = value; Dispatcher.BeginInvoke(() => Render()); });
        if (settings.LiveGlass)
        {
            SelectRow(stack, "透景风格", "", new[] { ("Clear", "清透"), ("Frosted", "磨砂") }, settings.LiveGlassStyle, value => { settings.LiveGlassStyle = value; Dispatcher.BeginInvoke(() => Render()); });
            if (settings.LiveGlassStyle == "Clear") SliderRow(stack, "窗口浓度", .25, .9, settings.BackdropOpacity, value => settings.BackdropOpacity = value, value => $"{value:P0}");
        }
        ToggleRow(stack, "玻璃流动", "随指针轻轻起伏，停下后逐渐收住", settings.GlassMotion, value => settings.GlassMotion = value);
        SliderRow(stack, "流动幅度", 0, 1, settings.GlassFlowStrength, value => settings.GlassFlowStrength = value, value => $"{value:P0}");
        if (!settings.LiveGlass) SliderRow(stack, "静态折射清晰度", .1, 1, settings.GlassClarity, value => settings.GlassClarity = value, value => $"{value:P0}");
        SelectRow(stack, "主题", "", new[] { ("System", "跟随系统"), ("Dark", "深色"), ("Light", "浅色") }, settings.Theme, value => settings.Theme = value);
        SliderRow(stack, "Dock 浓度", .02, 1, settings.Opacity, value => settings.Opacity = value, value => $"{value:P0}", "数值越低，底座越通透；图标保持清楚");
        SliderRow(stack, "圆角", 8, 36, settings.CornerRadius, value => settings.CornerRadius = value, value => $"{value:0} px");
        SliderRow(stack, "图标大小", 32, 80, settings.IconSize, value => settings.IconSize = value, value => $"{value:0} px");
        SliderRow(stack, "图标间距", 3, 24, settings.Spacing, value => settings.Spacing = value, value => $"{value:0} px");
        var dockHeading = Section(stack, "Dock", "");
        DockShapeRow(stack);
        ToggleRow(stack, "图标倒影", "淡淡映在底座上；省电与高对比度时暂时关闭", settings.IconReflection, value => settings.IconReflection = value);
        SliderRow(stack, "倒影强度", 0, .95, settings.ReflectionStrength, value => settings.ReflectionStrength = value, value => $"{value:P0}");
        SliderRow(stack, "图标鲜艳度", 0, 1, settings.DockIconVibrancy, value => settings.DockIconVibrancy = value, value => $"{value:P0}", "提亮图标、增加色彩；0% 保留原色");
        SelectRow(stack, "位置", "", new[] { ("Bottom", "底部"), ("Left", "左侧"), ("Right", "右侧"), ("Top", "顶部") }, settings.Position, value => settings.Position = value);
        var monitors = new List<(string, string)> { ("", "主显示器（自动）") };
        try { monitors.AddRange(MonitorService.GetMonitors().Select(m => (m.DeviceName, m.Label))); } catch (Exception ex) { Debug.WriteLine(ex); }
        SelectRow(stack, "显示器", "断开后使用主显示器", monitors.ToArray(), settings.MonitorDevice, value => settings.MonitorDevice = value);
        ToggleRow(stack, "自动隐藏", "鼠标靠近 Dock 边缘时重新显示", settings.AutoHide, value => settings.AutoHide = value);
        SliderRow(stack, "悬停放大强度", 0, 1, settings.AnimationStrength, value => settings.AnimationStrength = value, value => $"{value:P0}");
        var fontHeading = Section(stack, "字体与文字", "侧栏、栏目与应用名称一起调整，仅对 LumaDock 生效。");
        ToggleRow(stack, "跟随 Windows 图标字体", "读取系统的字体与粗细；名称字号随系统变化", settings.FollowSystemIconFont, value => { settings.FollowSystemIconFont = value; Dispatcher.BeginInvoke(() => Render()); });
        if (!settings.FollowSystemIconFont)
        {
            NameFontFamilyRow(stack);
            SliderRow(stack, "名称字号", 10, 40, settings.NameFontSize, value => { settings.NameFontSize = value; UpdateNameFontPreview(); }, value => $"{value * .75:0.#} 磅");
            ToggleRow(stack, "加粗", "同时用于侧栏、栏目和应用名称", settings.NameFontBold, value => { settings.NameFontBold = value; UpdateNameFontPreview(); });
            ToggleRow(stack, "斜体", "", settings.NameFontItalic, value => { settings.NameFontItalic = value; UpdateNameFontPreview(); });
        }
        TextColorRow(stack);
        BuildNameFontPreview(stack);
        var systemFont = _controller.SystemIconFont;
        ActionRow(stack, "系统图标字体", $"{systemFont.FamilyName} · {systemFont.SizeDip * .75:0.##} 磅{(systemFont.Weight >= 600 ? " · 加粗" : "")}", "刷新字体", () => { _controller.RefreshSystemFont(); _status.Text = "已读取系统图标字体"; });
        Section(stack, "应用整理", "");
        SelectRow(stack, "每行应用数", "", Enumerable.Range(4, 7).Select(i => (i.ToString(), i + " 个")).ToArray(), settings.Columns.ToString(), value => settings.Columns = int.Parse(value));
        ActionRow(stack, "已隐藏的应用", $"{_controller.State.Apps.Count(a => a.Hidden)} 个应用", "恢复全部", () => { foreach (var app in _controller.State.Apps) app.Hidden = false; _controller.Save(); BuildSettingsRefresh(); });
        ActionRow(stack, "Windows 桌面图标", "主动显示或隐藏，可用同一按钮或 Windows 桌面菜单恢复", "显示 / 隐藏", ToggleDesktopIcons);
        Section(stack, "性能与启动", "");
        ToggleRow(stack, "界面动画", "", settings.Animations, value => settings.Animations = value);
        ToggleRow(stack, "省电模式", "减少动画与悬停效果", settings.PowerSaver, value => settings.PowerSaver = value);
        ToggleRow(stack, "关闭窗口后驻留托盘", "取消后，关闭管理窗口会退出 LumaDock", settings.CloseToTray, value => settings.CloseToTray = value);
        bool startupEnabled; try { startupEnabled = StartupService.IsEnabled; } catch { startupEnabled = settings.StartWithWindows; }
        var startup = ToggleRow(stack, "随 Windows 启动", "登录后自动打开", startupEnabled, value => { }, save: false);
        startup.Click += (_, _) =>
        {
            var enable = startup.IsChecked == true;
            if (MessageBox.Show(this, enable ? "为当前用户启用 LumaDock 开机启动？\n将在用户“启动”文件夹创建 LumaDock 快捷方式。" : "关闭 LumaDock 开机启动？\n将移除 LumaDock 创建的启动快捷方式。", "更改开机启动", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { startup.IsChecked = !enable; return; }
            try
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
                if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请从发布目录的 LumaDock.exe 运行，再启用开机启动。");
                StartupService.SetEnabled(enable, executable); settings.StartWithWindows = enable; SaveSettings();
            }
            catch (Exception ex) { startup.IsChecked = !enable; ShowError(ex); }
        };
        Section(stack, "数据", "");
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) }; SettingsContainer(stack).Children.Add(actions);
        actions.Children.Add(ActionButton("导出配置", () => { var dialog = new SaveFileDialog { Filter = "LumaDock JSON|*.json", FileName = "LumaDock-config.json" }; if (dialog.ShowDialog(this) == true) Safe(() => _controller.Export(dialog.FileName)); }));
        actions.Children.Add(ActionButton("导入配置…", () => { var dialog = new OpenFileDialog { Filter = "LumaDock JSON|*.json" }; if (dialog.ShowDialog(this) == true && MessageBox.Show(this, "用所选配置替换当前整理结果与偏好？\n开机启动和桌面显示设置不会随导入而更改。建议先导出当前配置。", "导入配置", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) Safe(() => { _controller.Import(dialog.FileName); Render(); }); }));
        actions.Children.Add(ActionButton("恢复默认值…", () => { if (MessageBox.Show(this, "恢复 LumaDock 默认配置？栏目和 Dock 排列将重置。\n不会移动文件，也不会改变开机启动或桌面图标显示。", "恢复默认值", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) Safe(() => { _controller.Reset(); _category = "全部应用"; Render(); }); }));
        void JumpTo(FrameworkElement heading) => scroll.ScrollToVerticalOffset(Math.Max(0, heading.TranslatePoint(new Point(), stack).Y - 18));
        _tools.Children.Add(ActionButton("材质", () => JumpTo(materialHeading)));
        _tools.Children.Add(ActionButton("文字", () => JumpTo(fontHeading)));
        _tools.Children.Add(ActionButton("Dock", () => JumpTo(dockHeading)));
    }

    private void AccentPaletteRow(StackPanel parent)
    {
        var row = SettingRow(parent, "强调色", "用于按钮、选中状态和玻璃边缘");
        row.ColumnDefinitions[1].Width = new GridLength(322);
        var choices = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 322 };
        var colors = new (string Value, string Label, string Color)[]
        {
            ("Lagoon", "潮蓝", "#75B8E4"), ("Jade", "青瓷", "#78B99C"),
            ("Amber", "琥珀", "#D5A454"), ("Iris", "烟紫", "#B59BD7"),
            ("Rose", "玫瑰", "#D69BAA"), ("Graphite", "石墨", "#889AA9")
        };
        void UpdateSelection()
        {
            foreach (var button in choices.Children.OfType<Button>())
            {
                var selected = Equals(button.Tag, "Accent:" + _controller.State.Settings.Accent);
                button.SetResourceReference(BackgroundProperty, selected ? "SelectionBrush" : "PanelBrush");
                button.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "GlassHighlightBrush");
                System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已选择" : "");
            }
        }
        foreach (var color in colors)
        {
            var button = ActionButton("", () => { _controller.State.Settings.Accent = color.Value; SaveSettings(); UpdateSelection(); });
            button.Tag = "Accent:" + color.Value; button.Width = 48; button.Padding = new Thickness(4, 8, 4, 6); button.Margin = new Thickness(0, 0, 5, 0);
            ApplyButtonTemplate(button, new CornerRadius(9, 16, 9, 16));
            var content = new StackPanel();
            content.Children.Add(new Ellipse { Width = 17, Height = 17, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Color)), HorizontalAlignment = HorizontalAlignment.Center });
            var label = Text(color.Label, 10); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 5, 0, 0); content.Children.Add(label); button.Content = content;
            System.Windows.Automation.AutomationProperties.SetName(button, "强调色 · " + color.Label); choices.Children.Add(button);
        }
        UpdateSelection(); Grid.SetColumn(choices, 1); row.Children.Add(choices);
    }

    private void DockShapeRow(StackPanel parent)
    {
        var row = SettingRow(parent, "Dock 轮廓", "");
        var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var shapes = new (string Value, string Label, CornerRadius Radius)[]
        {
            ("Capsule", "胶囊", new CornerRadius(12)),
            ("Pebble", "卵石", new CornerRadius(8, 16, 8, 16)),
            ("Rail", "轨道", new CornerRadius(5))
        };
        void UpdateSelection()
        {
            foreach (var button in choices.Children.OfType<Button>())
            {
                var selected = Equals(button.Tag, "DockShape:" + _controller.State.Settings.DockShape);
                button.SetResourceReference(BackgroundProperty, selected ? "SelectionBrush" : "PanelBrush");
                button.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "GlassHighlightBrush");
                System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已选择" : "");
            }
        }
        foreach (var shape in shapes)
        {
            var button = ActionButton("", () => { _controller.State.Settings.DockShape = shape.Value; SaveSettings(); UpdateSelection(); });
            button.Tag = "DockShape:" + shape.Value; button.Width = 68; button.Padding = new Thickness(6, 9, 6, 7); button.Margin = new Thickness(0, 0, 6, 0);
            ApplyButtonTemplate(button, new CornerRadius(9, 16, 9, 16));
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < 3; i++)
            {
                var dot = new Border { Width = 5, Height = 7, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(2, 0, 2, 0), Opacity = i == 1 ? 1 : .65 };
                dot.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); dots.Children.Add(dot);
            }
            var preview = new Border { Width = 44, Height = shape.Value == "Rail" ? 18 : 24, CornerRadius = shape.Radius, BorderThickness = new Thickness(.8), Child = dots, VerticalAlignment = VerticalAlignment.Center };
            preview.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); preview.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            var previewArea = new Grid { Height = 25 }; previewArea.Children.Add(preview);
            var content = new StackPanel(); content.Children.Add(previewArea);
            var label = Text(shape.Label, 10); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 6, 0, 0); content.Children.Add(label); button.Content = content;
            System.Windows.Automation.AutomationProperties.SetName(button, "Dock 形状 · " + shape.Label); choices.Children.Add(button);
        }
        UpdateSelection(); Grid.SetColumn(choices, 1); row.Children.Add(choices);
    }

    private void NameFontFamilyRow(StackPanel parent)
    {
        var settings = _controller.State.Settings;
        var options = InstalledNameFonts.Value.ToList();
        if (!options.Any(option => option.Value.Equals(settings.NameFontFamily, StringComparison.OrdinalIgnoreCase)))
            options.Add(new Choice(settings.NameFontFamily, settings.NameFontFamily + "（未安装）"));
        var row = SettingRow(parent, "字体", "从已安装字体中选择");
        var combo = new ComboBox
        {
            ItemsSource = options, DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), SelectedValue = settings.NameFontFamily,
            IsEditable = false, IsTextSearchEnabled = true, Padding = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Center, MinHeight = 34,
            Tag = "NameFontFamily"
        };
        // Restrict selection to fonts enumerated by WPF; never interpret a typed URI or font-file path.
        TextSearch.SetTextPath(combo, nameof(Choice.Label));
        VirtualizingPanel.SetIsVirtualizing(combo, true); ScrollViewer.SetCanContentScroll(combo, true);
        System.Windows.Automation.AutomationProperties.SetName(combo, "字体");
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedValue is string value)
            {
                settings.NameFontFamily = value; SaveSettings(); UpdateNameFontPreview();
            }
        };
        Grid.SetColumn(combo, 1); row.Children.Add(combo);
    }

    private void TextColorRow(StackPanel parent)
    {
        var settings = _controller.State.Settings;
        var row = SettingRow(parent, "文字颜色", "自动配色随深浅主题切换");
        var palette = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 224 };
        var choices = new[] { ("", "自动"), ("#F5F2EC", "白瓷"), ("#B8E2F5", "冰蓝"), ("#BCE8D4", "薄荷"), ("#F3D7A8", "砂金"), ("#263749", "墨色") };
        void RefreshSelection()
        {
            foreach (var control in palette.Children.OfType<Button>())
            {
                var selected = Equals(control.Tag, "TextColor:" + settings.TextColor);
                control.SetResourceReference(BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
                System.Windows.Automation.AutomationProperties.SetItemStatus(control, selected ? "已选择" : "");
            }
        }
        foreach (var choice in choices)
        {
            var button = new Button { Tag = "TextColor:" + choice.Item1, Width = string.IsNullOrEmpty(choice.Item1) ? 49 : 28, Height = 30, Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(0), ToolTip = choice.Item2 };
            if (string.IsNullOrEmpty(choice.Item1)) button.Content = Text("自动", 10);
            else button.Content = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(4, 7, 4, 7), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(choice.Item1)) };
            button.SetResourceReference(BackgroundProperty, "CardBrush");
            ApplyButtonTemplate(button, new CornerRadius(7, 12, 7, 12));
            System.Windows.Automation.AutomationProperties.SetName(button, "文字颜色 · " + choice.Item2);
            button.Click += (_, _) => { settings.TextColor = choice.Item1; SaveSettings(); UpdateNameFontPreview(); RefreshSelection(); };
            palette.Children.Add(button);
        }
        RefreshSelection(); Grid.SetColumn(palette, 1); row.Children.Add(palette);
        ActionRow(parent, "自定义文字颜色", string.IsNullOrEmpty(settings.TextColor) ? "跟随主题" : settings.TextColor, "自选颜色…", () =>
        {
            var value = AskText("文字颜色", "十六进制色值，例如 #D7ECF4；留空恢复自动", settings.TextColor);
            if (value == null) return;
            value = value.Trim().ToUpperInvariant();
            if (value.Length != 0 && (value.Length != 7 || value[0] != '#' || !value.AsSpan(1).ToString().All(Uri.IsHexDigit)))
            { _controller.Notify("请输入六位十六进制颜色，例如 #D7ECF4。"); return; }
            settings.TextColor = value; SaveSettings(); Render();
        });
    }

    private void BuildNameFontPreview(StackPanel parent)
    {
        var card = new Border { CornerRadius = new CornerRadius(12, 21, 12, 21), BorderThickness = new Thickness(.8), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 13, 0, 9) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, "GlassHighlightBrush");
        var content = new StackPanel(); content.Children.Add(Text("文字预览", 11, FontWeights.Normal, true));
        var examples = _controller.State.Apps.Where(app => !app.Hidden).Select(app => app.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().Take(2).ToArray();
        _fontPreview = Text(examples.Length > 0 ? string.Join("    ", examples) : "我的应用    桌面快捷方式");
        _fontPreview.Tag = "NameFontPreview"; _fontPreview.TextWrapping = TextWrapping.Wrap; _fontPreview.Margin = new Thickness(0, 13, 0, 12);
        System.Windows.Automation.AutomationProperties.SetName(_fontPreview, "字体预览");
        content.Children.Add(_fontPreview);
        _fontPreviewInfo = Text("", 10, FontWeights.Normal, true); _fontPreviewInfo.TextWrapping = TextWrapping.Wrap; content.Children.Add(_fontPreviewInfo);
        card.Child = content; SettingsContainer(parent).Children.Add(card); UpdateNameFontPreview();
    }

    private void UpdateNameFontPreview()
    {
        if (_fontPreview == null) return;
        var font = _controller.NameFont;
        _fontPreview.FontFamily = new FontFamily(font.FamilyName); _fontPreview.FontSize = font.SizeDip;
        _fontPreview.FontWeight = FontWeight.FromOpenTypeWeight(font.Weight); _fontPreview.FontStyle = font.Italic ? FontStyles.Italic : FontStyles.Normal;
        _fontPreview.SetResourceReference(TextBlock.ForegroundProperty, "AppNameBrush");
        var decorations = new TextDecorationCollection();
        if (font.Underline) decorations.Add(TextDecorations.Underline[0]);
        if (font.Strikeout) decorations.Add(TextDecorations.Strikethrough[0]);
        _fontPreview.TextDecorations = decorations;
        var name = !_controller.State.Settings.FollowSystemIconFont && string.IsNullOrEmpty(_controller.State.Settings.NameFontFamily) ? "界面默认" : font.FamilyName;
        if (_fontPreviewInfo != null) _fontPreviewInfo.Text = $"{name} · {font.SizeDip * .75:0.#} 磅 · {(font.Weight >= 600 ? "加粗" : "常规")}{(font.Italic ? " · 斜体" : "")}";
    }

    private void BuildSettingsRefresh() => Render();

    private void SaveSettings()
    {
        _savingSettings = true;
        try
        {
            _controller.ApplySettings();
            _shell.CornerRadius = new CornerRadius(_controller.State.Settings.CornerRadius);
            _glassRim.Radius = _controller.State.Settings.CornerRadius;
            UpdateGlassMotion();
            _liquid.Refresh();
            WindowBackdrop.Refresh(this, Theme.IsDark, _controller.State.Settings.PowerSaver);
            UpdateSurfaceTint();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _savingSettings = false; }
    }

    private void UpdateGlassMotion()
    {
        _glassRim.AccentColor = Theme.AccentColor;
        _glassRim.MotionStrength = _controller.State.Settings.GlassFlowStrength;
        _glassRim.MotionEnabled = _controller.State.Settings.GlassMotion && _controller.State.Settings.LiquidGlass && !_controller.ReducedMotion;
    }

    private void UpdateSurfaceTint()
    {
        if (WindowBackdrop.GetStatus(this).ClearActive)
        {
            var color = ((SolidColorBrush)FindResource("OpaqueSurfaceBrush")).Color;
            color.A = (byte)Math.Round(_controller.State.Settings.BackdropOpacity * 255);
            var brush = new SolidColorBrush(color); brush.Freeze(); _shell.Background = brush;
        }
        else _shell.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
    }

    private StackPanel Section(StackPanel parent, string title, string subtitle)
    {
        var group = new StackPanel();
        var card = new Border { CornerRadius = new CornerRadius(15, 25, 15, 25), Padding = new Thickness(20, 17, 20, 7), BorderThickness = new Thickness(.65), Margin = new Thickness(0, 0, 8, 14), Child = group };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush"); card.SetResourceReference(Border.BorderBrushProperty, "GlassHighlightBrush");
        parent.Children.Add(card); _settingsGroup = group;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        var titleRow = new Grid(); titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37) }); titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = title switch { "材质与颜色" => "\uE790", "Dock" => "\uE739", "字体与文字" => "\uE8D2", "应用整理" => "\uE71D", "性能与启动" => "\uE7E8", _ => "\uE8A5" };
        var mark = Glyph(glyph, 13); mark.HorizontalAlignment = HorizontalAlignment.Center; mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var badge = new Border { Width = 27, Height = 27, CornerRadius = new CornerRadius(7, 11, 7, 11), HorizontalAlignment = HorizontalAlignment.Left, Child = mark };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); titleRow.Children.Add(badge);
        var name = Text(title, 15, FontWeights.SemiBold); name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 1); titleRow.Children.Add(name); heading.Children.Add(titleRow);
        if (!string.IsNullOrEmpty(subtitle)) { var hint = Text(subtitle, 10.5, FontWeights.Normal, true); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(37, 6, 0, 3); heading.Children.Add(hint); }
        group.Children.Add(heading); return heading;
    }

    private StackPanel SettingsContainer(StackPanel parent) => ReferenceEquals(parent, _settingsRoot) ? _settingsGroup ?? parent : parent;

    private Grid SettingRow(StackPanel parent, string title, string subtitle)
    {
        var border = new Border { BorderThickness = new Thickness(0, 0, 0, .5), Padding = new Thickness(0, 14, 0, 14) }; border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); SettingsContainer(parent).Children.Add(border);
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) }); border.Child = row;
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; label.Children.Add(Text(title, 13));
        if (!string.IsNullOrEmpty(subtitle)) { var sub = Text(subtitle, 10, FontWeights.Normal, true); sub.TextWrapping = TextWrapping.Wrap; sub.Margin = new Thickness(0, 5, 18, 0); label.Children.Add(sub); }
        row.Children.Add(label); return row;
    }

    private void SelectRow(StackPanel parent, string title, string subtitle, (string Value, string Label)[] options, string selected, Action<string> set)
    {
        var row = SettingRow(parent, title, subtitle); var combo = new ComboBox { ItemsSource = options.Select(x => new Choice(x.Value, x.Label)).ToArray(), DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value), SelectedValue = selected, Padding = new Thickness(9, 6, 9, 6), VerticalAlignment = VerticalAlignment.Center, MinHeight = 34 };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is string value) { set(value); SaveSettings(); } }; Grid.SetColumn(combo, 1); row.Children.Add(combo);
    }

    private CheckBox ToggleRow(StackPanel parent, string title, string subtitle, bool value, Action<bool> set, bool save = true)
    {
        var row = SettingRow(parent, title, subtitle); var check = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Template = CenterStyles.ToggleTemplate };
        System.Windows.Automation.AutomationProperties.SetName(check, title);
        check.SetResourceReference(ForegroundProperty, "TextBrush");
        check.Click += (_, _) => { set(check.IsChecked == true); if (save) SaveSettings(); }; Grid.SetColumn(check, 1); row.Children.Add(check); return check;
    }

    private void SliderRow(StackPanel parent, string title, double min, double max, double value, Action<double> set, Func<double, string> format, string subtitle = "")
    {
        var row = SettingRow(parent, title, subtitle); var group = new Grid(); group.ColumnDefinitions.Add(new ColumnDefinition()); group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), IsMoveToPointEnabled = true, Template = CenterStyles.SliderTemplate };
        System.Windows.Automation.AutomationProperties.SetName(slider, title);
        var number = Text(format(value), 11, FontWeights.Normal, true); number.VerticalAlignment = VerticalAlignment.Center; number.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(number, 1); group.Children.Add(number); group.Children.Add(slider);
        slider.ValueChanged += (_, _) => { set(slider.Value); number.Text = format(slider.Value); };
        slider.PreviewMouseLeftButtonUp += (_, _) => SaveSettings(); slider.LostKeyboardFocus += (_, _) => SaveSettings();
        Grid.SetColumn(group, 1); row.Children.Add(group);
    }

    private void ActionRow(StackPanel parent, string title, string subtitle, string action, Action callback)
    {
        var row = SettingRow(parent, title, subtitle); var button = ActionButton(action, callback); button.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(button, 1); row.Children.Add(button);
    }

    private void BuildAbout()
    {
        _title.Text = "关于"; _subtitle.Text = ""; _status.Text = "";
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -30, 0, 0) }; Grid.SetRow(panel, 2); _body.Children.Add(panel);
        var icon = new Image { Source = Icon, Width = 86, Height = 86, Margin = new Thickness(0, 0, 0, 22) }; RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality); panel.Children.Add(icon);
        var name = Text("LumaDock", 32, FontWeights.SemiBold); name.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(name);
        var description = Text("桌面 Dock 与应用管理", 13, FontWeights.Normal, true); description.HorizontalAlignment = HorizontalAlignment.Center; description.Margin = new Thickness(0, 9, 0, 15); panel.Children.Add(description);
        var version = Text("v" + (typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "0.10.0"), 11, FontWeights.Medium); version.HorizontalAlignment = HorizontalAlignment.Center;
        var versionBadge = new Border { Child = version, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(13, 6, 13, 6), CornerRadius = new CornerRadius(7, 13, 7, 13), Margin = new Thickness(0, 0, 0, 23) };
        versionBadge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); panel.Children.Add(versionBadge);
        var license = Text("MIT 许可证", 12, FontWeights.Normal, true); license.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(license);
        var credits = Text("应用名称及图标归各自所有者所有。", 11, FontWeights.Normal, true); credits.Margin = new Thickness(0, 8, 0, 0); credits.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(credits);
        var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 23, 0, 0) };
        links.Children.Add(ActionButton("使用指南", () => Process.Start(new ProcessStartInfo("https://github.com/FouHe0720/LumaDock/blob/main/docs/USER-GUIDE.md") { UseShellExecute = true })));
        links.Children.Add(ActionButton("GitHub", () => Process.Start(new ProcessStartInfo("https://github.com/FouHe0720/LumaDock") { UseShellExecute = true }))); panel.Children.Add(links);
    }

    private TextBlock Text(string text, double size = 13, FontWeight? weight = null, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "UiTextFamily");
        block.SetResourceReference(TextBlock.FontWeightProperty, (weight ?? FontWeights.Normal).ToOpenTypeWeight() >= 500 ? "UiEmphasisWeight" : "UiTextWeight");
        block.SetResourceReference(TextBlock.FontStyleProperty, "UiTextStyle");
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return block;
    }

    private Button ActionButton(string label, Action action, bool accent = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(0, 0, 7, 0), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.SetResourceReference(BackgroundProperty, accent ? "AccentBrush" : "PanelBrush");
        if (accent) button.Foreground = Theme.IsDark ? new SolidColorBrush(Color.FromRgb(23, 36, 49)) : Brushes.White; else button.SetResourceReference(ForegroundProperty, "TextBrush");
        ApplyButtonTemplate(button, new CornerRadius(9, 16, 9, 16), accent: accent); button.Click += (_, _) => Safe(action); return button;
    }

    private static void ApplyButtonTemplate(Button button, double radius, bool tile = false, bool accent = false)
        => ApplyButtonTemplate(button, new CornerRadius(radius), tile, accent);

    private static void ApplyButtonTemplate(Button button, CornerRadius radius, bool tile = false, bool accent = false, bool navigation = false)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Surface"); border.SetValue(Border.CornerRadiusProperty, radius); border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Button.Background)) { RelativeSource = RelativeSource.TemplatedParent }); border.SetBinding(Border.PaddingProperty, new Binding(nameof(Button.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(.8));
        if (tile || navigation) button.BorderBrush = Brushes.Transparent; else button.SetResourceReference(Control.BorderBrushProperty, "GlassHighlightBrush");
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Button.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new Binding(nameof(Button.HorizontalContentAlignment)) { RelativeSource = RelativeSource.TemplatedParent }); presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        if (accent) hover.Setters.Add(new Setter(OpacityProperty, .88));
        else hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SelectionBrush"), "Surface"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("GlassHighlightBrush"), "Surface")); template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "Surface")); template.Triggers.Add(focus);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(OpacityProperty, .78)); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .35)); template.Triggers.Add(disabled); button.Template = template;
    }

    private Button ChromeButton(string label, string tooltip, Action action)
    { var button = ActionButton("", action); button.Content = Glyph(label, 12); button.Width = 34; button.Height = 30; button.Padding = new Thickness(0); button.Background = Brushes.Transparent; button.ToolTip = tooltip; System.Windows.Automation.AutomationProperties.SetName(button, tooltip); ApplyButtonTemplate(button, 10, tile: true); return button; }
    private TextBlock Glyph(string value, double size) => new() { Text = value, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal, VerticalAlignment = VerticalAlignment.Center };
    private static MenuItem Menu(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); return item; }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { ShowError(ex); } }
    private void ShowError(Exception ex) { Debug.WriteLine(ex); _controller.Notify(ex.Message); }

    private string? AskText(string title, string hint, string initial, string confirm = "保存", int maxLength = 40)
    {
        var dialog = new Window { Owner = this, Title = title, Width = 400, Height = 245, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = FontFamily };
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        WindowBackdrop.Attach(dialog, () => Theme.IsDark, () => _controller.State.Settings.PowerSaver);
        var border = new Border { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(.8) }; border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); border.SetResourceReference(Border.BorderBrushProperty, "GlassHighlightBrush"); dialog.Content = border;
        var stack = new StackPanel { Margin = new Thickness(26, 23, 26, 24) }; border.Child = stack;
        var heading = Text(title, 19, FontWeights.SemiBold); heading.Margin = new Thickness(0, 0, 0, 18); heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) dialog.DragMove(); }; stack.Children.Add(heading);
        var prompt = Text(hint, 11, FontWeights.Normal, true); stack.Children.Add(prompt);
        var inputBorder = new Border { Margin = new Thickness(0, 8, 0, 20), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(.8) }; inputBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); inputBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); stack.Children.Add(inputBorder);
        var input = new TextBox { Text = initial, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 14, MaxLength = maxLength }; input.SetResourceReference(Control.ForegroundProperty, "TextBrush"); System.Windows.Automation.AutomationProperties.SetName(input, hint); var inputField = new Grid(); inputField.Children.Add(input); inputField.Children.Add(StaticCaret.Create(input)); inputBorder.Child = inputField;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; stack.Children.Add(buttons);
        buttons.Children.Add(ActionButton("取消", () => dialog.DialogResult = false)); var ok = ActionButton(confirm, () => dialog.DialogResult = true, true); ok.IsDefault = true; buttons.Children.Add(ok);
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.DialogResult = false; e.Handled = true; } };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void AnimateOpen()
    {
        var stopwatch = Stopwatch.StartNew();
        if (!_controller.ReducedMotion)
        { _shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop }); }
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { stopwatch.Stop(); LastOpenMilliseconds = stopwatch.Elapsed.TotalMilliseconds; });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Math.Abs(oldDpi.DpiScaleX - newDpi.DpiScaleX) > .001 || Math.Abs(oldDpi.DpiScaleY - newDpi.DpiScaleY) > .001)
            QueueGridResize();
    }

    private void QueueGridResize()
    {
        if (_resizeQueued || _disposed || _section is "Settings" or "About") return;
        _resizeQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        { _resizeQueued = false; if (IsVisible && _section is not ("Settings" or "About")) { if (_section == "Desktop") RenderDesktopGrid(); else RenderAppGrid(false); } });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) return;
        if (_controller.State.Settings.CloseToTray) { e.Cancel = true; HideSmoothly(); }
        else
        {
            // Exit closes this window through Dispose. Queue it after Closing unwinds to avoid recursive Close().
            e.Cancel = true;
            if (!_exitQueued) { _exitQueued = true; Dispatcher.BeginInvoke(() => _controller.Exit()); }
        }
    }

    private void DisposeSubscriptions() { if (_disposed) return; _disposed = true; StopDesktopWatching(); _importCancellation?.Cancel(); _controller.Changed -= ControllerChanged; }
    private sealed record Choice(string Value, string Label);
}
