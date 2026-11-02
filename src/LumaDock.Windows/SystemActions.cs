using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LumaDock.Windows;

public enum SystemAction { ShowDesktop, StartMenu, Search, TaskView, QuickSettings, NotificationCenter, InputSwitch, TrayKeyboardFocus, TaskManager }
public enum SystemSettingsPage { System, Network, Sound, Bluetooth, Display, Taskbar, DateAndTime, Keyboard, Notifications, AirplaneMode, Battery, Accessibility, NativeNetwork }
public sealed record SystemActionResult(bool Succeeded, string? Error = null);
public readonly record struct SystemKeyInput(ushort VirtualKey, bool KeyUp);

/// <summary>User-invoked system entry points only. No taskbar state, registry or Explorer changes.</summary>
public static class SystemActions
{
    public static SystemActionResult Execute(SystemAction action) => Execute(action, null, Environment.OSVersion.Version.Build);

    /// <summary>The injected sender is used by component tests; it prevents any actual keyboard input.</summary>
    public static SystemActionResult Execute(SystemAction action, Func<IReadOnlyList<SystemKeyInput>, bool>? sender, int build)
    {
        if (!Enum.IsDefined(action)) return new(false, "此系统入口不可用。");
        var keys = Keys(action, build);
        if (sender == null && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            return new(false, "请松开 Ctrl、Shift、Alt 或 Windows 键后再试。");
        var packets = keys.Select(key => new SystemKeyInput(key, false))
            .Concat(keys.Reverse().Select(key => new SystemKeyInput(key, true))).ToArray();
        try
        {
            if (!(sender ?? Send)(packets)) return new(false, "Windows 未接受此操作。请使用原生任务栏，或检查当前窗口的权限。");
            return new(true);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { return new(false, "系统入口暂时不可用。"); }
    }

    private static ushort[] Keys(SystemAction action, int build) => action switch
    {
        SystemAction.ShowDesktop => [0x5B, 0x44],
        SystemAction.StartMenu => [0x5B],
        SystemAction.Search => [0x5B, 0x53],
        SystemAction.TaskView => [0x5B, 0x09],
        SystemAction.QuickSettings => [0x5B, 0x41],
        SystemAction.NotificationCenter => [0x5B, build >= 22000 ? (ushort)0x4E : (ushort)0x41],
        SystemAction.InputSwitch => [0x5B, 0x20],
        // This documented shortcut focuses the native notification area; it does not clone its icons.
        SystemAction.TrayKeyboardFocus => [0x5B, 0x42],
        SystemAction.TaskManager => [0x11, 0x10, 0x1B],
        _ => []
    };

    public static string? SettingsUri(SystemSettingsPage page) => page switch
    {
        SystemSettingsPage.System => "ms-settings:",
        SystemSettingsPage.Network => "ms-settings:network-status",
        SystemSettingsPage.Sound => "ms-settings:sound",
        SystemSettingsPage.Bluetooth => "ms-settings:bluetooth",
        SystemSettingsPage.Display => "ms-settings:display",
        SystemSettingsPage.Taskbar => "ms-settings:taskbar",
        SystemSettingsPage.DateAndTime => "ms-settings:dateandtime",
        SystemSettingsPage.Keyboard => "ms-settings:typing",
        SystemSettingsPage.Notifications => "ms-settings:notifications",
        SystemSettingsPage.AirplaneMode => "ms-settings:network-airplanemode",
        SystemSettingsPage.Battery => "ms-settings:powersleep",
        SystemSettingsPage.Accessibility => "ms-settings:easeofaccess",
        SystemSettingsPage.NativeNetwork => "ms-settings:network-wifi",
        _ => null
    };

    public static SystemActionResult OpenSettings(SystemSettingsPage page)
    {
        var uri = SettingsUri(page);
        if (uri == null) return new(false, "此设置入口不可用。");
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return new(true);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { return new(false, "无法打开 Windows 设置。请从开始菜单打开。"); }
    }

    private static bool Send(IReadOnlyList<SystemKeyInput> packets)
    {
        var native = packets.Select(NativeInput).ToArray();
        var sent = SendInput((uint)native.Length, native, Marshal.SizeOf<Input>());
        if (sent == native.Length) return true;
        // A partial submission must not leave an injected modifier held down.
        var release = PendingReleases(packets, (int)sent).Select(NativeInput).ToArray();
        if (release.Length != 0) SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        return false;
    }

    private static Input NativeInput(SystemKeyInput packet) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = packet.VirtualKey,
            Flags = (packet.KeyUp ? 2u : 0u) | (packet.VirtualKey is 0x5B or 0x5C ? 1u : 0u) } }
    };

    private static SystemKeyInput[] PendingReleases(IReadOnlyList<SystemKeyInput> packets, int accepted)
    {
        var held = new List<ushort>();
        foreach (var packet in packets.Take(Math.Clamp(accepted, 0, packets.Count)))
        {
            if (packet.KeyUp) held.Remove(packet.VirtualKey);
            else if (!held.Contains(packet.VirtualKey)) held.Add(packet.VirtualKey);
        }
        return held.AsEnumerable().Reverse().Select(key => new SystemKeyInput(key, true)).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
