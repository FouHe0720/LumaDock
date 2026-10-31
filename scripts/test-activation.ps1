. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Activation.Tests', '-c', 'Release', '--', '--report', 'artifacts\tests\activation-results.json')
}
finally { Pop-Location }
