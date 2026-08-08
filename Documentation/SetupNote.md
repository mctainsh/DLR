# Local setup

Steps to get a fresh clone of DLR running against a local PostgreSQL. Skip
any you have already done.

For a server rather than a workstation: `deploy/README.md` covers the
supported Docker deployment, [SetupNote-IIS.md](SetupNote-IIS.md) covers
Windows Server under IIS, and [SetupNote-Linux.md](SetupNote-Linux.md)
covers Linux without Docker.

## 1. Install PostgreSQL

Download the Windows installer from
<https://www.postgresql.org/download/windows/> (EnterpriseDB's build).
Install with the default port `5432`, and remember the `postgres`
superuser password.

## 2. Create the DLR database and role

Open a `psql` prompt (Start menu → "SQL Shell (psql)"), log in as
`postgres`, and run:

```sql
CREATE ROLE dlr WITH LOGIN PASSWORD 'dlr';
CREATE DATABASE dlr OWNER dlr;
```

Choose your own password; the connection-string example below assumes
`dlr`.

## 3. Set the connection-string user secret

The placeholder in `appsettings.json` is deliberately empty (§14.3) — a
connection string is a credential, and that file ships with the code.
Store the real value in user secrets instead:

```
dotnet user-secrets set "ConnectionStrings:Dlr" "Host=localhost;Database=dlr;Username=dlr;Password=dlr" --project BlazorDLR/BlazorDLR.Web
```

For production, use the environment variable `ConnectionStrings__Dlr` or a
Docker secret instead.

## 4. Set the JWT signing key

Startup refuses to serve requests without one (§7.4). Any string of at
least 32 bytes works locally:

```
dotnet user-secrets set "Auth:SigningKey" "any-random-string-at-least-32-bytes-long-not-a-secret" --project BlazorDLR/BlazorDLR.Web
```

## 5. Apply the schema

`--migrate` applies every pending migration and exits without starting
Kestrel (§9):

```
dotnet run --project BlazorDLR/BlazorDLR.Web -- --migrate
```

## 6. Run the server

```
dotnet run --project BlazorDLR/BlazorDLR.Web
```

## Running the tests

The test suite starts its own PostgreSQL container via Testcontainers, so
none of the above is needed to run `dotnet test` — but Docker Desktop
must be running.
