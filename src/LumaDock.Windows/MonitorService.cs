using System.Runtime.InteropServices;

namespace LumaDock.Windows;

public sealed record MonitorInfo(string DeviceName, string Label, int Left, int Top, int Width, int Height,
    int WorkLeft, int WorkTop, int WorkWidth, int WorkHeight, bool IsPrimary);

public static class MonitorService
{
    public static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
                result.Add(new MonitorInfo(info.Device, $"显示器 {result.Count + 1}{((info.Flags & 1) != 0 ? " · 主屏幕" : "")} ({info.Monitor.Right - info.Monitor.Left} × {info.Monitor.Bottom - info.Monitor.Top})",
                    info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
                    info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoEx
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
