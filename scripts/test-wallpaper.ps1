. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '壁纸读取测试必须在 Windows 执行，只读系统设置与壁纸文件。' }
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests/LumaDock.Wallpaper.Tests', '-c', 'Release', '--', 'artifacts/tests/wallpaper-results.json')
}
finally { Pop-Location }
