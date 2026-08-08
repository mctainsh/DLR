# Setup — Linux without Docker

Running `DLR.Server` directly on a Linux host: systemd for the process, Caddy for the edge,
PostgreSQL from the distribution's packages. No containers anywhere.

`deploy/` describes the supported deployment — four containers on one VPS (§9). This note is the
same four jobs done by four ordinary system services. Nothing in the application changes: every
setting below is the key the compose file sets, because configuration is the only seam a host is
allowed to reach the server through (§14.3).

What each container becomes:

| Container | Here it is |
|---|---|
| `postgres` | The distribution's `postgresql-17` — step 3 |
| `migrate` | A `Type=oneshot` unit run before the server starts — step 8 |
| `server` | `dlr.service` under systemd — step 7 |
| `caddy` | Caddy from its apt repository, same Caddyfile — step 9 |
| `backup` | A `restic` script on a systemd timer — step 13 |
| The `blobs` volume | A directory owned by the service user — step 4 |
| `.env` | `/etc/dlr/dlr.env`, mode 0640 root:dlr — step 6 |

Examples assume Ubuntu 24.04 LTS and a host called `dumbluckrides.example`. Debian is the same;
RHEL-family differs in package names and in SELinux, which step 15 covers.

> A `§` reference below points at `Documentation/design-outline.md`, as everywhere else in this
> project. References to *this* note are written as "step 4".

---

## 1. Packages

```sh
sudo apt update
sudo apt install --yes postgresql-17 postgresql-client-17 libfontconfig1 restic curl
```

**`libfontconfig1` is load-bearing.** SkiaSharp's native library needs it even for an application
that draws no text (§16.4), and the only thing that touches SkiaSharp is photo ingest. Without it
the server runs perfectly until the first photo upload, then throws a confusing native stack trace.
This is the same package the Dockerfile installs, for the same reason.

### The .NET runtime

The server needs the **ASP.NET Core 10 runtime** — not the SDK, and not the plain .NET runtime.
`SkiaSharp.NativeAssets.Linux` ships the native library with the publish output, so nothing else is
needed for it.

```sh
sudo apt install --yes aspnetcore-runtime-10.0
```

If the distribution's feed does not carry 10.0 yet, use Microsoft's:

```sh
curl -sSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /tmp/ms.deb
sudo dpkg -i /tmp/ms.deb && sudo apt update
sudo apt install --yes aspnetcore-runtime-10.0
```

The build machine — which can be this host or somewhere else — needs the **SDK** instead, at the
version `global.json` pins: **10.0.100**, `rollForward: latestFeature`.

## 2. The service user and its directories

```sh
sudo useradd --system --shell /usr/sbin/nologin --home-dir /var/lib/dlr --create-home dlr

sudo mkdir -p /srv/dlr /var/lib/dlr/blobs /var/backups/dlr
sudo chown -R dlr:dlr /var/lib/dlr /var/backups/dlr
sudo chown -R root:root /srv/dlr          # the application reads its own directory, never writes it
sudo chmod 755 /srv/dlr
```

- `/srv/dlr` — the publish output.
- `/var/lib/dlr/blobs` — uploaded photos and track files (§9.1). This grows without bound until
  quotas and the nightly sweep hold it; if it is on its own filesystem, mount it before step 4.
- `/var/lib/dlr` — also the service user's `HOME`, which step 5 explains is not incidental.

## 3. PostgreSQL

```sh
sudo -u postgres psql <<'SQL'
CREATE ROLE dlr WITH LOGIN PASSWORD 'choose-a-real-one';
CREATE DATABASE dlr OWNER dlr;
SQL
```

Two settings from `docker-compose.yml` are worth carrying over.

**`max_connections=200`.** The default of 100 was outgrown during SRV-13 and production has the
same shape: one pool per process, and Npgsql opens more connectors than anybody expects.

```sh
sudo -u postgres psql -c "ALTER SYSTEM SET max_connections = 200;"
sudo systemctl restart postgresql
```

**Data checksums.** Checksums cost a few percent and turn silent corruption into a loud error,
which on consumer-grade storage is the right trade. They can only be set at `initdb` time — after
the first start it is a dump-and-restore. Ubuntu's packaging creates the cluster for you without
them, so if you want them, do it before there is anything to lose:

```sh
sudo pg_dropcluster --stop 17 main
sudo pg_createcluster 17 main -- --data-checksums
sudo pg_ctlcluster 17 main start
```

Leave PostgreSQL on its unix socket and loopback. Nothing outside this host has any business
reaching it, and a listening port on a VPS is a port the internet scans within the hour.

## 4. Publish and deploy

**Build from a clean, pushed clone.** The parent `Directory.Build.targets` appends `.dirty` to
`SourceRevisionId` when git reports uncommitted changes, and `GET /api/v1/about` reads it back
(§14.6.2). Stating which commit a server is running is an AGPL §13 obligation rather than a
nicety, so a shipped build should come from a tree with no local edits and an intact `.git`
directory.

On the build machine:

```sh
dotnet publish BlazorDLR/BlazorDLR.Web/BlazorDLR.Web.csproj \
	--configuration Release \
	--runtime linux-x64 \
	--self-contained false \
	--output /tmp/dlr-publish
```

Use `linux-arm64` on an ARM VPS — the runtime identifier matters here because it selects which
SkiaSharp native library is copied.

Then onto the server:

```sh
sudo rsync --archive --delete /tmp/dlr-publish/ root@dumbluckrides.example:/srv/dlr/
sudo chown -R root:root /srv/dlr
sudo chown dlr:dlr /var/lib/dlr/blobs
```

`--delete` is deliberate: stale files under `wwwroot/_framework` are served happily and are the
wrong version of the WASM bundle.

## 5. Data Protection keys — set `HOME`

`AddDataProtection()` is registered with no explicit key store, so the key ring goes to the
platform default: `$HOME/.aspnet/DataProtection-Keys`. A systemd unit does **not** set `HOME` for
you. Without it the keys are held in memory and thrown away on every restart.

What they seal is not incidental: the email-confirmation and password-reset tokens (§7.7) and the
antiforgery token on the cookie-to-access-token exchange (§7.5). Losing the ring invalidates every
reset link already sitting in somebody's inbox — which presents as "the link says it is invalid" a
day after a deploy, with nothing in the log naming the cause.

The unit in step 7 sets `Environment=HOME=/var/lib/dlr`, and step 2 gave the `dlr` user that home
directory with `--create-home` for exactly this. Nothing else is needed, but if the keys are not
landing:

```sh
sudo ls -l /var/lib/dlr/.aspnet/DataProtection-Keys/
```

An empty or missing directory after a successful sign-in means `HOME` is not reaching the process.

## 6. Settings and secrets

Every setting reaches the server as an environment variable with a **double** underscore between
levels. A single underscore is a different key and binds to nothing, silently.

**Not `appsettings.json`.** The signing key check refuses to start if the value came from a file
inside the content root (§7.4), and it is right to: a key that reaches git history is fixed by
rotating it, not by deleting the line. The connection string is under the same rule by convention.

```sh
sudo mkdir -p /etc/dlr
sudo install -o root -g dlr -m 0640 /dev/null /etc/dlr/dlr.env
sudo $EDITOR /etc/dlr/dlr.env
```

`0640`, owned `root:dlr` — the service reads it, nothing else on the box can. Its contents:

```ini
ASPNETCORE_ENVIRONMENT=Production

# Kestrel listens here; Caddy is the only thing that connects to it (§9).
ASPNETCORE_HTTP_PORTS=8080

ConnectionStrings__Dlr=Host=localhost;Database=dlr;Username=dlr;Password=…;Maximum Pool Size=20

# openssl rand -base64 48 — at least 32 bytes, or the server refuses to start (§7.4).
Auth__SigningKey=…

# Absolute, always. A relative value is refused because it would silently write every upload
# into the process working directory.
Blobs__RootPath=/var/lib/dlr/blobs

Links__BaseUrl=https://dumbluckrides.example
About__SourceUrl=https://github.com/mctainsh/dlr

# Caddy on loopback is the one trusted hop. See step 9 — this line is load-bearing for registration.
ForwardedHeaders__KnownProxies__0=127.0.0.1

Email__Host=smtp.zoho.com.au
Email__Port=587
Email__UserName=no-reply@dumbluckrides.example
Email__Password=…
Email__FromAddress=no-reply@dumbluckrides.example

# Optional — the display name on the From header. Defaults to "Dumb Luck Routes".
Email__FromName=Dumb Luck Routes

# §7.11's brakes, and the default is the safe one on purpose. See §14.
Maintenance__DryRun=true
Maintenance__AlertEmail=you@example.com

# Optional (§4.5). Without them the map states it has no credentials, which is a supported
# state rather than a broken one. The PEM's newlines must survive — see the note below.
Maps__MapKit__TeamId=
Maps__MapKit__KeyId=
Maps__MapKit__Origin=https://dumbluckrides.example
```

Generate the signing key with `openssl rand -base64 48`.

**The MapKit private key does not fit in an `EnvironmentFile`.** systemd's parser does not handle a
multi-line value, and the `.p8` is a PEM with real newlines. Either put it on one line with `\n`
escapes in a `systemd`-quoted `Environment=` directive in a drop-in, or leave MapKit unconfigured
until you need it. What must not happen is the `.p8` landing in the deploy directory: §14.2 has it
on the never-commit list, and a file in `/srv/dlr` is a file the next `rsync --delete` will
either wipe or preserve, neither of which you want to be guessing about.

**A note on the email keys.** They are `Email__UserName` and `Email__FromAddress` — not `User` and
`From`. Configuration binds by property name on `EmailOptions`, so a near-miss binds to nothing and
the failure is silent until the first confirmation email does not arrive. `Email__FromName` is
optional and defaults to *Dumb Luck Routes*.

Every setting the server understands, for reference:

| Variable | Required | Notes |
|---|---|---|
| `ConnectionStrings__Dlr` | yes | Checked before anything else, including `--migrate` |
| `Auth__SigningKey` | yes | ≥32 bytes; refused if it came from a file in the content root |
| `Blobs__RootPath` | yes | Must be **absolute** |
| `Links__BaseUrl` | yes | Where confirmation and reset links point (§7.7) |
| `About__SourceUrl` | yes | A fork owes its users *its own* source (§14.6.2) |
| `Email__*` | yes in practice | No email means no confirmation and no password reset |
| `ForwardedHeaders__KnownProxies__0` | yes here | The reverse proxy's address — step 9 |
| `Maintenance__DryRun` | defaults `true` | Leave it true for a week — step 14 |
| `Maintenance__AlertEmail` | — | Where the nightly summary goes |
| `Maps__MapKit__*` | optional | §4.5 |
| `Health__MinimumFreeMb` | defaults 2048 | Free space below which `/healthz` fails |
| `ASPNETCORE_HTTP_PORTS` | yes | What Caddy proxies to |

## 7. The systemd unit

`/etc/systemd/system/dlr.service`:

```ini
[Unit]
Description=Dumb Luck Rides — application server
After=network-online.target postgresql.service dlr-migrate.service
Wants=network-online.target
Requires=dlr-migrate.service

[Service]
Type=notify
User=dlr
Group=dlr
WorkingDirectory=/srv/dlr
EnvironmentFile=/etc/dlr/dlr.env

# Data Protection's key ring lives under $HOME, and systemd does not set it. See §5.
Environment=HOME=/var/lib/dlr

ExecStart=/usr/bin/dotnet /srv/dlr/DLR.Server.dll

Restart=always
RestartSec=5

# The blob directory is the only path this process writes.
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/dlr
PrivateTmp=true
NoNewPrivileges=true

# Logging goes to the journal; the application writes to stdout.
StandardOutput=journal
StandardError=journal
SyslogIdentifier=dlr

[Install]
WantedBy=multi-user.target
```

`Type=notify` works because ASP.NET Core signals readiness through systemd when it is hosted this
way; if the unit times out waiting on that, fall back to `Type=simple`.

`ProtectSystem=strict` makes the whole filesystem read-only except `ReadWritePaths`. That is the
arrangement the application already expects — the blob directory is the only path it writes — and
it turns a path misconfiguration into a startup failure rather than into files scattered somewhere
nobody looks.

## 8. The schema, as its own unit

`--migrate` applies every pending migration and exits without starting Kestrel (§9). It is
deliberately not a `Migrate()` call on the way up: that couples "is this server ready" to "has the
schema moved", which turns a failed migration into a crash loop.

`/etc/systemd/system/dlr-migrate.service`:

```ini
[Unit]
Description=Dumb Luck Rides — apply the database schema
After=postgresql.service
Requires=postgresql.service

[Service]
Type=oneshot
RemainAfterExit=false
User=dlr
Group=dlr
WorkingDirectory=/srv/dlr
EnvironmentFile=/etc/dlr/dlr.env
Environment=HOME=/var/lib/dlr
ExecStart=/usr/bin/dotnet /srv/dlr/DLR.Server.dll --migrate
```

`dlr.service` `Requires=` it, so the schema moves before the server accepts a request and a failed
migration stops the start rather than being discovered later. `--migrate` needs a database and
nothing else — not the signing key, not the blob path — so it also works on a host that is not yet
fully configured, which is the order anyone setting one up for the first time will actually work in.

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now dlr-migrate.service
sudo systemctl enable --now dlr.service
sudo systemctl status dlr --no-pager
```

## 9. Caddy

Caddy from its own apt repository, with the same `Caddyfile` the compose deployment uses — it
already handles TLS, HTTP/3, compression, the security headers, and the one log filter that
matters.

```sh
sudo apt install --yes debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key |
	sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt |
	sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update && sudo apt install --yes caddy
```

`/etc/caddy/Caddyfile` — `deploy/Caddyfile` with the container name replaced by loopback and the
two environment variables written out:

```caddyfile
{
	email you@example.com

	# On by default in Caddy 2, and named here so nobody "tidies" it away later.
	servers {
		protocols h1 h2 h3
	}
}

dumbluckrides.example {
	encode zstd gzip

	reverse_proxy 127.0.0.1:8080 {
		header_up X-Forwarded-Proto {scheme}

		# The ride hub is a long-lived websocket carrying a two-hour ride (§5.3, §7.6). The
		# default read timeout would close it mid-ride, which the client recovers from —
		# repeatedly, all afternoon, at the cost of the rider's battery (§10.3).
		transport http {
			read_timeout 0
			write_timeout 0
		}
	}

	header {
		Strict-Transport-Security "max-age=31536000; includeSubDomains"
		X-Content-Type-Options "nosniff"
		Referrer-Policy "strict-origin-when-cross-origin"

		# The map is why this is not simply 'none'. MapKit JS is loaded from Apple and OSM
		# tiles from OpenStreetMap (§4.5); everything else is served from here.
		Content-Security-Policy "default-src 'self'; img-src 'self' data: blob: https://*.tile.openstreetmap.org https://*.apple.com; script-src 'self' 'wasm-unsafe-eval' https://cdn.apple-mapkit.com; style-src 'self' 'unsafe-inline'; connect-src 'self' https://*.apple.com https://*.tile.openstreetmap.org; frame-ancestors 'none'"

		-Server
	}

	# The WASM bundle is content-addressed by the framework, so it can be cached hard (§18.4).
	@framework path /_framework/*
	header @framework Cache-Control "public, max-age=31536000, immutable"

	# A stale 200 here is worse than no monitoring, because it is monitoring that says
	# everything is fine (§9).
	@health path /healthz
	header @health Cache-Control "no-store"

	log {
		output file /var/log/caddy/access.log {
			roll_size 10MiB
			roll_keep 5
		}

		# §7.6 lifts the SignalR access token out of a query string, because a browser cannot
		# set an Authorization header on a websocket. That makes the access log a place live
		# credentials land — for weeks, across five rolled files — unless it is filtered here.
		# Choosing JSON alone does not do it: every format logs request>uri intact.
		format filter {
			wrap json
			fields {
				request>uri query {
					delete access_token
				}
			}
		}
	}
}
```

```sh
sudo mkdir -p /var/log/caddy && sudo chown caddy:caddy /var/log/caddy
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

**`ForwardedHeaders__KnownProxies__0=127.0.0.1` in step 6 is the other half of this.** Caddy sets
`X-Forwarded-For` itself; the server clears its known-proxy list at startup so the configured list
is the whole list, and only believes the header from an address on it. Get it wrong and every
signup looks as if it came from Caddy — §7.8's per-address rules and the registration ladder are
both wrong, and the ladder in particular asks the fourth account ever for an email address.
**Verify this before the first public signup.** It is load-bearing for registration, not hygiene.

Then close everything else:

```sh
sudo ufw allow 22/tcp
sudo ufw allow 80,443/tcp
sudo ufw allow 443/udp        # HTTP/3 is UDP. Without this, Caddy advertises Alt-Svc, the
                              # client tries QUIC, nothing answers, and it falls back forever.
sudo ufw enable
```

### If you would rather use nginx

Everything above still applies; only the edge changes. The four things that are easy to get wrong:

```nginx
server {
	listen 443 ssl;
	http2 on;
	server_name dumbluckrides.example;

	ssl_certificate     /etc/letsencrypt/live/dumbluckrides.example/fullchain.pem;
	ssl_certificate_key /etc/letsencrypt/live/dumbluckrides.example/privkey.pem;

	# Track import accepts 25 MB (TrackImportOptions.MaxUploadBytes). nginx's default is 1 MB,
	# and it rejects the body before the application sees it — so the server's own polite
	# "Files are limited to 25 MB" never gets a chance to answer.
	client_max_body_size 32m;

	location / {
		proxy_pass http://127.0.0.1:8080;
		proxy_http_version 1.1;

		# The websocket upgrade. Without these two the ride hub never connects and the map
		# simply never updates.
		proxy_set_header Upgrade $http_upgrade;
		proxy_set_header Connection $connection_upgrade;

		proxy_set_header Host $host;
		proxy_set_header X-Real-IP $remote_addr;
		proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
		proxy_set_header X-Forwarded-Proto $scheme;

		# A two-hour ride on one connection (§5.3). The 60-second default closes it repeatedly.
		proxy_read_timeout  1d;
		proxy_send_timeout  1d;
	}
}
```

with `map $http_upgrade $connection_upgrade { default upgrade; '' close; }` at `http` scope.

And **nginx logs the query string** in `combined`, which §7.6 puts an access token into. Define a
log format without `$request` — or with `$uri` in its place — before this handles real traffic.
TLS renewal is certbot's job here, not nginx's; Caddy did that for free.

## 10. Verify

```sh
curl --silent https://dumbluckrides.example/healthz | jq
```

Read the body, not just the status:

- `migrationsApplied: false` — `dlr-migrate.service` did not run or did not succeed. This is the
  failure the endpoint exists for: a server whose schema is a migration behind answers every
  request correctly until one touches the column that is not there yet.
- `blobVolume.ok: false` with `freeMb: 0` — `Blobs__RootPath` does not exist, or `dlr` cannot read
  it, or `ProtectSystem=strict` is hiding it. Uploads would otherwise fail a day later with a
  permission error nobody connects to this setting.
- `databaseReachable: false` — the connection string, `pg_hba.conf`, or the socket path.

Then the things `/healthz` cannot see:

```sh
curl --silent https://dumbluckrides.example/api/v1/about | jq   # commit and build time, no '.dirty'
```

Register an account and confirm the email arrives, open a ride and confirm the map updates (the
websocket path), and upload a photo (`libfontconfig1`).

## 11. Logs

```sh
sudo journalctl --unit dlr --follow
sudo journalctl --unit dlr --since '1 hour ago' --priority err
```

The journal is rotated by `systemd-journald`, so there is nothing to add — but check
`SystemMaxUse` in `/etc/systemd/journald.conf` on a small disk. The blob volume and the journal
compete for the same 40 GB, and it is the disk that runs out first on this project.

## 12. Deploying an update

```sh
sudo systemctl stop dlr
sudo rsync --archive --delete /tmp/dlr-publish/ /srv/dlr/
sudo systemctl start dlr-migrate      # `start dlr` pulls this in too; explicit is clearer
sudo systemctl start dlr
curl --silent --fail https://dumbluckrides.example/healthz > /dev/null && echo ok
```

Stop before copying: `--delete` over a running process replaces assemblies underneath it. The
downtime is a few seconds, which for one host is the honest trade — a rolling deploy needs two
hosts, and the `--migrate` split (step 8) is what makes that possible later without changing anything
here.

## 13. Backups

`deploy/backup.sh` runs `pg_dump` and the blob directory into `restic`, encrypted client-side
before it leaves the machine (§9.1, §10.1). Without the container it is a script and a timer.

`/usr/local/bin/dlr-backup.sh`:

```sh
#!/bin/sh
set -eu

pg_dump --host=localhost --username=dlr --format=custom \
	--file=/var/backups/dlr/dlr.dump dlr

# One snapshot for both. A database restored against blobs from another night gives you tracks
# pointing at files that are not there.
restic backup /var/backups/dlr /var/lib/dlr/blobs

restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune
```

Its credentials go in `/etc/dlr/backup.env`, mode 0600, owned by root:

```ini
PGPASSWORD=…
RESTIC_REPOSITORY=b2:dlr-backups:/dlr
RESTIC_PASSWORD=…
B2_ACCOUNT_ID=…
B2_ACCOUNT_KEY=…
```

`RESTIC_PASSWORD` belongs in a password manager, not only in a file on the machine being backed up.
Losing it loses every backup.

`/etc/systemd/system/dlr-backup.service` and `.timer`:

```ini
[Service]
Type=oneshot
User=dlr
EnvironmentFile=/etc/dlr/backup.env
ExecStart=/usr/local/bin/dlr-backup.sh
```

```ini
[Timer]
# 17:00 UTC, which is early morning in Australia.
OnCalendar=*-*-* 17:00:00 UTC
Persistent=true

[Install]
WantedBy=timers.target
```

```sh
sudo chmod +x /usr/local/bin/dlr-backup.sh
sudo systemctl enable --now dlr-backup.timer
sudo systemctl start dlr-backup.service   # once, now, to prove it works
```

**Run a restore drill.** B2's restore egress is free up to three times the stored volume, so a
drill costs nothing — which removes the only excuse. A backup you have never restored is a hope.

```sh
sudo -u dlr env $(cat /etc/dlr/backup.env | xargs) sh -c '
	restic snapshots
	restic restore latest --target /tmp/drill
	pg_restore --list /tmp/drill/var/backups/dlr/dlr.dump | head
'
```

Restoring for real:

```sh
sudo systemctl stop dlr
sudo -u dlr restic restore latest --target /restore
pg_restore --clean --if-exists --dbname=dlr /restore/var/backups/dlr/dlr.dump
sudo rsync --archive --delete /restore/var/lib/dlr/blobs/ /var/lib/dlr/blobs/
sudo systemctl start dlr
```

## 14. The three things that are not automatic

**Turn off the maintenance dry run — after a week, not before.** `Maintenance__DryRun` defaults to
`true` and gates every sweep (§7.11). Set `Maintenance__AlertEmail`, read the nightly summaries for
a week, and only then set it to `false`. It deletes accounts.

**Point something at `/healthz`.** A free uptime pinger is the whole alerting budget and it is
enough: the endpoint answers `503` when the database is unreachable, when the schema is behind, or
when free space on the blob volume drops below `Health:MinimumFreeMb` (2 GB by default). That last
one is §9's disk alert — a full disk stops PostgreSQL *writing*, which is far worse than a failed
upload, and on a 40 GB host it is the limit this project reaches first.

**Verify the forwarded-header configuration (step 9) before the first public signup.** It is
load-bearing for registration, not just for rate limiting.

## 15. When it does not start

```sh
sudo systemctl status dlr --no-pager
sudo journalctl --unit dlr --lines 50 --no-pager
```

The startup failures this application raises are written to say what to do: the signing-key,
connection-string and blob-path messages each name the setting and give the command. If the journal
shows one of those, the fix is in `/etc/dlr/dlr.env` and a `systemctl restart dlr`.

| Symptom | Cause |
|---|---|
| `Blob storage path '…' is relative` | `Blobs__RootPath` must be absolute |
| `Auth:SigningKey is set in '…appsettings.json'` | Move it to `/etc/dlr/dlr.env` and **rotate it** |
| `No database: ConnectionStrings:Dlr is not set` | The `EnvironmentFile` is unreadable by `dlr`, or a single underscore was used |
| Starts, then `blobVolume.ok: false` | `ReadWritePaths` or the directory's owner |
| Photo upload throws a native error | `libfontconfig1`, or the wrong `--runtime` at publish |
| Map never updates | The proxy is not upgrading the websocket, or is timing it out |
| Reset links "invalid" after a restart | `HOME` is not set on the unit — step 5 |
| Registration asks for an email far too early | `ForwardedHeaders__KnownProxies__0` — step 9 |

On RHEL-family hosts, SELinux blocks a reverse proxy from connecting to a local port and blocks
writes outside the expected labels. Both are quiet in the application's own logs and loud in
`ausearch -m avc -ts recent`:

```sh
sudo setsebool -P httpd_can_network_connect 1
sudo semanage fcontext -a -t var_lib_t '/var/lib/dlr(/.*)?' && sudo restorecon -R /var/lib/dlr
```
