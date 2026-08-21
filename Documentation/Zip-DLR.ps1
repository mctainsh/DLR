<#
.SYNOPSIS
	Zips the contents of the deployed DLR web site into C:\Temp.

.DESCRIPTION
	The archive is named "<prefix><file version>.zip" where the version is the
	FileVersion of DLR.Server.dll inside the source folder, e.g. www.DRL-8.0.0.25.zip.
	The zip contains the *contents* of the source folder (no top-level folder entry).

.EXAMPLE
	.\Zip-DLR.ps1

.EXAMPLE
	.\Zip-DLR.ps1 -SourcePath 'C:\inetpub\DLR' -DestinationPath 'D:\Builds'
#>
[CmdletBinding()]
param(
	[string]$SourcePath      = 'C:\inetpub\DLR',
	[string]$DestinationPath = 'C:\Temp',
	[string]$NamePrefix      = 'www.DRL-',
	[switch]$Force,
	[switch]$NoOpen
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) { throw "Source folder not found: $SourcePath" }

$dll = Join-Path $SourcePath 'DLR.Server.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Cannot determine version - file not found: $dll" }

$version = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw "DLR.Server.dll has no FileVersion: $dll" }
$version = $version.Trim()

if (-not (Test-Path -LiteralPath $DestinationPath -PathType Container)) {
	New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
}

$zipPath = Join-Path $DestinationPath ("{0}{1}.zip" -f $NamePrefix, $version)

if (Test-Path -LiteralPath $zipPath) {
	if (-not $Force) {
		$answer = Read-Host "$zipPath already exists. Overwrite? [y/N]"
		if ($answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; return }
	}
	Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "Source      : $SourcePath"
Write-Host "Version     : $version"
Write-Host "Destination : $zipPath"

Add-Type -AssemblyName System.IO.Compression.FileSystem

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
	[System.IO.Compression.ZipFile]::CreateFromDirectory(
		(Resolve-Path -LiteralPath $SourcePath).ProviderPath,
		$zipPath,
		[System.IO.Compression.CompressionLevel]::Optimal,
		$false)   # $false = do not include the source folder name in the archive
}
catch {
	if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }
	throw
}
$sw.Stop()

$zipItem = Get-Item -LiteralPath $zipPath
Write-Host ("Created {0} ({1:N2} MB) in {2:N1}s" -f $zipItem.FullName, ($zipItem.Length / 1MB), $sw.Elapsed.TotalSeconds) -ForegroundColor Green

# Open the destination folder in Explorer with the new zip selected
if (-not $NoOpen) { Start-Process -FilePath 'explorer.exe' -ArgumentList ('/select,"{0}"' -f $zipItem.FullName) }

$zipItem.FullName
