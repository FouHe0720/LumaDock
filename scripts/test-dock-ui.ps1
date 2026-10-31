. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Dock 界面测试需要 Windows WPF 交互会话。' }
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Dock.Ui.Tests\LumaDock.Dock.Ui.Tests.csproj', '-c', 'Release', '-p:UseAppHost=false', '--', '--report', (Join-Path $LumaRoot 'artifacts\tests\dock-ui-results.json'))
}
finally { Pop-Location }
