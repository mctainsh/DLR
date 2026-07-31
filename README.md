# Dumb Luck Rides

A group-ride companion for motorcyclists, cyclists and drivers. Record your rides, share live
location inside a ride you were actually invited to, and stop sharing the moment you want to.

> **Status: Milestone A.** The solution, the guards and the test harness exist; no feature does
> yet. `dotnet test` runs green against a throwaway PostgreSQL container, the architecture rules
> are enforced by the build, and the server can already tell you which commit it is. Everything
> else is still the design — [`Documentation/design-outline.md`](Documentation/design-outline.md)
> — and it is worth reading before writing any code.

---

## What it does

- **Record** GPS tracks on the phone, with no signal and the screen off.
- **Import** GPX files from the app or the website, and **edit** them on the web — trim the start,
  trim the end, cut a span out of the middle.
- **Group rides** — a time-boxed container people join. Inside one, members see each other's live
  position on a map and a shared planned route.
- **Markers** — drop a pin with an icon, a title, a note, a facing direction and a photo.
- **A thread per ride** — text, photos, pinned posts, reactions and polls, which deliberately goes
  quiet while people are actually riding.
- **Android Auto and CarPlay** — the group on the car screen, in the platforms' own templates.

## What it does not do

Stated plainly, because these are decisions rather than gaps:

- **No always-on friend tracking.** Location sharing exists inside a group ride and nowhere else.
- **No notifications for chatter mid-ride.** While a ride is live, only a pinned post from the
  organiser can raise a notification. The people this app notifies are operating vehicles.
- **No account required to start.** Record first; an account is a username and a password, and
  an email address is optional.

---

## The three ideas the design keeps coming back to

**1. Consent is per ride, defaults to off, and is revocable at any second.**
You are asked when you join. Dismissing the prompt is a *no*. Turning sharing off deletes the
stored position rather than merely stopping the broadcast — a last-known point at rest in a
database is exactly what someone turning sharing off is asking you not to keep. When a ride ends,
the organiser can stop everyone immediately (the default) or grant a **capped, unextendable,
server-enforced wind-down** so people can watch each other get home. Never open-ended.

**2. Measured location is deleted; authored content is kept.**
Live positions are one row per rider per ride, overwritten in place, with no history table
anywhere — and they are deleted when the ride ends. Markers and comments survive, because a person
deliberately wrote them. Keeping that line sharp is what lets the privacy claim stay short.

**3. If a rule matters, a test enforces it — not a convention.**
Whether the shared UI library can reference MAUI, whether a photo still carries EXIF GPS, whether
an ordinary comment can raise a notification mid-ride: each is an assertion in the suite, because
each is the kind of rule that erodes quietly a year later.

---

## Architecture in one paragraph

One shared **Razor** component library, `DLR.UI`, is compiled into two hosts: a **.NET MAUI Blazor
Hybrid** app covering Android and iOS from a single project, and a **Blazor WebAssembly** client for
the web. Both talk to one ASP.NET Core process that serves the REST API, the SignalR hub and the
static-rendered public pages, backed by PostgreSQL alone — no Redis, no separate API tier, no
managed SignalR, no CDN and no object store — it runs on one small VPS behind Caddy. Maps are
**Apple Maps via MapKit JS** on the phones and **MapLibre with OpenStreetMap tiles** on the web, both
behind a single `RideMap` component. Android Auto and CarPlay are the deliberate exception: native
template code with Mapsui drawing into a raw Surface, since a head unit has no browser.

```
   ONE MAUI PROJECT (Android + iOS)          BROWSER
┌───────────────────────────────────┐   ┌──────────────────┐
│  DLR.App — MAUI Blazor Hybrid     │   │ DLR.Web.Client   │
│    BlazorWebView → DLR.UI ────────┼───┼── DLR.UI         │  ← the same components
│  DLR.Core — domain/sync/SQLite    │   └────────┬─────────┘
│  Android Auto / CarPlay (native)  │            │
└────────┬──────────────────────────┘            │
         │  HTTPS (REST) + WSS (SignalR)         │
         └──────────────────┬────────────────────┘
                            ▼
              DLR.Server (ASP.NET Core)  →  PostgreSQL + a blob volume
```

| Project | What it is |
|---|---|
| `DLR.Core` | Domain, sync engine, SQLite repository, GPX codecs, track stats. No platform dependencies. |
| `DLR.UI` | Every screen, once. **References no MAUI assembly** — it has to compile into WebAssembly. |
| `DLR.App` | The MAUI single project: GPS, secure token storage, and the two car heads. |
| `DLR.Web.Client` | The WASM host for `DLR.UI`. |
| `DLR.Server` | Minimal APIs, SignalR hub, Identity, the position cache, the nightly maintenance job. |

---

## Getting started

**Prerequisites:** .NET 10 SDK, Docker (for PostgreSQL via Testcontainers), and the MAUI workloads
if you are building the app.

The repository root carries what is shared across the whole project — `.editorconfig`,
`Directory.Build.props`, `global.json`, the tool manifest, the licence gate and the docs. The
server and its solution live one level down in **`Web/`**. Run every command below from the
repository root and name the solution explicitly, so the root-relative paths in the licence gate
keep resolving:

```bash
git clone <this repo>
cd DLR
dotnet tool restore
dotnet restore Web/DLR.sln
dotnet test Web/DLR.sln     # needs Docker running; no credentials, no seed data
```

Two more gates run in CI and are worth running before you push:

```bash
dotnet format Web/DLR.sln --verify-no-changes
dotnet nuget-license -i Web/DLR.sln -t \
  -a build/licences/allowed-licences.json \
  -mapping build/licences/licence-url-mappings.json
```

**Every test runs against a throwaway PostgreSQL container and a fake email sender.** You need no
production access and no secrets to run the whole suite — that was chosen for test isolation and
turns out to be exactly what an outside contributor needs.

Local secrets go in **.NET User Secrets**, never in the repo. Every file you need to create locally
has a committed `*.template.json` or `.env.example` beside it showing its shape. See
[§14.3](Documentation/design-outline.md) for the full boundary.

---

## Contributing

Contributions are welcome. Two things to know before opening a PR:

- **Inbound = outbound.** Your contribution is licensed under the project's outbound terms —
  AGPL-3.0-only *including* the app-store additional permission. Sign off your commits
  (`git commit -s`) to assert the [DCO](https://developercertificate.org/). There is no CLA.
- **Tests come first.** Every delivery phase in the design names the failing test written before the
  code. Dependencies must be permissively licensed — CI fails on any package licence outside the
  allow-list, including *unknown*.

Read `CONTRIBUTING.md` for the detail, and the design outline for why almost everything is the way
it is.

---

## Security

Please **do not open a public issue for a security problem.** `SECURITY.md` has the private
reporting channel and what to expect.

This is a location-sharing app with authentication, so the areas most worth your attention are the
ride membership check, GPX and image ingest, and anywhere a shared profile field or a position could
reach someone the organiser never admitted.

---

## Licence

**[AGPL-3.0-only](LICENSE)**, plus an additional permission under GPL-3 §7 covering distribution
through app stores and linking the proprietary platform SDKs the app needs to run —
see [`LICENSE.exceptions`](LICENSE.exceptions).

AGPL rather than MIT or Apache for one specific reason: this is a hosted service, so it is never
*distributed* in the sense plain GPL cares about. Section 13 — the network-use clause — is the only
term in common use that reaches someone running a modified copy of this server, which is the only
way anyone would ever use it without the source.

**If you run a modified version of this server, you must offer its source to your users.** The
running instance does this itself: `GET /api/v1/about` returns the licence, the repository URL and
the exact commit the build came from, and the web footer shows the same on every page.

---

*Full design, decision history and the reasoning behind every trade-off:
[`Documentation/design-outline.md`](Documentation/design-outline.md).
Ordered build tasks for the server, each starting from a named failing test:
[`Documentation/tasks-server.md`](Documentation/tasks-server.md).*
