using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace LumaDock.App.Center;

public sealed partial class ControlCenterWindow
{
    private readonly DesktopFileService _desktopFiles;
    private DesktopSnapshot? _desktopSnapshot;
    private DesktopDirectoryWatcher? _desktopWatcher;
    private DispatcherTimer? _desktopDebounce;
    private bool _desktopDirty = true, _desktopLoading, _desktopWorking;
    private string _desktopQuery = "", _selectedDesktopPath = "", _desktopRevealPath = "";
    private readonly List<Button> _desktopTiles = [];

    private void UpdateDesktopWatching()
    {
        if (_disposed || !IsVisible || _section != "Desktop") { StopDesktopWatching(); return; }
        if (_desktopWatcher != null) return;
        try
        {
            _desktopWatcher = _desktopFiles.CreateWatcher();
            _desktopWatcher.Changed += DesktopDirectoryChanged;
            _desktopDirty = true; // Catch changes made while the view was hidden.
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void StopDesktopWatching()
    {
        _desktopDebounce?.Stop();
        if (_desktopWatcher != null) { _desktopWatcher.Changed -= DesktopDirectoryChanged; _desktopWatcher.Dispose(); _desktopWatcher = null; }
        _desktopDirty = true;
    }

    private void DesktopDirectoryChanged(object? sender, EventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_disposed || _section != "Desktop" || !IsVisible) return;
            _desktopDirty = true;
            if (_desktopDebounce == null)
            {
                _desktopDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(280) };
                _desktopDebounce.Tick += (_, _) => { _desktopDebounce.Stop(); _ = LoadDesktopAsync(); };
            }
            _desktopDebounce.Stop(); _desktopDebounce.Start();
        });
    }

    private async Task LoadDesktopAsync()
    {
        if (_desktopLoading || _disposed || !IsVisible || _section != "Desktop") return;
        _desktopLoading = true;
        try
        {
            do
            {
                _desktopDirty = false;
                var snapshot = await Task.Run(_desktopFiles.Scan);
                if (_disposed) return;
                _desktopSnapshot = snapshot;
            } while (_desktopDirty && IsVisible && _section == "Desktop");
            if (IsVisible && _section == "Desktop") RenderDesktopGrid();
        }
        catch (Exception ex) { if (!_disposed && IsVisible && _section == "Desktop") { _status.Text = "桌面暂时无法读取，请刷新重试。"; ShowError(ex); } }
        finally { _desktopLoading = false; }
    }

    private void BuildDesktop()
    {
        _title.Text = "桌面集";
        _title.ToolTip = "当前用户和公共桌面的真实文件与文件夹";
        var create = ActionButton("新建", () => { }); create.Tag = "DesktopNew";
        var createMenu = DesktopNewMenu(); createMenu.PlacementTarget = create; createMenu.Placement = PlacementMode.Bottom;
        create.Click += (_, _) => createMenu.IsOpen = true; _tools.Children.Add(create);
        _tools.Children.Add(ActionButton("打开桌面", () => Safe(() => OpenDesktopPath(_desktopFiles.UserDesktopDirectory))));
        var refresh = ActionButton("刷新", () => { _desktopDirty = true; _ = LoadDesktopAsync(); }); refresh.Tag = "DesktopRefresh"; _tools.Children.Add(refresh);
        var filters = new StackPanel(); Grid.SetRow(filters, 1); _body.Children.Add(filters);
        var searchBox = new Border { CornerRadius = new CornerRadius(12, 21, 12, 21), BorderThickness = new Thickness(.8), Padding = new Thickness(15, 10, 12, 10), Margin = new Thickness(0, 0, 0, 12) };
        searchBox.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); searchBox.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var field = new Grid(); field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) }); field.ColumnDefinitions.Add(new ColumnDefinition()); field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.Children.Add(Glyph("\uE721", 16));
        var search = new TextBox { Text = _desktopQuery, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
        search.SetResourceReference(ForegroundProperty, "TextBrush"); AutomationProperties.SetName(search, "搜索桌面");
        var input = new Grid(); input.Children.Add(search); input.Children.Add(StaticCaret.Create(search)); Grid.SetColumn(input, 1); field.Children.Add(input);
        var placeholder = Text("搜索文件、文件夹和快捷方式", 14, FontWeights.Normal, true); placeholder.IsHitTestVisible = false; placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.Visibility = _desktopQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed; input.Children.Add(placeholder);
        var clear = ChromeButton("\uE711", "清空搜索", () => search.Clear()); clear.Width = 23; clear.Height = 23; clear.Margin = new Thickness(5, -3, 0, -3); Grid.SetColumn(clear, 2); field.Children.Add(clear);
        search.TextChanged += (_, _) => { _desktopQuery = search.Text; _page = 0; placeholder.Visibility = _desktopQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RenderDesktopGrid(); };
        searchBox.Child = field; filters.Children.Add(searchBox);
        var hint = Text("与 Windows 桌面同步  ·  右键空白处新建，双击打开", 11, FontWeights.Normal, true); hint.Margin = new Thickness(1, 0, 0, 12); filters.Children.Add(hint);
        RenderDesktopGrid();
        if (_desktopDirty || _desktopSnapshot == null) _ = LoadDesktopAsync();
    }

    private List<DesktopItem> VisibleDesktopItems() => (_desktopSnapshot?.Items ?? [])
        .Where(item => string.IsNullOrWhiteSpace(_desktopQuery) || item.Name.Contains(_desktopQuery, StringComparison.CurrentCultureIgnoreCase))
        .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();

    private void RenderDesktopGrid()
    {
        if (_section != "Desktop" || _disposed) return;
        foreach (var child in _body.Children.Cast<UIElement>().Where(x => Grid.GetRow(x) == 2).ToArray()) _body.Children.Remove(child);
        _desktopTiles.Clear();
        var items = VisibleDesktopItems();
        var width = _body.ActualWidth > 0 ? _body.ActualWidth : Width - 266;
        var height = _body.ActualHeight > 0 ? _body.ActualHeight : Height - 114;
        double headings = 0;
        foreach (var header in _body.Children.Cast<UIElement>().Where(x => Grid.GetRow(x) < 2)) { header.Measure(new Size(width, double.PositiveInfinity)); headings += header.DesiredSize.Height; }
        _status.Measure(new Size(width, double.PositiveInfinity));
        var font = _controller.NameFont;
        var gridWidth = Math.Min(1080, Math.Max(300, width - 5));
        var metrics = AppGridLayout.Calculate(gridWidth, Math.Max(0, height - headings - _status.DesiredSize.Height - 44), _controller.State.Settings.Columns, font.SizeDip, new FontFamily(font.FamilyName).LineSpacing);
        var pageSize = metrics.Columns * metrics.Rows;
        if (_desktopRevealPath.Length > 0)
        {
            var index = items.FindIndex(item => item.Path.Equals(_desktopRevealPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _page = index / pageSize;
            _desktopRevealPath = "";
        }
        var pages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)pageSize)); _page = Math.Clamp(_page, 0, pages - 1);
        var host = new Grid { Background = Brushes.Transparent, Tag = "DesktopSurface", Focusable = true };
        host.RowDefinitions.Add(new RowDefinition()); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(host, 2); _body.Children.Add(host);
        host.ContextMenu = DesktopBackgroundMenu();
        host.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Grid or ScrollViewer or ScrollContentPresenter) { SelectDesktopItem(""); host.Focus(); } };
        var grid = new UniformGrid { Columns = metrics.Columns, VerticalAlignment = VerticalAlignment.Top, Width = gridWidth, HorizontalAlignment = HorizontalAlignment.Center };
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 5, 0) };
        scroll.Resources[typeof(ScrollBar)] = CenterStyles.ScrollBarStyle(); host.Children.Add(scroll);
        foreach (var item in items.Skip(_page * pageSize).Take(pageSize)) { var tile = CreateDesktopTile(item, metrics); grid.Children.Add(tile); _desktopTiles.Add(tile); }
        if (items.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var icon = Glyph("\uE8B7", 30); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 14); empty.Children.Add(icon);
            empty.Children.Add(Text(_desktopSnapshot == null ? "正在读取桌面…" : _desktopQuery.Length == 0 ? "桌面还是空的" : "没有找到项目", 16, FontWeights.Medium)); host.Children.Add(empty);
        }
        if (pages > 1)
        {
            var pager = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 34, Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(pager, 1); host.Children.Add(pager);
            var prev = ActionButton("‹", () => { _page--; RenderDesktopGrid(); }); prev.IsEnabled = _page > 0; pager.Children.Add(prev);
            var number = Text($"{_page + 1} / {pages}", 11, FontWeights.Normal, true); number.Margin = new Thickness(16, 0, 16, 0); number.VerticalAlignment = VerticalAlignment.Center; pager.Children.Add(number);
            var next = ActionButton("›", () => { _page++; RenderDesktopGrid(); }); next.IsEnabled = _page < pages - 1; pager.Children.Add(next);
        }
        _status.Text = _desktopSnapshot == null ? "正在读取桌面…" : $"{items.Count} 个项目 · {items.Count(item => item.IsDirectory)} 个文件夹";
        if (_desktopSnapshot?.Errors.Count > 0) { _status.Text += $" · {_desktopSnapshot.Errors.Count} 处未能读取，右键刷新重试"; _status.ToolTip = string.Join("\n", _desktopSnapshot.Errors.Select(error => error.Message)); }
        else _status.ToolTip = "文件来自当前用户桌面与公共桌面；这里只显示顶层项目";
        if (_desktopWatcher?.Errors.Count > 0) _status.Text += " · 自动同步暂不可用，请手动刷新";
        SelectDesktopItem(_selectedDesktopPath);
    }

    private Button CreateDesktopTile(DesktopItem item, AppGridMetrics metrics)
    {
        var entry = new AppEntry { Name = item.Name, LaunchPath = item.Path };
        var vm = new AppTileViewModel(entry);
        var tile = new Button { DataContext = item, Tag = "DesktopItem", Height = metrics.TileHeight, Margin = new Thickness(5), Padding = new Thickness(9, 6, 9, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = item.Path + (item.IsCommonDesktop ? "\n公共桌面" : "") };
        AutomationProperties.SetName(tile, item.Name); AutomationProperties.SetHelpText(tile, item.IsDirectory ? "桌面文件夹，双击打开" : "桌面文件，双击打开");
        ApplyButtonTemplate(tile, new CornerRadius(14, 23, 14, 23), tile: true);
        var content = new StackPanel();
        var image = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); image.SetBinding(Image.SourceProperty, new Binding(nameof(AppTileViewModel.Icon)) { Source = vm }); content.Children.Add(image);
        var name = Text(item.Name); name.Tag = "DesktopItemName";
        name.SetResourceReference(TextBlock.FontFamilyProperty, "AppNameFamily"); name.SetResourceReference(TextBlock.FontSizeProperty, "AppNameSize"); name.SetResourceReference(TextBlock.FontWeightProperty, "AppNameWeight"); name.SetResourceReference(TextBlock.FontStyleProperty, "AppNameStyle"); name.SetResourceReference(TextBlock.TextDecorationsProperty, "AppNameDecorations"); name.SetResourceReference(TextBlock.ForegroundProperty, "AppNameBrush");
        name.TextAlignment = TextAlignment.Center; name.TextWrapping = TextWrapping.Wrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.LineHeight = metrics.LineHeight; name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; name.Height = metrics.NameHeight; name.MaxHeight = metrics.NameHeight; name.Margin = new Thickness(0, 10, 0, 0); content.Children.Add(name); tile.Content = content;
        tile.Click += (_, _) => SelectDesktopItem(item.Path);
        tile.MouseDoubleClick += (_, e) => { if (e.ChangedButton != MouseButton.Left) return; Safe(() => OpenDesktopPath(item.Path)); e.Handled = true; };
        tile.PreviewMouseRightButtonDown += (_, _) => { SelectDesktopItem(item.Path); tile.Focus(); };
        tile.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Safe(() => OpenDesktopPath(item.Path)); e.Handled = true; } };
        tile.Loaded += async (_, _) => await vm.LoadIconAsync(_controller, Math.Clamp((int)Math.Ceiling(48 * VisualTreeHelper.GetDpi(image).DpiScaleX / 16) * 16, 80, 256));
        Point start = default; var dragging = false; var pressedHere = false;
        tile.PreviewMouseLeftButtonDown += (_, e) => { pressedHere = true; start = e.GetPosition(tile); SelectDesktopItem(item.Path); };
        tile.PreviewMouseLeftButtonUp += (_, _) => pressedHere = false;
        tile.PreviewMouseMove += (_, e) =>
        {
            if (!pressedHere || dragging || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(tile) - start).Length < 7) return;
            dragging = true;
            try { var data = new DataObject(); data.SetFileDropList(new StringCollection { item.Path }); DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Link); }
            finally { dragging = false; pressedHere = false; }
            e.Handled = true;
        };
        tile.ContextMenu = DesktopItemMenu(item);
        return tile;
    }

    private void SelectDesktopItem(string path)
    {
        _selectedDesktopPath = path;
        foreach (var tile in _desktopTiles)
        {
            if (tile.DataContext is DesktopItem item && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) tile.SetResourceReference(BackgroundProperty, "SelectionBrush");
            else tile.Background = Brushes.Transparent;
        }
    }

    private ContextMenu DesktopMenu()
    {
        var menu = new ContextMenu { Padding = new Thickness(6), BorderThickness = new Thickness(.8), Style = CenterStyles.DesktopMenuStyle() };
        menu.Resources[typeof(MenuItem)] = CenterStyles.DesktopMenuItemStyle();
        menu.Resources[typeof(ScrollBar)] = CenterStyles.ScrollBarStyle();
        menu.SetResourceReference(Control.BackgroundProperty, "PopupBrush"); menu.SetResourceReference(Control.ForegroundProperty, "TextBrush"); menu.SetResourceReference(Control.BorderBrushProperty, "BorderBrush"); return menu;
    }

    private ContextMenu DesktopNewMenu()
    {
        var menu = DesktopMenu();
        menu.Items.Add(Menu("文件夹", () => CreateDesktopEntry(true)));
        menu.Items.Add(Menu("文本文档 (.txt)", () => CreateDesktopEntry(false)));
        return menu;
    }

    private ContextMenu DesktopBackgroundMenu()
    {
        var menu = DesktopMenu();
        var create = new MenuItem { Header = "新建" };
        create.Items.Add(Menu("文件夹", () => CreateDesktopEntry(true))); create.Items.Add(Menu("文本文档 (.txt)", () => CreateDesktopEntry(false))); menu.Items.Add(create);
        menu.Items.Add(Menu("粘贴文件副本", () => _ = PasteDesktopFilesAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("刷新", () => { _desktopDirty = true; _ = LoadDesktopAsync(); }));
        menu.Items.Add(Menu("打开桌面文件夹", () => Safe(() => OpenDesktopPath(_desktopFiles.UserDesktopDirectory))));
        menu.Items.Add(Menu("显示 / 隐藏 Windows 桌面图标…", ToggleDesktopIcons));
        menu.Opened += (_, _) => { if (menu.Items[1] is MenuItem paste) { try { paste.IsEnabled = !_desktopWorking && Clipboard.ContainsFileDropList(); } catch { paste.IsEnabled = false; } } };
        return menu;
    }

    private ContextMenu DesktopItemMenu(DesktopItem item)
    {
        var menu = DesktopMenu();
        menu.Items.Add(Menu("打开", () => Safe(() => OpenDesktopPath(item.Path))));
        menu.Items.Add(Menu("在资源管理器中显示", () => Safe(() => { using var p = Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true }); })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("复制", () => Safe(() => Clipboard.SetFileDropList(new StringCollection { item.Path }))));
        menu.Items.Add(Menu("复制路径", () => Safe(() => Clipboard.SetText(item.Path))));
        menu.Items.Add(Menu("重命名…", () => RenameDesktopEntry(item)));
        if (!item.IsDirectory && new[] { ".exe", ".lnk", ".url", ".appref-ms" }.Contains(Path.GetExtension(item.Path).ToLowerInvariant()))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu("添加到应用库", () => _ = ImportDesktopApplicationAsync(item, false)));
            menu.Items.Add(Menu("固定到 Dock", () => _ = ImportDesktopApplicationAsync(item, true)));
        }
        return menu;
    }

    private static void OpenDesktopPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("项目已移动或删除，请刷新桌面集。", path);
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CreateDesktopEntry(bool folder)
    {
        if (_desktopWorking) return;
        var name = AskText(folder ? "新建文件夹" : "新建文本文档", "保存到 Windows 桌面", folder ? "新建文件夹" : "新建文本文档.txt", "新建", 180);
        if (name == null) return;
        _ = RunDesktopMutationAsync(() => folder ? _desktopFiles.CreateFolder(name) : _desktopFiles.CreateTextFile(name));
    }

    private void RenameDesktopEntry(DesktopItem item)
    {
        if (_desktopWorking) return;
        var name = AskText("重命名", item.IsDirectory ? "文件夹名称" : "文件名称（保留扩展名）", item.Name, "保存", 180);
        if (name == null || name == item.Name) return;
        if (!item.IsDirectory && !Path.GetExtension(name).Equals(Path.GetExtension(item.Name), StringComparison.OrdinalIgnoreCase)
            && System.Windows.MessageBox.Show(this, "更改扩展名可能使文件无法按原来的方式打开。要继续吗？", "更改扩展名", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _ = RunDesktopMutationAsync(() => _desktopFiles.Rename(item, name));
    }

    private async Task RunDesktopMutationAsync(Func<DesktopItem> action)
    {
        if (_desktopWorking) return;
        _desktopWorking = true;
        try { var result = await Task.Run(action); if (_disposed) return; _desktopQuery = ""; _selectedDesktopPath = result.Path; _desktopRevealPath = result.Path; _desktopDirty = true; if (IsVisible && _section == "Desktop") { Render(); await LoadDesktopAsync(); } }
        catch (Exception ex) { if (!_disposed) ShowError(ex); }
        finally { _desktopWorking = false; }
    }

    private async Task PasteDesktopFilesAsync()
    {
        if (_desktopWorking) return;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
            _desktopWorking = true;
            _status.Text = "正在复制文件到桌面…";
            var result = await Task.Run(() => _desktopFiles.CopyItems(paths));
            if (_disposed) return;
            _desktopQuery = "";
            if (result.Items.Count > 0) { _selectedDesktopPath = result.Items[0].Path; _desktopRevealPath = result.Items[0].Path; }
            _desktopDirty = true; if (IsVisible && _section == "Desktop") { Render(); await LoadDesktopAsync(); }
            if (result.Failures.Count > 0) _controller.Notify($"已复制 {result.Items.Count} 个文件，{result.Failures.Count} 项未复制。文件夹请在资源管理器中粘贴。原件均保留。");
        }
        catch (Exception ex) { if (!_disposed) ShowError(ex); }
        finally { _desktopWorking = false; }
    }

    private async Task ImportDesktopApplicationAsync(DesktopItem item, bool pin)
    {
        try
        {
            var result = await _controller.ImportApplicationsAsync([item.Path]);
            if (result.Entries.Count > 0) { if (pin) _controller.Pin(result.Entries[0]); _status.Text = pin ? "已固定到 Dock" : "已添加到应用库"; }
            else if (result.Failures.Count > 0) _controller.Notify("此项目不能作为应用导入，可直接从桌面集打开。");
        }
        catch (Exception ex) { if (!_disposed) ShowError(ex); }
    }

    private void DesktopKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_section != "Desktop" || Keyboard.FocusedElement is TextBox || _disposed) return;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.F5) { _desktopDirty = true; _ = LoadDesktopAsync(); e.Handled = true; }
        else if (control && e.Key == Key.V) { _ = PasteDesktopFilesAsync(); e.Handled = true; }
        else if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.N) { CreateDesktopEntry(true); e.Handled = true; }
        else if (_desktopSnapshot?.Items.FirstOrDefault(item => item.Path.Equals(_selectedDesktopPath, StringComparison.OrdinalIgnoreCase)) is { } selected)
        {
            if (e.Key == Key.F2) { RenameDesktopEntry(selected); e.Handled = true; }
            else if (control && e.Key == Key.C) { Safe(() => Clipboard.SetFileDropList(new StringCollection { selected.Path })); e.Handled = true; }
        }
    }
}
