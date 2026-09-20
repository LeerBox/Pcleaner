#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'PCleaner must be packaged on Windows.' }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runtime = 'win-x64'
$packageName = "PCleaner-$Version-$runtime"
$outputRoot = Join-Path $repoRoot 'artifacts/releases'
$stagingRoot = Join-Path $outputRoot ('.build-' + [guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $stagingRoot $packageName
$licensesRoot = Join-Path $publishRoot 'licenses'
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Push-Location $repoRoot
try {
    & dotnet publish src/PCleaner.App/PCleaner.App.csproj -c Release -r $runtime --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false `
        -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false `
        "-p:Version=$Version" -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'PCleaner.exe'))) {
        throw 'The published application is missing.'
    }

    # Keep license information outside the executable so it is readable without running the app.
    foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $publishRoot
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RELEASE_NOTES.md') -Destination (Join-Path $publishRoot 'README.md')
    $fontNotices = Join-Path $licensesRoot 'fonts'
    New-Item -ItemType Directory -Path $fontNotices -Force | Out-Null
    foreach ($name in @('LICENCE-Ubuntu-UFL.txt', 'COPYRIGHT-Ubuntu.txt')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "src/PCleaner.App/Fonts/$name") -Destination $fontNotices
    }
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | Copy-Item -Destination $licensesRoot

    $assets = Get-Content (Join-Path $repoRoot 'src/PCleaner.App/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $packageRoots = @($assets.packageFolders.Keys)
    $inventory = [System.Collections.Generic.List[object]]::new()

    function Copy-PackageNotices([string]$Id, [string]$PackageVersion) {
        $relativePackage = "$($Id.ToLowerInvariant())/$($PackageVersion.ToLowerInvariant())"
        $source = $null
        foreach ($candidateRoot in $packageRoots) {
            $candidate = Join-Path $candidateRoot $relativePackage
            if (Test-Path -LiteralPath $candidate -PathType Container) { $source = $candidate; break }
        }
        if (-not $source) { throw "Cannot find restored package $Id $PackageVersion." }
        $destination = Join-Path $licensesRoot "$Id-$PackageVersion"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $noticeFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object {
            $_.Name -match '(?i)(^licen[cs]e(?:\.|$)|notice|copyright|\.nuspec$)'
        })
        foreach ($file in $noticeFiles) {
            $relative = [System.IO.Path]::GetRelativePath($source, $file.FullName)
            $target = Join-Path $destination $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
        if ($noticeFiles.Count -eq 0) { throw "No license metadata found for $Id $PackageVersion." }
        $inventory.Add([ordered]@{ id = $Id; version = $PackageVersion; notices = "licenses/$Id-$PackageVersion" })
    }

    foreach ($key in @($assets.libraries.Keys | Sort-Object)) {
        if ($assets.libraries[$key].type -ne 'package') { continue }
        $parts = $key.Split('/')
        Copy-PackageNotices $parts[0] $parts[1]
    }

    # Runtime packs are download dependencies, so they do not appear in the normal package list.
    $runtimeConfig = Get-Content (Join-Path $publishRoot 'PCleaner.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        Copy-PackageNotices "$($framework.name).Runtime.$runtime" $framework.version
    }
    $inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $publishRoot 'DEPENDENCIES.json') -Encoding utf8NoBOM

    # Stable entry order and timestamps keep ZIP metadata independent of the build time.
    $temporaryZip = Join-Path $stagingRoot "$packageName.zip"
    $archive = [System.IO.Compression.ZipFile]::Open($temporaryZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName)) {
            $relative = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
            $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, "$packageName/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        }
    }
    finally { $archive.Dispose() }

    $hash = (Get-FileHash -LiteralPath $temporaryZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $temporaryChecksum = Join-Path $stagingRoot "$packageName.zip.sha256"
    "$hash  $packageName.zip" | Set-Content -LiteralPath $temporaryChecksum -Encoding ascii
    # Both source and destination are fixed children of this repository's artifacts/releases directory.
    $zip = Join-Path $outputRoot "$packageName.zip"
    Copy-Item -LiteralPath $temporaryZip -Destination $zip -Force
    Copy-Item -LiteralPath $temporaryChecksum -Destination "$zip.sha256" -Force
    Write-Host "Created $zip"
    Write-Host "SHA256: $hash"
}
finally { Pop-Location }