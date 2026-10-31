. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    $LumaReport = Join-Path $LumaRoot 'artifacts\tests\results.json'
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Tests\LumaDock.Tests.csproj', '-c', 'Release', '--', '--report', $LumaReport)
}
finally { Pop-Location }
