# Setup — Windows Server under IIS

Running `DLR.Server` on a Windows Server box behind IIS, with no Docker anywhere.

`deploy/` describes the supported deployment: four containers, Caddy at the edge, one VPS (§9).
This note is the same server with IIS doing Caddy's job and an application pool doing Compose's.
Nothing in the application changes — every setting below is the same key the compose file sets,
because configuration is the only seam the host is allowed to reach the server through (§14.3).

> A `§` reference below points at `Documentation/design-outline.md`, as everywhere else in this
> project. References to *this* note are written as "step 4".

What IIS has to take over, and where each one is covered:

| Caddy / Compose did | On IIS it is |
|---|---|
| TLS, HTTP/3, HSTS | A site binding and a certificate — steps 6 and 12 |
| `X-Forwarded-For` for §7.8 | **Nothing** — see step 9, the item most likely to be got wrong |
| The security headers | `web.config` — step 12 |
| Stripping `access_token` from the access log | IIS logging fields — step 13, do not skip it |
| WebSocket for the ride hub | An optional Windows feature — step 2 |
| `migrate` container | A one-shot `--migrate` run — step 10 |
| The `blobs` volume | A directory and an ACL — step 5 |
| Secrets in `.env` | Application-pool environment variables — step 7 |
| The nightly backup container | A scheduled task — step 14 |

---

## 1. Before you start

Decide these, because several steps below need them and changing one later is a re-run of the lot:

- The public hostname, and a certificate for it in the machine's certificate store.
- Where PostgreSQL lives — on this box, or elsewhere on the network.
- Where the blob volume lives (`C:\ProgramData\DLR\blobs` in the examples). This grows without bound until
  quotas and the nightly sweep hold it; put it on a data volume, not on `C:`.

Windows Server 2022 or 2025. Nothing here needs a GUI beyond the certificate import, so Server
Core works if that is what you have.

## 2. Install the prerequisites

Run in an elevated PowerShell.

**IIS, with the WebSocket feature.** The ride hub is a websocket that carries a two-hour ride
(§5.3, §7.6). Without this feature the site starts, serves every page, and fails only the hub —
which presents to a rider as a map that never updates.

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-Mgmt-Console
```

**The ASP.NET Core 10 Hosting Bundle.** This is the runtime *and* the ASP.NET Core Module (ANCM)
that lets IIS host the process. The plain .NET runtime is not enough — without the bundle IIS
answers `500.19` or `500.21` and nothing in the application log explains why.

Download `dotnet-hosting-10.0.x-win.exe` from <https://dotnet.microsoft.com/download/dotnet/10.0>,
then:

```powershell
Start-Process .\dotnet-hosting-10.0.10-win.exe -ArgumentList '/quiet','/norestart' -Wait

# The installer does not always restart WAS, and until it does IIS has not loaded the new module.
net stop was /y
net start w3svc
```

**The Visual C++ 2015–2022 redistributable (x64).** SkiaSharp's native library needs it, and the
only thing that touches SkiaSharp is photo ingest (§16.4) — so a missing redistributable is a
server that works perfectly until the first photo upload, then throws a `DllNotFoundException`
nobody connects to this line. This is the Windows counterpart of the `libfontconfig1` package the
Dockerfile installs.

```powershell
winget install --id Microsoft.VCRedist.2015+.x64 --silent
```

**PostgreSQL 17**, if it is going on this box. EnterpriseDB's installer from
<https://www.postgresql.org/download/windows/>, default port 5432.

## 3. Create the database

From "SQL Shell (psql)" as `postgres`:

```sql
CREATE ROLE dlr WITH LOGIN PASSWORD 'choose-a-real-one';
CREATE DATABASE dlr OWNER dlr;
```

Data checksums are worth having and can **only** be set at `initdb` time — after the first start
they are a dump-and-restore. The compose file passes `--data-checksums`; the Windows installer does
not offer it, so if you want them, run `initdb` yourself before creating the cluster. If you skip
it, skip it knowingly rather than by accident.

If PostgreSQL is on another machine, it needs a `pg_hba.conf` entry for this server's address and
`listen_addresses` set — and it should not be reachable from anywhere else. A published PostgreSQL
port is scanned within the hour.

## 4. Publish

**Build from a clean, pushed clone.** The parent `Directory.Build.targets` appends `.dirty` to
`SourceRevisionId` when git reports uncommitted changes, and `GET /api/v1/about` reads that back
(§14.6.2). It is visible to end users, and stating which commit a server is running is an AGPL §13
obligation rather than a nicety. Publish from a working tree with no local edits and no missing
`.git` directory.

On a machine with the SDK (`global.json` pins **10.0.100**):

```powershell
dotnet publish BlazorDLR\BlazorDLR.Web\BlazorDLR.Web.csproj `
	--configuration Release `
	--runtime win-x64 `
	--self-contained false `
	--output C:\publish\dlr
```

`--self-contained false` because the Hosting Bundle already put the runtime on the server. The
output contains `DLR.Server.dll`, the WASM client under `wwwroot\_framework`, and a `web.config`
that MSBuild generates with the ANCM handler already wired.

Copy the folder to the server — `C:\inetpub\dlr` in the examples below.

## 5. Create the blob directory

Uploaded photos and track files go here (§9.1). It is not optional and it is not defaulted: the
server refuses to start unless `Blobs:RootPath` is set *and* absolute, because a relative value
silently writes uploads into whatever the working directory happened to be.

```powershell
New-Item -ItemType Directory D:\dlr\blobs -Force

# The application-pool identity created in the next step. It needs to write here and nowhere else.
icacls C:\ProgramData\DLR\blobs /grant "IIS AppPool\DLR:(OI)(CI)(M)"
```

The site folder is the opposite case — the application pool should be able to *read* it and not
write to it. The publish output's inherited ACLs are usually already that; if you tightened them,
grant `IIS AppPool\DLR` read and execute on `C:\inetpub\dlr`.

## 6. Create the application pool and the site

```powershell
Import-Module WebAdministration

New-WebAppPool -Name DLR

# No managed code: this is not an ASP.NET application, and IIS loading the CLR into the worker
# process for a .NET Core app is wasted work at best.
Set-ItemProperty IIS:\AppPools\DLR -Name managedRuntimeVersion -Value ''

# The three that keep the process up. The server holds live ride state in memory — the position
# cache, the reaction dirty set, the flush timer (§17.4) — and an idle timeout or a nightly
# recycle throws that away mid-ride. PositionCacheRehydrator recovers the positions on the way
# back up; nothing recovers the seconds the riders spent looking at a stale map.
Set-ItemProperty IIS:\AppPools\DLR -Name startMode -Value AlwaysRunning
Set-ItemProperty IIS:\AppPools\DLR -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\AppPools\DLR -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)

# Data Protection. See step 8 — this pair is load-bearing, not tidiness.
Set-ItemProperty IIS:\AppPools\DLR -Name processModel.loadUserProfile -Value $true
Set-ItemProperty IIS:\AppPools\DLR -Name processModel.setProfileEnvironment -Value $true

New-Website -Name DLR -ApplicationPool DLR -PhysicalPath C:\inetpub\dlr -Port 80 `
	-HostHeader dumbluckrides.example
```

Then the HTTPS binding, against a certificate already in `Cert:\LocalMachine\My`:

```powershell
New-WebBinding -Name DLR -Protocol https -Port 443 -HostHeader dumbluckrides.example -SslFlags 1

$thumb = (Get-ChildItem Cert:\LocalMachine\My |
	Where-Object Subject -like '*dumbluckrides.example*').Thumbprint

New-Item "IIS:\SslBindings\!443!dumbluckrides.example" -Value $thumb -SSLFlags 1
```

IIS does not renew certificates for you the way Caddy does. If the certificate is from Let's
Encrypt, install **win-acme** and let it manage the binding — an expired certificate is the most
common way a working deployment stops working.

## 7. Settings and secrets

Every setting reaches the server as an environment variable with a **double** underscore between
levels. A single underscore is a different key and binds to nothing, silently.

**Not `appsettings.json`.** The signing key check refuses to start if the value came from a file
inside the content root (§7.4), and it is right to: a key that reaches git history is fixed by
rotating it, not by deleting the line. The connection string is under the same rule by convention.

Set them on the application pool, in `applicationHost.config`, rather than in the site's
`web.config`. `web.config` sits in the publish folder — it is overwritten by the next deploy, it is
readable by anything that can read the site directory, and it is one careless copy away from a
repository.

> **`applicationHost.config` is the machine-level file at
> `C:\Windows\System32\inetsrv\config\applicationHost.config`, and it is the only one IIS reads.**
> Do not create a file of that name in the site folder — IIS ignores it completely, so the
> settings never reach the worker process and the server fails at startup saying the signing key
> is not set. The script below edits the real file through the IIS configuration API; **run it in
> an elevated PowerShell**, do not save it anywhere as `applicationHost.config`.

```powershell
function Set-DlrEnv($name, $value) {
	$filter = "system.applicationHost/applicationPools/add[@name='DLR']/environmentVariables"

	# Remove first so re-running this is idempotent rather than an error.
	Remove-WebConfigurationProperty -PSPath MACHINE/WEBROOT/APPHOST -Filter $filter `
		-Name '.' -AtElement @{name=$name} -ErrorAction SilentlyContinue

	Add-WebConfigurationProperty -PSPath MACHINE/WEBROOT/APPHOST -Filter $filter `
		-Name '.' -Value @{name=$name; value=$value}
}

Set-DlrEnv 'ASPNETCORE_ENVIRONMENT'  'Production'
Set-DlrEnv 'ConnectionStrings__Dlr'  'Host=localhost;Database=dlr;Username=dlr;Password=…;Maximum Pool Size=20'
Set-DlrEnv 'Auth__SigningKey'        '<48 random bytes, base64>'
Set-DlrEnv 'Blobs__RootPath'         'C:\ProgramData\DLR\blobs'
Set-DlrEnv 'Links__BaseUrl'          'https://dumbluckrides.example'
Set-DlrEnv 'About__SourceUrl'        'https://github.com/mctainsh/dlr'

Set-DlrEnv 'Email__Host'             'smtp.zoho.com.au'
Set-DlrEnv 'Email__Port'             '587'
Set-DlrEnv 'Email__UserName'         'no-reply@dumbluckrides.example'
Set-DlrEnv 'Email__Password'         '<app-specific password>'
Set-DlrEnv 'Email__FromAddress'      'no-reply@dumbluckrides.example'

# Optional — the display name on the From header. Defaults to 'Dumb Luck Routes'.
Set-DlrEnv 'Email__FromName'         'Dumb Luck Routes'

Set-DlrEnv 'Maintenance__DryRun'     'true'
Set-DlrEnv 'Maintenance__AlertEmail' 'you@example.com'

# Optional (§4.5). Without them the map says it has no credentials, which is a supported state.
# Set all four or none — placeholder values are worse than nothing, because the options object
# then reports itself configured and MapKitSigningKey.Resolve() throws CryptographicException on
# the first map load rather than the map stating it has no key.
Set-DlrEnv 'Maps__MapKit__TeamId'         'A1B2C3D4E5'
Set-DlrEnv 'Maps__MapKit__KeyId'          'F6G7H8I9J0'
Set-DlrEnv 'Maps__MapKit__PrivateKeyPem'  (Get-Content C:\keys\AuthKey_F6G7H8I9J0.p8 -Raw)
Set-DlrEnv 'Maps__MapKit__Origin'         'https://dumbluckrides.example'
```

Read the `.p8` from wherever you are holding it, as above — the value is the key's *contents*, not
its path, and the file must not end up in the publish folder (§14.2 has it on the never-commit
list).

Generate the signing key with something that is actually random. It must be at least 32 bytes or
the server refuses to start — HS256 is only as strong as its key:

```powershell
$bytes = [byte[]]::new(48)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
[Convert]::ToBase64String($bytes)
```

**A note on the email keys.** They are `Email__UserName` and `Email__FromAddress` — not `User` and
`From`. Configuration binds by property name on `EmailOptions`, so a near-miss binds to nothing and
the failure is silent until the first confirmation email does not arrive. `Email__FromName` is
optional and defaults to *Dumb Luck Routes*.

**Restart the pool after changing any of these.** Environment variables are read when the worker
process starts and never again:

```powershell
Restart-WebAppPool DLR
```

Every setting the server understands, for reference:

| Variable | Required | Notes |
|---|---|---|
| `ConnectionStrings__Dlr` | yes | Checked before anything else, including `--migrate` |
| `Auth__SigningKey` | yes | ≥32 bytes; refused if it came from a file in the content root |
| `Blobs__RootPath` | yes | Must be **absolute** |
| `Links__BaseUrl` | yes | Where confirmation and reset links point (§7.7) |
| `About__SourceUrl` | yes | A fork owes its users *its own* source (§14.6.2) |
| `Email__*` | yes in practice | No email means no confirmation and no password reset |
| `Maintenance__DryRun` | defaults `true` | Leave it true for a week — step 15 |
| `Maintenance__AlertEmail` | — | Where the nightly summary goes |
| `ForwardedHeaders__KnownProxies__0` | **usually not** | Step 9 |
| `Maps__MapKit__*` | optional | §4.5 |
| `Health__MinimumFreeMb` | defaults 2048 | Free space below which `/healthz` fails |
| `ASPNETCORE_ENVIRONMENT` | yes | `Production` — anything else changes error handling |

## 8. Data Protection keys

`AddDataProtection()` is registered with no explicit key store, so the key ring goes wherever the
platform default puts it. Under IIS that is the application-pool identity's registry hive — which
**only exists if the user profile is loaded**, which is why step 6 sets `loadUserProfile` and
`setProfileEnvironment`.

Without them the keys are held in memory and thrown away on every recycle, and what they seal is
not incidental: the email-confirmation and password-reset tokens (§7.7) and the antiforgery token
on the cookie-to-access-token exchange (§7.5). Losing the ring invalidates every reset link
already in somebody's inbox. It presents as "the link in the email says it is invalid" a day after
a deploy, and nothing in the log names the cause.

If the pool runs as a domain account rather than `ApplicationPoolIdentity`, that account needs a
loadable profile too, or the key ring needs an explicit location and the same care over its ACL.

## 9. Forwarded headers — usually nothing to do

This is the item most likely to be got wrong, because the compose file has a value for it and the
instinct is to copy it.

With in-process hosting, IIS gives ASP.NET Core the **real client address** —
`HttpContext.Connection.RemoteIpAddress` is already the rider's, not a proxy's. `KnownProxies` is
cleared at startup so the configured list is the whole list, and an empty list means
`X-Forwarded-For` is ignored entirely. That is the correct configuration here: honouring the header
from anyone lets a caller choose their own rate-limit bucket by setting a header, which is worse
than not reading it at all because the limits then look enforced and are optional.

So: **leave `ForwardedHeaders__KnownProxies__0` unset** unless something else sits in front of IIS
— Cloudflare, an ARR farm, a load balancer. If something does, set it to that hop's address and
verify before the first public signup. §7.8's per-address rules and the registration ladder both
depend on this: get it wrong and every signup looks as if it came from the proxy, so the fourth
account ever is asked for an email address.

Verify it by registering an account from a machine on a known address and checking that the ladder
counted it against that address rather than against the proxy's.

## 10. Apply the schema

`--migrate` applies every pending migration and exits without starting Kestrel (§9). It is a
one-shot run, deliberately not a `Migrate()` call on the way up: that couples "is this server
ready" to "has the schema moved", which turns a failed migration into a crash loop.

Run it from the publish folder with the connection string in the shell's environment — the
application pool's variables do not reach an interactive process:

```powershell
$env:ConnectionStrings__Dlr = 'Host=localhost;Database=dlr;Username=dlr;Password=…'
$env:ASPNETCORE_ENVIRONMENT = 'Production'

dotnet C:\inetpub\dlr\DLR.Server.dll --migrate
```

It needs a database and nothing else — not the signing key, not the blob path — so this works on a
server that is not yet fully configured, which is the order anyone setting one up for the first
time will actually work in.

Run it **before** starting the site, and again before starting the site after every deploy that
carries a migration.

## 11. Request limits, compression, and the hub

Edit the `web.config` in the publish folder. MSBuild generated it with the ANCM handler; these are
additions to it, and they need re-applying (or including in a deployment script) after every
publish.

```xml
<system.webServer>
	<security>
		<!-- removeServerHeader is one fewer thing telling a scanner what to try. Mirrors Caddy's -Server. -->
		<requestFiltering removeServerHeader="true">
			<!--
				Track import accepts 25 MB (TrackImportOptions.MaxUploadBytes) and photos 12 MB.
				IIS's default ceiling is 30,000,000 bytes, which a 25 MB multipart body plus its
				boundaries can exceed — and IIS rejects it before the application sees it, so the
				server's own polite "Files are limited to 25 MB" never gets a chance to answer.
			-->
			<requestLimits maxAllowedContentLength="34603008" />
		</requestFiltering>
	</security>

	<!--
		The application already compresses (UseResponseCompression) because the points endpoint
		sends ~200 KB of encoded polyline for a long ride (§15.5). Letting IIS compress the same
		bytes again is CPU spent to no effect.
	-->
	<urlCompression doDynamicCompression="false" />

	<webSocket enabled="true" />
</system.webServer>
```

Also raise the ANCM request body limit if you raise the upload caps — the `aspNetCore` element
takes no size attribute, but `IISServerOptions.MaxRequestBodySize` defaults to 30 MB in the
application. At the current caps it is not in the way; if track imports ever grow past ~28 MB it
will be, and the symptom is a 413 the application did not send.

## 12. Security headers and HSTS

The Caddyfile sets these at the edge (§9). IIS must, or they are simply absent — the application
adds HSTS in production but none of the rest.

```xml
<system.webServer>
	<httpProtocol>
		<customHeaders>
			<remove name="X-Powered-By" />
			<add name="X-Content-Type-Options" value="nosniff" />
			<add name="Referrer-Policy" value="strict-origin-when-cross-origin" />
			<!--
				The map is the reason this is not simply 'none'. MapKit JS is loaded from Apple
				and OSM tiles from OpenStreetMap (§4.5); everything else is served from here.
				'wasm-unsafe-eval' is what lets the Blazor WebAssembly client start at all.
			-->
			<add name="Content-Security-Policy"
				value="default-src 'self'; img-src 'self' data: blob: https://*.tile.openstreetmap.org https://*.apple.com; script-src 'self' 'wasm-unsafe-eval' https://cdn.apple-mapkit.com; style-src 'self' 'unsafe-inline'; connect-src 'self' https://*.apple.com https://*.tile.openstreetmap.org; frame-ancestors 'none'" />
		</customHeaders>
	</httpProtocol>
</system.webServer>
```

HSTS comes from the application (`UseHsts` outside Development), so there is nothing to add — but
it only means anything if HTTP redirects to HTTPS. Add an IIS URL Rewrite rule for that, or accept
that a first visit over HTTP is served over HTTP. The refresh cookie is `__Host-` prefixed (§7.5),
which already requires HTTPS for it to be stored at all, so a plain-HTTP visit cannot sign in — it
simply fails in a way nobody will diagnose.

## 13. Turn off query strings in the IIS access log

**Do not skip this one.** A browser cannot set an `Authorization` header on a websocket, so §7.6
lifts the SignalR access token out of the query string on `/hubs/ride`. That makes any log
recording query strings a place live credentials land — for weeks, across every rolled file. The
Caddyfile filters exactly that parameter out; IIS logs `cs-uri-query` by default.

```powershell
Set-WebConfigurationProperty "/system.applicationHost/sites/site[@name='DLR']/logFile" `
	-PSPath MACHINE/WEBROOT/APPHOST -Name logExtFileFlags `
	-Value 'Date,Time,ClientIP,UserName,ServerIP,Method,UriStem,HttpStatus,Win32Status,TimeTaken,ServerPort,UserAgent,Referer,HttpSubStatus'
```

That is the default field set with `UriQuery` removed. If you would rather keep query strings for
other diagnostics, then the token has to be stripped some other way before anything writes it —
but the default arrangement writes it.

The same applies to Failed Request Tracing if you enable it while debugging: turn it off again
afterwards, and delete what it wrote.

## 14. Backups

`deploy/backup.sh` runs `pg_dump` and the blob directory into `restic`, encrypted client-side
before it leaves the machine (§9.1, §10.1). The Windows equivalent is the same two commands under
a scheduled task. `restic` has a Windows build; install it with `winget install restic.restic`.

```powershell
$env:RESTIC_REPOSITORY = 'b2:dlr-backups:/dlr'
$env:RESTIC_PASSWORD   = '<from the password manager>'
$env:B2_ACCOUNT_ID     = '…'
$env:B2_ACCOUNT_KEY    = '…'
$env:PGPASSWORD        = '…'

& 'C:\Program Files\PostgreSQL\17\bin\pg_dump.exe' `
	--host=localhost --username=dlr --format=custom --file=D:\dlr\dump\dlr.dump dlr

restic backup D:\dlr\dump C:\ProgramData\DLR\blobs
restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune
```

The database and the blobs go into the **same** snapshot on purpose: a database restored against
blobs from another night gives you tracks pointing at files that are not there.

`RESTIC_PASSWORD` belongs in a password manager, not only on the machine being backed up. Losing it
loses every backup.

**Run a restore drill.** B2's restore egress is free up to three times the stored volume, so a
drill costs nothing — which removes the only excuse. A backup you have never restored is a hope.

## 15. The three things that are not automatic

**Turn off the maintenance dry run — after a week, not before.** `Maintenance__DryRun` defaults to
`true` and gates every sweep (§7.11). Set `Maintenance__AlertEmail`, read the nightly summaries for
a week, and only then set it to `false`. It deletes accounts.

**Point something at `/healthz`.** A free uptime pinger is the whole alerting budget and it is
enough: the endpoint answers `503` when the database is unreachable, when the schema is behind, or
when free space on the blob volume drops below `Health:MinimumFreeMb`. That last one is §9's disk
alert — a full disk stops PostgreSQL *writing*, which is far worse than a failed upload.

**Verify the forwarded-header decision in step 9 before the first public signup.** It is load-bearing
for registration, not just for rate limiting.

## 16. Verify

```powershell
Invoke-RestMethod https://dumbluckrides.example/healthz
```

Read the body, not just the status:

- `migrationsApplied: false` — the `--migrate` run in step 10 did not run or did not succeed. This is
  the failure the endpoint exists for: a server whose schema is a migration behind answers every
  request correctly until one touches the column that is not there yet.
- `blobVolume.ok: false` with `freeMb: 0` — `Blobs__RootPath` points somewhere that does not exist,
  or the pool identity cannot read it. Uploads would fail a day later with a permission error
  nobody connects to this setting.
- `databaseReachable: false` — the connection string, `pg_hba.conf`, or the firewall.

Then check the things `/healthz` cannot see:

```powershell
Invoke-RestMethod https://dumbluckrides.example/api/v1/about   # commit and build time — no '.dirty'
```

Register an account and confirm the email arrives (the step 7 email keys), open a ride and confirm the map
updates (the websocket feature, step 2), and upload a photo (the VC++ redistributable, step 2).

## 17. Deploying an update

```powershell
# app_offline.htm makes ANCM shut the application down and serve this file. Without it the
# publish folder is in use and the copy fails halfway, which is worse than being offline.
Set-Content C:\inetpub\dlr\app_offline.htm '<html><body>Back shortly.</body></html>'

robocopy C:\publish\dlr C:\inetpub\dlr /MIR /XF app_offline.htm

$env:ConnectionStrings__Dlr = '…'
dotnet C:\inetpub\dlr\DLR.Server.dll --migrate

Remove-Item C:\inetpub\dlr\app_offline.htm
```

`/MIR` deletes files the new publish does not have, which is what you want for the WASM bundle —
stale `_framework` files are served happily and are the wrong version. Re-apply the `web.config`
additions from steps 11 and 12, since `/MIR` replaces the generated file.

## 18. When it does not start

IIS failures here are opaque by design; these are the ones this application actually produces.

| Symptom | Cause |
|---|---|
| `HTTP 500.19` / `500.21` | Hosting Bundle not installed, or WAS not restarted after installing it |
| `HTTP 500.30` — in-process start failure | The application threw during startup. Almost always one of the step 7 settings |
| `HTTP 500.31` — failed to load ASP.NET Core runtime | Runtime version mismatch; the bundle is older than `net10.0` |
| `HTTP 500.32` / `.33` | x86/x64 mismatch — the pool has "Enable 32-Bit Applications" on |
| Site works, map never updates | The WebSocket feature (step 2) |
| Site works, photo upload throws | The VC++ redistributable (step 2) |
| Reset links "invalid" after a recycle | Data Protection keys (step 8) |
| Rate limits behave oddly, registration asks for an email early | Forwarded headers (step 9) |

For a `500.30`, get the actual exception. The startup failures this application raises are written
to say what to do — the signing-key and blob-path messages name the setting and give the command —
but you have to be able to read them:

```xml
<aspNetCore ... stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout">
```

Create the `logs` directory and grant the pool identity write access to it, restart the pool, and
read the file. **Turn it back off afterwards** — it grows without bound and the log is inside the
site directory.

The Windows Application event log also carries ANCM's own errors, which is where a `500.31` or a
`500.32` explains itself:

```powershell
Get-WinEvent -LogName Application -MaxEvents 20 |
	Where-Object ProviderName -match 'IIS AspNetCore Module' |
	Format-List TimeCreated, Message
```
