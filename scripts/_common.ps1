$ErrorActionPreference = 'Stop'
$LumaRoot = Split-Path -Parent $PSScriptRoot
$LumaDotnet = Join-Path $LumaRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $LumaDotnet)) {
    $LumaCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $LumaCommand) { throw '没有找到 .NET 10 SDK。请先安装 SDK 或将其解压到项目 .dotnet 目录。请参阅 README。' }
    $LumaDotnet = $LumaCommand.Source
}
$env:DOTNET_ROOT = Split-Path -Parent $LumaDotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
function Invoke-LumaDotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    # Explicit argument arrays preserve MSBuild -p: values across the PowerShell function binder.
    & $LumaDotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码 $LASTEXITCODE" }
}
