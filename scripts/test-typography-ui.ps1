. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '字体界面集成测试需要真实 Windows WPF 环境。'
}
Push-Location $LumaRoot
try {
    $LumaTypographyUiReport = Join-Path $LumaRoot 'artifacts\tests\typography-ui-results.json'
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Typography.Ui.Tests\LumaDock.Typography.Ui.Tests.csproj', '-c', 'Release', '-p:UseAppHost=false', '--', '--report', $LumaTypographyUiReport)
}
finally { Pop-Location }
