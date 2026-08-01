# Deployment

One VPS, four containers, four volumes (§9). Everything here is committed; the only file that is
not is `deploy/.env`, which `.gitignore` refuses.

## What is here

| File | What it is |
|---|---|
| `Dockerfile` | Two-stage build of the server image. Build context is the **repository root**. |
| `Dockerfile.dockerignore` | What stays out of the image. Named for BuildKit's convention, not by choice — see the file. |
| `docker-compose.yml` | PostgreSQL, the one-shot schema step, the server, Caddy, and the nightly backup. |
| `Caddyfile` | TLS, HTTP/3, compression, and the `X-Forwarded-For` §7.8 depends on. |
| `backup.sh` | `pg_dump` + the blob volume → `restic` → Backblaze B2, encrypted before it leaves. |
| `.env.example` | Every secret, with no values. Copy to `.env`. |

## First run

```sh
cp deploy/.env.example deploy/.env
$EDITOR deploy/.env                      # every key marked Required

docker compose --file deploy/docker-compose.yml up --detach --build
```

Then **check the address Caddy actually got**, because `CADDY_IP` is a guess until you look:

```sh
docker compose --file deploy/docker-compose.yml exec server getent hosts caddy
```

If it differs from `CADDY_IP`, fix `.env` and restart the server container. This is not tidiness:
without it every signup looks as if it came from Caddy, and §7.8's registration ladder asks the
fourth account ever for an email address. **Verify it in staging before the first public signup** —
it is load-bearing for registration, not just for rate limiting.

Then confirm the deploy finished:

```sh
curl https://your-domain/healthz
```

`migrationsApplied: false` means the `migrate` container did not run or did not succeed. That is
the failure this endpoint exists for: a server whose schema is a migration behind answers every
request correctly until one touches the column that is not there yet.

## The three things that are not automatic

**Turn off the dry run — after a week, not before.** `MAINTENANCE_DRY_RUN` defaults to `true` and
gates every sweep (§7.11). Set `MAINTENANCE_ALERT_EMAIL`, read the nightly summaries for a week,
and only then set it to `false`. It deletes accounts.

**Run a restore drill.** Restore egress from B2 is free up to three times the stored volume, so a
drill costs nothing — which removes the only excuse. A backup you have never restored is a hope.

```sh
docker compose --file deploy/docker-compose.yml run --rm backup sh -c '
  apk add --no-cache postgresql17-client restic
  restic snapshots
  restic restore latest --target /tmp/drill
  pg_restore --list /tmp/drill/tmp/dump/dlr.dump | head
'
```

**Point something at `/healthz`.** A free uptime pinger is the whole alerting budget, and it is
enough: the endpoint answers `503` when the database is unreachable, when the schema is behind, or
when the blob volume drops below `Health:MinimumFreeMb` (2 GB by default). That last one is §9's
disk alert — a full disk stops PostgreSQL *writing*, which is far worse than a failed upload, and
on a 40 GB CX22 it is the limit this project reaches first.

## Restoring

```sh
docker compose --file deploy/docker-compose.yml stop server
docker compose --file deploy/docker-compose.yml run --rm backup sh -c '
  apk add --no-cache postgresql17-client restic
  restic restore latest --target /restore
  pg_restore --clean --if-exists --dbname=dlr /restore/tmp/dump/dlr.dump
'
docker compose --file deploy/docker-compose.yml start server
```

Blobs come out of the same snapshot, which is why they are backed up together: a database restored
against blobs from another night gives you tracks pointing at files that are not there.

## Scaling the disk

A Hetzner volume is about €0.05/GB a month and one command — provided somebody noticed in time,
which is what `/healthz` is for.
