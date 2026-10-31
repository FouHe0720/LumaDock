using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LumaDock.Windows;

/// <summary>Opt-in visibility only. This changes the Explorer listview, never deletes/moves files.</summary>
public static class DesktopVisibility
{
    public static bool IsVisible => FindListView() is var hwnd && hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
    public static void SetVisible(bool visible)
    {
        var list = FindListView();
        if (list == IntPtr.Zero) throw new InvalidOperationException("未找到 Explorer 桌面图标窗口。请先确认 Explorer 正在运行。");
        ShowWindow(list, visible ? 5 : 0);
    }
    private static IntPtr FindListView()
    {
        IntPtr list = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var def = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero) list = FindWindowEx(def, IntPtr.Zero, "SysListView32", "FolderView");
            return list == IntPtr.Zero;
        }, IntPtr.Zero);
        return list;
    }
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
}

public static class StartupService
{
    private const string OwnershipMarker = "LumaDock · 桌面与应用管理";
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LumaDock.lnk");
    public static bool IsEnabled => File.Exists(Shortcut) && IsOwnedShortcut();
    /// <summary>Call only after explicit user confirmation. Only LumaDock's own shortcut is changed.</summary>
    public static void SetEnabled(bool enabled, string executable)
    {
        if (File.Exists(Shortcut) && !IsOwnedShortcut())
            throw new InvalidOperationException("启动文件夹中已有同名 LumaDock.lnk，但它不是本应用创建的。为保护现有文件，本次操作已取消。请先自行检查该快捷方式。");
        if (!enabled) { if (File.Exists(Shortcut)) File.Delete(Shortcut); return; }
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 LumaDock 可执行文件。", executable);
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能将 SDK dotnet.exe 注册为 LumaDock 开机启动。请从发布目录的 LumaDock.exe 运行后重试。");
        AppDiscovery.OnSta(() =>
        {
            object? shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!) ?? throw new InvalidOperationException("Windows Script Host 激活失败。");
                shortcut = ((dynamic)shell).CreateShortcut(Shortcut);
                ((dynamic)shortcut).TargetPath = executable;
                ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(executable);
                ((dynamic)shortcut).Description = OwnershipMarker;
                ((dynamic)shortcut).Save();
            }
            finally { AppDiscovery.Release(shortcut); AppDiscovery.Release(shell); }
            return true;
        });
    }

    private static bool IsOwnedShortcut() => AppDiscovery.OnSta(() =>
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!) ?? throw new InvalidOperationException("Windows Script Host 激活失败。");
            shortcut = ((dynamic)shell).CreateShortcut(Shortcut);
            return string.Equals(Convert.ToString(((dynamic)shortcut).Description), OwnershipMarker, StringComparison.Ordinal);
        }
        catch (COMException) { return false; }
        finally { AppDiscovery.Release(shortcut); AppDiscovery.Release(shell); }
    });
}

public static class NativeWindowHelper
{
    public static void ConfigureDock(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        // WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE; do not steal keyboard focus from foreground apps.
        SetWindowLongPtr(hwnd, -20, new IntPtr((style | 0x80 | 0x08000000) & ~0x40000));
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    }
    public static void SetPosition(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, new IntPtr(-1), x, y, width, height, 0x0010);
    public static uint GetDpi(IntPtr hwnd) => GetDpiForWindow(hwnd);
    public static void SetCorners(IntPtr hwnd, bool rounded = true)
    {
        var preference = rounded ? 2 : 1;
        DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    public event EventHandler? Pressed;
    public bool IsRegistered { get; }
    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("LumaDock hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        IsRegistered = RegisterHotKey(_source.Handle, 0x4C44, 0x0001 | 0x0002 | 0x4000, 0x20); // Ctrl+Alt+Space, MOD_NOREPEAT
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0312 && wparam.ToInt32() == 0x4C44) { handled = true; Pressed?.Invoke(this, EventArgs.Empty); }
        return IntPtr.Zero;
    }
    public void Dispose() { if (IsRegistered) UnregisterHotKey(_source.Handle, 0x4C44); _source.RemoveHook(Hook); _source.Dispose(); }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
