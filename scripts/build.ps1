param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try { Invoke-LumaDotnet -Arguments @('build', 'LumaDock.slnx', '-c', $Configuration, '--nologo') }
finally { Pop-Location }
