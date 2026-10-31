param(
    [string]$Executable = '',
    [ValidateRange(15,86400)][int]$Seconds = 120,
    [ValidateRange(1,60)][int]$IntervalSeconds = 5,
    [ValidateRange(0,120)][int]$WarmupSeconds = 15,
    [switch]$ShowLaunchpad
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
        public static bool IsForeground(int processId) {
            uint foregroundId; GetWindowThreadProcessId(GetForegroundWindow(), out foregroundId);
            return foregroundId == (uint)processId;
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
$LumaArguments = '--data-dir "' + $LumaDataDir + '" --diagnostics-dir "' + $LumaDiagnosticDir + '"'
if ($ShowLaunchpad) { $LumaArguments += ' --show-launchpad' }
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
    sampleCount = $LumaSamples.Count
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
