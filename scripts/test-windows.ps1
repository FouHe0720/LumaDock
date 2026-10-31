. "$PSScriptRoot\_common.ps1"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Windows Shell 集成测试必须在真实 Windows 交互会话运行，不能用 Linux/WSL 编译结果替代。'
}
Push-Location $LumaRoot
try {
    $LumaWindowsReport = Join-Path $LumaRoot 'artifacts\tests\windows-results.json'
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Windows.Tests\LumaDock.Windows.Tests.csproj', '-c', 'Release', '--', '--report', $LumaWindowsReport)
}
finally { Pop-Location }
