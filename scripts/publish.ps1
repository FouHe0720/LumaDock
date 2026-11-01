param([string]$SourceCommit = '')
. "$PSScriptRoot\_common.ps1"
Push-Location $LumaRoot
try {
    $LumaPublishPath = Join-Path $LumaRoot ('artifacts\package-' + [Guid]::NewGuid().ToString('N'))
    # A fresh output prevents unrelated files from a previous local run entering the archive.
    Invoke-LumaDotnet -Arguments @('publish', 'src\LumaDock.App\LumaDock.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $LumaPublishPath)
    Copy-Item -LiteralPath (Join-Path $LumaRoot 'README.md'),(Join-Path $LumaRoot 'LICENSE') -Destination $LumaPublishPath -Force
    Copy-Item -LiteralPath (Join-Path $LumaRoot 'docs') -Destination $LumaPublishPath -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $LumaRoot 'src\LumaDock.App\Assets\LumaDock.ico') -Destination $LumaPublishPath -Force
    $LumaLicenseDir = Join-Path $LumaPublishPath 'licenses'
    New-Item -ItemType Directory -Path $LumaLicenseDir -Force | Out-Null
    $LumaSdkRoot = Split-Path -Parent $LumaDotnet
    Copy-Item -LiteralPath (Join-Path $LumaSdkRoot 'LICENSE.txt') -Destination (Join-Path $LumaLicenseDir 'Microsoft.NET-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $LumaSdkRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $LumaLicenseDir 'ThirdPartyNotices.txt') -Force
    # Ship only sanitized reports and numeric samples, never scanned app configs or local logs.
    $LumaEvidenceDir = Join-Path $LumaPublishPath 'artifacts'
    New-Item -ItemType Directory -Path $LumaEvidenceDir -Force | Out-Null
    $LumaVersion = [string]([xml](Get-Content -LiteralPath (Join-Path $LumaRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $LumaShortVersion = ($LumaVersion -split '\.')[0..1] -join '.'
    $LumaPublicTests = Join-Path $LumaEvidenceDir 'tests'
    New-Item -ItemType Directory -Path $LumaPublicTests -Force | Out-Null
    # Explicit allowlist: local upgrade reports, configurations and captures are never shipping evidence.
    $LumaTestNames = @('results.json','lens-results.json','layout-results.json','cadence-results.json',
        "results-$LumaShortVersion.json","dock-ui-$LumaShortVersion-results.json",
        "typography-ui-$LumaShortVersion-results.json","compositor-ui-$LumaShortVersion-results.json",
        "windows-results-$LumaShortVersion.json","icon-results-$LumaShortVersion.json",
        "ui-results-$LumaShortVersion.json","backdrop-results-$LumaShortVersion.json",
        "font-results-$LumaShortVersion.json","catalog-audit-$LumaShortVersion-results.json",
        "drag-interaction-$LumaShortVersion-results.json",
        "inspection-comparison-$LumaShortVersion-results.json",
        "portable-smoke-$LumaShortVersion-results.json","shortcut-$LumaShortVersion-results.json")
    foreach ($LumaTestName in $LumaTestNames) {
        $LumaTestPath = Join-Path $LumaRoot ('artifacts\tests\' + $LumaTestName)
        if (-not (Test-Path -LiteralPath $LumaTestPath)) { continue }
        $LumaTestValue = Get-Content -LiteralPath $LumaTestPath -Raw | ConvertFrom-Json
        foreach ($LumaPrivateField in @('fixtureDirectory','executable','target','icon')) {
            if ($LumaTestValue.PSObject.Properties.Name -contains $LumaPrivateField) { $LumaTestValue.$LumaPrivateField = 'isolated-test-resource' }
        }
        $LumaTestText = $LumaTestValue | ConvertTo-Json -Depth 40
        if ($LumaTestText -match '(?i)[C-Z]:\\\\Users\\\\|[D-Z]:\\\\') {
            Write-Host "未打包含本机路径的报告：$LumaTestName"
            continue
        }
        $LumaTestText | Set-Content -LiteralPath (Join-Path $LumaPublicTests $LumaTestName) -Encoding utf8
    }
    $LumaPerformanceRoot = Join-Path $LumaRoot 'artifacts\performance'
    if (Test-Path -LiteralPath $LumaPerformanceRoot) {
        foreach ($LumaRun in (Get-ChildItem -LiteralPath $LumaPerformanceRoot -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' })) {
            $LumaSummaryPath = Join-Path $LumaRun.FullName 'summary.json'
            if (-not (Test-Path -LiteralPath $LumaSummaryPath)) { continue }
            $LumaSummaryValue = Get-Content -LiteralPath $LumaSummaryPath -Raw | ConvertFrom-Json
            $LumaMeasuredApp = $LumaSummaryValue.binaryManifest | Where-Object name -EQ 'LumaDock.dll' | Select-Object -First 1
            if ($LumaSummaryValue.failure -or -not $LumaMeasuredApp -or $LumaMeasuredApp.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $LumaPublishPath 'LumaDock.dll')).Hash) { continue }
            $LumaManifestMatches = $true
            foreach ($LumaRequiredBinary in @('LumaDock.exe','LumaDock.dll','LumaDock.Windows.dll','LumaDock.Core.dll','LumaDock.Storage.dll')) {
                $LumaMeasuredBinary = @($LumaSummaryValue.binaryManifest | Where-Object name -EQ $LumaRequiredBinary)
                $LumaPublishedBinary = Join-Path $LumaPublishPath $LumaRequiredBinary
                if ($LumaMeasuredBinary.Count -ne 1 -or -not (Test-Path -LiteralPath $LumaPublishedBinary) -or $LumaMeasuredBinary[0].sha256 -ne (Get-FileHash -LiteralPath $LumaPublishedBinary).Hash) {
                    $LumaManifestMatches = $false
                    break
                }
            }
            if (-not $LumaManifestMatches) { continue }
            $LumaEvidenceRun = Join-Path (Join-Path $LumaEvidenceDir 'performance') $LumaRun.Name
            New-Item -ItemType Directory -Path $LumaEvidenceRun -Force | Out-Null
            $LumaSummaryValue.executable = 'LumaDock.exe'
            $LumaSummaryValue | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $LumaEvidenceRun 'summary.json') -Encoding utf8
            foreach ($LumaReportName in @('samples.csv', 'binary-manifest.json')) {
                $LumaReportPath = Join-Path $LumaRun.FullName $LumaReportName
                if (Test-Path -LiteralPath $LumaReportPath) { Copy-Item -LiteralPath $LumaReportPath -Destination $LumaEvidenceRun -Force }
            }
        }
    }
    $LumaVersion = ([xml](Get-Content -LiteralPath (Join-Path $LumaRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    $LumaCommit = $SourceCommit
    if (-not $LumaCommit -and (Get-Command git -ErrorAction SilentlyContinue)) {
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
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $LumaPublishPath 'release.json') -Encoding utf8
    $LumaZip = Join-Path $LumaRoot ("artifacts\LumaDock-$LumaVersion-win-x64.zip")
    Compress-Archive -Path (Join-Path $LumaPublishPath '*') -DestinationPath $LumaZip -Force
    $LumaStablePath = Join-Path $LumaRoot 'artifacts\publish'
    New-Item -ItemType Directory -Path $LumaStablePath -Force | Out-Null
    Get-ChildItem -LiteralPath $LumaPublishPath -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $LumaStablePath -Recurse -Force }
    Get-FileHash -LiteralPath $LumaZip -Algorithm SHA256 | Format-List
    Write-Host "便携发行版：$LumaZip"
}
finally { Pop-Location }
