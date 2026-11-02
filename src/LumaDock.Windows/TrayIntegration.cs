using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace LumaDock.Windows;

/// <summary>Opens Explorer's own overflow surface via its accessibility pattern.
/// Never enumerates notification-icon memory, injects into Explorer or imitates icons.</summary>
public static class TrayIntegration
{
    private static readonly object gate = new();
    private static Task<SystemActionResult>? pending;
    private static readonly string[] Names = ["显示隐藏的图标", "显示隐藏图标", "Show hidden icons"];

    /// <summary>Read-only capability check; never opens or closes Windows UI.</summary>
    public static async Task<SystemActionResult> ProbeOverflowAsync()
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.5);
        try
        {
            return await Task.Run(() =>
            {
                try { var found = FindButton(deadline); return found.Button != null ? new(true) : new SystemActionResult(false, found.Error); }
                catch (Exception error) when (IsAutomationError(error)) { return new(false, "暂时无法读取 Windows 后台应用入口。"); }
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException) { return new(false, "读取 Windows 后台应用入口超时。"); }
    }

    public static async Task<SystemActionResult> OpenOverflowAsync()
    {
        Task<SystemActionResult> operation;
        lock (gate)
        {
            if (pending is { IsCompleted: false }) return new(false, "后台应用列表正在打开，请稍候。");
            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.5);
            operation = pending = Task.Run(() => Open(deadline));
        }
        // A stuck third-party accessibility provider must not freeze the Dock.
        // Only one worker is allowed; expiration is checked again before invocation.
        try { return await operation.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { return new(false, "Windows 托盘暂时没有响应，请从屏幕边缘打开原生任务栏后重试。"); }
    }

    private static SystemActionResult Open(long deadline)
    {
        try
        {
            if (OverflowVisible()) return new(true);
            var found = FindButton(deadline);
            if (found.Button == null) return new(false, found.Error);
            if (Stopwatch.GetTimestamp() > deadline) return new(false, "后台应用入口已过期，请重试。");
            if (!found.Button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                return new(false, "此任务栏不提供后台应用入口，请从原生任务栏打开。");
            ((InvokePattern)pattern).Invoke();
            // Invocation alone is not evidence that a popup appeared.
            var end = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * .7);
            while (Stopwatch.GetTimestamp() < end)
            {
                if (OverflowVisible()) return new(true);
                Thread.Sleep(35);
            }
            return new(false, "Windows 没有展开后台应用列表。请先从屏幕边缘唤出任务栏后重试。");
        }
        catch (Exception error) when (IsAutomationError(error))
        { return new(false, "暂时无法打开 Windows 后台应用列表，请使用原生任务栏入口。"); }
    }

    private static (AutomationElement? Button, string? Error) FindButton(long deadline)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return (null, "Windows 任务栏未运行，无法读取后台应用。");
        GetWindowThreadProcessId(taskbar, out var shellPid);
        if (shellPid == 0) return (null, "Windows 任务栏未就绪。");
        var root = AutomationElement.FromHandle(taskbar);
        var buttons = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var candidates = new List<AutomationElement>();
        foreach (AutomationElement button in buttons)
        {
            if (Stopwatch.GetTimestamp() > deadline) return (null, "读取后台应用超时，请从原生任务栏打开。");
            var current = button.Current;
            if (current.ProcessId != shellPid || !current.IsEnabled) continue;
            var id = current.AutomationId;
            var knownId = id is "SystemTray.OverflowButton" or "1502";
            var name = current.Name.Trim().TrimEnd('.');
            if (knownId || Names.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase))) candidates.Add(button);
        }
        return candidates.Count == 1 ? (candidates[0], null)
            : (null, "未找到隐藏图标入口。请在 Windows 任务栏设置中开启“隐藏的图标菜单”。");
    }

    private static bool IsAutomationError(Exception error) => error is ElementNotAvailableException or ElementNotEnabledException
        or InvalidOperationException or COMException or UnauthorizedAccessException;

    private static bool OverflowVisible() => new[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" }
        .Any(className => FindWindow(className, null) is var handle && handle != IntPtr.Zero && IsWindowVisible(handle));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
