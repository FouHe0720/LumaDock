using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumaDock.Core;

namespace LumaDock.Windows;

public sealed record RunningWindow(IntPtr Handle, string Title, string ExecutablePath, string AppUserModelId, uint ProcessId);

/// <summary>Real, switchable top-level windows, refreshed from WinEvents only. No process polling.</summary>
public sealed class WindowTracker : IDisposable
{
    private static readonly ConcurrentDictionary<int, BitmapSource> GenericIcons = new();
    // These executables can host unrelated applications. A shortcut's arguments
    // do not identify which of the host's windows is being displayed.
    private static readonly HashSet<string> AmbiguousHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "applicationframehost.exe", "rundll32.exe", "dllhost.exe", "svchost.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe",
        "wscript.exe", "cscript.exe", "msiexec.exe", "wwahost.exe", "runtimebroker.exe", "mmc.exe",
        "dotnet.exe", "electron.exe", "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe",
        "opera.exe", "vivaldi.exe", "iexplore.exe", "browser.exe", "arc.exe"
    };
    private readonly List<IntPtr> _hooks = [];
    private readonly WinEventCallback _callback;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private IReadOnlyList<RunningWindow> _windows = Array.Empty<RunningWindow>();
    private bool _disposed;
    private int _pendingDispatch;
    private int _identityRefreshNeeded;
    public event EventHandler? Changed;
    public IReadOnlyList<RunningWindow> Windows => _windows;
    public IntPtr ForegroundWindow => _disposed ? IntPtr.Zero : GetForegroundWindow();
    public bool IsDesktopForeground => !_disposed && IsDesktopSurface(GetForegroundWindow());

    public WindowTracker()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callback = OnWinEvent;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(140) };
        _debounce.Tick += RefreshTick;
    }

    public void Start()
    {
        if (_hooks.Count > 0 || _disposed) return;
        foreach (var range in new[] { (0x0003u, 0x0003u), (0x0016u, 0x0017u), (0x8000u, 0x8003u), (0x800Bu, 0x800Bu) })
        {
            var hook = SetWinEventHook(range.Item1, range.Item2, IntPtr.Zero, _callback, 0, 0, 0x0002 /* SKIPOWNPROCESS */);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }
        Refresh();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero || (eventType >= 0x8000 && (objectId != 0 || childId != 0))) return;
        // Location events can be frequent during a drag. Only the foreground window can change
        // fullscreen suppression; those events never need to re-enumerate process identities.
        if (eventType == 0x800B && hwnd != GetForegroundWindow()) return;
        // Switching between already indexed windows only changes activation/fullscreen
        // state. Avoid opening every process and property store on each Alt+Tab.
        // Creation/show/hide/destruction still invalidate the index; an unknown
        // foreground window also requests a full discovery pass.
        var refreshIdentity = eventType is >= 0x8000 and <= 0x8003
            || (eventType == 0x0003 && !_windows.Any(window => window.Handle == hwnd));
        if (refreshIdentity) Interlocked.Exchange(ref _identityRefreshNeeded, 1);
        if (Interlocked.Exchange(ref _pendingDispatch, 1) != 0) return;
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _pendingDispatch, 0);
                if (!_disposed) { _debounce.Stop(); _debounce.Start(); }
            }));
    }
    private void RefreshTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        if (Interlocked.Exchange(ref _identityRefreshNeeded, 0) != 0) Refresh();
        else if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        if (_disposed) return;
        var result = new List<RunningWindow>();
        var processCache = new Dictionary<uint, (string Path, string Aumid)>();
        (string Path, string Aumid) ProcessInfo(uint pid)
        {
            if (processCache.TryGetValue(pid, out var value)) return value;
            value = ReadProcess(pid); processCache[pid] = value; return value;
        }
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0 || GetAncestor(hwnd, 2) != hwnd) return true;
            var style = GetWindowLongPtr(hwnd, -20).ToInt64();
            if ((style & (0x80 | 0x08000000)) != 0) return true; // Tool and non-activating helper surfaces.
            if (IsShellSurface(hwnd)) return true;
            // Owned dialogs belong to the owner's taskbar group, unless they explicitly
            // request their own application button. Switching the owner lets Windows
            // activate its current modal dialog rather than creating duplicate entries.
            if (GetWindow(hwnd, 4) != IntPtr.Zero && (style & 0x40000) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || pid == 0) return true;
            var info = ProcessInfo(pid);
            // UWP windows may be owned by ApplicationFrameHost; application child carries identity.
            if (info.Path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                EnumChildWindows(hwnd, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out var childPid);
                    if (childPid != 0 && childPid != pid)
                    {
                        var childInfo = ProcessInfo(childPid);
                        if (!string.IsNullOrEmpty(childInfo.Path)) info = childInfo;
                    }
                    return true;
                }, IntPtr.Zero);
            var aumid = ReadWindowAumid(hwnd);
            if (string.IsNullOrEmpty(aumid)) aumid = info.Aumid;
            var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (!string.IsNullOrWhiteSpace(title.ToString()))
                result.Add(new RunningWindow(hwnd, title.ToString(), info.Path, aumid, pid));
            return true;
        }, IntPtr.Zero);
        _windows = result.AsReadOnly();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRunning(AppEntry entry) => _windows.Any(window => Matches(entry, window));
    public IReadOnlyList<RunningWindow> GetWindows(AppEntry entry) => _disposed
        ? Array.Empty<RunningWindow>() : _windows.Where(window => Matches(entry, window)).ToArray();

    public bool Activate(AppEntry entry)
    {
        var candidates = _windows.Where(window => Matches(entry, window) && IsCurrentWindow(window)).ToList();
        if (candidates.Count == 0) return false;
        var foreground = GetForegroundWindow();
        var currentIndex = candidates.FindIndex(w => w.Handle == foreground);
        return ActivateWindow(candidates[(currentIndex + 1) % candidates.Count]);
    }

    /// <summary>Request normal activation; Windows foreground and privilege rules still apply.</summary>
    public bool ActivateWindow(RunningWindow window)
    {
        if (_disposed || !IsCurrentWindow(window)) return false;
        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9);
        var target = window.Handle;
        var popup = GetLastActivePopup(target);
        if (popup != IntPtr.Zero && popup != target && IsWindowVisible(popup))
        {
            GetWindowThreadProcessId(popup, out var owner);
            if (owner == window.ProcessId) target = popup;
        }
        return SetForegroundWindow(target);
    }

    public bool MinimizeWindow(RunningWindow window) => !_disposed && IsCurrentWindow(window)
        && ShowWindowAsync(window.Handle, 6);

    /// <summary>Asks the target to close normally; no process termination or forced timeout.</summary>
    public bool CloseWindow(RunningWindow window) => !_disposed && IsCurrentWindow(window)
        && PostMessage(window.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);

    private static bool IsCurrentWindow(RunningWindow window)
    {
        if (window.Handle == IntPtr.Zero || window.ProcessId == 0 || !IsWindow(window.Handle) || IsShellSurface(window.Handle)) return false;
        GetWindowThreadProcessId(window.Handle, out var owner);
        return owner == window.ProcessId;
    }

    /// <summary>Reads a current caption only when it is needed by a window menu.
    /// It never refreshes process identity or adds a title-change event subscription.</summary>
    public static string ReadWindowTitle(RunningWindow window)
    {
        if (!IsCurrentWindow(window)) return "已关闭窗口";
        var length = Math.Clamp(GetWindowTextLength(window.Handle), 0, 32767);
        var title = new StringBuilder(length + 1);
        if (GetWindowText(window.Handle, title, title.Capacity) > 0 && !string.IsNullOrWhiteSpace(title.ToString()))
            return title.ToString();
        return string.IsNullOrWhiteSpace(window.Title) ? "未命名窗口" : window.Title;
    }

    /// <summary>Only Explorer's desktop and native taskbar surfaces are desktop.
    /// A normal File Explorer folder window remains an application window.</summary>
    public static bool IsDesktopSurface(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        var className = new StringBuilder(128);
        GetClassName(window, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    public static bool Matches(AppEntry entry, RunningWindow window)
    {
        if (!string.IsNullOrWhiteSpace(entry.AppUserModelId) && string.Equals(entry.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase)) return true;
        // Both packaged and ordinary desktop identities can share an executable.
        // A conflicting explicit identity must never fall back to the host's path.
        if (!string.IsNullOrWhiteSpace(entry.AppUserModelId) && !string.IsNullOrWhiteSpace(window.AppUserModelId)) return false;
        var executable = !string.IsNullOrWhiteSpace(entry.ExecutablePath) ? entry.ExecutablePath : entry.LaunchPath;
        if (string.IsNullOrEmpty(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        // Without an exact AUMID, profiles, PWA URLs, documents and runtime scripts
        // cannot be attributed reliably merely because their host executable matches.
        if (!string.IsNullOrWhiteSpace(entry.Arguments) && AmbiguousHosts.Contains(Path.GetFileName(executable))) return false;
        return string.Equals(Environment.ExpandEnvironmentVariables(executable), window.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies a borrowed window icon to a frozen WPF source. It never owns or destroys the HICON.</summary>
    public static BitmapSource? GetWindowIcon(RunningWindow window, int pixels = 64)
    {
        if (!IsCurrentWindow(window)) return null;
        pixels = Math.Clamp(pixels, 16, 256);
        try
        {
            IntPtr icon = IntPtr.Zero;
            foreach (var size in new[] { pixels > 24 ? 1 : 2, 0, 1 })
            {
                // One 100 ms deadline for an unresponsive process. Do not continue to
                // subsequent WM_GETICON variants when the first request timed out.
                if (SendMessageTimeout(window.Handle, 0x007F, new IntPtr(size), IntPtr.Zero, 0x0002, 100, out var result) == IntPtr.Zero) break;
                icon = new IntPtr(unchecked((long)result.ToUInt64()));
                if (icon != IntPtr.Zero) break;
            }
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window.Handle, pixels > 24 ? -14 : -34);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window.Handle, -14);
            if (icon == IntPtr.Zero || !IsCurrentWindow(window)) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(pixels, pixels));
            bitmap.Freeze(); return bitmap;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        { Debug.WriteLine(error.Message); return null; }
    }

    /// <summary>The Windows shared default application icon, explicitly used as an unidentified-icon fallback.</summary>
    public static BitmapSource? GetGenericWindowIcon(int pixels = 64)
    {
        var size = Math.Clamp((int)Math.Ceiling(Math.Clamp(pixels, 16, 256) / 16d) * 16, 16, 256);
        if (GenericIcons.TryGetValue(size, out var cached)) return cached;
        try
        {
            var icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
            if (icon == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            image.Freeze(); return GenericIcons.GetOrAdd(size, image);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException)
        { Debug.WriteLine(error.Message); return null; }
    }

    private static bool IsShellSurface(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "Shell_InputSwitchTopLevelWindow" or "TaskListThumbnailWnd";
    }

    public bool IsForegroundFullscreen
    {
        get
        {
            var hwnd = FullscreenCandidate();
            if (hwnd == IntPtr.Zero) return false;
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorNative { Size = Marshal.SizeOf<MonitorNative>() };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var rect)) return false;
            return rect.Left <= info.Monitor.Left + 1 && rect.Top <= info.Monitor.Top + 1 &&
                   rect.Right >= info.Monitor.Right - 1 && rect.Bottom >= info.Monitor.Bottom - 1;
        }
    }

    /// <summary>Suppress a Dock only when the fullscreen foreground covers its selected display.</summary>
    public bool IsForegroundFullscreenOnMonitor(string deviceName)
    {
        var hwnd = FullscreenCandidate();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;
        var monitors = MonitorService.GetMonitors();
        var selected = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (selected == null) return false;
        return rect.Left <= selected.Left + 1 && rect.Top <= selected.Top + 1 &&
               rect.Right >= selected.Left + selected.Width - 1 && rect.Bottom >= selected.Top + selected.Height - 1;
    }

    private static IntPtr FullscreenCandidate()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return IntPtr.Zero;
        if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return IntPtr.Zero;
        var className = new StringBuilder(128); GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? IntPtr.Zero : hwnd;
    }

    private static (string Path, string Aumid) ReadProcess(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return ("", "");
        try
        {
            var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
            var executable = QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : "";
            uint idSize = 0;
            string aumid = "";
            if (GetApplicationUserModelId(process, ref idSize, null) == 122 && idSize is > 0 and < 32768)
            {
                var id = new StringBuilder((int)idSize);
                if (GetApplicationUserModelId(process, ref idSize, id) == 0) aumid = id.ToString();
            }
            return (executable, aumid);
        }
        finally { CloseHandle(process); }
    }

    private static string ReadWindowAumid(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return "";
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            if (store.GetValue(ref key, out value) == 0 && value.Type == 31 && value.Pointer != IntPtr.Zero)
                return Marshal.PtrToStringUni(value.Pointer) ?? "";
        }
        catch (COMException ex) { Debug.WriteLine(ex.Message); }
        finally { PropVariantClear(ref value); if (store != null) Marshal.FinalReleaseComObject(store); }
        return "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _debounce.Stop(); _debounce.Tick -= RefreshTick;
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear(); Changed = null; _windows = Array.Empty<RunningWindow>();
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime);
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorNative { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint milliseconds, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorNative info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
