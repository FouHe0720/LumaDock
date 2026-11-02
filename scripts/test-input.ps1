. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try { Invoke-LumaDotnet -Arguments @('run','--project','tests\LumaDock.Input.Tests','-c','Release','--','--report','artifacts/tests/input-results.json') }
finally { Pop-Location }
