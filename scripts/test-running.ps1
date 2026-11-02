. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try { Invoke-LumaDotnet -Arguments @('run','--project','tests\LumaDock.Running.Tests','-c','Release','--','--report','artifacts/tests/running-results.json') }
finally { Pop-Location }
