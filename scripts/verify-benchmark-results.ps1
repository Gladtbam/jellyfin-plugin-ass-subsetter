[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$ResultsPath,
    [Parameter(Mandatory = $true)] [string]$ContractPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    [Console]::Error.WriteLine($Message)
    exit 1
}

function Number([object]$Value, [string]$Description) {
    $number = 0.0
    if ($null -eq $Value -or
        -not [double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
        [double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -le 0) {
        Fail "$Description must be a finite non-zero number."
    }

    return $number
}

function Get-ReportFiles([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $file = Get-Item -LiteralPath $Path
        if ($file.Name -notlike '*-report-full.json') {
            Fail "Results file must be an exact *-report-full.json export: $Path"
        }

        return @($file)
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Fail "Results path does not exist: $Path"
    }

    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object { $_.Name -like '*-report-full.json' })
    if ($files.Count -eq 0) {
        Fail "Results directory contains no *-report-full.json files: $Path"
    }

    $directories = @($files | ForEach-Object { $_.DirectoryName } | Sort-Object -Unique)
    if ($directories.Count -ne 1) {
        Fail "Results directory must identify one unique run directory; found $($directories.Count)."
    }

    return $files
}

function Get-ReportRows([object]$Document) {
    if ($Document -is [System.Array]) { return @($Document) }
    if ($null -ne $Document.PSObject.Properties['Benchmarks']) { return @($Document.Benchmarks) }
    if ($null -ne $Document.PSObject.Properties['Results']) { return @($Document.Results) }
    return @($Document)
}

try { $contract = Get-Content -Raw -LiteralPath $ContractPath | ConvertFrom-Json }
catch { Fail "Could not parse benchmark contract JSON: $($_.Exception.Message)" }

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($file in @(Get-ReportFiles $ResultsPath)) {
    try { $document = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json }
    catch { Fail "Could not parse benchmark JSON: $($file.FullName): $($_.Exception.Message)" }
    foreach ($row in @(Get-ReportRows $document)) { [void]$rows.Add($row) }
}

$required = @('ExactLookup', 'LooseLookup', 'ConstructIndex', 'GenerateCandidates', 'NativeSubset', 'DirectResolver', 'CatalogResolver', 'DirectAssProcessorArtifactAsync', 'CatalogAssProcessorArtifactAsync')
if ($rows.Count -eq 0) { Fail 'Benchmark JSON contains no benchmark rows.' }
$byMethod = @{}
foreach ($row in $rows) {
    $method = [string]$row.Method
    if ([string]::IsNullOrWhiteSpace($method)) { Fail 'Benchmark result lacks Method.' }
    if ($byMethod.ContainsKey($method)) { Fail "Duplicate benchmark result: $method" }
    $byMethod[$method] = $row
}
foreach ($method in $required) {
    if (-not $byMethod.ContainsKey($method)) { Fail "Required benchmark result is missing: $method" }
}

if ($DryRun) {
    Write-Host 'Dry benchmark shape verification passed; no performance claim was made.'
    exit 0
}

foreach ($method in $required) {
    $stats = $byMethod[$method].Statistics
    if ($null -eq $stats) { Fail "$method lacks statistics (Dry output is not accepted)." }
    [void](Number $stats.Median "$method median")
    [void](Number $stats.Percentiles.P95 "$method p95")
    [void](Number $byMethod[$method].Memory.BytesAllocatedPerOperation "$method allocation")
}

if ((Number $byMethod['ExactLookup'].Statistics.Percentiles.P95 'ExactLookup p95') -ge 1000000) { Fail 'ExactLookup p95 must be below 1 ms (statistics are nanoseconds).' }
if ((Number $byMethod['LooseLookup'].Statistics.Percentiles.P95 'LooseLookup p95') -ge 10000000) { Fail 'LooseLookup p95 must be below 10 ms (statistics are nanoseconds).' }
$native = Number $byMethod['NativeSubset'].Statistics.Median 'NativeSubset median'
$direct = Number $byMethod['DirectResolver'].Statistics.Median 'DirectResolver median'
$catalog = Number $byMethod['CatalogResolver'].Statistics.Median 'CatalogResolver median'
$ratio = ($native + $catalog) / ($native + $direct)
if ($ratio -gt 1.01) { Fail "Compositional catalog/direct median ratio $ratio exceeds exact 1.01 limit." }
$directArtifact = Number $byMethod['DirectAssProcessorArtifactAsync'].Statistics.Median 'DirectAssProcessorArtifactAsync median'
$catalogArtifact = Number $byMethod['CatalogAssProcessorArtifactAsync'].Statistics.Median 'CatalogAssProcessorArtifactAsync median'
$artifactRatio = $catalogArtifact / $directArtifact
if ($artifactRatio -gt 1.01) { Fail "Complete AssProcessor catalog/direct median ratio $artifactRatio exceeds exact 1.01 limit." }

$bucketPairCount = $contract.bucketPairCount
if ($null -eq $bucketPairCount) { $bucketPairCount = $contract.candidatePairCount }
$bucketPairCountValue = Number $bucketPairCount 'contract bucket pair count'
$deepComparisonCount = Number $contract.deepComparisonCount 'contract deep comparison count'
$allPairsCount = Number $contract.allPairsCount 'contract all-pairs count'
if ([int]$contract.fileCount -ne 20000 -or
    $bucketPairCountValue -ge $allPairsCount -or
    $deepComparisonCount -ge $allPairsCount -or
    -not [bool]$contract.nativeSubsetValidated) {
    Fail 'Benchmark contract does not prove a bounded 20,000-file native-subsetting workload.'
}

Write-Host "Benchmark point-estimate verification passed: exact p95 < 1ms, loose p95 < 10ms, complete AssProcessor catalog/direct median ratio $artifactRatio; compositional diagnostic ratio $ratio. Statistical and host validity require separate review."
