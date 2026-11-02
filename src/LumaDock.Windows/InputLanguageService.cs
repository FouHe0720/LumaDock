using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Threading;

namespace LumaDock.Windows;

public readonly record struct InputTarget(IntPtr Handle, uint ProcessId, uint ThreadId);
public sealed record InputProfileEntry(string Id, string Name, string LanguageName, string ShortText,
    uint ProfileType, ushort LanguageId, Guid ClassId, Guid ProfileId, IntPtr KeyboardLayout, bool IsActive);
public sealed record InputLanguageSnapshot(string ModeText, bool ModeAvailable, string ProfileText,
    string ProfileName, InputTarget Target, string Reason)
{
    public bool CanToggleMode => ModeAvailable;
}

/// <summary>
/// Enabled TSF profiles and the remembered foreground application's IMM compatibility state.
/// UI-thread lifetime, foreground / IME / taskbar accessibility events; no periodic polling.
/// Profile activation never enables a profile or changes the user's default language registration.
/// </summary>
public sealed class InputLanguageService : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WinEventCallback callback;
    private readonly List<IntPtr> hooks = [];
    private readonly object? profilesObject;
    private readonly IInputProcessorProfileMgr? manager;
    private readonly IInputProcessorProfiles? descriptions;
    private InputTarget remembered;
    private int queued;
    private volatile bool disposed;
    private bool indicatorQueued;
    private InputTarget nativeTarget;
    private InputProfileEntry? nativeProfile;
    private static readonly Guid KeyboardCategory = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    public InputLanguageSnapshot Current { get; private set; } = new("—", false, "键", "键盘布局", default, "请先选择输入窗口。");
    public event EventHandler? Changed;

    public InputLanguageService()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("33C53A50-F456-4884-B049-85FD643ECFED"), false);
            profilesObject = type == null ? null : Activator.CreateInstance(type);
            manager = profilesObject as IInputProcessorProfileMgr;
            descriptions = profilesObject as IInputProcessorProfiles;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or TypeLoadException) { }
        callback = OnWinEvent;
        AddHook(3, 3); // EVENT_SYSTEM_FOREGROUND
        AddHook(0x8027, 0x8029); // EVENT_OBJECT_IME_SHOW / HIDE / CHANGE
        AddHook(0x800A, 0x800C); // state, location, name; filtered to Shell's indicators below.
        Refresh();
    }

    private void AddHook(uint first, uint last)
    {
        var hook = SetWinEventHook(first, last, IntPtr.Zero, callback, 0, 0, 2);
        if (hook != IntPtr.Zero) hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint kind, IntPtr hwnd, int objectId, int childId, uint threadId, uint timestamp)
    {
        if (disposed || hwnd == IntPtr.Zero) return;
        if (kind is >= 0x800A and <= 0x800C)
        {
            if (kind == 0x800B || !IsShellIndicator(hwnd)) return;
        }
        if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (!disposed) { Refresh(); _ = RefreshNativeIndicatorAsync(); }
        }));
    }

    private static bool IsShellIndicator(IntPtr window)
    {
        var root = GetAncestor(window, 2);
        return WindowClass(root) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    public InputTarget CaptureTarget()
    {
        dispatcher.VerifyAccess();
        if (disposed) return default;
        var foreground = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(foreground, out var process);
        if (process != 0 && process != Environment.ProcessId && IsWindow(foreground)
            && WindowClass(foreground) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            && !IsShellIndicator(foreground))
            remembered = new(foreground, process, thread);
        return IsTargetValid(remembered) ? remembered : default;
    }

    public static bool IsTargetValid(InputTarget target) => target.Handle != IntPtr.Zero && target.ProcessId != 0
        && target.ThreadId != 0 && IsWindow(target.Handle)
        && GetWindowThreadProcessId(target.Handle, out var process) == target.ThreadId && process == target.ProcessId;

    private static bool IsForegroundTarget(InputTarget target) => IsTargetValid(target) && GetForegroundWindow() == target.Handle;

    public bool Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return false;
        var target = CaptureTarget();
        var layout = target.Handle != IntPtr.Zero ? GetKeyboardLayout(target.ThreadId) : IntPtr.Zero;
        var lang = unchecked((ushort)layout.ToInt64());
        var profile = ReadActiveProfile(layout, lang) ?? (nativeTarget == target ? nativeProfile : null);
        var mode = ReadMode(target, lang);
        var latest = new InputLanguageSnapshot(mode.Text, mode.Available,
            profile?.ShortText ?? (layout == IntPtr.Zero ? "键" : ShortLanguage(lang)),
            profile?.Name ?? LanguageName(lang), target, mode.Reason);
        if (latest == Current) return false;
        Current = latest; Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    /// <summary>
    /// The Windows input indicator can expose a TSF profile without an HKL substitute.
    /// Query only on input events or user interaction, never on a repeating timer.
    /// A single shared bounded read prevents a slow accessibility provider creating workers.
    /// </summary>
    public async Task RefreshNativeIndicatorAsync()
    {
        dispatcher.VerifyAccess();
        if (disposed || indicatorQueued) return;
        var target = CaptureTarget();
        // CaptureTarget intentionally remembers the last external app while our
        // own panels are active. The native taskbar describes the actual foreground
        // thread, so its state must never be assigned to that remembered app.
        if (!IsForegroundTarget(target)) return;
        indicatorQueued = true;
        var entries = EnumerateProfiles();
        try
        {
            var native = await NativeInputIndicator.ReadAsync(entries);
            if (disposed || !IsForegroundTarget(target) || CaptureTarget() != target) return;
            nativeTarget = target; nativeProfile = native.Profile;
            var current = Current;
            var latest = current with
            {
                ModeText = native.ModeText ?? current.ModeText,
                ModeAvailable = native.ModeText != null || current.ModeAvailable,
                ProfileName = native.Profile?.Name ?? current.ProfileName,
                ProfileText = native.Profile?.ShortText ?? current.ProfileText,
                Reason = native.ModeText is { } text ? (text == "中" ? "中文输入 · 点击切换" : "英文输入 · 点击切换") : current.Reason
            };
            if (latest != current) { Current = latest; Changed?.Invoke(this, EventArgs.Empty); }
        }
        finally { indicatorQueued = false; }
    }

    /// <summary>Enumerates enabled profiles only; disabled installations are not offered as working choices.</summary>
    public IReadOnlyList<InputProfileEntry> EnumerateProfiles()
    {
        dispatcher.VerifyAccess();
        if (disposed || manager == null) return [];
        IEnumInputProcessorProfiles? enumeration = null;
        try
        {
            if (manager.EnumProfiles(0, out enumeration) != 0 || enumeration == null) return [];
            var result = new List<InputProfileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 512; index++)
            {
                var hr = enumeration.Next(1, out var native, out var fetched);
                if (fetched != 1 || hr < 0) break;
                if ((native.Flags & 2) == 0 || (native.ProfileType == 1 && native.Category != KeyboardCategory)) continue;
                // A substituted HKL is the same TSF input processor, not another user-selectable item.
                if (native.ProfileType == 2 && (native.Flags & 4) != 0) continue;
                var entry = Describe(native);
                // The native ACTIVE flag describes this process, not an arbitrary
                // foreground app. Only the native input-indicator confirmation can
                // highlight a profile shared by multiple modern TSF HKLs.
                entry = entry with { IsActive = nativeTarget == remembered && nativeProfile?.Id == entry.Id };
                if (seen.Add(entry.Id)) result.Add(entry);
                if (hr != 0) break;
            }
            return result;
        }
        catch (Exception error) when (error is COMException or InvalidComObjectException) { return []; }
        finally { if (enumeration != null && Marshal.IsComObject(enumeration)) Marshal.ReleaseComObject(enumeration); }
    }

    private InputProfileEntry? ReadActiveProfile(IntPtr targetLayout, ushort targetLanguage)
    {
        if (manager == null || targetLayout == IntPtr.Zero) return null;
        try
        {
            var category = KeyboardCategory;
            if (manager.GetActiveProfile(ref category, out var native) != 0) return null;
            // TSF's active-profile query is process scoped. Never label a different
            // foreground HKL as this process's input processor. An unknown profile is generic.
            var handle = native.ProfileType == 2 ? native.KeyboardLayout : native.SubstituteLayout;
            if (handle == IntPtr.Zero || handle != targetLayout || native.Language != targetLanguage) return null;
            if (EnumerateProfiles().Count(item => item.KeyboardLayout == handle) != 1) return null;
            return Describe(native);
        }
        catch (Exception error) when (error is COMException or InvalidComObjectException) { return null; }
    }

    private InputProfileEntry Describe(NativeProfile native)
    {
        var id = native.ProfileType == 1 ? $"{native.ClassId:B}/{native.Language:X4}/{native.ProfileId:B}"
            : $"hkl:{native.KeyboardLayout.ToInt64():X16}";
        var name = "";
        if (native.ProfileType == 1 && descriptions != null)
        {
            var clsid = native.ClassId; var profile = native.ProfileId;
            if (descriptions.GetLanguageProfileDescription(ref clsid, native.Language, ref profile, out var description) == 0)
                name = description?.Trim() ?? "";
        }
        var language = LanguageName(native.Language);
        if (name.Length == 0) name = language + (native.ProfileType == 1 ? " · 输入法" : " · 键盘");
        var shortText = name.Contains("拼音", StringComparison.Ordinal) || name.Contains("Pinyin", StringComparison.OrdinalIgnoreCase)
            ? "拼" : native.ProfileType == 1 && (native.Language & 0x3ff) == 4 ? name[..1] : ShortLanguage(native.Language);
        return new(id, name, language, shortText, native.ProfileType, native.Language, native.ClassId,
            native.ProfileId, native.ProfileType == 2 ? native.KeyboardLayout : native.SubstituteLayout, (native.Flags & 1) != 0);
    }

    private static string LanguageName(ushort id)
    {
        if (id == 0) return "键盘布局";
        try { return CultureInfo.GetCultureInfo(id).DisplayName; }
        catch (CultureNotFoundException) { return "键盘布局 " + id.ToString("X4", CultureInfo.InvariantCulture); }
    }

    private static string ShortLanguage(ushort id)
    {
        try
        {
            var tag = CultureInfo.GetCultureInfo(id).TwoLetterISOLanguageName;
            return tag == "zh" ? "键" : tag.ToUpperInvariant();
        }
        catch (CultureNotFoundException) { return "键"; }
    }

    private static IntPtr TargetImeWindow(InputTarget target)
    {
        if (!IsTargetValid(target)) return IntPtr.Zero;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var focus = GetGUIThreadInfo(target.ThreadId, ref info) ? info.Focus : IntPtr.Zero;
        if (focus != IntPtr.Zero && GetWindowThreadProcessId(focus, out var pid) == target.ThreadId && pid == target.ProcessId)
            return ImmGetDefaultIMEWnd(focus);
        return ImmGetDefaultIMEWnd(target.Handle);
    }

    private readonly record struct ModeRead(string Text, bool Available, string Reason);
    private static ModeRead ReadMode(InputTarget target, ushort language)
    {
        if (!IsTargetValid(target)) return new("—", false, "请先选择需要输入的窗口。");
        if ((language & 0x3ff) != 4) return new(ShortLanguage(language), false, "此键盘没有中／英输入模式。");
        var ime = TargetImeWindow(target);
        if (ime == IntPtr.Zero || !TryImeControl(ime, 5, IntPtr.Zero, out var open))
            return new("—", false, "此应用未公开输入模式，请使用输入法自身的切换方式。");
        if (open == IntPtr.Zero) return new("英", true, "英文输入 · 点击切换");
        if (!TryImeControl(ime, 1, IntPtr.Zero, out var conversion))
            return new("—", false, "无法读取此输入法的转换模式。");
        return (conversion.ToInt64() & 1) != 0 ? new("中", true, "中文输入 · 点击切换") : new("英", true, "英文输入 · 点击切换");
    }

    private static bool TryImeControl(IntPtr window, uint command, IntPtr value, out IntPtr result)
        => SendMessageTimeout(window, 0x0283, new IntPtr(command), value, 2 | 0x20, 80, out result) != IntPtr.Zero;

    /// <summary>Restores the captured app and verifies the result before reporting success.</summary>
    public SystemActionResult ToggleMode(InputTarget target)
    {
        dispatcher.VerifyAccess();
        if (disposed || !RestoreTarget(target)) return new(false, "请先回到需要输入的窗口，再切换中／英。");
        var language = unchecked((ushort)GetKeyboardLayout(target.ThreadId).ToInt64());
        var before = ReadMode(target, language);
        if (!before.Available) return new(false, before.Reason);
        var ime = TargetImeWindow(target);
        var native = before.Text != "中";
        // Preserve unrelated conversion bits (full-width, symbol, etc.). Modern TSF
        // implementations may reject the IMM bridge; that is an explicit unsupported result.
        if (native)
        {
            if (!TryImeControl(ime, 6, new IntPtr(1), out _)) return new(false, "输入法没有响应。");
            if (TryImeControl(ime, 1, IntPtr.Zero, out var conversion))
                TryImeControl(ime, 2, new IntPtr(conversion.ToInt64() | 1), out _);
        }
        else
        {
            if (!TryImeControl(ime, 6, IntPtr.Zero, out _)) return new(false, "输入法没有响应。");
        }
        remembered = target; Refresh();
        var after = ReadMode(target, language);
        return after.Available && after.Text != before.Text
            ? new(true, null) : new(false, "此输入法没有接受中／英切换，请使用输入法自身的切换方式。");
    }

    public async Task<SystemActionResult> ToggleModeAsync(InputTarget target)
    {
        dispatcher.VerifyAccess();
        if (disposed || !RestoreTarget(target)) return new(false, "请先回到需要输入的窗口，再切换中／英。");
        var before = ReadMode(target, unchecked((ushort)GetKeyboardLayout(target.ThreadId).ToInt64()));
        // A submitted IMM write with an ambiguous acknowledgement must not be
        // followed by another click: the second operation could undo a real change.
        if (before.Available)
        {
            var direct = ToggleMode(target);
            if (!disposed) await RefreshNativeIndicatorAsync();
            return direct;
        }
        var result = await NativeInputIndicator.ToggleModeAsync(target);
        if (!disposed) { Refresh(); await RefreshNativeIndicatorAsync(); }
        return result;
    }

    public SystemActionResult ActivateProfile(InputProfileEntry profile, InputTarget target)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(profile);
        if (disposed || manager == null) return new(false, "Windows 键盘接口不可用。");
        // Do not trust a stale menu entry: disabled or uninstalled profiles are not enabled implicitly.
        if (!EnumerateProfiles().Any(item => item.Id == profile.Id)) return new(false, "这个键盘布局已经不可用，请重新打开列表。");
        if (!RestoreTarget(target)) return new(false, "原来的输入窗口已关闭或无法激活。");
        try
        {
            var clsid = profile.ClassId; var guid = profile.ProfileId;
            var hr = manager.ActivateProfile(profile.ProfileType, profile.LanguageId, ref clsid, ref guid,
                profile.ProfileType == 2 ? profile.KeyboardLayout : IntPtr.Zero, 0x20000000);
            if (hr != 0) return new(false, "此布局暂时无法切换，可使用 Windows 键盘面板。");
            remembered = target; Refresh();
            // The TSF call succeeded, but don't claim a new per-app profile until the
            // OS confirms a compatible target HKL and an active profile record.
            var category = KeyboardCategory;
            var confirmed = manager.GetActiveProfile(ref category, out var active) == 0
                && active.ProfileType == profile.ProfileType && active.Language == profile.LanguageId
                && (profile.ProfileType == 2 ? active.KeyboardLayout == profile.KeyboardLayout
                    : active.ClassId == profile.ClassId && active.ProfileId == profile.ProfileId);
            var actual = GetKeyboardLayout(target.ThreadId);
            // Modern TSF profiles often have no substitute HKL. FORSESSION is the
            // documented all-threads-on-this-desktop activation; don't fabricate an
            // HKL for these profiles or incorrectly report every successful request as failure.
            var targetConfirmed = profile.KeyboardLayout == IntPtr.Zero
                ? unchecked((ushort)actual.ToInt64()) == profile.LanguageId : actual == profile.KeyboardLayout;
            _ = RefreshNativeIndicatorAsync();
            return confirmed && targetConfirmed ? new(true, null)
                : new(false, "Windows 已收到布局请求；此应用尚未确认切换，可在 Windows 键盘面板检查。");
        }
        catch (Exception error) when (error is COMException or InvalidComObjectException)
        { return new(false, "Windows 未接受这个键盘布局。"); }
    }

    private static bool RestoreTarget(InputTarget target)
    {
        if (!IsTargetValid(target)) return false;
        if (GetForegroundWindow() != target.Handle) SetForegroundWindow(target.Handle);
        return GetForegroundWindow() == target.Handle && IsTargetValid(target);
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        foreach (var hook in hooks) UnhookWinEvent(hook);
        hooks.Clear(); Changed = null;
        if (profilesObject != null && Marshal.IsComObject(profilesObject)) Marshal.ReleaseComObject(profilesObject);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeProfile
    {
        public uint ProfileType; public ushort Language; public Guid ClassId, ProfileId, Category;
        public IntPtr SubstituteLayout; public uint Capabilities; public IntPtr KeyboardLayout; public uint Flags;
    }
    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputProcessorProfileMgr
    {
        [PreserveSig] int ActivateProfile(uint type, ushort lang, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);
        [PreserveSig] int DeactivateProfile(uint type, ushort lang, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);
        [PreserveSig] int GetProfile(uint type, ushort lang, ref Guid clsid, ref Guid profile, IntPtr hkl, out NativeProfile result);
        [PreserveSig] int EnumProfiles(ushort lang, out IEnumInputProcessorProfiles result);
        [PreserveSig] int ReleaseInputProcessor(ref Guid clsid, uint flags);
        [PreserveSig] int RegisterProfile(ref Guid clsid, ushort lang, ref Guid profile, IntPtr desc, uint descLength, IntPtr file, uint fileLength, uint icon, IntPtr substitute, uint layout, int enabled, uint flags);
        [PreserveSig] int UnregisterProfile(ref Guid clsid, ushort lang, ref Guid profile, uint flags);
        [PreserveSig] int GetActiveProfile(ref Guid category, out NativeProfile profile);
    }
    [ComImport, Guid("71c6e74d-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumInputProcessorProfiles
    {
        [PreserveSig] int Clone(out IEnumInputProcessorProfiles result);
        [PreserveSig] int Next(uint count, out NativeProfile profile, out uint fetched);
        [PreserveSig] int Reset();
        [PreserveSig] int Skip(uint count);
    }
    [ComImport, Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputProcessorProfiles
    {
        [PreserveSig] int Register(ref Guid clsid);
        [PreserveSig] int Unregister(ref Guid clsid);
        [PreserveSig] int AddLanguageProfile(ref Guid clsid, ushort lang, ref Guid profile, IntPtr desc, uint descLength, IntPtr file, uint fileLength, uint icon);
        [PreserveSig] int RemoveLanguageProfile(ref Guid clsid, ushort lang, ref Guid profile);
        [PreserveSig] int EnumInputProcessorInfo(out IntPtr result);
        [PreserveSig] int GetDefaultLanguageProfile(ushort lang, ref Guid category, out Guid clsid, out Guid profile);
        [PreserveSig] int SetDefaultLanguageProfile(ushort lang, ref Guid clsid, ref Guid profile);
        [PreserveSig] int ActivateLanguageProfile(ref Guid clsid, ushort lang, ref Guid profile);
        [PreserveSig] int GetActiveLanguageProfile(ref Guid clsid, out ushort lang, out Guid profile);
        [PreserveSig] int GetLanguageProfileDescription(ref Guid clsid, ushort lang, ref Guid profile, [MarshalAs(UnmanagedType.BStr)] out string description);
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    private delegate void WinEventCallback(IntPtr hook, uint kind, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr command, IntPtr data, uint flags, uint timeout, out IntPtr result);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);

    private sealed record NativeIndicatorRead(string? ModeText = null, InputProfileEntry? Profile = null);
    private static class NativeInputIndicator
    {
        private static readonly object Gate = new();
        private static Task<NativeIndicatorRead>? pendingRead;
        private static Task<SystemActionResult>? pendingToggle;

        public static async Task<NativeIndicatorRead> ReadAsync(IReadOnlyList<InputProfileEntry> profiles)
        {
            Task<NativeIndicatorRead> operation;
            lock (Gate)
            {
                // Another instance can remember a different foreground app. Do not
                // share its in-flight snapshot across those independent targets.
                if (pendingRead is { IsCompleted: false }) return new();
                var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.6);
                operation = pendingRead = Task.Run(() => Read(profiles, deadline));
            }
            try { return await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { return new(); }
        }

        private static NativeIndicatorRead Read(IReadOnlyList<InputProfileEntry> profiles, long deadline)
        {
            try
            {
                string? mode = null; InputProfileEntry? profile = null;
                foreach (var button in IndicatorButtons(deadline))
                {
                    if (Expired(deadline)) break;
                    var name = button.Current.Name;
                    mode ??= ModeName(name);
                    var matches = profiles.Where(item => name.Contains(item.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1) profile = matches[0];
                }
                return new(mode, profile);
            }
            catch (Exception error) when (AutomationError(error)) { return new(); }
        }

        public static async Task<SystemActionResult> ToggleModeAsync(InputTarget target)
        {
            Task<SystemActionResult> operation;
            lock (Gate)
            {
                if (pendingToggle is { IsCompleted: false }) return new(false, "输入模式正在切换，请稍候。");
                var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.7);
                operation = pendingToggle = Task.Run(() => Toggle(target, deadline));
            }
            try { return await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { return new(false, "Windows 输入指示器没有响应，请使用输入法自身的切换方式。"); }
        }

        private static SystemActionResult Toggle(InputTarget target, long deadline)
        {
            try
            {
                var candidates = new Dictionary<string, (AutomationElement Command, AutomationElement Label, string Mode)>();
                foreach (var label in IndicatorButtons(deadline))
                {
                    var text = ModeName(label.Current.Name);
                    if (text == null) continue;
                    AutomationElement? command = label;
                    for (var depth = 0; depth < 3 && command != null && !Expired(deadline); depth++)
                    {
                        if (command.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                        {
                            candidates.TryAdd(string.Join(".", command.GetRuntimeId()), (command, label, text));
                            break;
                        }
                        command = TreeWalker.ControlViewWalker.GetParent(command);
                    }
                }
                var buttons = candidates.Values.ToArray();
                if (buttons.Length != 1 || Expired(deadline) || !IsTargetValid(target) || GetForegroundWindow() != target.Handle)
                    return new(false, "此应用未公开中／英切换，请使用输入法自身的切换方式。");
                var button = buttons[0]; var before = button.Mode;
                if (!button.Command.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    return new(false, "Windows 输入指示器不支持点击切换。");
                // No delayed action is permitted after the foreground app or deadline changes.
                if (Expired(deadline) || GetForegroundWindow() != target.Handle || !IsTargetValid(target))
                    return new(false, "输入窗口已经改变，请重试。");
                ((InvokePattern)pattern).Invoke();
                var verifyUntil = Math.Min(deadline, Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * .6));
                while (!Expired(verifyUntil))
                {
                    var after = ModeName(button.Label.Current.Name);
                    if (after != null && after != before) return new(true);
                    Thread.Sleep(35);
                }
                return new(false, "输入法尚未确认中／英切换，请使用输入法自身的切换方式。");
            }
            catch (Exception error) when (AutomationError(error))
            { return new(false, "Windows 输入指示器暂时不可用。"); }
        }

        private static string? ModeName(string name)
        {
            var text = name.Trim();
            if (text is "中" || text.Contains("中文模式", StringComparison.Ordinal) || text.Contains("Chinese mode", StringComparison.OrdinalIgnoreCase)) return "中";
            if (text is "英" || text.Contains("英文模式", StringComparison.Ordinal) || text.Contains("English mode", StringComparison.OrdinalIgnoreCase)) return "英";
            return null;
        }

        private static IEnumerable<AutomationElement> IndicatorButtons(long deadline)
        {
            var handle = FindWindow("Shell_TrayWnd", null);
            if (handle == IntPtr.Zero || Expired(deadline)) yield break;
            var root = AutomationElement.FromHandle(handle);
            var buttons = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
            foreach (AutomationElement button in buttons)
            {
                if (Expired(deadline)) yield break;
                if (!button.Current.IsEnabled) continue;
                // Restrict to native input-indicator descendants. App buttons whose
                // titles happen to contain an input method name must never be invoked.
                AutomationElement? ancestor = button;
                for (var depth = 0; depth < 6 && ancestor != null; depth++)
                {
                    if (Expired(deadline)) yield break;
                    var id = ancestor.Current.AutomationId;
                    var native = ancestor.Current.NativeWindowHandle;
                    if (id.Contains("SystemTray", StringComparison.OrdinalIgnoreCase)
                        || native != 0 && WindowClass(new IntPtr(native)) == "TrayNotifyWnd"
                        || id.Contains("InputIndicator", StringComparison.OrdinalIgnoreCase)
                        || id.Contains("LanguageButton", StringComparison.OrdinalIgnoreCase)
                        || id.Contains("InputMethod", StringComparison.OrdinalIgnoreCase))
                    { yield return button; break; }
                    ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor);
                }
            }
        }

        private static bool Expired(long deadline) => Stopwatch.GetTimestamp() >= deadline;
        private static bool AutomationError(Exception error) => error is ElementNotAvailableException or ElementNotEnabledException
            or InvalidOperationException or COMException or UnauthorizedAccessException;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    }
}
