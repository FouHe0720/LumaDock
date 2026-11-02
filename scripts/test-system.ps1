. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try { Invoke-LumaDotnet -Arguments @('run','--project','tests\LumaDock.System.Tests','-c','Release','--','--report','artifacts/tests/system-results.json') }
finally { Pop-Location }
