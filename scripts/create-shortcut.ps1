param(
    [string]$Destination = '',
    [string]$Executable = ''
)
$ErrorActionPreference = 'Stop'
$LumaRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $LumaRoot 'artifacts\publish\LumaDock.exe' }
if (-not $Destination) { $Destination = Join-Path $LumaRoot 'LumaDock.lnk' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$Destination = [IO.Path]::GetFullPath($Destination)
if ([IO.Path]::GetExtension($Destination) -ne '.lnk') { throw '快捷方式文件名必须以 .lnk 结尾。' }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $Destination) -PathType Container)) { throw '快捷方式所在文件夹不存在。' }
$LumaMarker = 'LumaDock 应用启动器'
$LumaShell = $null
$LumaShortcut = $null
try {
    $LumaShell = New-Object -ComObject WScript.Shell
    $LumaShortcut = $LumaShell.CreateShortcut($Destination)
    if ((Test-Path -LiteralPath $Destination) -and $LumaShortcut.Description -ne $LumaMarker) {
        throw '此处已有同名快捷方式，本次未作修改。请换一个文件名。'
    }
    $LumaShortcut.TargetPath = $Executable
    $LumaShortcut.Arguments = '--show-launchpad'
    $LumaShortcut.WorkingDirectory = Split-Path -Parent $Executable
    $LumaStandaloneIcon = Join-Path (Split-Path -Parent $Executable) 'LumaDock.ico'
    $LumaShortcut.IconLocation = if (Test-Path -LiteralPath $LumaStandaloneIcon -PathType Leaf) { $LumaStandaloneIcon + ',0' } else { $Executable + ',0' }
    $LumaShortcut.Description = $LumaMarker
    $LumaShortcut.WindowStyle = 1
    $LumaShortcut.Save()
    Write-Host "已创建快捷方式：$Destination"
}
finally {
    if ($LumaShortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($LumaShortcut) }
    if ($LumaShell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($LumaShell) }
}
