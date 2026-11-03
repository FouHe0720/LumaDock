. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('run', '--project', 'tests\LumaDock.Desktop.Tests\LumaDock.Desktop.Tests.csproj', '-c', 'Release', '--', (Join-Path $LumaRoot 'artifacts\tests\desktop-files-results.json'))
}
finally { Pop-Location }
