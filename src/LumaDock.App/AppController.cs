using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;
using LumaDock.App.Center;
using Forms = System.Windows.Forms;

namespace LumaDock.App;

public sealed class AppController : IDisposable
{
    private readonly StateStore store;
    private readonly IconService icons = new();
    private readonly DesktopFontService desktopFont = new();
    public WindowTracker Tracker { get; } = new();
    private readonly string? diagnostics;
    private readonly Dictionary<string, object> metrics = new();
    private Forms.NotifyIcon? tray;
    private DockWindow? dock;
    private ControlCenterWindow? center;
    private HwndSource? messages;
    private bool disposed, refreshing, exiting;
    private Stopwatch? centerClock;
    public AppState State { get; private set; }
    internal bool IsDisposed => disposed;
    public event EventHandler? Changed;
    public DesktopFontInfo SystemIconFont => desktopFont.Current;
    public DesktopFontInfo NameFont => State.Settings.FollowSystemIconFont ? desktopFont.Current
        : new(string.IsNullOrWhiteSpace(State.Settings.NameFontFamily) ? "Segoe UI Variable Text, Microsoft YaHei UI" : State.Settings.NameFontFamily,
            State.Settings.NameFontSize, State.Settings.NameFontBold ? 700 : 400, State.Settings.NameFontItalic, false, false);
    public bool ReducedMotion => State.Settings.PowerSaver || !State.Settings.Animations || SystemParameters.ClientAreaAnimation == false || Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline;

    public AppController(string directory, string? diagnosticsDirectory)
    {
        store = new(directory); diagnostics = diagnosticsDirectory;
        State = store.Load(); Theme.Apply(State.Settings.Theme, State.Settings.Accent); ApplyNameFont();
        desktopFont.Changed += DesktopFontChanged;
    }

    public void Start()
    {
        messages = new HwndSource(new HwndSourceParameters("LumaDock.Events") { Width = 0, Height = 0, WindowStyle = 0 });
        messages.AddHook(MessageHook);
        if (!Native.RegisterHotKey(messages.Handle, 1, 0x4000 | 0x0001 | 0x0002, 0x20)) Log.Write("Ctrl+Alt+Space unavailable; use the tray menu.");
        CreateTray(); Tracker.Changed += WindowsChanged; Tracker.Start();
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        RestartDock();
        if (store.RecoveryMessage != null) Notify(store.RecoveryMessage);
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x312 && w.ToInt32() == 1) { ShowCenter(); handled = true; }
        if (msg is 0x001A or 0x001D) RefreshSystemFont(); // WM_SETTINGCHANGE / WM_FONTCHANGE.
        return IntPtr.Zero;
    }
    private void WindowsChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => { if (!disposed) dock?.RefreshWindows(); });
    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        // Font, mouse and other preference broadcasts must not rewrite configuration
        // or recreate the Dock when the resolved system theme has not changed.
        if (disposed || State.Settings.Theme != "System" || Theme.ResolveDark("System") == Theme.IsDark) return;
        Theme.Apply("System", State.Settings.Accent);
        dock?.UpdateSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    });
    private void DisplaysChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => { if (!disposed) dock?.Reposition(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => { if (!disposed) dock?.UpdateSettings(); });
    public void RefreshSystemFont() { if (!disposed) desktopFont.Refresh(); }
    private void DesktopFontChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (State.Settings.FollowSystemIconFont) ApplyNameFont();
        // Keep the settings font-information row current even while names use
        // the user's manual font. A font update never saves or rebuilds the Dock.
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void ApplyNameFont()
    {
        var font = NameFont; var resources = Application.Current.Resources;
        resources["AppNameFamily"] = new System.Windows.Media.FontFamily(font.FamilyName);
        resources["AppNameSize"] = font.SizeDip;
        resources["AppNameWeight"] = FontWeight.FromOpenTypeWeight(font.Weight);
        resources["AppNameStyle"] = font.Italic ? FontStyles.Italic : FontStyles.Normal;
        var decorations = new TextDecorationCollection();
        if (font.Underline) decorations.Add(TextDecorations.Underline[0]);
        if (font.Strikeout) decorations.Add(TextDecorations.Strikethrough[0]);
        decorations.Freeze(); resources["AppNameDecorations"] = decorations;
    }

    public async Task RefreshAppsAsync()
    {
        if (refreshing) return; refreshing = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var found = await RunStaAsync(AppDiscovery.Scan);
            if (disposed) return;
            var first = State.Apps.Count == 0;
            Catalog.Merge(State, found);
            if (first) State.PinnedIds = State.Apps.Where(a => !a.Hidden).OrderBy(a => a.Category == "浏览器" ? 0 : a.Category == "开发工具" ? 1 : 2).Take(6).Select(a => a.Id).ToList();
            Save(); metrics["scanMs"] = sw.Elapsed.TotalMilliseconds; metrics["appCount"] = State.Apps.Count; WriteMetrics();
            Log.Write($"Scan completed: {found.Count} discovered, {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception e) { Log.Write("Scan failed", e); Notify("应用扫描失败：" + e.Message); }
        finally { refreshing = false; }
    }

    internal static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.SetResult(action()); } catch (Exception e) { result.SetException(e); } }) { IsBackground = true, Name = "LumaDock.ShellScan" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }

    public Task<BitmapSource?> GetIconAsync(AppEntry entry) => icons.GetAsync(entry, 80);
    public void Save()
    {
        try { store.Save(State); Changed?.Invoke(this, EventArgs.Empty); dock?.Rebuild(); }
        catch (Exception ex) { Log.Write("Save failed", ex); Notify("配置保存失败：" + ex.Message); }
    }
    public void Pin(AppEntry app) { app.Hidden = false; if (!State.PinnedIds.Contains(app.Id)) State.PinnedIds.Add(app.Id); Save(); }
    public void Unpin(AppEntry app) { State.PinnedIds.Remove(app.Id); Save(); }
    public void Remove(AppEntry app) { app.Hidden = true; State.PinnedIds.Remove(app.Id); Save(); }
    public void AddManual(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到应用或快捷方式。", path);
        if (!new[] { ".exe", ".lnk", ".url", ".appref-ms" }.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("请选择 .exe、.lnk、.url 或 .appref-ms 文件。");
        var app = AppDiscovery.FromPath(path); var old = State.Apps.FirstOrDefault(a => a.Id == app.Id);
        if (old != null) old.Hidden = false; else { app.Category = Catalog.SuggestCategory(app.Name); State.Apps.Add(app); } Save();
    }
    public void Launch(AppEntry app)
    {
        try
        {
            if (Tracker.IsRunning(app)) { if (!Tracker.Activate(app)) Notify("Windows 限制了窗口切换，请从任务栏切换到 " + app.Name + "。"); }
            else ShellLauncher.Launch(app);
        }
        catch (Exception e) { Log.Write("Launch failed: " + app.Name, e); Notify("无法打开 " + app.Name + "：" + e.Message); }
    }
    public void ShowCenter(string section = "Launchpad")
    {
        if (center?.IsVisible == true && center.IsActive && center.WindowState != WindowState.Minimized && section == "Launchpad" && center.CurrentSection == "Launchpad") { center.HideSmoothly(); return; }
        RevealCenter(section);
    }
    public void RevealCenter(string section = "Launchpad")
    {
        if (disposed) return;
        RefreshSystemFont();
        centerClock = Stopwatch.StartNew();
        if (center == null) { center = new(this, section); center.Closed += (_, _) => center = null; }
        else center.Navigate(section);
        center.WindowState = WindowState.Normal; center.Show(); center.Activate();
    }
    public void RecordLaunchpadFrame()
    {
        if (diagnostics != null) { metrics["systemIconFont"] = SystemIconFont; metrics["effectiveNameFont"] = NameFont; }
        if (centerClock != null) { metrics["launchpadFirstFrameMs"] = centerClock.Elapsed.TotalMilliseconds; centerClock = null; WriteMetrics(); }
    }
    internal void RecordGlassFrame(string surface, string source, double milliseconds)
    {
        if (diagnostics == null) return;
        metrics[surface + "Glass"] = new { source, milliseconds }; WriteMetrics();
    }
    internal async void RecordDockFrame()
    {
        metrics.TryAdd("startupToDockMs", Program.Clock.Elapsed.TotalMilliseconds); WriteMetrics();
        if (diagnostics == null) return;
        try { await Task.Delay(2200); if (!disposed && dock?.IsVisible == true) dock.CapturePng(Path.Combine(diagnostics, "dock-render.png")); }
        catch (Exception e) { Log.Write("Dock diagnostic render failed", e); }
    }
    private void WriteMetrics()
    {
        if (diagnostics == null) return;
        try { Directory.CreateDirectory(diagnostics); File.WriteAllText(Path.Combine(diagnostics, "startup.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write("Diagnostics write failed", e); }
    }
    public void RestartDock() { dock?.Close(); dock = new(this); dock.Show(); }
    public void ApplySettings() { Catalog.Normalize(State); Theme.Apply(State.Settings.Theme, State.Settings.Accent); ApplyNameFont(); dock?.UpdateSettings(); Save(); }
    public void Export(string path) => StateStore.Export(State, path);
    public void Import(string path)
    {
        var imported = StateStore.Read(path);
        // An imported preference cannot enable autorun. Only the explicit settings action can.
        imported.Settings.StartWithWindows = StartupService.IsEnabled;
        State = imported; ApplySettings();
    }
    public void Reset()
    {
        var apps = State.Apps;
        State = new AppState { Apps = apps, Settings = new DockSettings { StartWithWindows = StartupService.IsEnabled } };
        foreach (var a in State.Apps) { a.Hidden = false; a.Category = Catalog.SuggestCategory(a.Name); }
        ApplySettings();
    }
    public void Notify(string message)
    {
        Log.Write(message);
        tray?.ShowBalloonTip(5000, "LumaDock", message, Forms.ToolTipIcon.Info);
    }
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("应用库  Ctrl+Alt+Space", null, (_, _) => ShowCenter());
        menu.Items.Add("偏好设置", null, (_, _) => ShowCenter("Settings"));
        menu.Items.Add("重新扫描应用", null, async (_, _) => await RefreshAppsAsync());
        menu.Items.Add("重启 Dock", null, (_, _) => RestartDock());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 LumaDock", null, (_, _) => Exit());
        tray = new Forms.NotifyIcon { Text = "LumaDock", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => RevealCenter();
    }
    public void Exit() { if (exiting) return; exiting = true; Save(); Dispose(); Application.Current.Shutdown(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        desktopFont.Changed -= DesktopFontChanged; desktopFont.Dispose();
        SystemEvents.UserPreferenceChanged -= PreferencesChanged; SystemEvents.DisplaySettingsChanged -= DisplaysChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        Tracker.Changed -= WindowsChanged; Tracker.Dispose(); icons.Dispose();
        if (messages != null) { Native.UnregisterHotKey(messages.Handle, 1); messages.Dispose(); }
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose(); }
        dock?.Close(); center?.ClosePermanently();
    }
    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
