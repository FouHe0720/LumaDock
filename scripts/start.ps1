param([switch]$Launchpad)
. "$PSScriptRoot\_common.ps1"
$LumaExe = Join-Path $LumaRoot 'artifacts\publish\LumaDock.exe'
if (-not (Test-Path -LiteralPath $LumaExe)) {
    & "$PSScriptRoot\build.ps1"
    $LumaExe = Join-Path $LumaRoot 'src\LumaDock.App\bin\Release\net10.0-windows\LumaDock.exe'
}
$LumaStart = @{ FilePath = $LumaExe; WorkingDirectory = (Split-Path -Parent $LumaExe); WindowStyle = 'Hidden'; PassThru = $true }
if ($Launchpad) { $LumaStart.ArgumentList = '--show-launchpad' }
$LumaProcess = Start-Process @LumaStart
Write-Host "LumaDock 已启动，进程 ID $($LumaProcess.Id)。通过 Dock 或系统托盘打开启动台/设置。"
