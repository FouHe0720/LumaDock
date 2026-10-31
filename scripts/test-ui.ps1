. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'WPF UI 生命周期测试需要真实 Windows 交互会话。'
}
Push-Location $LumaRoot
try {
    # This test briefly creates real windows, without tray/hotkeys, app launching or system settings changes.
    # Run separately from performance sampling so rendering work does not contaminate idle CPU measurements.
    $LumaUiReport = Join-Path $LumaRoot 'artifacts\tests\ui-results.json'
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Ui.Tests\LumaDock.Ui.Tests.csproj', '-c', 'Release', '-p:UseAppHost=false', '--', '--report', $LumaUiReport)
}
finally { Pop-Location }
