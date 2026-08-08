# Sets the DLR server's configuration as environment variables on its IIS application pool.
#
# Run ELEVATED. It writes to the machine-level applicationHost.config
# (C:\Windows\System32\inetsrv\config\applicationHost.config) through the IIS configuration API —
# which is the only applicationHost.config IIS reads. A file of that name anywhere else is inert.

#Requires -RunAsAdministrator

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module WebAdministration

# ---------------------------------------------------------------------------
# Which application pool
# ---------------------------------------------------------------------------
# The event log said '/LM/W3SVC/3/ROOT', so the site's ID is 3. Resolved rather than hard-coded,
# because setting variables on a pool the site does not use fails exactly as silently as the
# stray config file did.

$site = Get-Website | Where-Object { $_.Name -eq 'DLR' }

if (-not $site) {
	throw "No site with Name 'DLR'. Run 'Get-Website' and pick the right one."
}

$pool = $site.applicationPool

Write-Host "Site '$($site.Name)' -> application pool '$pool' (physical path $($site.PhysicalPath))"

$filter = "system.applicationHost/applicationPools/add[@name='$pool']/environmentVariables"

function Set-DlrEnv($name, $value) {
	# Remove first so re-running this is idempotent rather than an error.
	Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter `
		-Name '.' -AtElement @{name = $name} -ErrorAction SilentlyContinue

	Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter `
		-Name '.' -Value @{name = $name; value = $value}

	Write-Host "  set $name"
}

# ---------------------------------------------------------------------------
# The settings
# ---------------------------------------------------------------------------

Set-DlrEnv 'ASPNETCORE_ENVIRONMENT'  'Production'
Set-DlrEnv 'ConnectionStrings__Dlr'  'Host=localhost;Port=5432;Database=dlr;Username=dlr;Password=PASSWORD_GOES_HERE;Maximum Pool Size=20'
Set-DlrEnv 'Auth__SigningKey'        '<48 random bytes, base64>'
Set-DlrEnv 'Blobs__RootPath'         'C:\ProgramData\DLR\blobs'
Set-DlrEnv 'Links__BaseUrl'          'https://dlr.securehub.net'
Set-DlrEnv 'About__SourceUrl'        'https://github.com/mctainsh/dlr'

# UserName and FromAddress, not User and From — these bind by property name on EmailOptions, so a
# near-miss binds to nothing and stays silent until the first confirmation email does not arrive.
Set-DlrEnv 'Email__Host'             'smtp.zoho.com.au'
Set-DlrEnv 'Email__Port'             '587'
Set-DlrEnv 'Email__UserName'         'no-reply@dumbluckrides.example'
Set-DlrEnv 'Email__Password'         '<app-specific password>'
Set-DlrEnv 'Email__FromAddress'      'no-reply@dumbluckrides.example'

# Optional — the display name on the From header. Defaults to 'Dumb Luck Routes'.
Set-DlrEnv 'Email__FromName'         'Dumb Luck Routes'

Set-DlrEnv 'Maintenance__DryRun'     'true'
Set-DlrEnv 'Maintenance__AlertEmail' 'you@example.com'

# The Maps__MapKit__* lines from the original file are deliberately NOT here. With placeholder
# values the options object reports itself configured, so MapKitSigningKey.Resolve() calls
# ImportFromPem on the text '…' and throws CryptographicException on the first map load — a
# harder failure than having no key at all. Unset, §4.5's map states it has no credentials,
# which is a supported state. Add them when the real .p8 exists.

# ---------------------------------------------------------------------------
# The blob directory
# ---------------------------------------------------------------------------
# /healthz reports the server unhealthy while this is missing or unwritable, and uploads fail
# with a permission error nobody connects back to this line.

if (-not (Test-Path 'C:\ProgramData\DLR\blobs')) {
	New-Item -ItemType Directory -Path 'C:\ProgramData\DLR\blobs' | Out-Null
}

icacls 'C:\ProgramData\DLR\blobs' /grant "IIS AppPool\${pool}:(OI)(CI)(M)" | Out-Null
Write-Host "  granted 'IIS AppPool\$pool' modify on C:\ProgramData\DLR\blobs"

# ---------------------------------------------------------------------------
# Data Protection, and keeping the process up
# ---------------------------------------------------------------------------
# loadUserProfile/setProfileEnvironment are what give the pool identity a registry hive to keep
# the Data Protection key ring in. Without them the keys are in memory and lost on every recycle,
# which invalidates every password-reset link already sitting in somebody's inbox.

Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.loadUserProfile     -Value $true
Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.setProfileEnvironment -Value $true
Set-ItemProperty "IIS:\AppPools\$pool" -Name managedRuntimeVersion            -Value ''
Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode                        -Value 'AlwaysRunning'
Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.idleTimeout         -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$pool" -Name recycling.periodicRestart.time   -Value ([TimeSpan]::Zero)

# ---------------------------------------------------------------------------
# Apply and show what took
# ---------------------------------------------------------------------------

Restart-WebAppPool $pool
Write-Host "`nRestarted '$pool'. Variables now on the pool:`n"

(Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter).Collection |
	Select-Object name, value | Format-Table -AutoSize
