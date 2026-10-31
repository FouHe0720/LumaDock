. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '图标集成测试需要 Windows WPF 与 Shell 环境。' }
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Icon.Tests\LumaDock.Icon.Tests.csproj', '-c', 'Release', '--', '--report', (Join-Path $LumaRoot 'artifacts\tests\icon-results-0.8.json'))
}
finally { Pop-Location }
