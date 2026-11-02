using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LumaDock.Windows;

public sealed record AudioSnapshot(bool Available, double Volume, bool Muted, string DeviceName);

/// <summary>Default render endpoint, controlled only by explicit user actions. UI-thread COM lifetime, event callbacks only queue work.</summary>
public sealed class AudioEndpointService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly EndpointCallback volumeCallback;
    private readonly DeviceCallback deviceCallback;
    private readonly Guid eventContext = Guid.NewGuid();
    private IMMDeviceEnumerator? enumerator;
    private IMMDevice? device;
    private IAudioEndpointVolume? endpoint;
    private string endpointId = "", deviceName = "";
    private int queued, rebindRequested;
    private bool notificationsRegistered, volumeRegistered;
    private volatile bool disposed;
    public AudioSnapshot Current { get; private set; } = new(false, 0, false, "无可用输出设备");
    public event EventHandler? Changed;

    public AudioEndpointService()
    {
        volumeCallback = new(this); deviceCallback = new(this);
        try
        {
            enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
            notificationsRegistered = enumerator.RegisterEndpointNotificationCallback(deviceCallback) >= 0;
            Refresh();
        }
        catch (COMException) { ReleaseAll(); }
    }

    public bool Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return false;
        IMMDevice? latest = null;
        try
        {
            // A temporarily unavailable audio service can recover on the next user refresh.
            if (enumerator == null)
            {
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                notificationsRegistered = enumerator.RegisterEndpointNotificationCallback(deviceCallback) >= 0;
            }
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out latest) < 0 || latest == null)
                return Unavailable();
            Marshal.ThrowExceptionForHR(latest.GetId(out var id));
            if (Interlocked.Exchange(ref rebindRequested, 0) != 0 || endpoint == null || id != endpointId)
            {
                ReleaseEndpoint();
                device = latest; latest = null; endpointId = id;
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var activated));
                endpoint = (IAudioEndpointVolume)activated;
                volumeRegistered = endpoint.RegisterControlChangeNotify(volumeCallback) >= 0;
                deviceName = ReadDeviceName(device);
            }
            Marshal.ThrowExceptionForHR(endpoint!.GetMasterVolumeLevelScalar(out var volume));
            Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
            if (!float.IsFinite(volume)) return Unavailable();
            return Publish(new(true, Math.Clamp(volume, 0, 1), muted, deviceName));
        }
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidComObjectException)
        { return Unavailable(); }
        finally { ReleaseCom(latest); }
    }

    public SystemActionResult SetVolume(double volume)
    {
        dispatcher.VerifyAccess();
        if (disposed || !double.IsFinite(volume)) return new(false, "音量值不可用。");
        Refresh();
        if (endpoint == null || !Current.Available) return new(false, "没有可用的音频输出设备。");
        try
        {
            var context = eventContext;
            Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(volume, 0, 1), ref context));
            Refresh(); return new(true);
        }
        catch (COMException) { Refresh(); return new(false, "无法调整系统音量，请打开声音设置。"); }
    }

    public SystemActionResult SetMuted(bool muted)
    {
        dispatcher.VerifyAccess();
        if (disposed) return new(false, "音频服务已关闭。");
        Refresh();
        if (endpoint == null || !Current.Available) return new(false, "没有可用的音频输出设备。");
        try
        {
            var context = eventContext;
            Marshal.ThrowExceptionForHR(endpoint.SetMute(muted, ref context));
            Refresh(); return new(true);
        }
        catch (COMException) { Refresh(); return new(false, "无法更改静音状态，请打开声音设置。"); }
    }

    private bool Unavailable()
    {
        ReleaseEndpoint(); return Publish(new(false, 0, false, "无可用输出设备"));
    }
    private bool Publish(AudioSnapshot value)
    {
        if (value == Current) return false;
        Current = value; Changed?.Invoke(this, EventArgs.Empty); return true;
    }
    private void QueueRefresh(bool rebind = false)
    {
        if (rebind) Interlocked.Exchange(ref rebindRequested, 1);
        if (disposed || dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (!disposed) Refresh();
        }));
    }

    private static string ReadDeviceName(IMMDevice device)
    {
        IPropertyStore? store = null; var value = new PropVariant();
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store == null) return "默认输出设备";
            var key = new PropertyKey { FormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
            if (store.GetValue(ref key, out value) >= 0 && value.Type == 31 && value.Pointer != IntPtr.Zero)
                return Marshal.PtrToStringUni(value.Pointer) ?? "默认输出设备";
        }
        catch (COMException) { }
        finally { PropVariantClear(ref value); ReleaseCom(store); }
        return "默认输出设备";
    }

    private void ReleaseEndpoint()
    {
        if (endpoint != null && volumeRegistered)
        {
            try { endpoint.UnregisterControlChangeNotify(volumeCallback); } catch (COMException) { }
        }
        volumeRegistered = false; ReleaseCom(endpoint); endpoint = null;
        ReleaseCom(device); device = null; endpointId = ""; deviceName = "";
    }
    private void ReleaseAll()
    {
        ReleaseEndpoint();
        if (enumerator != null && notificationsRegistered)
        {
            try { enumerator.UnregisterEndpointNotificationCallback(deviceCallback); } catch (COMException) { }
        }
        notificationsRegistered = false; ReleaseCom(enumerator); enumerator = null;
    }
    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true; Changed = null; ReleaseAll();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointCallback(AudioEndpointService owner) : IAudioEndpointVolumeCallback
    {
        private readonly WeakReference<AudioEndpointService> service = new(owner);
        public int OnNotify(IntPtr notification) { if (service.TryGetTarget(out var target)) target.QueueRefresh(); return 0; }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceCallback(AudioEndpointService owner) : IMMNotificationClient
    {
        private readonly WeakReference<AudioEndpointService> service = new(owner);
        private int Changed() { if (service.TryGetTarget(out var target)) target.QueueRefresh(true); return 0; }
        public int OnDeviceStateChanged(string id, uint state) => Changed();
        public int OnDeviceAdded(string id) => Changed();
        public int OnDeviceRemoved(string id) => Changed();
        public int OnDefaultDeviceChanged(int flow, int role, string? id) => flow == 0 && role is 0 or 1 ? Changed() : 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => Changed();
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint mode, out IPropertyStore? store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }
    [ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr notification); }
    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid FormatId; public uint Id; }
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
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
