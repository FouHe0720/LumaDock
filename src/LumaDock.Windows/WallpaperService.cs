using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumaDock.Windows;

/// <summary>Windows' placement of the original desktop image; values match DESKTOP_WALLPAPER_POSITION.</summary>
public enum WallpaperPosition { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

/// <summary>Attached physical monitor coordinates and the actual Shell wallpaper file, never captured screen content.</summary>
public sealed record WallpaperMonitor(string DeviceName, string MonitorId, int Left, int Top, int Width, int Height,
    string ImagePath, bool ImageAvailable, long LastWriteUtcTicks = 0, long ImageBytes = 0);

/// <param name="BackgroundColor">Native COLORREF: red in the low byte, then green and blue.</param>
public sealed record DesktopWallpaperSnapshot(bool Available, WallpaperPosition Position, uint BackgroundColor,
    IReadOnlyList<WallpaperMonitor> Monitors, string? Failure);

/// <summary>
/// Read-only Shell wallpaper metadata. Refreshes on relevant system notifications;
/// there is no screen capture, decoder, polling timer or continuously running worker.
/// Create and dispose on the WPF UI thread; Changed is delivered on that thread.
/// </summary>
public sealed class WallpaperService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private int refreshQueued;
    private volatile bool disposed;
    public DesktopWallpaperSnapshot Current { get; private set; }
    public event EventHandler? Changed;

    public WallpaperService()
    {
        Current = Read();
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
    }

    /// <summary>Call after WM_SETTINGCHANGE or when revealing a previously hidden surface.</summary>
    public bool Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return false;
        var latest = Read();
        if (Equivalent(Current, latest)) return false;
        Current = latest;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Returns actual attached-monitor wallpaper metadata. COM reads run in an STA and release all native allocations.</summary>
    public static DesktopWallpaperSnapshot Read() => AppDiscovery.OnSta(ReadCore);

    private static DesktopWallpaperSnapshot ReadCore()
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), throwOnError: true)!;
            wallpaper = (IDesktopWallpaper)Activator.CreateInstance(type)!;
            Marshal.ThrowExceptionForHR(wallpaper.GetPosition(out var position));
            Marshal.ThrowExceptionForHR(wallpaper.GetBackgroundColor(out var background));
            Marshal.ThrowExceptionForHR(wallpaper.GetMonitorDevicePathCount(out var count));
            if (!Enum.IsDefined(position)) return Unavailable("Unknown desktop wallpaper position");
            // The Shell can retain detached monitors. GetMonitorRECT distinguishes them.
            var nativeMonitors = MonitorService.GetMonitors();
            var result = new List<WallpaperMonitor>();
            for (uint index = 0; index < Math.Min(count, 64); index++)
            {
                IntPtr monitorPointer = IntPtr.Zero, imagePointer = IntPtr.Zero;
                try
                {
                    if (wallpaper.GetMonitorDevicePathAt(index, out monitorPointer) != 0 || monitorPointer == IntPtr.Zero) continue;
                    var id = Marshal.PtrToStringUni(monitorPointer) ?? "";
                    if (wallpaper.GetMonitorRECT(id, out var rect) != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
                    var pathResult = wallpaper.GetWallpaper(id, out imagePointer);
                    var path = pathResult >= 0 && imagePointer != IntPtr.Zero ? Marshal.PtrToStringUni(imagePointer) ?? "" : "";
                    var monitor = nativeMonitors.FirstOrDefault(m => m.Left == rect.Left && m.Top == rect.Top
                        && m.Width == rect.Right - rect.Left && m.Height == rect.Bottom - rect.Top);
                    bool available = false; long modified = 0, bytes = 0;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            var file = new FileInfo(path);
                            if (file.Exists) { available = true; modified = file.LastWriteTimeUtc.Ticks; bytes = file.Length; }
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { /* Retain the Shell's path, but advertise no readable image. */ }
                    result.Add(new WallpaperMonitor(monitor?.DeviceName ?? "", id, rect.Left, rect.Top,
                        rect.Right - rect.Left, rect.Bottom - rect.Top, path, available, modified, bytes));
                }
                finally
                {
                    if (imagePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(imagePointer);
                    if (monitorPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(monitorPointer);
                }
            }
            return result.Count == 0 ? Unavailable("No attached wallpaper monitors")
                : new(true, position, background, Array.AsReadOnly(result.ToArray()), null);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or TypeLoadException
            or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Unavailable($"Shell wallpaper unavailable (0x{error.HResult:X8})"); }
        finally
        {
            if (wallpaper != null && Marshal.IsComObject(wallpaper)) Marshal.FinalReleaseComObject(wallpaper);
        }
    }

    private static DesktopWallpaperSnapshot Unavailable(string reason) => new(false, WallpaperPosition.Fill, 0,
        Array.Empty<WallpaperMonitor>(), reason);
    private static bool Equivalent(DesktopWallpaperSnapshot first, DesktopWallpaperSnapshot second) =>
        first.Available == second.Available && first.Position == second.Position && first.BackgroundColor == second.BackgroundColor
        && first.Failure == second.Failure && first.Monitors.SequenceEqual(second.Monitors);

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle) QueueRefresh();
    }
    private void DisplaysChanged(object? sender, EventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        if (disposed || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            if (!disposed) Refresh();
        }));
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        Changed = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    // Native declarations preserve the documented vtable order. Setter slots are
    // declared solely to reach the read methods; this service never invokes them.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, out IntPtr path);
        [PreserveSig] int GetMonitorDevicePathAt(uint index, out IntPtr monitorId);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRect rect);
        [PreserveSig] int SetBackgroundColor(uint color);
        [PreserveSig] int GetBackgroundColor(out uint color);
        [PreserveSig] int SetPosition(WallpaperPosition position);
        [PreserveSig] int GetPosition(out WallpaperPosition position);
    }
}
