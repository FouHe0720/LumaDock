. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    $LumaLensReport = Join-Path $LumaRoot 'artifacts\tests\lens-results.json'
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Lens.Tests\LumaDock.Lens.Tests.csproj', '-c', 'Release', '--', '--report', $LumaLensReport)
}
finally { Pop-Location }
