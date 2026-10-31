. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    Invoke-LumaDotnet -Arguments @('publish', 'src\LumaDock.App\LumaDock.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-o', 'artifacts\publish')
    Copy-Item -LiteralPath (Join-Path $LumaRoot 'README.md'),(Join-Path $LumaRoot 'LICENSE') -Destination (Join-Path $LumaRoot 'artifacts\publish') -Force
    Copy-Item -LiteralPath (Join-Path $LumaRoot 'docs') -Destination (Join-Path $LumaRoot 'artifacts\publish') -Recurse -Force
    $LumaLicenseDir = Join-Path $LumaRoot 'artifacts\publish\licenses'
    New-Item -ItemType Directory -Path $LumaLicenseDir -Force | Out-Null
    $LumaSdkRoot = Split-Path -Parent $LumaDotnet
    Copy-Item -LiteralPath (Join-Path $LumaSdkRoot 'LICENSE.txt') -Destination (Join-Path $LumaLicenseDir 'Microsoft.NET-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $LumaSdkRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $LumaLicenseDir 'ThirdPartyNotices.txt') -Force
    # Ship only sanitized reports and numeric samples, never scanned app configs or local logs.
    $LumaEvidenceDir = Join-Path $LumaRoot 'artifacts\publish\artifacts'
    New-Item -ItemType Directory -Path $LumaEvidenceDir -Force | Out-Null
    if (Test-Path -LiteralPath (Join-Path $LumaRoot 'artifacts\tests')) {
        Copy-Item -LiteralPath (Join-Path $LumaRoot 'artifacts\tests') -Destination $LumaEvidenceDir -Recurse -Force
    }
    $LumaPerformanceRoot = Join-Path $LumaRoot 'artifacts\performance'
    if (Test-Path -LiteralPath $LumaPerformanceRoot) {
        foreach ($LumaRun in (Get-ChildItem -LiteralPath $LumaPerformanceRoot -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' })) {
            $LumaEvidenceRun = Join-Path (Join-Path $LumaEvidenceDir 'performance') $LumaRun.Name
            New-Item -ItemType Directory -Path $LumaEvidenceRun -Force | Out-Null
            foreach ($LumaReportName in @('summary.json', 'samples.csv', 'binary-manifest.json')) {
                $LumaReportPath = Join-Path $LumaRun.FullName $LumaReportName
                if (Test-Path -LiteralPath $LumaReportPath) { Copy-Item -LiteralPath $LumaReportPath -Destination $LumaEvidenceRun -Force }
            }
        }
    }
    $LumaVersion = ([xml](Get-Content -LiteralPath (Join-Path $LumaRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $LumaCommit = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $LumaCommit = & git -C $LumaRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { $LumaCommit = $null }
    }
    [ordered]@{
        version = [string]$LumaVersion
        commit = $LumaCommit
        architecture = 'win-x64'
        selfContained = $true
        createdAt = (Get-Date).ToString('o')
        verification = 'See docs/validation.md and artifacts/tests; rebuilding does not rerun those checks.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LumaRoot 'artifacts\publish\release.json') -Encoding utf8
    $LumaZip = Join-Path $LumaRoot ("artifacts\LumaDock-$LumaVersion-win-x64.zip")
    Compress-Archive -Path (Join-Path $LumaRoot 'artifacts\publish\*') -DestinationPath $LumaZip -Force
    Get-FileHash -LiteralPath $LumaZip -Algorithm SHA256 | Format-List
    Write-Host "便携发行版：$LumaZip"
}
finally { Pop-Location }
