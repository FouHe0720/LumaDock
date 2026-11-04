using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace LumaDock.Windows;

/// <summary>Clear extended-frame composition or Desktop Acrylic on ordinary, non-layered WPF windows.</summary>
public static class WindowBackdrop
{
    private const int SystemBackdropAttribute = 38;
    private const int DesktopAcrylic = 3;
    private const int NoBackdrop = 1;
    private static readonly ConditionalWeakTable<Window, Registration> Registrations = new();

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>Call once before the HWND is created. Draw only a translucent tint above this backdrop.</summary>
    public static void Attach(Window window, Func<bool> isDark, Func<bool> powerSaver, Func<bool>? clearBackdrop = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(isDark);
        ArgumentNullException.ThrowIfNull(powerSaver);
        window.Dispatcher.VerifyAccess();
        if (Registrations.TryGetValue(window, out _)) return;
        if (window.AllowsTransparency)
            throw new InvalidOperationException("原生玻璃窗口不能使用 WPF 分层透明窗口。请在创建窗口前设置 AllowsTransparency=false。");
        var registration = new Registration(window, isDark, powerSaver, clearBackdrop);
        Registrations.Add(window, registration);
        registration.Attach();
    }

    /// <summary>Refresh after a theme/performance setting change. Returns whether the selected native composition path is active.</summary>
    public static bool Refresh(Window window, bool isDark, bool powerSaver)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (!Registrations.TryGetValue(window, out var registration))
            throw new InvalidOperationException("请先将原生玻璃效果附加到窗口。");
        return registration.Apply(isDark, powerSaver);
    }

    public static BackdropStatus GetStatus(Window window) => Registrations.TryGetValue(window, out var registration)
        ? registration.Status
        : new(false, IsSupported, null, 0, "Not attached");

    public sealed record BackdropStatus(bool AcrylicActive, bool Supported, int? HResult, int AppliedBackdrop, string Reason, bool ClearActive = false, int NonClientPolicy = 0);

    private sealed class Registration(Window window, Func<bool> isDark, Func<bool> powerSaver, Func<bool>? clearBackdrop)
    {
        private readonly WindowChrome chrome = new()
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(24),
            GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness,
            UseAeroCaptionButtons = false,
            ResizeBorderThickness = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip ? new Thickness(6) : new Thickness(0)
        };
        private HwndSource? source;
        private bool closed;
        private bool refreshQueued;
        private bool applying;
        private bool hasApplied;
        private bool lastDark;
        private bool lastSavePower;
        private bool lastClear;
        private string lastEligibility = "";
        public BackdropStatus Status { get; private set; } = new(false, IsSupported, null, 0, "Waiting for window");

        public void Attach()
        {
            // Keep standard WindowChrome hit testing and resize behavior.

            window.SourceInitialized += SourceInitialized;
            window.Loaded += Loaded;
            window.Closed += Closed;
            WindowChrome.SetWindowChrome(window, chrome);
            window.Background = CanUseAcrylic(powerSaver(), out _) ? WpfBrushes.Transparent : FallbackBrush(isDark());
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) SourceInitialized(window, EventArgs.Empty);
        }

        private void SourceInitialized(object? sender, EventArgs e)
        {
            if (closed) return;
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(Hook);
            var hwnd = source?.Handle ?? IntPtr.Zero;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
            WTSRegisterSessionNotification(source?.Handle ?? IntPtr.Zero, 0);
            Apply(isDark(), powerSaver());
        }

        private void Loaded(object sender, RoutedEventArgs e)
        {
            // WindowChrome finishes native setup after SourceInitialized.
            // Apply the selected composition policy after that initial setup.
            source?.RemoveHook(Hook); source?.AddHook(Hook);
            hasApplied = false; Apply(isDark(), powerSaver());
        }

        public bool Apply(bool dark, bool savePower)
        {
            if (closed || applying) return Status.AcrylicActive || Status.ClearActive;
            applying = true;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    window.Background = CanUseAcrylic(savePower, out _) ? WpfBrushes.Transparent : FallbackBrush(dark);
                    return false;
                }
                var eligible = CanUseAcrylic(savePower, out var reason);
                var clear = clearBackdrop?.Invoke() == true;
                if (hasApplied && lastDark == dark && lastSavePower == savePower && lastEligibility == reason && lastClear == clear)
                    return Status.AcrylicActive || Status.ClearActive;
                hasApplied = true; lastDark = dark; lastSavePower = savePower; lastEligibility = reason; lastClear = clear;
                // A resizable extended frame otherwise paints its caption colour
                // beneath the whole client surface. Keep native resize semantics,
                // but let the clear client pixels composite without that opaque fill.
                var nonClientPolicy = eligible && clear ? 1 : 0; // DWMNCRP_DISABLED / USEWINDOWSTYLE.
                var darkMode = dark ? 1 : 0;
                var corners = 2;
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
                DwmSetWindowAttribute(hwnd, 33, ref corners, sizeof(int));
                // An enabled Windows accent-titlebar preference can color the native
                // caption even when client content is extended over it. Keep it neutral.
                var captionColor = dark ? 0x0039302B : 0x00E3DDD9;
                DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
                // Our own subtle edge is drawn by WPF; retain the native shadow and corner clipping.
                var borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));

                int? result = null;
                var active = false;
                if (eligible)
                {
                    chrome.GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness;
                    var margins = new Margins(-1);
                    var frameResult = DwmExtendFrameIntoClientArea(hwnd, ref margins);
                    if (frameResult >= 0)
                    {
                        var type = clear ? NoBackdrop : DesktopAcrylic;
                        result = DwmSetWindowAttribute(hwnd, SystemBackdropAttribute, ref type, sizeof(int));
                        active = result >= 0;
                        reason = active ? clear ? "Clear extended frame" : "Desktop Acrylic" : "DWM rejected composition mode";
                    }
                    else { result = frameResult; reason = "DWM frame extension unavailable"; }
                }

                var applied = 0;
                if (active)
                {
                    window.Background = WpfBrushes.Transparent;
                    if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
                    if (DwmGetWindowAttribute(hwnd, SystemBackdropAttribute, out applied, sizeof(int)) < 0) applied = clear ? NoBackdrop : DesktopAcrylic;
                    var policyResult = DwmSetWindowAttribute(hwnd, 2, ref nonClientPolicy, sizeof(int));
                    var extended = new Margins(-1);
                    var extensionResult = DwmExtendFrameIntoClientArea(hwnd, ref extended);
                    if (clear && (policyResult < 0 || extensionResult < 0)) { active = false; reason = "Clear composition unavailable"; }
                }
                if (!active)
                {
                    nonClientPolicy = 0;
                    DwmSetWindowAttribute(hwnd, 2, ref nonClientPolicy, sizeof(int));
                    var none = NoBackdrop;
                    if (IsSupported) DwmSetWindowAttribute(hwnd, SystemBackdropAttribute, ref none, sizeof(int));
                    chrome.GlassFrameThickness = new Thickness(0);
                    var margins = new Margins(0);
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);
                    var fallback = FallbackBrush(dark);
                    window.Background = fallback;
                    if (source?.CompositionTarget is { } target) target.BackgroundColor = fallback.Color;
                    applied = NoBackdrop;
                }
                Status = new(active && !clear, IsSupported, result, applied, reason, active && clear, nonClientPolicy);
                return active;
            }
            finally { applying = false; }
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0084 && window.WindowState == WindowState.Maximized
                && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            { handled = true; return new IntPtr(1); }
            // Clear extended frames can suppress the normal non-client border.
            // Return native sizing zones explicitly, leaving the rest of the
            // client and all caption-button input to WPF/WindowChrome.
            if (message == 0x0084 /* WM_NCHITTEST */ && window.WindowState == WindowState.Normal
                && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip && GetWindowRect(hwnd, out var rect))
            {
                var packed = lParam.ToInt64();
                var x = (int)(short)(packed & 0xffff); var y = (int)(short)((packed >> 16) & 0xffff);
                var scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96d;
                var border = Math.Max(6, (int)Math.Ceiling(8 * scale));
                var corner = Math.Max(border, (int)Math.Ceiling(16 * scale));
                var inside = x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
                if (inside)
                {
                    var left = x < rect.Left + border; var right = x >= rect.Right - border;
                    var top = y < rect.Top + border; var bottom = y >= rect.Bottom - border;
                    var nearLeft = x < rect.Left + corner; var nearRight = x >= rect.Right - corner;
                    var nearTop = y < rect.Top + corner; var nearBottom = y >= rect.Bottom - corner;
                    var hit = (left && nearTop || top && nearLeft) ? 13 : (right && nearTop || top && nearRight) ? 14
                        : (left && nearBottom || bottom && nearLeft) ? 16 : (right && nearBottom || bottom && nearRight) ? 17
                        : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
                    if (hit != 0) { handled = true; return new IntPtr(hit); }
                }
            }
            if (message == 0x0024 /* WM_GETMINMAXINFO */)
            {
                var monitor = MonitorFromWindow(hwnd, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    limits.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
                    limits.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
                    var scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96d;
                    limits.MinTrackSize = new NativePoint { X = (int)Math.Ceiling(window.MinWidth * scale), Y = (int)Math.Ceiling(window.MinHeight * scale) };
                    Marshal.StructureToPtr(limits, lParam, false);
                    handled = true;
                }
                return IntPtr.Zero;
            }
            // Reapply only on composition/theme/accessibility/session changes, never on a frame timer.
            if (message is 0x031E /* WM_DWMCOMPOSITIONCHANGED */ or 0x031A /* WM_THEMECHANGED */ or 0x02E0 /* WM_DPICHANGED */ or 0x001A /* WM_SETTINGCHANGE */ or 0x02B1 /* WM_WTSSESSION_CHANGE */ or 0x0218 /* WM_POWERBROADCAST */)
            {
                if (message is 0x031E or 0x031A or 0x02E0) hasApplied = false;
                if (!refreshQueued && !closed && !window.Dispatcher.HasShutdownStarted)
                {
                    refreshQueued = true;
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        refreshQueued = false;
                        if (!closed) Apply(isDark(), powerSaver());
                    });
                }
            }
            return IntPtr.Zero;
        }

        private void Closed(object? sender, EventArgs e)
        {
            closed = true;
            window.SourceInitialized -= SourceInitialized;
            window.Loaded -= Loaded;
            window.Closed -= Closed;
            if (source != null) { WTSUnRegisterSessionNotification(source.Handle); source.RemoveHook(Hook); }
            source = null;
            Registrations.Remove(window);
        }
    }

    private static SolidColorBrush FallbackBrush(bool dark)
    {
        if (SystemParameters.HighContrast) return System.Windows.SystemColors.WindowBrush;
        var brush = new SolidColorBrush(dark ? WpfColor.FromRgb(25, 30, 39) : WpfColor.FromRgb(242, 246, 250));
        brush.Freeze();
        return brush;
    }

    private static bool CanUseAcrylic(bool savePower, out string reason)
    {
        if (!IsSupported) { reason = "Windows 11 22621 or later required"; return false; }
        if (savePower) { reason = "Power saver"; return false; }
        if (SystemParameters.HighContrast) { reason = "High contrast"; return false; }
        if (GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0) { reason = "Remote session"; return false; }
        if (GetSystemPowerStatus(out var power) && power.SystemStatusFlag == 1) { reason = "System battery saver"; return false; }
        if (DwmIsCompositionEnabled(out var composition) < 0 || !composition) { reason = "DWM composition unavailable"; return false; }
        // Read the user's preference; never turn Windows transparency on or write this setting.
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) is int value && value == 0)
            { reason = "System transparency disabled"; return false; }
        }
        catch (System.Security.SecurityException) { /* DWM still enforces the system policy. */ }
        reason = "Eligible";
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int all)
    {
        public int Left = all, Right = all, Top = all, Bottom = all;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
