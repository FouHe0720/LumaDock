. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    $LumaLayoutReport = Join-Path $LumaRoot 'artifacts\tests\layout-results.json'
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Layout.Tests\LumaDock.Layout.Tests.csproj', '-c', 'Release', '--', '--report', $LumaLayoutReport)
}
finally { Pop-Location }
