using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LumaDock.Windows;

public sealed record BatterySnapshot(bool HasBattery, bool? IsCharging, int? Percent, string Text, bool Available = true);
public sealed record NetworkSnapshot(bool Connected, string Text, bool Available = true);
public sealed record KeyboardSnapshot(string Text, string LanguageTag);
public sealed record SystemStatusSnapshot(BatterySnapshot Battery, NetworkSnapshot Network, KeyboardSnapshot Keyboard);

/// <summary>Read-only battery, usable-interface and foreground language state. UI-thread lifetime; no polling.</summary>
public sealed class SystemStatusService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WinEventCallback foregroundCallback;
    private IntPtr foregroundHook, externalWindow;
    private int queued, generalRefresh;
    private volatile bool disposed;
    public SystemStatusSnapshot Current { get; private set; }
    public event EventHandler? Changed;

    public SystemStatusService()
    {
        RememberExternalWindow();
        Current = new(ReadBattery(), ReadNetwork(), ReadKeyboard());
        foregroundCallback = (_, _, _, _, _, _, _) => QueueRefresh(false);
        foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 2);
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    public bool Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return false;
        RememberExternalWindow();
        return Publish(new(ReadBattery(), ReadNetwork(), ReadKeyboard()));
    }

    /// <summary>Refresh just the displayed percentage when a visible minute clock ticks;
    /// network discovery remains driven by NetworkChange events or a panel opening.</summary>
    public bool RefreshPower()
    {
        dispatcher.VerifyAccess();
        return !disposed && Publish(Current with { Battery = ReadBattery() });
    }

    private bool Publish(SystemStatusSnapshot latest)
    {
        if (latest == Current) return false;
        Current = latest; Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    private void QueueRefresh(bool general)
    {
        if (general) Interlocked.Exchange(ref generalRefresh, 1);
        if (disposed || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed) return;
            if (Interlocked.Exchange(ref generalRefresh, 0) != 0) Refresh();
            else
            {
                // Foreground changes do not re-enumerate network interfaces or power state.
                RememberExternalWindow(); Publish(Current with { Keyboard = ReadKeyboard() });
            }
        }));
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => QueueRefresh(true);
    private void NetworkAddressChanged(object? sender, EventArgs e) => QueueRefresh(true);
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) => QueueRefresh(true);

    public static BatterySnapshot ReadBattery() => GetSystemPowerStatus(out var status)
        ? DescribeBattery(status.BatteryFlag, status.BatteryLifePercent, status.ACLineStatus)
        : new(false, null, null, "电量未知", false);

    public static BatterySnapshot DescribeBattery(byte flags, byte percent, byte acLine)
    {
        if (flags == 255) return new(false, null, null, "电量未知", false);
        if ((flags & 128) != 0) return new(false, null, null, acLine == 1 ? "外接电源" : "无电池");
        var knownPercent = percent <= 100 ? (int?)percent : null;
        var charging = (flags & 8) != 0;
        var text = knownPercent is int value ? $"{value}%" : "电量未知";
        if (charging) text += " · 充电中";
        else if (acLine == 1) text += " · 已接通电源";
        return new(true, charging, knownPercent, text);
    }

    private static NetworkSnapshot ReadNetwork()
    {
        try
        {
            var available = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is not
                    (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).ToArray();
            var connected = available.Where(adapter => adapter.GetIPProperties().UnicastAddresses.Count > 0).ToArray();
            if (connected.Length == 0) return new(false, "网络未连接");
            var wireless = connected.Any(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            return new(true, wireless ? "Wi-Fi 已连接" : "网络已连接");
        }
        catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        { return new(false, "网络状态未知", false); }
    }

    private void RememberExternalWindow()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var pid);
        if (pid != 0 && pid != (uint)Environment.ProcessId) externalWindow = window;
    }

    private KeyboardSnapshot ReadKeyboard()
    {
        if (externalWindow == IntPtr.Zero || !IsWindow(externalWindow)) return new("输入法", "");
        var thread = GetWindowThreadProcessId(externalWindow, out _);
        if (thread == 0) return new("输入法", "");
        var layout = GetKeyboardLayout(thread);
        if (layout == IntPtr.Zero) return new("输入法", "");
        try
        {
            var culture = CultureInfo.GetCultureInfo((int)(layout.ToInt64() & 0xffff));
            if (culture.Name.Length == 0) return new("输入法", "");
            var text = culture.TwoLetterISOLanguageName switch
            { "zh" => "中文", "en" => "ENG", "ja" => "日语", "ko" => "韩语", _ => culture.NativeName };
            return new(text, culture.Name);
        }
        catch (CultureNotFoundException) { return new("输入法", ""); }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        foregroundHook = IntPtr.Zero;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        Changed = null;
    }

    private delegate void WinEventCallback(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
