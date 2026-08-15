#Requires -Version 5.1
<#
.SYNOPSIS
    Builds one offline PMTiles map pack per Australian state and territory.

.DESCRIPTION
    Runs `pmtiles extract` over the bounding boxes documented in "Mapping README.md", then records
    the size and SHA-256 of each result (offline-maps-plan.md section 4.2, build step 3) and writes a
    catalogue.json in the shape of DLR.Core.Contracts.Maps.MapPackSummary.

    Requires pmtiles.exe in the same folder as this script.
#>


[CmdletBinding()]
param(
    [string] $Source = 'http://127.0.0.1:818/20260812.pmtiles',
    [string] $OutDir,
    [ValidateRange(0, 15)]
    [int] $MaxZoom = 14,
    [ValidateRange(0, 15)]
    [int] $MinZoom = 0,
    [string[]] $Only,
    [switch] $Force,
    [int] $PackVersion = 1,
    [string] $BaseUrl = 'https://tiles.example.invalid/mappacks'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve script root safely for PowerShell 5.1
if (-not $PSScriptRoot) {
    $scriptRoot = Get-Location
} else {
    $scriptRoot = $PSScriptRoot
}

# Default OutDir if not supplied
if (-not $OutDir) {
    $OutDir = Join-Path $scriptRoot 'mappacks'
}

# Resolve pmtiles.exe in script directory
$pmtiles = Join-Path $scriptRoot 'pmtiles.exe'
if (-not (Test-Path -LiteralPath $pmtiles)) {
    throw "pmtiles.exe not found in script directory: $pmtiles"
}


# minLon, minLat, maxLon, maxLat
$packs = @(
    [pscustomobject] @{ Id = 'au-nsw'; Name = 'New South Wales';              MinLon = 140.99; MinLat = -37.52; MaxLon = 153.65; MaxLat = -28.15 }
    [pscustomobject] @{ Id = 'au-vic'; Name = 'Victoria';                     MinLon = 140.95; MinLat = -39.20; MaxLon = 150.00; MaxLat = -33.95 }
    [pscustomobject] @{ Id = 'au-qld'; Name = 'Queensland';                   MinLon = 137.99; MinLat = -29.20; MaxLon = 153.60; MaxLat =  -9.10 }
    [pscustomobject] @{ Id = 'au-sa';  Name = 'South Australia';              MinLon = 128.95; MinLat = -38.10; MaxLon = 141.05; MaxLat = -25.95 }
    [pscustomobject] @{ Id = 'au-wa';  Name = 'Western Australia';            MinLon = 112.90; MinLat = -35.25; MaxLon = 129.05; MaxLat = -13.50 }
    [pscustomobject] @{ Id = 'au-nt';  Name = 'Northern Territory';           MinLon = 128.95; MinLat = -26.05; MaxLon = 138.05; MaxLat = -10.90 }
    [pscustomobject] @{ Id = 'au-tas'; Name = 'Tasmania';                     MinLon = 143.75; MinLat = -43.90; MaxLon = 148.55; MaxLat = -39.15 }
    [pscustomobject] @{ Id = 'au-act'; Name = 'Australian Capital Territory'; MinLon = 148.75; MinLat = -35.95; MaxLon = 149.42; MaxLat = -35.10 }
)

$culture = [cultureinfo]::InvariantCulture
function Format-Coord([double] $value) { $value.ToString('0.00##', $culture) }

$allPackIds = @($packs.Id)

if ($Only) {
    $unknown = $Only | Where-Object { $allPackIds -notcontains $_ }
    if ($unknown) {
        throw "Unknown pack id(s): $($unknown -join ', '). Known ids: $($allPackIds -join ', ')."
    }
    $packs = $packs | Where-Object { $Only -contains $_.Id }
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

Write-Host "Source : $Source"
Write-Host "Output : $OutDir"
Write-Host "Zoom   : z$MinZoom-z$MaxZoom"
Write-Host ''

$catalogue = New-Object System.Collections.Generic.List[object]
$failed = New-Object System.Collections.Generic.List[string]

foreach ($pack in $packs) {
    $file = Join-Path $OutDir "$($pack.Id).v$PackVersion.pmtiles"
    $bbox = '{0},{1},{2},{3}' -f (Format-Coord $pack.MinLon), (Format-Coord $pack.MinLat),
        (Format-Coord $pack.MaxLon), (Format-Coord $pack.MaxLat)

    if ((Test-Path -LiteralPath $file) -and -not $Force) {
        Write-Host "$($pack.Id.PadRight(7)) skipped (exists; -Force to rebuild)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "$($pack.Id.PadRight(7)) extracting --bbox=$bbox ..." -ForegroundColor Cyan
        $started = Get-Date

        $part = "$file.part"
        if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }

        $outerPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $pmtiles extract $Source $part "--bbox=$bbox" "--maxzoom=$MaxZoom" "--minzoom=$MinZoom"
        }
        finally {
            $ErrorActionPreference = $outerPreference
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "$($pack.Id): pmtiles exited $LASTEXITCODE - skipping this pack."
            if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
            $failed.Add($pack.Id)
            continue
        }

        Move-Item -LiteralPath $part -Destination $file -Force
        $elapsed = (Get-Date) - $started
        Write-Host ("$($pack.Id.PadRight(7)) done in {0:hh\:mm\:ss}" -f $elapsed) -ForegroundColor Green
    }

    $info = Get-Item -LiteralPath $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()

    $catalogue.Add([pscustomobject] [ordered] @{
        id     = $pack.Id
        name   = $pack.Name
        bounds = [pscustomobject] [ordered] @{
            minLatitude  = $pack.MinLat
            minLongitude = $pack.MinLon
            maxLatitude  = $pack.MaxLat
            maxLongitude = $pack.MaxLon
        }
        minZoom   = $MinZoom
        maxZoom   = $MaxZoom
        sizeBytes = $info.Length
        sha256    = $hash
        version   = $PackVersion
        url       = "$($BaseUrl.TrimEnd('/'))/$($info.Name)"
    })
}

if ($catalogue.Count -eq 0) {
    throw 'No packs were built.'
}

$cataloguePath = Join-Path $OutDir 'catalogue.json'

if (Test-Path -LiteralPath $cataloguePath) {
    $builtIds = @($catalogue | ForEach-Object { $_.id })
    $loaded = Get-Content -LiteralPath $cataloguePath -Raw | ConvertFrom-Json
    foreach ($entry in @($loaded)) {
        if ($entry -and ($builtIds -notcontains $entry.id)) { $catalogue.Add($entry) }
    }
}

$ordered = @($catalogue | Sort-Object {
    $i = $allPackIds.IndexOf($_.id)
    if ($i -lt 0) { [int]::MaxValue } else { $i }
})
$ordered | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $cataloguePath -Encoding utf8

Write-Host ''
$ordered | ForEach-Object {
    [pscustomobject] @{
        Pack   = $_.id
        Name   = $_.name
        SizeMB = [math]::Round($_.sizeBytes / 1MB, 1)
        Sha256 = $_.sha256.Substring(0, 16) + '...'
    }
} | Format-Table -AutoSize

$totalMb = [math]::Round(($ordered | Measure-Object -Property sizeBytes -Sum).Sum / 1MB, 1)
Write-Host "Catalogue holds $($ordered.Count) pack(s), $totalMb MB total"
Write-Host "Catalogue: $cataloguePath"

if ($failed.Count -gt 0) {
    Write-Warning "Failed: $($failed -join ', '). Re-run with -Only <ids> to retry."
    exit 1
}
