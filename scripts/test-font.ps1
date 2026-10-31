. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '桌面字体集成测试需要真实 Windows；测试只读取当前字体，不修改系统设置。'
}
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests/LumaDock.Font.Tests', '-c', 'Release', '--', 'artifacts/tests/font-results.json')
}
finally { Pop-Location }
