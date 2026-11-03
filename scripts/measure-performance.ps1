param(
    [string]$Executable = '',
    [ValidateRange(15,86400)][int]$Seconds = 120,
    [ValidateRange(1,60)][int]$IntervalSeconds = 5,
    [ValidateRange(0,120)][int]$WarmupSeconds = 15,
    [switch]$ShowLaunchpad,
    [switch]$ShowDesktop,
    [string]$SeedConfig = ''
)
# Measurement is outside the app. Only the process created here is terminated.
. "$PSScriptRoot\_common.ps1"
if (-not ('LumaDock.PerformanceNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LumaDock {
    public static class PerformanceNative {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [StructLayout(LayoutKind.Sequential)] private struct CaretRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo {
            public int Size; public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public CaretRect CaretBounds;
        }
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo information);
        public static bool IsForeground(int processId) {
            uint foregroundId; GetWindowThreadProcessId(GetForegroundWindow(), out foregroundId);
            return foregroundId == (uint)processId;
        }
        private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        public static bool MainWindowVisible(int processId) {
            bool visible = false;
            EnumWindows((window, unused) => {
                uint owner; GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)processId || !IsWindowVisible(window) || (GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true;
                var title = new System.Text.StringBuilder(128); GetWindowText(window,title,title.Capacity);
                if (title.ToString() == "LumaDock") visible = true;
                return true;
            }, IntPtr.Zero);
            return visible;
        }
        public static bool HasNativeCaret(int processId) {
            uint foregroundId;
            var thread = GetWindowThreadProcessId(GetForegroundWindow(), out foregroundId);
            if (foregroundId != (uint)processId) return false;
            var information = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref information) && information.Caret != IntPtr.Zero
                && information.CaretBounds.Bottom > information.CaretBounds.Top;
        }
    }
}
'@
}
if (-not $Executable) { $Executable = Join-Path $LumaRoot 'artifacts\publish\LumaDock.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$LumaRunDir = Join-Path $LumaRoot ('artifacts\performance\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $LumaRunDir -Force | Out-Null
$LumaBinaryManifest = foreach ($LumaBinaryName in @('LumaDock.exe', 'LumaDock.dll', 'LumaDock.Windows.dll', 'LumaDock.Core.dll', 'LumaDock.Storage.dll')) {
    $LumaBinaryPath = Join-Path (Split-Path -Parent $Executable) $LumaBinaryName
    if (Test-Path -LiteralPath $LumaBinaryPath) {
        $LumaBinaryInfo = Get-Item -LiteralPath $LumaBinaryPath
        [pscustomobject]@{ name = $LumaBinaryName; sha256 = (Get-FileHash -LiteralPath $LumaBinaryPath -Algorithm SHA256).Hash; bytes = $LumaBinaryInfo.Length; modified = $LumaBinaryInfo.LastWriteTime.ToString('o') }
    }
}
$LumaBinaryManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LumaRunDir 'binary-manifest.json') -Encoding utf8
$LumaDiagnosticDir = Join-Path $LumaRunDir 'diagnostics'
$LumaDataDir = Join-Path $LumaRunDir 'data'
if ($SeedConfig) {
    $LumaSeedPath = (Resolve-Path -LiteralPath $SeedConfig).Path
    if ((Get-Item -LiteralPath $LumaSeedPath).Length -gt 16MB) { throw '测试配置超过 16 MB。' }
    New-Item -ItemType Directory -Path $LumaDataDir -Force | Out-Null
    Copy-Item -LiteralPath $LumaSeedPath -Destination (Join-Path $LumaDataDir 'config.json')
}
$LumaArguments = '--data-dir "' + $LumaDataDir + '" --diagnostics-dir "' + $LumaDiagnosticDir + '"'
if ($ShowDesktop) { $LumaArguments += ' --show-desktop' }
elseif ($ShowLaunchpad) { $LumaArguments += ' --show-launchpad' }
$LumaTimer = [Diagnostics.Stopwatch]::StartNew()
$LumaMeasuredProcess = Start-Process -FilePath $Executable -ArgumentList $LumaArguments -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
$LumaSamples = [Collections.Generic.List[object]]::new()
$LumaProcessorCount = [Environment]::ProcessorCount
$LumaPreviousCpu = 0.0
$LumaPreviousTime = 0.0
$LumaFailure = $null
try {
    Start-Sleep -Seconds $WarmupSeconds
    $LumaMeasuredProcess.Refresh()
    if ($LumaMeasuredProcess.HasExited) { throw "LumaDock 提前退出：$($LumaMeasuredProcess.ExitCode)" }
    $LumaPreviousCpu = $LumaMeasuredProcess.TotalProcessorTime.TotalMilliseconds
    $LumaPreviousTime = $LumaTimer.Elapsed.TotalMilliseconds
    $LumaMeasurementStart = $LumaTimer.Elapsed.TotalSeconds
    while ($LumaTimer.Elapsed.TotalSeconds - $LumaMeasurementStart -lt $Seconds) {
        Start-Sleep -Seconds $IntervalSeconds
        $LumaMeasuredProcess.Refresh()
        if ($LumaMeasuredProcess.HasExited) { throw "测量期间进程退出：$($LumaMeasuredProcess.ExitCode)" }
        $LumaNow = $LumaTimer.Elapsed.TotalMilliseconds
        $LumaCpu = $LumaMeasuredProcess.TotalProcessorTime.TotalMilliseconds
        $LumaPerf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($LumaMeasuredProcess.Id)" -ErrorAction SilentlyContinue | Select-Object -First 1
        $LumaPrivateWorkingSet = if ($LumaPerf) { [double]$LumaPerf.WorkingSetPrivate / 1MB } else { $null }
        $LumaSamples.Add([pscustomobject]@{
            elapsedSeconds = [math]::Round($LumaNow / 1000, 3)
            cpuPercentMachine = [math]::Round(100 * ($LumaCpu - $LumaPreviousCpu) / ($LumaNow - $LumaPreviousTime) / $LumaProcessorCount, 4)
            cpuPercentOneCore = [math]::Round(100 * ($LumaCpu - $LumaPreviousCpu) / ($LumaNow - $LumaPreviousTime), 4)
            workingSetMB = [math]::Round($LumaMeasuredProcess.WorkingSet64 / 1MB, 3)
            privateBytesMB = [math]::Round($LumaMeasuredProcess.PrivateMemorySize64 / 1MB, 3)
            privateWorkingSetMB = if ($null -ne $LumaPrivateWorkingSet) { [math]::Round($LumaPrivateWorkingSet, 3) } else { $null }
            handles = $LumaMeasuredProcess.HandleCount
            threads = $LumaMeasuredProcess.Threads.Count
            isForeground = [LumaDock.PerformanceNative]::IsForeground($LumaMeasuredProcess.Id)
            hasNativeCaret = [LumaDock.PerformanceNative]::HasNativeCaret($LumaMeasuredProcess.Id)
            mainWindowVisible = [LumaDock.PerformanceNative]::MainWindowVisible($LumaMeasuredProcess.Id)
        })
        $LumaPreviousCpu = $LumaCpu
        $LumaPreviousTime = $LumaNow
    }
}
catch { $LumaFailure = $_.Exception.Message }
finally {
    $LumaMeasuredProcess.Refresh()
    if (-not $LumaMeasuredProcess.HasExited) {
        # This is an isolated measurement instance, identified by the Process object created above.
        Stop-Process -InputObject $LumaMeasuredProcess -ErrorAction SilentlyContinue
    }
    $LumaTimer.Stop()
}
$LumaStartupPath = Join-Path $LumaDiagnosticDir 'startup.json'
$LumaStartup = if (Test-Path -LiteralPath $LumaStartupPath) { Get-Content -LiteralPath $LumaStartupPath -Raw | ConvertFrom-Json } else { $null }
$LumaFinalConfigPath = Join-Path $LumaDataDir 'config.json'
$LumaFinalConfig = if (Test-Path -LiteralPath $LumaFinalConfigPath) { Get-Content -LiteralPath $LumaFinalConfigPath -Raw | ConvertFrom-Json } else { $null }
$LumaSummary = [ordered]@{
    measuredAt = (Get-Date).ToString('o')
    executable = $Executable
    os = [Environment]::OSVersion.VersionString
    logicalProcessors = $LumaProcessorCount
    processId = $LumaMeasuredProcess.Id
    warmupSeconds = $WarmupSeconds
    requestedMeasurementSeconds = $Seconds
    actualTotalSeconds = [math]::Round($LumaTimer.Elapsed.TotalSeconds, 3)
    launchpadOpen = [bool]$ShowLaunchpad
    desktopOpen = [bool]$ShowDesktop
    seededConfiguration = [bool]$SeedConfig
    configuredPinnedApps = if ($LumaFinalConfig) { @($LumaFinalConfig.PinnedIds).Count } else { $null }
    iconReflectionEnabled = if ($LumaFinalConfig) { [bool]$LumaFinalConfig.Settings.IconReflection } else { $null }
    dockConcentration = if ($LumaFinalConfig) { $LumaFinalConfig.Settings.Opacity } else { $null }
    reflectionStrength = if ($LumaFinalConfig) { $LumaFinalConfig.Settings.ReflectionStrength } else { $null }
    dockIconVibrancy = if ($LumaFinalConfig) { $LumaFinalConfig.Settings.DockIconVibrancy } else { $null }
    sampleCount = $LumaSamples.Count
    foregroundSamples = @($LumaSamples | Where-Object isForeground).Count
    nativeCaretSamples = @($LumaSamples | Where-Object hasNativeCaret).Count
    mainWindowVisibleSamples = @($LumaSamples | Where-Object mainWindowVisible).Count
    averageCpuPercentMachine = ($LumaSamples | Measure-Object cpuPercentMachine -Average).Average
    averageCpuPercentOneCore = ($LumaSamples | Measure-Object cpuPercentOneCore -Average).Average
    peakWorkingSetMB = ($LumaSamples | Measure-Object workingSetMB -Maximum).Maximum
    peakPrivateBytesMB = ($LumaSamples | Measure-Object privateBytesMB -Maximum).Maximum
    peakPrivateWorkingSetMB = ($LumaSamples | Where-Object { $null -ne $_.privateWorkingSetMB } | Measure-Object privateWorkingSetMB -Maximum).Maximum
    workingSetGrowthMB = if ($LumaSamples.Count -gt 1) { [math]::Round($LumaSamples[-1].workingSetMB - $LumaSamples[0].workingSetMB, 3) } else { $null }
    privateBytesGrowthMB = if ($LumaSamples.Count -gt 1) { [math]::Round($LumaSamples[-1].privateBytesMB - $LumaSamples[0].privateBytesMB, 3) } else { $null }
    startupTelemetry = $LumaStartup
    binaryManifest = $LumaBinaryManifest
    failure = $LumaFailure
    notes = 'CPU machine = process CPU delta / elapsed delta / logical processors. OneCore omits processor division. Private bytes are committed private memory, NOT private working set. Private working set comes from WMI performance data and may be unavailable. Startup telemetry is in-process; process spawn time is not the same measure. Run on an otherwise idle desktop, without mouse hover over Dock. This short measurement does not establish long-run stability.'
}
$LumaSamples | Export-Csv -LiteralPath (Join-Path $LumaRunDir 'samples.csv') -NoTypeInformation -Encoding utf8
$LumaSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $LumaRunDir 'summary.json') -Encoding utf8
$LumaSummary | ConvertTo-Json -Depth 8
Write-Host "测量记录：$LumaRunDir"
if ($LumaFailure) { throw $LumaFailure }
