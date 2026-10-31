. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests/LumaDock.Backdrop.Tests', '-c', 'Release', '--', 'artifacts/tests/backdrop-results.json')
}
finally { Pop-Location }
