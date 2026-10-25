$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactVerifier = Join-Path $repositoryRoot 'scripts/verify-plugin-artifacts.ps1'
$benchmarkVerifier = Join-Path $repositoryRoot 'scripts/verify-benchmark-results.ps1'
$fixtures = Join-Path $PSScriptRoot 'fixtures'

function Invoke-VerificationScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]]$ArgumentList
    )

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Expected verification script does not exist: $ScriptPath"
    }

    $powershell = (Get-Command powershell -CommandType Application).Source
    $process = Start-Process -FilePath $powershell -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $ArgumentList) -NoNewWindow -Wait -PassThru
    return $process.ExitCode
}

function Assert-ExitCode {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Expected,

        [Parameter(Mandatory = $true)]
        [int]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Case
    )

    if ($Actual -ne $Expected) {
        throw "$Case expected exit code $Expected but received $Actual."
    }
}

$acceptedPackage = Join-Path $fixtures 'accepted-package'
$rejectedPackage = Join-Path $fixtures 'rejected-package'
$validDeps = Join-Path $fixtures 'valid-jellyfin.deps.json'
$invalidDeps = Join-Path $fixtures 'invalid-jellyfin.deps.json'

Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $acceptedPackage)) 'accepted package directory'
Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $rejectedPackage)) 'bundled managed SQLite dependency'
Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $acceptedPackage, '-JellyfinDepsPath', $validDeps)) 'supported Jellyfin dependency graph'
Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $acceptedPackage, '-JellyfinDepsPath', $invalidDeps)) 'mismatched Jellyfin dependency graph'

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ass-subsetter-artifact-tests-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $oldMajorDeps = Join-Path $temporaryRoot 'old-major.deps.json'
    (Get-Content -Raw -LiteralPath $validDeps).Replace('/10.0.12', '/9.0.11') | Set-Content -LiteralPath $oldMajorDeps
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $acceptedPackage, '-JellyfinDepsPath', $oldMajorDeps)) 'complete old database major version'

    $olderPatchDeps = Join-Path $temporaryRoot 'older-patch.deps.json'
    (Get-Content -Raw -LiteralPath $validDeps).Replace('/10.0.12', '/10.0.11') | Set-Content -LiteralPath $olderPatchDeps
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $acceptedPackage, '-JellyfinDepsPath', $olderPatchDeps)) 'older host assembly patch version'

    $missingRuntimeDeps = Join-Path $temporaryRoot 'missing-runtime.deps.json'
    $missingRuntime = Get-Content -Raw -LiteralPath $validDeps | ConvertFrom-Json
    $missingRuntime.libraries.PSObject.Properties.Remove('Microsoft.Data.Sqlite.Core/10.0.12')
    $missingRuntime | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $missingRuntimeDeps
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $acceptedPackage, '-JellyfinDepsPath', $missingRuntimeDeps)) 'missing SQLite runtime package'

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $temporaryRoot 'accepted.zip'
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('plugin/Jellyfin.Plugin.AssSubsetter.dll')
        $stream = $entry.Open()
        try {
            $stream.WriteByte(0)
        }
        finally {
            $stream.Dispose()
        }

        $archive.CreateEntry('../../must-not-be-extracted.txt') | Out-Null
    }
    finally {
        $archive.Dispose()
    }

    Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $zipPath)) 'ZIP package without extraction'
    if (Test-Path -LiteralPath (Join-Path $temporaryRoot 'must-not-be-extracted.txt')) {
        throw 'ZIP verification extracted an archive entry.'
    }
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

$nestedZipRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ass-subsetter-nested-artifact-tests-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($nestedZipRoot) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $nestedZip = Join-Path $nestedZipRoot 'nested-forbidden.zip'
    $archive = [System.IO.Compression.ZipFile]::Open($nestedZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $archive.CreateEntry('plugin/Jellyfin.Plugin.AssSubsetter.dll') | Out-Null
        $archive.CreateEntry('nested/SQLitePCLRAW.BUNDLE_E_SQLITE3.dll') | Out-Null
    }
    finally { $archive.Dispose() }
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $nestedZip)) 'nested case-insensitive forbidden artifact'


    $efZip = Join-Path $nestedZipRoot 'ef-forbidden.zip'
    $archive = [System.IO.Compression.ZipFile]::Open($efZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $archive.CreateEntry('plugin/Jellyfin.Plugin.AssSubsetter.dll') | Out-Null
        $archive.CreateEntry('nested/MICROSOFT.ENTITYFRAMEWORKCORE.SQLITE.dll') | Out-Null
    }
    finally { $archive.Dispose() }
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $efZip)) 'bundled host EF runtime'

    $missingZip = Join-Path $nestedZipRoot 'missing-plugin.zip'
    $archive = [System.IO.Compression.ZipFile]::Open($missingZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try { $archive.CreateEntry('nested/readme.txt') | Out-Null }
    finally { $archive.Dispose() }
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $artifactVerifier -ArgumentList @('-PackagePath', $missingZip)) 'missing plugin DLL'
}
finally { Remove-Item -LiteralPath $nestedZipRoot -Recurse -Force }

$passingResults = Join-Path $fixtures 'benchmark-results-passing.json'
$slowSubsetting = Join-Path $fixtures 'benchmark-results-slow-subsetting.json'
$slowLookup = Join-Path $fixtures 'benchmark-results-slow-lookup.json'
$dryResults = Join-Path $fixtures 'benchmark-results-dry.json'
$boundedContract = Join-Path $fixtures 'benchmark-contract-bounded.json'
$allPairsContract = Join-Path $fixtures 'benchmark-contract-all-pairs.json'
$benchmarkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ass-subsetter-benchmark-tests-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($benchmarkRoot) | Out-Null
try {
    $passingObject = Get-Content -Raw -LiteralPath $passingResults | ConvertFrom-Json
    $passingReport = Join-Path $benchmarkRoot 'passing-report-full.json'
    $passingObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $passingReport
    $slowSubsettingReport = Join-Path $benchmarkRoot 'slow-subsetting-report-full.json'
    Copy-Item -LiteralPath $slowSubsetting -Destination $slowSubsettingReport
    $slowLookupReport = Join-Path $benchmarkRoot 'slow-lookup-report-full.json'
    Copy-Item -LiteralPath $slowLookup -Destination $slowLookupReport
    $dryReport = Join-Path $benchmarkRoot 'dry-report-full.json'
    Copy-Item -LiteralPath $dryResults -Destination $dryReport

    Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $passingReport, '-ContractPath', $boundedContract)) 'passing benchmark thresholds'
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $slowSubsettingReport, '-ContractPath', $boundedContract)) 'subsetting median above one percent'
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $boundedContract, '-ContractPath', $boundedContract)) 'non-report-full result rejection'
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $slowLookupReport, '-ContractPath', $boundedContract)) 'lookup p95 threshold'
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $passingReport, '-ContractPath', $allPairsContract)) 'all-pairs candidate generation'
    $deepAllPairsContractObject = Get-Content -Raw -LiteralPath $boundedContract | ConvertFrom-Json
    $deepAllPairsContractObject.deepComparisonCount = $deepAllPairsContractObject.allPairsCount
    $deepAllPairsContract = Join-Path $benchmarkRoot 'deep-all-pairs-contract.json'
    $deepAllPairsContractObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $deepAllPairsContract
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $passingReport, '-ContractPath', $deepAllPairsContract)) 'deep comparisons all-pairs independent guard'
    $deepOverAllPairsContractObject = Get-Content -Raw -LiteralPath $boundedContract | ConvertFrom-Json
    $deepOverAllPairsContractObject.deepComparisonCount = [double]$deepOverAllPairsContractObject.allPairsCount + 1
    $deepOverAllPairsContract = Join-Path $benchmarkRoot 'deep-over-all-pairs-contract.json'
    $deepOverAllPairsContractObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $deepOverAllPairsContract
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $passingReport, '-ContractPath', $deepOverAllPairsContract)) 'deep comparisons over all-pairs guard'
    $deepZeroContractObject = Get-Content -Raw -LiteralPath $boundedContract | ConvertFrom-Json
    $deepZeroContractObject.deepComparisonCount = 0
    $deepZeroContract = Join-Path $benchmarkRoot 'deep-zero-contract.json'
    $deepZeroContractObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $deepZeroContract
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $passingReport, '-ContractPath', $deepZeroContract)) 'zero deep comparisons guard'
    $deepNonfiniteContractObject = Get-Content -Raw -LiteralPath $boundedContract | ConvertFrom-Json
    $deepNonfiniteContractObject.deepComparisonCount = 'NaN'
    $deepNonfiniteContract = Join-Path $benchmarkRoot 'deep-nonfinite-contract.json'
    $deepNonfiniteContractObject | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $deepNonfiniteContract
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $passingReport, '-ContractPath', $deepNonfiniteContract)) 'nonfinite deep comparisons guard'
    Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $dryReport, '-ContractPath', $boundedContract, '-DryRun')) 'dry-run execution shape'
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $dryReport, '-ContractPath', $boundedContract)) 'dry result rejected in normal mode'

    $multiFileRoot = Join-Path $benchmarkRoot 'multi-file-run'
    [System.IO.Directory]::CreateDirectory($multiFileRoot) | Out-Null
    $passingObject.Benchmarks | Select-Object -First 4 | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $multiFileRoot 'lookup-report-full.json')
    $passingObject.Benchmarks | Select-Object -Skip 4 | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $multiFileRoot 'subsetting-report-full.json')
    Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $multiFileRoot, '-ContractPath', $boundedContract)) 'multi-file report directory'

    function Set-Median([object]$Document, [string]$Method, [double]$Median) {
        ($Document.Benchmarks | Where-Object Method -eq $Method).Statistics.Median = $Median
    }

    $boundary = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    Set-Median $boundary 'NativeSubset' 100
    Set-Median $boundary 'DirectResolver' 100
    Set-Median $boundary 'CatalogResolver' 102
    $boundaryPath = Join-Path $benchmarkRoot 'boundary-report-full.json'
    $boundary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $boundaryPath
    Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $boundaryPath, '-ContractPath', $boundedContract)) 'exact 1.01 composition boundary'
    Set-Median $boundary 'CatalogResolver' 103
    $aboveBoundaryPath = Join-Path $benchmarkRoot 'above-boundary-report-full.json'
    $boundary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $aboveBoundaryPath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $aboveBoundaryPath, '-ContractPath', $boundedContract)) 'composition above 1.01 boundary'

    $fullPathBoundary = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    Set-Median $fullPathBoundary 'DirectAssProcessorArtifactAsync' 1000000
    Set-Median $fullPathBoundary 'CatalogAssProcessorArtifactAsync' 1010000
    $fullPathBoundaryPath = Join-Path $benchmarkRoot 'full-path-boundary-report-full.json'
    $fullPathBoundary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fullPathBoundaryPath
    Assert-ExitCode 0 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $fullPathBoundaryPath, '-ContractPath', $boundedContract)) 'exact 1.01 complete-path boundary'
    Set-Median $fullPathBoundary 'CatalogAssProcessorArtifactAsync' 1010001
    $fullPathAbovePath = Join-Path $benchmarkRoot 'full-path-above-report-full.json'
    $fullPathBoundary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fullPathAbovePath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $fullPathAbovePath, '-ContractPath', $boundedContract)) 'complete-path ratio above 1.01 boundary'

    $missingFullPath = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    $missingFullPath.Benchmarks = @($missingFullPath.Benchmarks | Where-Object Method -ne 'CatalogAssProcessorArtifactAsync')
    $missingFullPathPath = Join-Path $benchmarkRoot 'missing-full-path-report-full.json'
    $missingFullPath | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $missingFullPathPath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $missingFullPathPath, '-ContractPath', $boundedContract)) 'missing complete AssProcessor path result'

    $missing = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    $missing.Benchmarks = @($missing.Benchmarks | Where-Object Method -ne 'CatalogResolver')
    $missingPath = Join-Path $benchmarkRoot 'missing-report-full.json'
    $missing | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $missingPath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $missingPath, '-ContractPath', $boundedContract)) 'missing benchmark row'

    $duplicate = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    $duplicate.Benchmarks = @($duplicate.Benchmarks) + $duplicate.Benchmarks[0]
    $duplicatePath = Join-Path $benchmarkRoot 'duplicate-report-full.json'
    $duplicate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $duplicatePath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $duplicatePath, '-ContractPath', $boundedContract)) 'duplicate benchmark row'

    $zero = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    Set-Median $zero 'DirectResolver' 0
    $zeroPath = Join-Path $benchmarkRoot 'zero-report-full.json'
    $zero | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $zeroPath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $zeroPath, '-ContractPath', $boundedContract)) 'zero statistic guard'

    $nonfinite = Get-Content -Raw -LiteralPath $passingReport | ConvertFrom-Json
    ($nonfinite.Benchmarks | Where-Object Method -eq 'DirectResolver').Statistics.Median = 'NaN'
    $nonfinitePath = Join-Path $benchmarkRoot 'nonfinite-report-full.json'
    $nonfinite | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $nonfinitePath
    Assert-ExitCode 1 (Invoke-VerificationScript -ScriptPath $benchmarkVerifier -ArgumentList @('-ResultsPath', $nonfinitePath, '-ContractPath', $boundedContract)) 'nonfinite statistic guard'
}
finally { Remove-Item -LiteralPath $benchmarkRoot -Recurse -Force }

Write-Host 'Task 12 script contract tests passed.'
