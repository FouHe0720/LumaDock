. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try { Invoke-LumaDotnet -Arguments @('run','--project','tests\LumaDock.System.Ui.Tests','-c','Release','--','--report','artifacts/tests/system-ui-results.json') }
finally { Pop-Location }
