#Requires -Version 5.1
<#
.SYNOPSIS
    Builds offline PMTiles map packs - one per Australian state and territory, plus world regions.

.DESCRIPTION
    Runs `pmtiles extract` over the bounding boxes documented in "Mapping README.md", then records
    the size and SHA-256 of each result (offline-maps-plan.md section 4.2, build step 3) and writes a
    catalogue.json in the shape of DLR.Core.Contracts.Maps.MapPackSummary.

    -Group picks which set to build: 'au' (the default, the eight state and territory packs),
    'world' (the 41 regions covering the rest of the planet) or 'all'. The default stays 'au'
    because a world run pulls hundreds of GB of ranges and takes hours. Packs already on disk are
    skipped unless -Force, and catalogue.json accumulates across runs, so 'au' then 'world' lands
    the same catalogue as 'all'.

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
    [ValidateSet('au', 'world', 'all')]
    [string] $Group = 'au',
    [switch] $Force,
    [int] $PackVersion = 1,
    [string] $BaseUrl = 'http://pmtiles.securehub.net'
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


# minLon, minLat, maxLon, maxLat. No box may cross the antimeridian - see "Mapping README.md".
$packs = @(
    [pscustomobject] @{ Group = 'au'; Id = 'au-nsw'; Name = 'New South Wales';              MinLon = 140.99; MinLat = -37.52; MaxLon = 153.65; MaxLat = -28.15 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-vic'; Name = 'Victoria';                     MinLon = 140.95; MinLat = -39.20; MaxLon = 150.00; MaxLat = -33.95 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-qld'; Name = 'Queensland';                   MinLon = 137.99; MinLat = -29.20; MaxLon = 153.60; MaxLat =  -9.10 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-sa';  Name = 'South Australia';              MinLon = 128.95; MinLat = -38.10; MaxLon = 141.05; MaxLat = -25.95 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-wa';  Name = 'Western Australia';            MinLon = 112.90; MinLat = -35.25; MaxLon = 129.05; MaxLat = -13.50 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-nt';  Name = 'Northern Territory';           MinLon = 128.95; MinLat = -26.05; MaxLon = 138.05; MaxLat = -10.90 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-tas'; Name = 'Tasmania';                     MinLon = 143.75; MinLat = -43.90; MaxLon = 148.55; MaxLat = -39.15 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-act'; Name = 'Australian Capital Territory'; MinLon = 148.75; MinLat = -35.95; MaxLon = 149.42; MaxLat = -35.10 }

    # Oceania beyond Australia
    [pscustomobject] @{ Group = 'world'; Id = 'oc-nz';      Name = 'New Zealand';                  MinLon = 166.00; MinLat = -47.50; MaxLon = 179.30; MaxLat = -33.90 }
    [pscustomobject] @{ Group = 'world'; Id = 'oc-png';     Name = 'Papua New Guinea';             MinLon = 140.80; MinLat = -11.70; MaxLon = 155.70; MaxLat =  -0.80 }
    [pscustomobject] @{ Group = 'world'; Id = 'oc-pacific'; Name = 'Melanesia and Fiji';           MinLon = 155.00; MinLat = -23.50; MaxLon = 180.00; MaxLat =  -4.90 }

    # Asia
    [pscustomobject] @{ Group = 'world'; Id = 'as-japan-korea';         Name = 'Japan and Korea';              MinLon = 122.00; MinLat =  23.50; MaxLon = 146.50; MaxLat =  46.10 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-china';               Name = 'China and Mongolia';           MinLon =  73.00; MinLat =  17.80; MaxLon = 135.20; MaxLat =  53.70 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-philippines';         Name = 'The Philippines';              MinLon = 116.50; MinLat =   4.30; MaxLon = 127.00; MaxLat =  21.50 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-indonesia';           Name = 'Indonesia and Timor-Leste';    MinLon =  94.50; MinLat = -11.50; MaxLon = 141.50; MaxLat =   8.50 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-southeast-mainland';  Name = 'Indochina and Myanmar';        MinLon =  91.50; MinLat =   5.40; MaxLon = 110.00; MaxLat =  29.00 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-south';               Name = 'India and the Himalaya';       MinLon =  60.00; MinLat =  -1.00; MaxLon =  92.60; MaxLat =  37.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-central';             Name = 'Central Asia and Afghanistan'; MinLon =  46.00; MinLat =  29.00; MaxLon =  88.00; MaxLat =  56.00 }
    [pscustomobject] @{ Group = 'world'; Id = 'as-middle-east';         Name = 'The Middle East';              MinLon =  25.00; MinLat =  11.80; MaxLon =  63.50; MaxLat =  43.60 }

    # Russia
    [pscustomobject] @{ Group = 'world'; Id = 'ru-west';     Name = 'European Russia';     MinLon =  26.50; MinLat = 41.00; MaxLon =  60.50; MaxLat = 70.20 }
    [pscustomobject] @{ Group = 'world'; Id = 'ru-siberia';  Name = 'Siberia';             MinLon =  60.00; MinLat = 48.00; MaxLon = 120.00; MaxLat = 78.00 }
    [pscustomobject] @{ Group = 'world'; Id = 'ru-far-east'; Name = 'Russian Far East';    MinLon = 120.00; MinLat = 42.00; MaxLon = 180.00; MaxLat = 73.00 }

    # Europe
    [pscustomobject] @{ Group = 'world'; Id = 'eu-british-isles';   Name = 'The British Isles';            MinLon = -11.00; MinLat = 49.80; MaxLon =   2.10; MaxLat = 61.10 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-iceland';         Name = 'Iceland and the Faroes';       MinLon = -25.00; MinLat = 61.30; MaxLon =  -6.30; MaxLat = 66.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-iberia';          Name = 'Spain and Portugal';           MinLon = -10.00; MinLat = 35.80; MaxLon =   4.40; MaxLat = 44.40 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-france-benelux';  Name = 'France and the Low Countries'; MinLon =  -5.20; MinLat = 41.30; MaxLon =   8.30; MaxLat = 53.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-germany-alps';    Name = 'Germany and the Alps';         MinLon =   5.80; MinLat = 45.70; MaxLon =  17.20; MaxLat = 55.10 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-italy';           Name = 'Italy and Malta';              MinLon =   6.50; MinLat = 35.40; MaxLon =  18.60; MaxLat = 47.10 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-nordic';          Name = 'Scandinavia and Finland';      MinLon =   4.00; MinLat = 54.50; MaxLon =  31.60; MaxLat = 71.30 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-central';         Name = 'Poland, the Baltics, Ukraine'; MinLon =  13.90; MinLat = 44.30; MaxLon =  40.30; MaxLat = 56.30 }
    [pscustomobject] @{ Group = 'world'; Id = 'eu-balkans';         Name = 'The Balkans and Greece';       MinLon =  13.30; MinLat = 34.70; MaxLon =  30.10; MaxLat = 48.60 }

    # Africa
    [pscustomobject] @{ Group = 'world'; Id = 'af-north';        Name = 'North Africa';              MinLon = -17.30; MinLat =  19.00; MaxLon = 37.00; MaxLat =  37.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'af-west';         Name = 'West Africa';               MinLon = -17.60; MinLat =   3.90; MaxLon = 16.20; MaxLat =  27.70 }
    [pscustomobject] @{ Group = 'world'; Id = 'af-central';      Name = 'Central Africa';            MinLon =   7.90; MinLat = -13.50; MaxLon = 31.60; MaxLat =  15.10 }
    [pscustomobject] @{ Group = 'world'; Id = 'af-east';         Name = 'East Africa and the Horn';  MinLon =  21.80; MinLat = -12.00; MaxLon = 51.50; MaxLat =  23.20 }
    [pscustomobject] @{ Group = 'world'; Id = 'af-south';        Name = 'Southern Africa';           MinLon =   9.50; MinLat = -35.00; MaxLon = 41.00; MaxLat =  -8.00 }
    [pscustomobject] @{ Group = 'world'; Id = 'af-indian-ocean'; Name = 'Madagascar and Mascarenes'; MinLon =  42.50; MinLat = -26.00; MaxLon = 58.00; MaxLat = -11.30 }

    # North America
    [pscustomobject] @{ Group = 'world'; Id = 'na-alaska';          Name = 'Alaska';                       MinLon = -172.50; MinLat = 51.00; MaxLon = -129.50; MaxLat = 71.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-canada-west';     Name = 'Western Canada';               MinLon = -141.10; MinLat = 48.00; MaxLon =  -94.50; MaxLat = 70.00 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-canada-east';     Name = 'Eastern Canada';               MinLon =  -95.20; MinLat = 41.60; MaxLon =  -52.30; MaxLat = 74.00 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-arctic';          Name = 'Arctic Canada and Greenland';  MinLon = -128.00; MinLat = 66.00; MaxLon =  -11.00; MaxLat = 83.80 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-us-west';         Name = 'Western United States';        MinLon = -125.10; MinLat = 31.20; MaxLon = -100.90; MaxLat = 49.40 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-us-east';         Name = 'Eastern United States';        MinLon = -101.00; MinLat = 24.30; MaxLon =  -66.80; MaxLat = 49.40 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-hawaii';          Name = 'Hawaii';                       MinLon = -160.60; MinLat = 18.60; MaxLon = -154.60; MaxLat = 22.40 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-mexico';          Name = 'Mexico';                       MinLon = -118.60; MinLat = 14.30; MaxLon =  -86.60; MaxLat = 32.80 }
    [pscustomobject] @{ Group = 'world'; Id = 'na-central-america'; Name = 'Central America, Caribbean';   MinLon =  -92.50; MinLat =  7.00; MaxLon =  -58.90; MaxLat = 27.60 }

    # South America
    [pscustomobject] @{ Group = 'world'; Id = 'sa-north';  Name = 'The northern Andes and Peru';     MinLon = -82.00; MinLat = -19.00; MaxLon = -58.90; MaxLat =  13.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'sa-brazil'; Name = 'Brazil';                          MinLon = -74.50; MinLat = -34.00; MaxLon = -33.90; MaxLat =   5.60 }
    [pscustomobject] @{ Group = 'world'; Id = 'sa-south';  Name = 'Chile, Argentina and Uruguay';    MinLon = -76.00; MinLat = -56.00; MaxLon = -52.00; MaxLat = -17.00 }
)

$culture = [cultureinfo]::InvariantCulture
function Format-Coord([double] $value) { $value.ToString('0.00##', $culture) }

$allPackIds = @($packs.Id)

if ($Only) {
    # -Only names packs outright, so it wins over -Group rather than intersecting with it.
    $unknown = $Only | Where-Object { $allPackIds -notcontains $_ }
    if ($unknown) {
        throw "Unknown pack id(s): $($unknown -join ', '). Known ids: $($allPackIds -join ', ')."
    }
    $packs = @($packs | Where-Object { $Only -contains $_.Id })
}
elseif ($Group -ne 'all') {
    $packs = @($packs | Where-Object { $_.Group -eq $Group })
}

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

Write-Host "Source : $Source"
Write-Host "Output : $OutDir"
Write-Host "Zoom   : z$MinZoom-z$MaxZoom"
if ($Only) {
    Write-Host "Packs  : $($Only -join ', ')"
} else {
    Write-Host "Packs  : $($packs.Count) in group '$Group'"
}
Write-Host ''

# Ids run from 'au-sa' to 'as-southeast-mainland', so pad the progress lines to the widest one.
$idWidth = ($packs | ForEach-Object { $_.Id.Length } | Measure-Object -Maximum).Maximum

$catalogue = New-Object System.Collections.Generic.List[object]
$failed = New-Object System.Collections.Generic.List[string]

foreach ($pack in $packs) {
    $file = Join-Path $OutDir "$($pack.Id).v$PackVersion.pmtiles"
    $bbox = '{0},{1},{2},{3}' -f (Format-Coord $pack.MinLon), (Format-Coord $pack.MinLat),
        (Format-Coord $pack.MaxLon), (Format-Coord $pack.MaxLat)

    if ((Test-Path -LiteralPath $file) -and -not $Force) {
        Write-Host "$($pack.Id.PadRight($idWidth)) skipped (exists; -Force to rebuild)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "$($pack.Id.PadRight($idWidth)) extracting --bbox=$bbox ..." -ForegroundColor Cyan
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
        Write-Host ("$($pack.Id.PadRight($idWidth)) done in {0:hh\:mm\:ss}" -f $elapsed) -ForegroundColor Green
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
