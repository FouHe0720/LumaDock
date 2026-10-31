. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Cadence.Tests\LumaDock.Cadence.Tests.csproj', '-c', 'Release', '--', '--report', (Join-Path $LumaRoot 'artifacts\tests\cadence-results.json'))
}
finally { Pop-Location }
