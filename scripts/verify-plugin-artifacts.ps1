[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [string]$JellyfinDepsPath
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    [Console]::Error.WriteLine($Message)
    exit 1
}

function Test-ForbiddenName([string]$Name) {
    $leaf = [System.IO.Path]::GetFileName($Name)
    return $leaf -match '^(Microsoft\.EntityFrameworkCore.*|Microsoft\.Data\.Sqlite.*|SQLitePCLRaw.*|e_sqlite3\.dll|libe_sqlite3\.so|libe_sqlite3\.dylib)$'
}

function Test-PluginName([string]$Name) {
    return [System.IO.Path]::GetFileName($Name) -ieq 'Jellyfin.Plugin.AssSubsetter.dll'
}

function Get-PackageEntryNames([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $root = $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        return Get-ChildItem -LiteralPath $Path -Recurse -File | ForEach-Object {
            $_.FullName.Substring($root.Length)
        }
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "Package path does not exist: $Path"
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }
}

function Test-JellyfinDependencies([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "Jellyfin deps file does not exist: $Path"
    }

    try { $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { Fail "Jellyfin deps file is not valid JSON: $Path" }

    $libraries = $json.libraries
    if ($null -eq $libraries) { Fail 'Jellyfin deps file has no libraries object.' }
    $required = @(
        'Microsoft.EntityFrameworkCore/9.0.11',
        'Microsoft.EntityFrameworkCore.Abstractions/9.0.11',
        'Microsoft.EntityFrameworkCore.Relational/9.0.11',
        'Microsoft.EntityFrameworkCore.Sqlite/9.0.11',
        'Microsoft.EntityFrameworkCore.Sqlite.Core/9.0.11',
        'Microsoft.Data.Sqlite/9.0.11',
        'Microsoft.Data.Sqlite.Core/9.0.11',
        'SQLitePCLRaw.bundle_e_sqlite3/2.1.10',
        'SQLitePCLRaw.core/2.1.10',
        'SQLitePCLRaw.lib.e_sqlite3/2.1.10',
        'SQLitePCLRaw.provider.e_sqlite3/2.1.10'
    )
    foreach ($key in $required) {
        if ($libraries.PSObject.Properties.Name -notcontains $key) { Fail "Jellyfin 10.11.9 dependency is missing or mismatched: $key" }
    }
}

$entries = @(Get-PackageEntryNames $PackagePath)
if (-not ($entries | Where-Object { Test-PluginName $_ })) { Fail 'Package does not contain Jellyfin.Plugin.AssSubsetter.dll.' }

$forbidden = @($entries | Where-Object { Test-ForbiddenName $_ })
if ($forbidden.Count -gt 0) { Fail "Package bundles forbidden host database runtime artifacts: $($forbidden -join ', ')" }

if ($PSBoundParameters.ContainsKey('JellyfinDepsPath')) { Test-JellyfinDependencies $JellyfinDepsPath }
Write-Host "Artifact verification passed: $PackagePath"
