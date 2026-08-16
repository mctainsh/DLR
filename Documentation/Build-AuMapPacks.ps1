#Requires -Version 5.1
<#
.SYNOPSIS
    Builds offline PMTiles map packs - one per Australian state and territory, plus world regions.

.DESCRIPTION
    Runs `pmtiles extract` over the bounding boxes documented in "Mapping README.md", then records
    the size and SHA-256 of each result (offline-maps-plan.md section 4.2, build step 3) and writes a
    catalogue.json in the shape of DLR.Core.Contracts.Maps.MapPackSummary.

    -Group picks which set to build: 'au' (the eight state and territory packs), one continent -
    'oceania', 'asia', 'russia', 'europe', 'africa', 'north-america', 'south-america' - 'world'
    (everything but Australia), or 'all', the default. A full run pulls the best part of a planet's
    worth of ranges and takes many hours, so it is usually taken a continent at a time: packs
    already on disk are skipped unless -Force, and catalogue.json accumulates across runs, so a
    continent at a time lands the same catalogue as one 'all' run. -ListPacks prints the selection
    and exits without extracting anything.

    Every box is drawn to come in under 1 GB at z14 - see the sizing note above the table. After a
    build the script warns about anything that came out over -OversizeBytes.

    Every pack carries a Region as well as a Group, and both go in the catalogue. Group is what
    -Group selects on and is a continent; Region is the country the pack is part of - 'France',
    'Canada', 'Australia' - or the smallest natural grouping when no one country owns it, and it is
    what the settings screen puts in its first dropdown. Two hundred regions in one list is not a
    choice anybody can make on a phone, so the screen asks for the country first and the area within
    it second. Both fields belong to the pack rather than to the run, so a build of one continent
    stamps a region on every entry it keeps, not only on the ones it just extracted.

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
    [ValidateSet('au', 'oceania', 'asia', 'russia', 'europe', 'africa', 'north-america', 'south-america', 'world', 'all')]
    [string] $Group = 'all',
    [switch] $ListPacks,
    [switch] $Force,
    [int] $PackVersion = 1,
    [string] $BaseUrl = 'http://pmtiles.securehub.net',
    [long] $OversizeBytes = 1GB
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

# Resolve pmtiles.exe in script directory. -ListPacks reads the table only, so it does not need it.
$pmtiles = Join-Path $scriptRoot 'pmtiles.exe'
if (-not $ListPacks -and -not (Test-Path -LiteralPath $pmtiles)) {
    throw "pmtiles.exe not found in script directory: $pmtiles"
}


# minLon, minLat, maxLon, maxLat. No box may cross the antimeridian - see "Mapping README.md".
#
# Every box is drawn to land under 1 GB at z14, and these boundaries are not guesses: a full build
# measured all of them, and the 38 packs that came out over ~850 MB were re-cut against a data
# density map solved from those measurements. Area is a poor predictor - Finland is denser than
# Kazakhstan and the Canadian Arctic is denser than the Sahara, because lakes, glaciers and
# coastline carry geometry. Predicted worst case is now 847 MB (na-us-carolinas), median 452 MB.
# If a rebuild reports something over the cap, cut it here and rebuild that id with -Only -Force.
$packs = @(
    [pscustomobject] @{ Group = 'au'; Id = 'au-nsw'; Region = 'Australia'; Name = 'New South Wales';              MinLon = 140.99; MinLat = -37.52; MaxLon = 153.65; MaxLat = -28.15 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-vic'; Region = 'Australia'; Name = 'Victoria';                     MinLon = 140.95; MinLat = -39.20; MaxLon = 150.00; MaxLat = -33.95 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-qld'; Region = 'Australia'; Name = 'Queensland';                   MinLon = 137.99; MinLat = -29.20; MaxLon = 153.60; MaxLat =  -9.10 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-sa';  Region = 'Australia'; Name = 'South Australia';              MinLon = 128.95; MinLat = -38.10; MaxLon = 141.05; MaxLat = -25.95 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-wa';  Region = 'Australia'; Name = 'Western Australia';            MinLon = 112.90; MinLat = -35.25; MaxLon = 129.05; MaxLat = -13.50 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-nt';  Region = 'Australia'; Name = 'Northern Territory';           MinLon = 128.95; MinLat = -26.05; MaxLon = 138.05; MaxLat = -10.90 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-tas'; Region = 'Australia'; Name = 'Tasmania';                     MinLon = 143.75; MinLat = -43.90; MaxLon = 148.55; MaxLat = -39.15 }
    [pscustomobject] @{ Group = 'au'; Id = 'au-act'; Region = 'Australia'; Name = 'Australian Capital Territory'; MinLon = 148.75; MinLat = -35.95; MaxLon = 149.42; MaxLat = -35.10 }

    # Oceania beyond Australia
    [pscustomobject] @{ Group = 'oceania'; Id = 'oc-nz-north';     Region = 'New Zealand';         Name = 'New Zealand - North Island';          MinLon = 172.00; MinLat = -41.80; MaxLon = 179.30; MaxLat = -34.00 }
    [pscustomobject] @{ Group = 'oceania'; Id = 'oc-nz-south';     Region = 'New Zealand';         Name = 'New Zealand - South Island';          MinLon = 166.00; MinLat = -47.50; MaxLon = 175.00; MaxLat = -40.20 }
    [pscustomobject] @{ Group = 'oceania'; Id = 'oc-png';          Region = 'Papua New Guinea';    Name = 'Papua New Guinea';                    MinLon = 140.80; MinLat = -11.70; MaxLon = 155.70; MaxLat =  -0.80 }
    [pscustomobject] @{ Group = 'oceania'; Id = 'oc-pacific-west'; Region = 'The Pacific islands'; Name = 'Solomons, Vanuatu and New Caledonia'; MinLon = 155.00; MinLat = -23.50; MaxLon = 172.00; MaxLat =  -4.90 }
    [pscustomobject] @{ Group = 'oceania'; Id = 'oc-pacific-east'; Region = 'The Pacific islands'; Name = 'Fiji and Tuvalu';                     MinLon = 172.00; MinLat = -21.50; MaxLon = 180.00; MaxLat =  -5.50 }

    # East Asia
    [pscustomobject] @{ Group = 'asia'; Id = 'as-japan-north';  Region = 'Japan';    Name = 'Japan - Hokkaido and Tohoku';      MinLon = 138.00; MinLat = 34.80; MaxLon = 146.50; MaxLat = 46.10 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-japan-kyushu'; Region = 'Japan';    Name = 'Kyushu and western Honshu';        MinLon = 128.50; MinLat = 30.00; MaxLon = 133.50; MaxLat = 36.20 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-japan-kansai'; Region = 'Japan';    Name = 'Osaka, Nagoya and Shikoku';        MinLon = 131.70; MinLat = 30.00; MaxLon = 141.00; MaxLat = 37.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-okinawa';      Region = 'Japan';    Name = 'Okinawa and the Ryukyus';          MinLon = 122.50; MinLat = 23.50; MaxLon = 131.50; MaxLat = 30.20 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-korea';        Region = 'Korea';    Name = 'Korea';                            MinLon = 124.00; MinLat = 33.00; MaxLon = 131.60; MaxLat = 43.10 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-taiwan';       Region = 'Taiwan';   Name = 'Taiwan';                           MinLon = 119.20; MinLat = 21.70; MaxLon = 122.20; MaxLat = 25.40 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-northeast';    Region = 'China';    Name = 'China - the Northeast';            MinLon = 118.00; MinLat = 38.50; MaxLon = 135.20; MaxLat = 53.70 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-north';        Region = 'China';    Name = 'China - Beijing and the north';    MinLon = 110.00; MinLat = 32.00; MaxLon = 122.90; MaxLat = 43.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-east';         Region = 'China';    Name = 'China - Shanghai and the east';    MinLon = 113.00; MinLat = 26.50; MaxLon = 123.00; MaxLat = 36.20 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-south';        Region = 'China';    Name = 'China - Guangdong and the south';  MinLon = 104.00; MinLat = 17.80; MaxLon = 120.50; MaxLat = 27.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-southwest';    Region = 'China';    Name = 'China - Sichuan and Yunnan';       MinLon =  97.00; MinLat = 21.00; MaxLon = 112.00; MaxLat = 33.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-northwest';    Region = 'China';    Name = 'China - Gansu and Inner Mongolia'; MinLon =  93.00; MinLat = 32.00; MaxLon = 112.00; MaxLat = 45.00 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-tibet';        Region = 'China';    Name = 'Tibet and Qinghai';                MinLon =  78.00; MinLat = 26.50; MaxLon =  99.50; MaxLat = 36.80 }
    [pscustomobject] @{ Group = 'asia'; Id = 'cn-xinjiang';     Region = 'China';    Name = 'Xinjiang';                         MinLon =  73.00; MinLat = 34.00; MaxLon =  96.50; MaxLat = 49.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'mn-mongolia';     Region = 'Mongolia'; Name = 'Mongolia';                         MinLon =  87.50; MinLat = 41.50; MaxLon = 120.00; MaxLat = 52.20 }

    # South-East Asia
    [pscustomobject] @{ Group = 'asia'; Id = 'as-myanmar';         Region = 'Myanmar';                    Name = 'Myanmar';                          MinLon =  91.50; MinLat =   9.40; MaxLon = 101.50; MaxLat = 28.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-thailand';        Region = 'Thailand';                   Name = 'Thailand';                         MinLon =  96.50; MinLat =   5.50; MaxLon = 106.00; MaxLat = 20.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-indochina';       Region = 'Vietnam, Laos and Cambodia'; Name = 'Vietnam, Laos and Cambodia';       MinLon = 100.00; MinLat =   8.20; MaxLon = 109.60; MaxLat = 23.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-malaysia';        Region = 'Malaysia and Singapore';     Name = 'Malaysia and Singapore';           MinLon =  99.00; MinLat =   0.80; MaxLon = 105.60; MaxLat =  7.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-borneo';          Region = 'Indonesia and Borneo';       Name = 'Borneo';                           MinLon = 108.50; MinLat =  -4.50; MaxLon = 119.50; MaxLat =  7.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-philippines';     Region = 'The Philippines';            Name = 'The Philippines';                  MinLon = 116.50; MinLat =   4.30; MaxLon = 127.00; MaxLat = 21.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-sumatra';         Region = 'Indonesia and Borneo';       Name = 'Sumatra';                          MinLon =  94.50; MinLat =  -6.50; MaxLon = 107.00; MaxLat =  6.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-java';            Region = 'Indonesia and Borneo';       Name = 'Java, Bali and the Lesser Sundas'; MinLon = 104.50; MinLat = -11.00; MaxLon = 127.50; MaxLat = -5.00 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-sulawesi-maluku'; Region = 'Indonesia and Borneo';       Name = 'Sulawesi and Maluku';              MinLon = 117.00; MinLat =  -6.50; MaxLon = 132.00; MaxLat =  5.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-papua';           Region = 'Indonesia and Borneo';       Name = 'Western New Guinea';               MinLon = 130.50; MinLat =  -9.50; MaxLon = 141.50; MaxLat =  0.50 }

    # South Asia
    [pscustomobject] @{ Group = 'asia'; Id = 'as-pakistan';   Region = 'Pakistan'; Name = 'Pakistan';                           MinLon = 60.00; MinLat = 23.00; MaxLon = 78.00; MaxLat = 37.30 }
    [pscustomobject] @{ Group = 'asia'; Id = 'in-northwest';  Region = 'India';    Name = 'India - Delhi and the northwest';    MinLon = 68.00; MinLat = 22.50; MaxLon = 79.50; MaxLat = 34.00 }
    [pscustomobject] @{ Group = 'asia'; Id = 'in-northeast';  Region = 'India';    Name = 'India - the Ganges plain and Nepal'; MinLon = 79.00; MinLat = 21.00; MaxLon = 89.50; MaxLat = 31.20 }
    [pscustomobject] @{ Group = 'asia'; Id = 'in-far-east';   Region = 'India';    Name = 'Bangladesh, Assam and Bhutan';       MinLon = 88.00; MinLat = 20.50; MaxLon = 97.50; MaxLat = 29.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'in-west';       Region = 'India';    Name = 'India - Mumbai and the west';        MinLon = 68.50; MinLat = 14.00; MaxLon = 80.50; MaxLat = 23.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'in-south-west'; Region = 'India';    Name = 'Karnataka, Kerala and the Maldives'; MinLon = 72.00; MinLat = -1.00; MaxLon = 78.50; MaxLat = 18.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'in-south-east'; Region = 'India';    Name = 'Tamil Nadu, Andhra and Sri Lanka';   MinLon = 76.00; MinLat =  4.00; MaxLon = 85.50; MaxLat = 18.50 }

    # Central Asia
    [pscustomobject] @{ Group = 'asia'; Id = 'as-kazakhstan-west'; Region = 'Kazakhstan';                  Name = 'Western Kazakhstan';           MinLon = 46.00; MinLat = 40.50; MaxLon = 68.00; MaxLat = 56.00 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-kazakhstan-east'; Region = 'Kazakhstan';                  Name = 'Eastern Kazakhstan';           MinLon = 68.00; MinLat = 40.50; MaxLon = 88.00; MaxLat = 56.00 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-turkestan';       Region = 'Uzbekistan and Central Asia'; Name = 'Uzbekistan and the Tian Shan'; MinLon = 52.00; MinLat = 35.00; MaxLon = 80.50; MaxLat = 46.00 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-afghanistan';     Region = 'Afghanistan';                 Name = 'Afghanistan';                  MinLon = 60.00; MinLat = 29.00; MaxLon = 75.50; MaxLat = 39.00 }

    # The Middle East
    [pscustomobject] @{ Group = 'asia'; Id = 'as-turkey-west';  Region = 'Turkiye';      Name = 'Istanbul, Izmir and Ankara';         MinLon = 25.50; MinLat = 35.50; MaxLon = 33.50; MaxLat = 42.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-turkey-east';  Region = 'Turkiye';      Name = 'Eastern Anatolia';                   MinLon = 32.50; MinLat = 35.50; MaxLon = 45.00; MaxLat = 42.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-caucasus';     Region = 'The Caucasus'; Name = 'Georgia, Armenia and Azerbaijan';    MinLon = 39.50; MinLat = 37.50; MaxLon = 51.00; MaxLat = 43.80 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-levant';       Region = 'The Levant';   Name = 'Syria, Lebanon, Israel and Jordan';  MinLon = 32.00; MinLat = 28.50; MaxLon = 42.50; MaxLat = 37.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-iraq';         Region = 'Iraq';         Name = 'Iraq and Kuwait';                    MinLon = 38.50; MinLat = 28.50; MaxLon = 49.20; MaxLat = 37.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-iran';         Region = 'Iran';         Name = 'Iran';                               MinLon = 43.00; MinLat = 24.50; MaxLon = 63.50; MaxLat = 40.50 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-arabia-north'; Region = 'Arabia';       Name = 'Northern Saudi Arabia and the Gulf'; MinLon = 34.00; MinLat = 20.00; MaxLon = 60.00; MaxLat = 32.60 }
    [pscustomobject] @{ Group = 'asia'; Id = 'as-arabia-south'; Region = 'Arabia';       Name = 'Yemen, Oman and the Empty Quarter';  MinLon = 41.00; MinLat = 11.80; MaxLon = 60.00; MaxLat = 23.20 }

    # Russia
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-saint-petersburg';    Region = 'Russia'; Name = 'St Petersburg, Pskov and Novgorod';    MinLon =  26.50; MinLat = 55.00; MaxLon =  35.50; MaxLat = 62.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-karelia';             Region = 'Russia'; Name = 'Karelia, Vologda and Arkhangelsk';     MinLon =  29.00; MinLat = 59.00; MaxLon =  45.00; MaxLat = 66.50 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-murmansk';            Region = 'Russia'; Name = 'The Kola peninsula and the White Sea'; MinLon =  26.50; MinLat = 65.00; MaxLon =  45.00; MaxLat = 70.20 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-moscow-city';         Region = 'Russia'; Name = 'Moscow, Tver and Smolensk';            MinLon =  31.00; MinLat = 53.80; MaxLon =  40.50; MaxLat = 59.50 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-chernozem';           Region = 'Russia'; Name = 'Bryansk, Kursk and Voronezh';          MinLon =  30.00; MinLat = 49.50; MaxLon =  43.20; MaxLat = 54.20 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-upper-volga';         Region = 'Russia'; Name = 'Nizhny Novgorod and the upper Volga';  MinLon =  39.50; MinLat = 53.50; MaxLon =  45.00; MaxLat = 59.50 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-volga';               Region = 'Russia'; Name = 'Volgograd, Saratov and Samara';        MinLon =  43.00; MinLat = 47.00; MaxLon =  52.00; MaxLat = 57.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-tatarstan';           Region = 'Russia'; Name = 'Kazan, Izhevsk and Kirov';             MinLon =  45.00; MinLat = 54.00; MaxLon =  56.00; MaxLat = 62.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-urals';               Region = 'Russia'; Name = 'Ufa, Chelyabinsk and Yekaterinburg';   MinLon =  53.00; MinLat = 50.00; MaxLon =  62.00; MaxLat = 62.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-caucasus-north';      Region = 'Russia'; Name = 'Southern Russia and the Caucasus';     MinLon =  36.00; MinLat = 41.00; MaxLon =  50.00; MaxLat = 48.50 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-arctic-west';         Region = 'Russia'; Name = 'Komi, Nenets and Novaya Zemlya';       MinLon =  40.00; MinLat = 62.00; MaxLon =  60.50; MaxLat = 77.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-siberia-omsk';        Region = 'Russia'; Name = 'Tyumen, Omsk and Kurgan';              MinLon =  60.00; MinLat = 48.00; MaxLon =  76.00; MaxLat = 60.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-siberia-novosibirsk'; Region = 'Russia'; Name = 'Novosibirsk, Barnaul and Kemerovo';    MinLon =  75.00; MinLat = 48.00; MaxLon =  90.00; MaxLat = 60.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-siberia-northwest';   Region = 'Russia'; Name = 'West Siberia - Yamal and Taymyr';      MinLon =  60.00; MinLat = 60.00; MaxLon =  90.00; MaxLat = 78.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-siberia-southeast';   Region = 'Russia'; Name = 'East Siberia - Baikal and Chita';      MinLon =  90.00; MinLat = 48.00; MaxLon = 120.00; MaxLat = 60.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-siberia-northeast';   Region = 'Russia'; Name = 'East Siberia - Evenkia';               MinLon =  90.00; MinLat = 60.00; MaxLon = 120.00; MaxLat = 78.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-far-east-south';      Region = 'Russia'; Name = 'Amur, Primorye and Sakhalin';          MinLon = 120.00; MinLat = 42.00; MaxLon = 148.00; MaxLat = 58.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-yakutia';             Region = 'Russia'; Name = 'Yakutia';                              MinLon = 120.00; MinLat = 55.00; MaxLon = 150.00; MaxLat = 74.00 }
    [pscustomobject] @{ Group = 'russia'; Id = 'ru-kamchatka';           Region = 'Russia'; Name = 'Kamchatka and Chukotka';               MinLon = 148.00; MinLat = 50.00; MaxLon = 180.00; MaxLat = 73.00 }

    # Europe
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-ireland';              Region = 'Ireland';                      Name = 'Ireland';                                   MinLon = -11.00; MinLat = 51.30; MaxLon =  -5.30; MaxLat = 55.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-england-south';        Region = 'The United Kingdom';           Name = 'London, the southwest and south Wales';     MinLon =  -6.50; MinLat = 49.80; MaxLon =   2.10; MaxLat = 52.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-england-north';        Region = 'The United Kingdom';           Name = 'The Midlands, north Wales and Yorkshire';   MinLon =  -5.50; MinLat = 52.00; MaxLon =   2.10; MaxLat = 54.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-scotland';             Region = 'The United Kingdom';           Name = 'Scotland and northern England';             MinLon =  -8.70; MinLat = 53.50; MaxLon =  -0.50; MaxLat = 61.10 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-iceland';              Region = 'Iceland';                      Name = 'Iceland and the Faroes';                    MinLon = -25.00; MinLat = 61.30; MaxLon =  -6.30; MaxLat = 66.60 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-iberia-northwest';     Region = 'Spain and Portugal';           Name = 'Madrid, Galicia and northern Portugal';     MinLon =  -9.60; MinLat = 39.80; MaxLon =  -1.50; MaxLat = 44.40 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-iberia-northeast';     Region = 'Spain and Portugal';           Name = 'Catalonia, Aragon and the Basque country';  MinLon =  -2.00; MinLat = 39.80; MaxLon =   4.40; MaxLat = 44.40 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-iberia-south';         Region = 'Spain and Portugal';           Name = 'Andalusia and southern Portugal';           MinLon =  -9.60; MinLat = 35.80; MaxLon =   1.00; MaxLat = 40.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-canaries';             Region = 'Spain and Portugal';           Name = 'The Canaries and Madeira';                  MinLon = -18.50; MinLat = 27.40; MaxLon = -13.00; MaxLat = 33.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-northwest';     Region = 'France';                       Name = 'Brittany, Normandy and the Loire';          MinLon =  -5.20; MinLat = 46.00; MaxLon =   0.50; MaxLat = 49.90 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-paris';         Region = 'France';                       Name = 'Paris and the north';                       MinLon =   0.20; MinLat = 47.80; MaxLon =   4.40; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-burgundy';      Region = 'France';                       Name = 'Centre and Burgundy';                       MinLon =   0.20; MinLat = 46.00; MaxLon =   5.60; MaxLat = 48.60 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-champagne';     Region = 'France';                       Name = 'Champagne and the Ardennes';                MinLon =   3.60; MinLat = 47.20; MaxLon =   5.90; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-alsace';        Region = 'France';                       Name = 'Alsace and Lorraine';                       MinLon =   5.30; MinLat = 47.20; MaxLon =   7.70; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-southwest';     Region = 'France';                       Name = 'Bordeaux, Toulouse and the Pyrenees';       MinLon =  -2.00; MinLat = 42.30; MaxLon =   2.20; MaxLat = 46.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-mediterranean'; Region = 'France';                       Name = 'Provence, Languedoc and Corsica';           MinLon =   1.50; MinLat = 41.30; MaxLon =   9.70; MaxLat = 44.60 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-france-rhone-alpes';   Region = 'France';                       Name = 'Lyon, the Massif Central and the Alps';     MinLon =   2.20; MinLat = 44.00; MaxLon =   7.80; MaxLat = 46.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-belgium';              Region = 'Belgium and Luxembourg';       Name = 'Belgium and Luxembourg';                    MinLon =   2.40; MinLat = 49.40; MaxLon =   6.60; MaxLat = 51.60 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-netherlands';          Region = 'The Netherlands';              Name = 'The Netherlands';                           MinLon =   2.40; MinLat = 50.70; MaxLon =   7.30; MaxLat = 53.70 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-germany-rhine';        Region = 'Germany';                      Name = 'The Rhine, the Ruhr and Hesse';             MinLon =   5.80; MinLat = 50.00; MaxLon =  10.50; MaxLat = 52.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-germany-northwest';    Region = 'Germany';                      Name = 'Lower Saxony, Hamburg and Schleswig';       MinLon =   5.80; MinLat = 51.50; MaxLon =  10.50; MaxLat = 55.10 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-germany-northeast';    Region = 'Germany';                      Name = 'Berlin, Brandenburg and Mecklenburg';       MinLon =  10.00; MinLat = 51.50; MaxLon =  15.10; MaxLat = 55.10 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-germany-saxony';       Region = 'Germany';                      Name = 'Saxony, Thuringia and Saxony-Anhalt';       MinLon =  10.00; MinLat = 50.00; MaxLon =  15.10; MaxLat = 52.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-germany-baden';        Region = 'Germany';                      Name = 'Baden-Wurttemberg and the Black Forest';    MinLon =   7.40; MinLat = 47.20; MaxLon =  10.50; MaxLat = 50.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-germany-bavaria';      Region = 'Germany';                      Name = 'Bavaria';                                   MinLon =  10.00; MinLat = 47.20; MaxLon =  14.00; MaxLat = 50.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-switzerland';          Region = 'Switzerland';                  Name = 'Switzerland and Liechtenstein';             MinLon =   5.80; MinLat = 45.70; MaxLon =  10.70; MaxLat = 47.90 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-austria-west';         Region = 'Austria and Slovenia';         Name = 'Tyrol, Salzburg and Vorarlberg';            MinLon =   9.50; MinLat = 46.30; MaxLon =  13.50; MaxLat = 48.30 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-austria-east';         Region = 'Austria and Slovenia';         Name = 'Vienna, Styria and Slovenia';               MinLon =  12.80; MinLat = 45.40; MaxLon =  17.20; MaxLat = 49.10 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-italy-northwest';      Region = 'Italy';                        Name = 'Piedmont, Liguria and Lombardy';            MinLon =   6.50; MinLat = 43.20; MaxLon =  10.50; MaxLat = 47.10 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-italy-northeast';      Region = 'Italy';                        Name = 'Veneto, Emilia-Romagna and Friuli';         MinLon =   9.80; MinLat = 43.20; MaxLon =  14.00; MaxLat = 47.10 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-italy-central';        Region = 'Italy';                        Name = 'Central Italy and Sardinia';                MinLon =   8.00; MinLat = 38.80; MaxLon =  18.60; MaxLat = 44.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-italy-south';          Region = 'Italy';                        Name = 'Southern Italy, Sicily and Malta';          MinLon =  11.50; MinLat = 35.40; MaxLon =  18.60; MaxLat = 40.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-denmark';              Region = 'Denmark';                      Name = 'Denmark';                                   MinLon =   7.80; MinLat = 54.40; MaxLon =  15.30; MaxLat = 58.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-norway-west';          Region = 'Norway';                       Name = 'Bergen, Stavanger and the fjords';          MinLon =   4.00; MinLat = 57.80; MaxLon =   8.50; MaxLat = 63.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-norway-oslo';          Region = 'Norway';                       Name = 'Oslo and the eastern valleys';              MinLon =   7.50; MinLat = 57.80; MaxLon =  12.50; MaxLat = 63.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-norway-trondelag';     Region = 'Norway';                       Name = 'Trondheim and Nordland';                    MinLon =  10.00; MinLat = 63.00; MaxLon =  19.00; MaxLat = 68.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-norway-finnmark';      Region = 'Norway';                       Name = 'Tromso, Finnmark and Lapland';              MinLon =  10.00; MinLat = 66.50; MaxLon =  31.60; MaxLat = 71.30 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-sweden-south';         Region = 'Sweden';                       Name = 'Southern Sweden';                           MinLon =  11.00; MinLat = 55.00; MaxLon =  19.50; MaxLat = 61.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-sweden-central';       Region = 'Sweden';                       Name = 'Central Sweden and Dalarna';                MinLon =  12.00; MinLat = 61.00; MaxLon =  21.00; MaxLat = 65.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-sweden-lapland';       Region = 'Sweden';                       Name = 'Swedish Lapland';                           MinLon =  14.00; MinLat = 64.50; MaxLon =  24.50; MaxLat = 69.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-finland-south';        Region = 'Finland';                      Name = 'Helsinki, Turku and the lakes';             MinLon =  19.00; MinLat = 59.50; MaxLon =  31.60; MaxLat = 63.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-finland-north';        Region = 'Finland';                      Name = 'Oulu and Finnish Lapland';                  MinLon =  20.00; MinLat = 63.00; MaxLon =  31.60; MaxLat = 70.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-poland-north';         Region = 'Poland';                       Name = 'Gdansk and the Baltic coast';               MinLon =  13.90; MinLat = 52.00; MaxLon =  24.50; MaxLat = 55.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-poland-central';       Region = 'Poland';                       Name = 'Warsaw, Poznan and Lodz';                   MinLon =  13.90; MinLat = 50.80; MaxLon =  24.50; MaxLat = 52.60 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-poland-southwest';     Region = 'Poland';                       Name = 'Wroclaw and Silesia';                       MinLon =  13.90; MinLat = 48.90; MaxLon =  19.50; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-poland-southeast';     Region = 'Poland';                       Name = 'Krakow, Lublin and Rzeszow';                MinLon =  18.50; MinLat = 48.90; MaxLon =  24.50; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-baltics';              Region = 'The Baltic states';            Name = 'Estonia, Latvia and Lithuania';             MinLon =  20.50; MinLat = 53.80; MaxLon =  28.40; MaxLat = 59.90 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-czechia-west';         Region = 'Czechia';                      Name = 'Prague and Bohemia';                        MinLon =  12.00; MinLat = 48.40; MaxLon =  15.60; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-czechia-east';         Region = 'Czechia';                      Name = 'Brno, Ostrava and Moravia';                 MinLon =  15.00; MinLat = 48.40; MaxLon =  19.10; MaxLat = 51.20 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-slovakia';             Region = 'Slovakia';                     Name = 'Slovakia';                                  MinLon =  16.60; MinLat = 47.40; MaxLon =  22.80; MaxLat = 49.80 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-hungary';              Region = 'Hungary';                      Name = 'Hungary';                                   MinLon =  15.80; MinLat = 45.60; MaxLon =  23.00; MaxLat = 48.80 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-belarus';              Region = 'Belarus';                      Name = 'Belarus';                                   MinLon =  23.00; MinLat = 51.20; MaxLon =  32.90; MaxLat = 56.30 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-ukraine-northwest';    Region = 'Ukraine';                      Name = 'Kyiv, Lviv and Volyn';                      MinLon =  22.00; MinLat = 48.50; MaxLon =  32.00; MaxLat = 52.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-ukraine-southwest';    Region = 'Ukraine';                      Name = 'Odesa, Vinnytsia and the Carpathians';      MinLon =  22.00; MinLat = 44.20; MaxLon =  32.00; MaxLat = 49.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-ukraine-northeast';    Region = 'Ukraine';                      Name = 'Kharkiv, Sumy and Poltava';                 MinLon =  30.00; MinLat = 48.00; MaxLon =  40.40; MaxLat = 52.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-ukraine-southeast';    Region = 'Ukraine';                      Name = 'Dnipro, the Donbas and Crimea';             MinLon =  30.00; MinLat = 44.30; MaxLon =  40.40; MaxLat = 49.00 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-croatia';              Region = 'Croatia';                      Name = 'Slovenia, Croatia and the Dalmatian coast'; MinLon =  13.30; MinLat = 42.30; MaxLon =  19.50; MaxLat = 46.90 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-bosnia-albania';       Region = 'The western Balkans';          Name = 'Bosnia, Montenegro and Albania';            MinLon =  15.50; MinLat = 41.70; MaxLon =  21.00; MaxLat = 45.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-serbia';               Region = 'The western Balkans';          Name = 'Serbia and Montenegro';                     MinLon =  18.80; MinLat = 42.20; MaxLon =  23.50; MaxLat = 46.50 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-romania';              Region = 'Romania and Moldova';          Name = 'Romania and Moldova';                       MinLon =  20.00; MinLat = 42.60; MaxLon =  29.80; MaxLat = 48.60 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-bulgaria';             Region = 'Bulgaria and North Macedonia'; Name = 'Bulgaria and North Macedonia';              MinLon =  20.40; MinLat = 40.80; MaxLon =  29.00; MaxLat = 44.30 }
    [pscustomobject] @{ Group = 'europe'; Id = 'eu-greece';               Region = 'Greece';                       Name = 'Greece and Crete';                          MinLon =  19.30; MinLat = 34.70; MaxLon =  29.80; MaxLat = 41.80 }

    # Africa
    [pscustomobject] @{ Group = 'africa'; Id = 'af-morocco';             Region = 'Morocco';                         Name = 'Morocco and Western Sahara';     MinLon = -17.30; MinLat =  20.70; MaxLon = -0.90; MaxLat =  36.10 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-algeria-tunisia';     Region = 'Algeria and Tunisia';             Name = 'Algeria and Tunisia';            MinLon =  -2.50; MinLat =  18.90; MaxLon = 12.00; MaxLat =  37.60 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-libya';               Region = 'Libya';                           Name = 'Libya';                          MinLon =   9.30; MinLat =  19.50; MaxLon = 25.20; MaxLat =  33.20 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-egypt';               Region = 'Egypt';                           Name = 'Egypt';                          MinLon =  24.60; MinLat =  21.70; MaxLon = 37.00; MaxLat =  31.70 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-west-coast';          Region = 'West Africa';                     Name = 'Senegal to Cote d''Ivoire';      MinLon = -17.60; MinLat =   3.90; MaxLon = -2.00; MaxLat =  17.00 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-nigeria';             Region = 'West Africa';                     Name = 'Ghana, Benin and Nigeria';       MinLon =  -4.00; MinLat =   3.90; MaxLon = 15.00; MaxLat =  14.20 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-sahel-west';          Region = 'The Sahel';                       Name = 'Mauritania and Mali';            MinLon = -17.60; MinLat =  11.00; MaxLon =  4.30; MaxLat =  27.70 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-sahel-east';          Region = 'The Sahel';                       Name = 'Burkina Faso and Niger';         MinLon =   2.00; MinLat =  11.00; MaxLon = 16.20; MaxLat =  24.00 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-chad';                Region = 'The Sahel';                       Name = 'Chad';                           MinLon =  13.00; MinLat =   7.00; MaxLon = 24.50; MaxLat =  23.50 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-sudan';               Region = 'Sudan and South Sudan';           Name = 'Sudan';                          MinLon =  21.80; MinLat =   8.50; MaxLon = 39.50; MaxLat =  23.20 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-south-sudan';         Region = 'Sudan and South Sudan';           Name = 'South Sudan';                    MinLon =  23.50; MinLat =   3.40; MaxLon = 36.00; MaxLat =  12.50 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-ethiopia';            Region = 'The Horn of Africa';              Name = 'Ethiopia, Eritrea and Djibouti'; MinLon =  32.90; MinLat =   3.30; MaxLon = 48.00; MaxLat =  18.10 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-somalia';             Region = 'The Horn of Africa';              Name = 'Somalia';                        MinLon =  40.90; MinLat =  -1.70; MaxLon = 51.50; MaxLat =  12.10 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-cameroon-gabon';      Region = 'Central Africa';                  Name = 'Cameroon, Gabon and Congo';      MinLon =   7.90; MinLat =  -6.00; MaxLon = 19.00; MaxLat =  13.10 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-drc-north';           Region = 'DR Congo';                        Name = 'Northern DR Congo';              MinLon =  17.00; MinLat =  -5.00; MaxLon = 31.50; MaxLat =  11.00 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-drc-south';           Region = 'DR Congo';                        Name = 'Southern DR Congo';              MinLon =  17.00; MinLat = -13.50; MaxLon = 31.60; MaxLat =  -4.00 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-kenya-uganda';        Region = 'East Africa';                     Name = 'Kenya and Uganda';               MinLon =  28.00; MinLat =  -4.70; MaxLon = 42.00; MaxLat =   5.50 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-tanzania';            Region = 'East Africa';                     Name = 'Tanzania';                       MinLon =  29.30; MinLat = -12.00; MaxLon = 40.50; MaxLat =  -0.90 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-angola';              Region = 'Angola';                          Name = 'Angola';                         MinLon =  11.60; MinLat = -18.10; MaxLon = 24.20; MaxLat =  -4.30 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-zambia-malawi';       Region = 'Southern Africa';                 Name = 'Zambia and Malawi';              MinLon =  21.90; MinLat = -18.10; MaxLon = 36.00; MaxLat =  -8.10 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-zimbabwe-mozambique'; Region = 'Southern Africa';                 Name = 'Zimbabwe and Mozambique';        MinLon =  25.20; MinLat = -27.00; MaxLon = 41.00; MaxLat = -15.50 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-namibia-botswana';    Region = 'Southern Africa';                 Name = 'Namibia and Botswana';           MinLon =  11.60; MinLat = -29.50; MaxLon = 29.50; MaxLat = -16.90 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-south-africa';        Region = 'South Africa';                    Name = 'South Africa and Lesotho';       MinLon =  16.30; MinLat = -35.00; MaxLon = 33.10; MaxLat = -22.00 }
    [pscustomobject] @{ Group = 'africa'; Id = 'af-indian-ocean';        Region = 'Madagascar and the Indian Ocean'; Name = 'Madagascar and the Mascarenes';  MinLon =  42.50; MinLat = -26.00; MaxLon = 58.00; MaxLat = -11.30 }

    # North America
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-alaska-south';             Region = 'The United States'; Name = 'Southern Alaska and the Aleutians';          MinLon = -172.50; MinLat = 51.00; MaxLon = -140.00; MaxLat = 63.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-alaska-north';             Region = 'The United States'; Name = 'Northern Alaska';                            MinLon = -168.00; MinLat = 60.00; MaxLon = -140.00; MaxLat = 71.60 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-bc-south';          Region = 'Canada';            Name = 'Vancouver, the Okanagan and the Kootenays';  MinLon = -139.20; MinLat = 48.00; MaxLon = -114.00; MaxLat = 54.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-bc-north';          Region = 'Canada';            Name = 'Prince George, Haida Gwaii and the Peace';   MinLon = -139.20; MinLat = 52.00; MaxLon = -114.00; MaxLat = 60.20 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-yukon';             Region = 'Canada';            Name = 'Yukon and the Mackenzie';                    MinLon = -141.10; MinLat = 59.00; MaxLon = -120.00; MaxLat = 70.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-alberta-south';     Region = 'Canada';            Name = 'Calgary and southern Alberta';               MinLon = -120.50; MinLat = 48.00; MaxLon = -109.50; MaxLat = 54.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-alberta-north';     Region = 'Canada';            Name = 'Edmonton and northern Alberta';              MinLon = -120.50; MinLat = 53.50; MaxLon = -109.50; MaxLat = 60.20 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-sask-manitoba';     Region = 'Canada';            Name = 'Saskatchewan and Manitoba';                  MinLon = -110.50; MinLat = 48.00; MaxLon =  -95.00; MaxLat = 60.20 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-nwt-south';         Region = 'Canada';            Name = 'Yellowknife and Great Slave Lake';           MinLon = -125.00; MinLat = 59.00; MaxLon = -108.00; MaxLat = 66.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-nwt-north';         Region = 'Canada';            Name = 'Great Bear Lake and the Arctic coast';       MinLon = -125.00; MinLat = 64.00; MaxLon = -108.00; MaxLat = 72.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-nunavut';           Region = 'Canada';            Name = 'Western Nunavut';                            MinLon = -110.00; MinLat = 59.00; MaxLon =  -95.00; MaxLat = 72.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-ontario-southwest'; Region = 'Canada';            Name = 'Southwestern Ontario';                       MinLon =  -95.50; MinLat = 41.60; MaxLon =  -79.00; MaxLat = 47.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-ontario-east';      Region = 'Canada';            Name = 'Toronto, Ottawa and the St Lawrence';        MinLon =  -80.00; MinLat = 43.00; MaxLon =  -74.30; MaxLat = 48.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-ontario-northwest'; Region = 'Canada';            Name = 'Thunder Bay and Kenora';                     MinLon =  -95.50; MinLat = 46.50; MaxLon =  -85.00; MaxLat = 57.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-ontario-northeast'; Region = 'Canada';            Name = 'Timmins and James Bay';                      MinLon =  -86.00; MinLat = 46.50; MaxLon =  -74.30; MaxLat = 57.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-quebec-south';      Region = 'Canada';            Name = 'Montreal and the Eastern Townships';         MinLon =  -80.00; MinLat = 44.90; MaxLon =  -70.00; MaxLat = 48.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-quebec-east';       Region = 'Canada';            Name = 'Quebec City, Saguenay and Gaspe';            MinLon =  -71.00; MinLat = 45.50; MaxLon =  -56.00; MaxLat = 50.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-quebec-north';      Region = 'Canada';            Name = 'Nord-du-Quebec and James Bay';               MinLon =  -80.00; MinLat = 48.30; MaxLon =  -68.00; MaxLat = 63.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-labrador';          Region = 'Canada';            Name = 'Labrador and the Cote-Nord';                 MinLon =  -69.00; MinLat = 49.50; MaxLon =  -56.00; MaxLat = 63.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-atlantic';          Region = 'Canada';            Name = 'The Maritimes and Newfoundland';             MinLon =  -69.50; MinLat = 43.20; MaxLon =  -52.30; MaxLat = 52.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-arctic-west';       Region = 'Canada';            Name = 'Banks Island and Amundsen Gulf';             MinLon = -128.00; MinLat = 66.00; MaxLon = -108.00; MaxLat = 73.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-arctic-northwest';  Region = 'Canada';            Name = 'Melville and Prince Patrick islands';        MinLon = -128.00; MinLat = 71.50; MaxLon = -108.00; MaxLat = 83.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-arctic-central';    Region = 'Canada';            Name = 'Victoria Island and Boothia';                MinLon = -110.00; MinLat = 66.00; MaxLon =  -92.00; MaxLat = 83.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-arctic-southeast';  Region = 'Canada';            Name = 'Baffin Island and Foxe Basin';               MinLon =  -95.00; MinLat = 66.00; MaxLon =  -60.00; MaxLat = 74.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-canada-arctic-northeast';  Region = 'Canada';            Name = 'Ellesmere and Devon islands';                MinLon =  -95.00; MinLat = 73.00; MaxLon =  -60.00; MaxLat = 83.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-greenland';                Region = 'Greenland';         Name = 'Greenland';                                  MinLon =  -73.50; MinLat = 59.50; MaxLon =  -11.00; MaxLat = 83.80 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-pacific-northwest';     Region = 'The United States'; Name = 'Washington, Oregon and Idaho';               MinLon = -125.10; MinLat = 41.90; MaxLon = -110.90; MaxLat = 49.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-california';            Region = 'The United States'; Name = 'California and Nevada';                      MinLon = -124.60; MinLat = 32.40; MaxLon = -114.00; MaxLat = 42.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-arizona';               Region = 'The United States'; Name = 'Arizona and Nevada';                         MinLon = -120.10; MinLat = 31.20; MaxLon = -109.00; MaxLat = 42.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-new-mexico';            Region = 'The United States'; Name = 'Utah, Colorado and New Mexico';              MinLon = -114.50; MinLat = 31.20; MaxLon = -102.90; MaxLat = 42.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-mountain';              Region = 'The United States'; Name = 'Montana, Wyoming and Colorado';              MinLon = -116.20; MinLat = 36.90; MaxLon = -102.00; MaxLat = 49.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-plains';                Region = 'The United States'; Name = 'The Dakotas, Nebraska and Kansas';           MinLon = -104.10; MinLat = 36.90; MaxLon =  -93.50; MaxLat = 49.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-texas';                 Region = 'The United States'; Name = 'Texas and Oklahoma';                         MinLon = -107.00; MinLat = 25.80; MaxLon =  -93.50; MaxLat = 37.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-great-lakes';           Region = 'The United States'; Name = 'Michigan, Chicago and Wisconsin';            MinLon =  -92.00; MinLat = 40.80; MaxLon =  -82.40; MaxLat = 49.40 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-upper-midwest';         Region = 'The United States'; Name = 'Minnesota and the upper Mississippi';        MinLon =  -97.30; MinLat = 42.50; MaxLon =  -87.00; MaxLat = 49.40 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-missouri';              Region = 'The United States'; Name = 'Missouri, Kansas City and southern Iowa';    MinLon =  -97.30; MinLat = 36.00; MaxLon =  -88.50; MaxLat = 42.80 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-illinois';              Region = 'The United States'; Name = 'Illinois, Indiana and western Kentucky';     MinLon =  -90.00; MinLat = 36.00; MaxLon =  -84.00; MaxLat = 42.80 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-gulf';                  Region = 'The United States'; Name = 'Louisiana, Mississippi and Arkansas';        MinLon =  -94.70; MinLat = 28.90; MaxLon =  -86.00; MaxLat = 36.00 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-tennessee';             Region = 'The United States'; Name = 'Tennessee, Kentucky and northern Alabama';   MinLon =  -90.00; MinLat = 32.50; MaxLon =  -80.80; MaxLat = 37.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-florida';               Region = 'The United States'; Name = 'Florida and the Gulf coast';                 MinLon =  -88.50; MinLat = 24.40; MaxLon =  -78.50; MaxLat = 31.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-carolinas';             Region = 'The United States'; Name = 'Atlanta and the Carolinas';                  MinLon =  -85.00; MinLat = 27.60; MaxLon =  -75.40; MaxLat = 36.70 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-ohio';                  Region = 'The United States'; Name = 'Ohio, West Virginia and Kentucky';           MinLon =  -84.90; MinLat = 36.50; MaxLon =  -79.50; MaxLat = 42.60 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-virginia';              Region = 'The United States'; Name = 'Virginia, Maryland and Pennsylvania';        MinLon =  -81.00; MinLat = 36.50; MaxLon =  -73.80; MaxLat = 42.60 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-new-york';              Region = 'The United States'; Name = 'New York state and upstate';                 MinLon =  -80.60; MinLat = 40.40; MaxLon =  -73.00; MaxLat = 45.10 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-new-england';           Region = 'The United States'; Name = 'New England and Maine';                      MinLon =  -74.00; MinLat = 38.80; MaxLon =  -66.80; MaxLat = 47.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-us-nyc';                   Region = 'The United States'; Name = 'New York City, New Jersey and Philadelphia'; MinLon =  -76.50; MinLat = 38.80; MaxLon =  -71.50; MaxLat = 41.80 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-hawaii';                   Region = 'The United States'; Name = 'Hawaii';                                     MinLon = -160.60; MinLat = 18.60; MaxLon = -154.60; MaxLat = 22.40 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-mexico-north';             Region = 'Mexico';            Name = 'Northern Mexico';                            MinLon = -117.20; MinLat = 26.00; MaxLon =  -97.00; MaxLat = 32.80 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-mexico-central';           Region = 'Mexico';            Name = 'Central Mexico';                             MinLon = -106.00; MinLat = 17.50; MaxLon =  -96.00; MaxLat = 26.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-mexico-south';             Region = 'Mexico';            Name = 'Southern Mexico and Yucatan';                MinLon =  -99.00; MinLat = 14.30; MaxLon =  -86.60; MaxLat = 22.50 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-central-america';          Region = 'Central America';   Name = 'Guatemala to Panama';                        MinLon =  -92.50; MinLat =  7.00; MaxLon =  -77.00; MaxLat = 18.60 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-caribbean-west';           Region = 'The Caribbean';     Name = 'Cuba, Hispaniola and the Bahamas';           MinLon =  -85.50; MinLat = 17.50; MaxLon =  -68.00; MaxLat = 27.60 }
    [pscustomobject] @{ Group = 'north-america'; Id = 'na-caribbean-east';           Region = 'The Caribbean';     Name = 'Puerto Rico and the Antilles';               MinLon =  -69.00; MinLat = 10.00; MaxLon =  -58.90; MaxLat = 19.50 }

    # South America
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-colombia';            Region = 'Colombia';         Name = 'Colombia';                           MinLon = -79.10; MinLat =  -4.30; MaxLon = -66.80; MaxLat =  13.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-venezuela';           Region = 'Venezuela';        Name = 'Venezuela';                          MinLon = -73.40; MinLat =   0.60; MaxLon = -59.80; MaxLat =  12.70 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-guianas';             Region = 'The Guianas';      Name = 'Guyana, Suriname, French Guiana';    MinLon = -61.50; MinLat =   1.00; MaxLon = -51.50; MaxLat =   8.70 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-ecuador';             Region = 'Ecuador and Peru'; Name = 'Ecuador and northern Peru';          MinLon = -81.40; MinLat =  -9.50; MaxLon = -73.00; MaxLat =   1.50 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-peru';                Region = 'Ecuador and Peru'; Name = 'Southern Peru';                      MinLon = -78.00; MinLat = -18.40; MaxLon = -68.60; MaxLat =  -8.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-bolivia';             Region = 'Bolivia';          Name = 'Bolivia';                            MinLon = -69.70; MinLat = -23.00; MaxLon = -57.40; MaxLat =  -9.60 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-amazon-west';  Region = 'Brazil';           Name = 'Western Amazonia';                   MinLon = -74.50; MinLat = -11.50; MaxLon = -58.00; MaxLat =   5.30 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-amazon-east';  Region = 'Brazil';           Name = 'Para and Amapa';                     MinLon = -58.50; MinLat = -11.50; MaxLon = -43.00; MaxLat =   5.60 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-northeast';    Region = 'Brazil';           Name = 'The Brazilian Northeast';            MinLon = -47.50; MinLat = -18.50; MaxLon = -34.00; MaxLat =  -1.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-central';      Region = 'Brazil';           Name = 'Brasilia and Mato Grosso';           MinLon = -61.50; MinLat = -24.50; MaxLon = -45.00; MaxLat =  -9.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-sao-paulo';    Region = 'Brazil';           Name = 'Sao Paulo and Parana';               MinLon = -53.50; MinLat = -25.50; MaxLon = -44.00; MaxLat = -19.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-rio-minas';    Region = 'Brazil';           Name = 'Rio de Janeiro and Minas Gerais';    MinLon = -47.50; MinLat = -25.50; MaxLon = -38.50; MaxLat = -13.50 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-brazil-south';        Region = 'Brazil';           Name = 'Southern Brazil';                    MinLon = -58.00; MinLat = -34.00; MaxLon = -47.00; MaxLat = -22.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-paraguay';            Region = 'Paraguay';         Name = 'Paraguay';                           MinLon = -62.70; MinLat = -27.70; MaxLon = -54.20; MaxLat = -19.20 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-argentina-northwest'; Region = 'Argentina';        Name = 'Mendoza, Cordoba and the northwest'; MinLon = -73.60; MinLat = -35.60; MaxLon = -62.00; MaxLat = -21.50 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-argentina-east';      Region = 'Argentina';        Name = 'Buenos Aires and Uruguay';           MinLon = -64.00; MinLat = -35.60; MaxLon = -53.00; MaxLat = -25.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-argentina-south';     Region = 'Argentina';        Name = 'Patagonia and Tierra del Fuego';     MinLon = -74.00; MinLat = -56.00; MaxLon = -56.00; MaxLat = -35.00 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-chile-north';         Region = 'Chile';            Name = 'Northern Chile';                     MinLon = -71.00; MinLat = -32.50; MaxLon = -66.50; MaxLat = -17.40 }
    [pscustomobject] @{ Group = 'south-america'; Id = 'sa-chile-central';       Region = 'Chile';            Name = 'Central Chile and the Lakes';        MinLon = -75.80; MinLat = -46.00; MaxLon = -69.50; MaxLat = -32.00 }
)

$culture = [cultureinfo]::InvariantCulture
function Format-Coord([double] $value) { $value.ToString('0.00##', $culture) }

$allPackIds = @($packs.Id)

# The whole table, captured before -Group and -Only cut it down. A run that builds one continent
# still rewrites the entries it kept from the last one, and those need their region from here - the
# alternative is a catalogue where a pack's country depends on which run last touched it.
$regionById = @{}
foreach ($pack in $packs) { $regionById[$pack.Id] = $pack.Region }

if ($Only) {
    # -Only names packs outright, so it wins over -Group rather than intersecting with it.
    $unknown = $Only | Where-Object { $allPackIds -notcontains $_ }
    if ($unknown) {
        throw "Unknown pack id(s): $($unknown -join ', '). Run -ListPacks -Group all to see the $($allPackIds.Count) known ids."
    }
    $packs = @($packs | Where-Object { $Only -contains $_.Id })
}
elseif ($Group -eq 'world') {
    $packs = @($packs | Where-Object { $_.Group -ne 'au' })
}
elseif ($Group -ne 'all') {
    $packs = @($packs | Where-Object { $_.Group -eq $Group })
}

if ($ListPacks) {
    $packs | Format-Table -AutoSize Group, Region, Id, Name,
        @{ Label = 'BBox'; Expression = {
            '{0},{1},{2},{3}' -f (Format-Coord $_.MinLon), (Format-Coord $_.MinLat),
                (Format-Coord $_.MaxLon), (Format-Coord $_.MaxLat) } }
    Write-Host "$($packs.Count) pack(s)"
    return
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
        # The country this pack is part of, and the first dropdown on the settings screen. Published
        # rather than worked out on the phone: the app would have to carry a table of which slug
        # belongs to which country, and it would be this one, a release behind.
        region = $pack.Region
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

$retired = New-Object System.Collections.Generic.List[string]

if (Test-Path -LiteralPath $cataloguePath) {
    $builtIds = @($catalogue | ForEach-Object { $_.id })
    $loaded = Get-Content -LiteralPath $cataloguePath -Raw | ConvertFrom-Json
    foreach ($entry in @($loaded)) {
        if (-not $entry -or ($builtIds -contains $entry.id)) { continue }
        # The table is the source of truth: an id it no longer lists was re-cut into smaller packs,
        # and leaving it in the catalogue would keep offering riders a pack nobody rebuilds.
        if ($allPackIds -notcontains $entry.id) { $retired.Add($entry.id); continue }
        # Stamped from the table rather than carried over, so a catalogue written before regions
        # existed - or by a run that predates a pack moving country - comes out of this one right.
        # -Force because the property is already there on everything but the first such run, and
        # appended rather than placed because the entry is otherwise passed through untouched.
        $entry | Add-Member -NotePropertyName region -NotePropertyValue $regionById[$entry.id] -Force
        $catalogue.Add($entry)
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

if ($retired.Count -gt 0) {
    Write-Host ''
    Write-Host "Dropped $($retired.Count) retired pack(s) from the catalogue: $($retired -join ', ')" -ForegroundColor Yellow
    Write-Host 'Their .pmtiles files are still on disk and can be deleted once the host is updated.'
}

# The boxes are sized off an estimate, so let a real build say which ones the estimate got wrong.
$oversize = @($ordered | Where-Object { $_.sizeBytes -gt $OversizeBytes })
if ($oversize.Count -gt 0) {
    $names = $oversize | ForEach-Object { "$($_.id) $([math]::Round($_.sizeBytes / 1GB, 1)) GB" }
    Write-Warning "Over the $([math]::Round($OversizeBytes / 1GB, 1)) GB target: $($names -join ', '). Split these in the pack table and rebuild with -Force."
}

if ($failed.Count -gt 0) {
    Write-Warning "Failed: $($failed -join ', '). Re-run with -Only <ids> to retry."
    exit 1
}
