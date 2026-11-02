using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LumaDock.Windows;

public sealed record BrightnessSnapshot(bool Available, double Percent, string Reason);

/// <summary>The laptop display's WMI brightness control. Queries happen only on request,
/// off the UI thread; unsupported external displays never receive a fabricated value.</summary>
public sealed class BrightnessService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool disposed;
    private string instanceName = "";
    private Task? pendingRefresh;
    public BrightnessSnapshot Current { get; private set; } = new(false, 0, "打开面板后读取屏幕亮度");
    public event EventHandler? Changed;

    public Task RefreshAsync()
    {
        dispatcher.VerifyAccess();
        if (disposed) return Task.CompletedTask;
        if (pendingRefresh is { IsCompleted: false }) return pendingRefresh;
        return pendingRefresh = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            var result = await Task.Run(Read);
            await PublishAsync(result.Snapshot, result.Instance);
        }
        finally { gate.Release(); }
    }

    public async Task<SystemActionResult> SetBrightnessAsync(double fraction)
    {
        dispatcher.VerifyAccess();
        if (disposed || !double.IsFinite(fraction)) return new(false, "亮度值不可用。");
        await gate.WaitAsync();
        try
        {
            if (disposed) return new(false, "亮度服务已关闭。");
            // Do not issue a queued write against a different display after a reconnect.
            var expected = instanceName;
            var percent = (byte)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
            var result = await Task.Run(() => Write(expected, percent));
            await PublishAsync(result.Snapshot, result.Instance);
            return result.Action;
        }
        finally { gate.Release(); }
    }

    private async Task PublishAsync(BrightnessSnapshot value, string instance)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        void Apply() { if (!disposed) { instanceName = instance; Publish(value); } }
        if (dispatcher.CheckAccess()) { Apply(); return; }
        try { await dispatcher.InvokeAsync(Apply, DispatcherPriority.Background); }
        catch (TaskCanceledException) { /* Dispatcher shutdown cancels queued notifications. */ }
    }

    private void Publish(BrightnessSnapshot value)
    {
        if (Current == value) return;
        Current = value; Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (BrightnessSnapshot Snapshot, string Instance) Read()
    {
        object? locator = null, service = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\WMI");
            return ReadFrom(service!);
        }
        catch (Exception error) when (IsWmiError(error))
        { return (new(false, 0, "此屏幕未提供亮度接口，请使用 Windows 显示设置。"), ""); }
        finally { Release(service); Release(locator); }
    }

    private static (BrightnessSnapshot Snapshot, string Instance) ReadFrom(object service)
    {
        object? set = null;
        try
        {
            // SWbemObjectSet's .NET IEnumVARIANT adapter resets the enumerator;
            // wbemFlagForwardOnly (32) rejects that reset. There is one small result.
            set = ((dynamic)service).ExecQuery("SELECT Active,InstanceName,CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE", "WQL", 16);
            var values = new List<(double Value, string Instance)>();
            Visit(set!, item =>
            {
                var value = Convert.ToDouble(Property(item, "CurrentBrightness"));
                var name = Convert.ToString(Property(item, "InstanceName")) ?? "";
                if (double.IsFinite(value) && value is >= 0 and <= 100 && name.Length > 0) values.Add((value, name));
            });
            return values.Count switch
            {
                1 => (new(true, values[0].Value, "内置屏幕"), values[0].Instance),
                > 1 => (new(false, 0, "检测到多块可调亮度屏幕，请在 Windows 显示设置中选择。"), ""),
                _ => (new(false, 0, "此屏幕未提供亮度接口，请使用 Windows 显示设置。"), "")
            };
        }
        finally { Release(set); }
    }

    private static (BrightnessSnapshot Snapshot, string Instance, SystemActionResult Action) Write(string expected, byte percent)
    {
        object? locator = null, service = null, set = null;
        try
        {
            if (expected.Length == 0) return (new(false, 0, "请先读取屏幕亮度。"), "", new(false, "请先读取屏幕亮度。"));
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            service = ((dynamic)locator!).ConnectServer(".", @"root\WMI");
            var before = ReadFrom(service!);
            if (!before.Snapshot.Available || before.Instance != expected)
                return (before.Snapshot, before.Instance, new(false, "屏幕已变更，请重新打开面板。"));
            set = ((dynamic)service!).ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE", "WQL", 16);
            var sent = false;
            Visit(set!, item =>
            {
                if (sent || Convert.ToString(Property(item, "InstanceName")) != expected) return;
                object? methods = null, method = null, definition = null, input = null, output = null;
                try
                {
                    methods = ((dynamic)item).Methods_;
                    method = ((dynamic)methods!).Item("WmiSetBrightness");
                    definition = ((dynamic)method!).InParameters;
                    input = ((dynamic)definition!).SpawnInstance_();
                    SetProperty(input!, "Timeout", 0u);
                    SetProperty(input!, "Brightness", percent);
                    output = ((dynamic)item).ExecMethod_("WmiSetBrightness", input);
                    var status = Convert.ToUInt32(Property(output!, "ReturnValue"));
                    sent = status == 0;
                }
                finally { Release(output); Release(input); Release(definition); Release(method); Release(methods); }
            });
            var after = ReadFrom(service!);
            return (after.Snapshot, after.Instance, sent ? new(true) : new(false, "屏幕未接受亮度调整，请使用 Windows 显示设置。"));
        }
        catch (Exception error) when (IsWmiError(error))
        { return (new(false, 0, "亮度接口暂时不可用。"), "", new(false, "无法调整亮度，请使用 Windows 显示设置。")); }
        finally { Release(set); Release(service); Release(locator); }
    }

    private static object? Property(object owner, string name)
    {
        object? properties = null, property = null;
        try { properties = ((dynamic)owner).Properties_; property = ((dynamic)properties!).Item(name); return ((dynamic)property!).Value; }
        finally { Release(property); Release(properties); }
    }
    private static void SetProperty(object owner, string name, object value)
    {
        object? properties = null, property = null;
        try { properties = ((dynamic)owner).Properties_; property = ((dynamic)properties!).Item(name); ((dynamic)property!).Value = value; }
        finally { Release(property); Release(properties); }
    }
    private static void Visit(object set, Action<object> visitor)
    {
        var iterator = ((IEnumerable)set).GetEnumerator();
        try
        {
            while (iterator.MoveNext())
            {
                var item = iterator.Current;
                try { if (item != null) visitor(item); }
                finally { Release(item); }
            }
        }
        finally { Release(iterator); }
    }
    private static bool IsWmiError(Exception error) => error is COMException or InvalidCastException or InvalidOperationException
        or InvalidComObjectException or TypeLoadException or UnauthorizedAccessException or System.Reflection.TargetInvocationException
        or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public void Dispose() { dispatcher.VerifyAccess(); disposed = true; Changed = null; }
}
