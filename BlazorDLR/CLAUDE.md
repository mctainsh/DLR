# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

The working directory `BlazorDLR/` is the solution root — `BlazorDLR.slnx` is the solution
file. The **parent** `DLR/` directory holds the AGPL licence gate, `.editorconfig`,
`Directory.Build.props`/`.targets`, `global.json` (pins **.NET 10 SDK 10.0.100**), and the design
docs under `Documentation/` (`design-outline.md`, `SharedFrontend.md`, `tasks-server.md`) — these
are the source of truth for the `§` section references sprinkled through the code.

Note: `README.md` at the parent and some doc references still use the older `DLR.App` / `DLR.UI` /
`Web/DLR.sln` names. The actual projects are `BlazorDLR` (MAUI), `BlazorDLR.Shared`, `BlazorDLR.Web`
(assembly name `DLR.Server`), `BlazorDLR.Web.Client`; the solution is `BlazorDLR.slnx`.

## Commands

Run from the `BlazorDLR/` directory (this folder):

```bash
dotnet restore BlazorDLR.slnx
dotnet build BlazorDLR.slnx
dotnet test BlazorDLR.slnx                      # Docker must be running: Testcontainers spins up PostgreSQL 17
dotnet test tests/DLR.Core.Tests                # No Docker needed (pure logic)
dotnet test tests/DLR.Architecture.Tests        # No Docker needed
dotnet test tests/DLR.UI.Tests                  # No Docker; bUnit renders shared components
dotnet test tests/DLR.Server.Tests --filter "FullyQualifiedName~SomeTest"   # Single test
dotnet format BlazorDLR.slnx --verify-no-changes                            # CI gate
```

Running the server locally (fails fast unless configured — this is intentional, §7.4):

```bash
cd BlazorDLR.Web
dotnet user-secrets set "Auth:SigningKey" "any-32-characters-or-more-will-do-here"
dotnet user-secrets set "ConnectionStrings:Dlr" "Host=localhost;Database=dlr;Username=dlr;Password=…"
cd ..
dotnet run --project BlazorDLR.Web -- --migrate    # applies schema and exits (does NOT boot Kestrel)
dotnet run --project BlazorDLR.Web                 # boots the server
```

Migrations are a **one-shot `--migrate` run** by design (§9), not a boot-time `Migrate()` — this
decouples "server ready" from "schema moved" so rolling deploys and a second container are safe.
`ValidateOnBuild + ValidateScopes` runs in every environment (Program.cs:35-39), so DI-graph
mistakes surface at startup rather than as a heisenbug.

## Architecture

**One Razor Class Library, three hosts** (`Documentation/SharedFrontend.md §1`). `BlazorDLR.Shared`
contains every screen and is referenced by all three hosts:

- `BlazorDLR.Web` — ASP.NET Core; serves REST controllers, the SignalR `RideHub`, the WASM shell,
  and the SSR pass. Assembly name is **`DLR.Server`** (`.csproj` line 14) — keep it that way so
  `WebApplicationFactory<Program>` in tests and the architecture-test `CompiledAssembly.Named(...)`
  lookups keep binding.
- `BlazorDLR.Web.Client` — Blazor WebAssembly host.
- `BlazorDLR` — .NET MAUI Blazor Hybrid app (Android/iOS; Windows/macOS stubs).

Every host-specific concern reaches shared code through an **interface + per-host DI registration**
(see `BlazorDLR.Shared/Services/`: `IFormFactor`, `ITokenStore`, `ILocationProvider`, `IMapInterop`,
etc.). Each host's `Program.cs` / `MauiProgram.cs` wires a different implementation. The SSR pass in
`BlazorDLR.Web/Program.cs` also has to register every seam because the shared pipeline compiles
into it — stubs there answer safely for the prerender.

**Persistence** lives in `DLR.Server.Migrations` (root namespace `DLR.Server.Data`), split from the
server assembly to avoid a reference cycle with EF Core's design-time tooling. PostgreSQL only via
Npgsql, with `EFCore.NamingConventions` (snake_case) applied at model-build time.

**Domain / DTOs** live in `DLR.Core` — no platform dependencies. Contracts under
`DLR.Core/Contracts/` are the wire types every host talks in terms of, so a wire break is a build
failure.

## Load-bearing rules (enforced by `tests/DLR.Architecture.Tests/`)

These aren't stylistic — the arch tests will fail the build. Before adding a dependency or a using,
skim the relevant rule file:

- **`UiLayeringRules`** — `BlazorDLR.Shared` may reference **no MAUI / WebView assembly**, and its
  source may contain **no `#if ANDROID/IOS/MACCATALYST/WINDOWS`**. `DLR.UI.Tests` may reference no
  MAUI assembly either (bUnit tests must run in plain `dotnet test`).
- **`LayeringRules`** — `DLR.Core` references no MAUI assembly. No inspectable assembly references MAUI.
- **`ClockRules`** — **Never** `DateTime.Now/UtcNow/Today` or `DateTimeOffset.Now/UtcNow` outside
  `tests/DLR.TestSupport/`. Resolve `TimeProvider` from DI and call `GetUtcNow()`. Registered from
  day one in every host so tests advance a `FakeTimeProvider`.
- **`SqlRules`** — Raw SQL / `NpgsqlCommand` / `FromSqlRaw` etc. only in `BlazorDLR.Web/Positions/`,
  `BlazorDLR.Web/Identity/`, `BlazorDLR.Web/Maintenance/`. Everywhere else use EF Core.
- **`ImageRules`** — Image decoders (`SkiaSharp`, `SKCodec`, `SKBitmap`, `ImageSharp`,
  `System.Drawing`) only in `BlazorDLR.Web/Photos/`. Only `DLR.Server` (ingest) and
  `BlazorDLR.Shared` (map overlay draw) may link SkiaSharp. Call `ImageIngest` from anywhere else.
- **`XmlRules`** — No `XmlDocument`/`XPathDocument` anywhere. `DtdProcessing` must always be
  `Prohibit` (XXE guard on GPX import, §15.3).
- **`ApiSurfaceRules`** — Response factories (`Results.Ok/Created/Accepted/Json`) may never carry
  `AppUser`. Project to `SharedProfile` or another contract in `DLR.Core.Contracts`.

If a rule genuinely needs to change, edit the rule in the same PR and say why — don't just widen it.

## Testing model

- `DLR.Core.Tests` — pure-logic, no I/O.
- `DLR.Server.Tests` — integration via `WebApplicationFactory<Program>` in
  `DLR.TestSupport/Hosting/DlrWebApplicationFactory.cs`, one throwaway PostgreSQL DB per test on a
  shared Testcontainers instance (`PostgresFixture`, wired as an assembly fixture in
  `DatabaseFixture.cs`). Time is
  `FakeTimeProvider` anchored to `2026-01-01 UTC`. Email is `CollectingEmailSender`. Rate limits
  are relaxed and the nightly maintenance timer is off unless a test asks for them.
  `factory.FlushPositionsAsync()` / `FlushReactionsAsync()` / `RunMaintenanceAsync()` drive the
  background services synchronously — never sleep or advance the clock to trigger a `PeriodicTimer`.
  Test classes run **in parallel** (capped in `tests/DLR.Server.Tests/xunit.runner.json`), so a new
  test may share the container — never the database — with whatever else is running. Nothing may
  depend on a fixed port, a shared directory or being the only test in flight.
  The schema is applied once to a template database and copied per test, so adding a migration
  costs the suite one replay rather than five hundred.
  Password hashing runs at `DlrWebApplicationFactory.CheapPasswordHasherIterations`; a test about
  what hashing *costs* asks for `ShippedPasswordHasherIterations` through `settings:`.
- `DLR.UI.Tests` — bUnit against `BlazorDLR.Shared`. No simulator, emulator or browser.
- `DLR.Architecture.Tests` — reflection + source-text rules described above.
- `DLR.TestSupport` — the only project allowed to read the real clock; `IsTestProject=false`.
  Assertions are **Shouldly** (not FluentAssertions — licence decision, §14.6.3); mocking is **NSubstitute**.

The runner is **xunit.v3** on **Microsoft.Testing.Platform**. The .NET 10 SDK no longer hosts
xunit.v3 under VSTest, so `"test": { "runner": "Microsoft.Testing.Platform" }` in the parent
`global.json` is what keeps `dotnet test` working — remove it and every test project fails to run
rather than fails a test. The test projects are therefore `OutputType=Exe` (the runner links in),
and `DLR.TestSupport` takes `xunit.v3.extensibility.core` instead, because it is a fixture library
rather than a test assembly.

## Style enforced by build

Tabs, width 4 (Use tabs in Razor, CS, HTML, YAML and Markdown — mandatory). `EnforceCodeStyleInBuild=true` in
`Directory.Build.props`. File-scoped namespaces, `using` directives outside the namespace with
`System` first (`.editorconfig`). Nullable + implicit usings enabled everywhere.
`InvariantGlobalization=true` for the whole solution.
Always use CRLF for line endings

### Single line if and for
Don't wrap a single line command after an `if` or `for` in curly brackets if it only occupies a single line.
```
// Never
if (blah)
{
	return;
}
``` 

## AGPL provenance

The parent `Directory.Build.targets` appends `.dirty` to `SourceRevisionId` when git reports
uncommitted changes, and embeds a build timestamp into `DLR.Server`'s assembly attributes for
`GET /api/v1/about` (§14.6.2). Both are visible to end users — a shipped build should be from a
clean, pushed tree.
