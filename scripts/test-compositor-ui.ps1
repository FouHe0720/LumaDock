. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '合成背景测试需要 Windows WPF 交互会话。' }
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Compositor.Ui.Tests\LumaDock.Compositor.Ui.Tests.csproj', '-c', 'Release', '-p:UseAppHost=false', '--', '--report', (Join-Path $LumaRoot 'artifacts\tests\compositor-ui-results.json'))
}
finally { Pop-Location }
