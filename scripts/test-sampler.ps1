. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests/LumaDock.Sampler.Tests', '-c', 'Release', '--', 'artifacts/tests/sampler-results.json')
}
finally { Pop-Location }
