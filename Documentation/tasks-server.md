# Dumb Luck Rides — Server Build Tasks

> Companion to [`design-outline.md`](design-outline.md) v0.19. This is the **server** list —
> `DLR.Server`, `DLR.Core` and the test projects. The `DLR.UI` / `DLR.App` / `DLR.Web.Client`
> lists follow separately; a few tasks below deliberately stop at the API boundary and say so.

## How to work this list

Every task is a TDD loop, and the list is written so the loop is unambiguous:

1. **Write the named test. Watch it fail** — for the right reason. A test that fails because a type
   does not compile has not told you anything yet; get it red on an assertion.
2. **Write the least code that turns it green.**
3. **Refactor**, with the suite green.
4. **Only then** move to the next test in the task.

**No production type is introduced without a red test that demands it** (§10.4). Where a task lists
several tests, they are in the order to write them — the first one usually forces the type into
existence and the rest shape it.

**Definition of done** for every task: named tests green, whole suite green, `dotnet format
--verify-no-changes` clean, architecture tests green, licence gate green, and the design section
referenced still describes what you built. If it does not, the doc is now wrong — fix it or record
the change in the revision history. That is a real step, not a courtesy.

**Test names in `code font` already exist in the design document.** They were written before any of
this and are quoted verbatim so the two documents cannot drift.

**Marking a task off:** a task heading gains a **✅** and a `**Status:**` line naming what is
actually in the tree, once the definition of done above holds in full. A task is not marked from
memory of having written the code — it is marked after the suite is green on the command line.

---

## Milestones

| Milestone | Tasks | Maps to §11 | Outcome |
|---|---|---|---|
| **A — Skeleton and guards** ✅ | SRV-01 … SRV-05 | Phase 0 | An empty solution that already enforces its own rules |
| **B — Identity** ✅ | SRV-06 … SRV-13 | Phase 1 | Register, sign in, never sign in again, recover if you left an address |
| **C — Tracks** ✅ | SRV-14 … SRV-19 | Phase 1 | Record, upload, import GPX, edit on the web |
| **D — Group rides** | SRV-20 … SRV-25 | Phase 2 | Join, consent, live positions, the wind-down |
| **E — Content** | SRV-26 … SRV-30 | Phase 2 | Markers, photos, the thread, polls |
| **F — Operations** | SRV-31 … SRV-35 | Phase 2–3 | Moderation, the nightly job, deployment, backups |

Milestone A is a prerequisite for everything. Within B–E, the order below is the dependency order;
C and B can overlap once SRV-09 exists, because tracks need an owner but not a full auth story.

---

# Milestone A — Skeleton and guards

The guards come first because they are cheap now and unenforceable later. Every one of these tasks
is a rule the rest of the project leans on.

### SRV-01 — Solution, style and the licence files ✅
**Status:** done. Seven projects under `Web/`, shared settings at the repository root, all six
licence and policy files present.
**First red test:** none — this is the only task in the list without one.
**Then build:** `Web/DLR.sln`; `DLR.Core`, `DLR.Server`, `DLR.Server.Migrations`; `DLR.Core.Tests`,
`DLR.Server.Tests`, `DLR.Architecture.Tests`, `DLR.TestSupport`. At the **repository root**, one
level above the solution (§3): `Directory.Build.props` with `net10.0`, `EnforceCodeStyleInBuild`,
`PackageLicenseExpression=AGPL-3.0-only`, SourceLink; `.editorconfig` with tabs and the
**mandatory** YAML/Markdown carve-outs. Both reach the projects by MSBuild's and EditorConfig's
walk up the directory tree, so the test-project carve-out is globbed `**/tests/**`, not `tests/**`.
**Also:** `LICENSE` (verbatim AGPL-3.0), `LICENSE.exceptions`, `README.md`, `SECURITY.md`,
`CONTRIBUTING.md`, `.gitignore` from §14.3.
**Done when:** `dotnet build Web/DLR.sln` and `dotnet test Web/DLR.sln` both succeed with zero
tests, and the repo could be made public today without granting nobody anything.
**Refs:** §3, §10.5, §14.1, §14.3, §14.6.1

### SRV-02 — Architecture tests, before there is architecture to break ✅
**Status:** done. `LayeringRules`, `ClockRules`, `XmlRules`, `SqlRules` — 8 tests. Each rule is
checked twice where it can be, once against the compiled assemblies and once against the source
tree, because a rule about code that has not been written yet has no assembly to inspect.
**First red test:** `Core_ReferencesNoMauiAssembly` — passes trivially, and that is the point: it
fails the first time somebody adds the reference.
**Then build:** `DLR.Architecture.Tests` with the §10.4 list that is checkable today —
no MAUI in `DLR.Core`, no `DateTime.Now`/`UtcNow` outside `DLR.TestSupport`, no `XmlDocument`
anywhere, no raw SQL outside the three permitted folders.
**Done when:** each rule has a test, and you have deliberately broken each one once to watch it go
red. A guard you have never seen fail is a guard you do not have.
**Refs:** §10.4

### SRV-03 — CI: build, test, format, licence gate ✅
**Status:** done. `.github/workflows/ci.yml` — three jobs: `build` (restore, build, test, format),
`licences` (the transitive scan), `dco` (sign-off, checked in-repo rather than by a third-party
action). `pull_request`, `permissions: contents: read`, and no job reads a secret, so a fork PR
runs everything.
**First red test:** a deliberately added package with a non-approved licence fails the gate.
**Then build:** GitHub Actions — restore, build, `dotnet test`, `dotnet format
--verify-no-changes`, and a transitive licence scan that fails on **unknown** as well as
disallowed. `pull_request` (never `pull_request_target`) for fork PRs; no job that touches a
secret runs on one. DCO sign-off check.
**Done when:** a fork PR runs the full suite and sees no secrets.
**Refs:** §14.4, §14.6.3, §14.6.4

### SRV-04 — Test harness: Postgres, clock, email ✅
**Status:** done. `PostgresFixture` (one container per collection, a fresh database per factory),
`DlrWebApplicationFactory`, `FakeTimeProvider` starting at a fixed instant, `CollectingEmailSender`.
`Email_CanBeAssertedOnAfterAdvancingTheClockSixMonths` is the proof.
**Amended after SRV-13.** The container is now validated in **every** environment
(`UseDefaultServiceProvider` with `ValidateScopes` and `ValidateOnBuild`), not just Development
where the default puts it. A singleton holding a scoped service passed the entire suite and only
failed under `dotnet run`, because the test host runs as `Testing`. Every server test now builds a
validated container, and `Container_ScopeValidation_IsOnInEveryEnvironment` is the guard on that
guard — without it, deleting the call would restore the blind spot silently.
**First red test:** `Database_Container_StartsAndAppliesMigrations`
**Then build:** in `DLR.TestSupport` — a Testcontainers PostgreSQL fixture shared per collection,
a `WebApplicationFactory` wired to it, `FakeTimeProvider` registered over `TimeProvider`, and a
collecting fake `IEmailSender`. Register `TimeProvider` in DI from day one; retrofitting it is
miserable.
**Done when:** a test can advance the clock six months and assert on a captured email. Everything
in Milestones B–F depends on this and on nothing else external.
**Refs:** §10.4

### SRV-05 — `/api/v1/about` and the AGPL §13 source offer ✅
**Status:** done. `BuildInformation.ForAssembly` reads the SourceLink-embedded commit off
`AssemblyInformationalVersion`; `Directory.Build.targets` appends `+dirty` for a modified tree.
The web footer link is a UI task and is not covered here.
**First red test:** `About_ReturnsSourceUrlAndCommitOfRunningBuild`
**Then:** `About_IsReachableWithoutAuthentication`,
`About_CommitMatchesAssemblyInformationalVersion`
**Then build:** the endpoint, commit embedded at build time via SourceLink —
**never a hand-maintained constant** — with a `+dirty` marker for an unpushed tree.
**Done when:** the running server can state exactly which commit it is. This is small, it is a
licence obligation the moment the server is reachable, and doing it now proves SourceLink works
before anything depends on it.
**Refs:** §14.6.2

---

# Milestone B — Identity

§7.15 is the largest single block of tests in the project. Work it in the order below; each task is
a coherent slice of that list.

### SRV-06 — `AppUser`, Identity configuration, and the username rules ✅
**Status:** done. `AddIdentityCore` (not `AddIdentity` — no cookie scheme until SRV-34),
`IdentityUserContext` rather than `IdentityDbContext` because no §7 rule is expressed as a role,
`UserNameValidator` for the two rules Identity has no setting for, and the `AddIdentity` migration.
`AppUser` deliberately adds no columns yet; the §7.13 fields arrive with the tasks that need them.
**Also built:** `Register_UsernameOutsideLengthBounds_IsRejected`. §7.2 states 3–20 characters and
§7.15 named no test for it, so the rule was going to ship unguarded; §7.15 has been updated.
**Watch out:** the `*.sql`-free schema check in `Register_NullEmails_DoNotCollideOnUniqueIndex`
asserts the index definition directly. PostgreSQL treats NULLs as distinct in a unique index, so
three email-less accounts coexist whether or not the filter exists — the passing count proves
`RequireUniqueEmail = false`, not the constraint.
**First red test:** `Register_UsernameAndPasswordOnly_Succeeds`
**Then:** `Register_NoEmail_AccountIsFullyUsable`,
`Register_DuplicateUsername_IsRejectedCaseInsensitively`,
`Register_MixedCaseUsername_IsStoredAndReturnedAsTyped`,
`Register_UsernameDifferingOnlyByCase_IsRejected`, `Register_NonAsciiUsername_IsRejected`,
`Register_ReservedUsername_IsRejected`, `Register_NullEmails_DoNotCollideOnUniqueIndex`,
`Username_CannotBeChangedByAnyEndpoint`
**Then build:** `AppUser : IdentityUser<Guid>`, `RequireUniqueEmail = false` with the partial
unique index doing the work, `AllowedUserNameCharacters` **including uppercase**, the reserved-name
list, and the first migration.
**Watch for:** the uppercase charset. The lowercase-only default silently rejects `DaveSmith`, and
the username is a map label.
**Refs:** §7.2, §7.13

### SRV-07 — Password policy and the breach check ✅
**Status:** done. Ten characters, all four composition rules explicitly off,
`BreachedPasswordValidator` over `IBreachedPasswordCheck`, and `PwnedPasswordsClient` doing the
k-anonymity range lookup with a 3-second timeout. The fake is on the factory as `app.Breaches`, so
no test reaches the network and the outage clause is reachable on demand.
**Watch out:** the outcome switch names every case and *throws* on the discard. Written with a
permissive default, deleting the `Unavailable` arm changes nothing until a real outage arrives —
which is exactly what happened the first time this was checked by breaking it deliberately.
**First red test:** `Register_WeakOrBreachedPassword_IsRejected`
**Then:** `Register_BreachServiceUnavailable_StillAllowsRegistration`
**Then build:** 10-character minimum, no composition rules, Pwned Passwords range API behind an
interface so the test can fake an outage. **A third-party outage must not stop signups.**
**Refs:** §7.2

### SRV-08 — `/auth/token`: JWT access tokens ✅
**Status:** done. The password grant, `AccessTokenIssuer` (HS256, `kid`, 15 minutes,
`sub`/`unm`/`dev`/`jti`), two-key rotation, `DummyPasswordVerifier`, 5→15-minute lockout, and
`SigningKeySource` refusing to start on a key that ships with the code. The refresh grant returns
400 by name until SRV-09.
**Also built:** tests for the claim set, `kid` rotation, and the signing-key guard. §7.4 specifies
all three exactly and §7.15 named no test for any of them.
**Two things worth knowing before the next task:**
- **Token lifetime is validated against `TimeProvider`, not the ambient clock.** `JwtOptions`
  supplies a `LifetimeValidator` because the issuer stamps `exp` from the project clock — left to
  the library, a test that advanced `app.Clock` would find every token already expired, and
  `Hub_LongLivedConnection_SurvivesAccessTokenExpiry` (SRV-23) would be unwritable.
- **Identity 10 takes no `TimeProvider`.** `AccessFailedAsync` reads the ambient clock, so the
  fifteen minutes is asserted as configuration plus the observable refusal, not as elapsed time.
  It also **resets `AccessFailedCount` to zero when it locks**, so the end date is the only
  durable evidence a lockout happened.
**Amended after SRV-13.** The refusal now names the fix. It said "an environment variable or a
Docker secret", which is true in production and useless on a laptop — the local answer is **user
secrets**, which the guard already permitted (that file is outside the content root) but never
mentioned. `RequiredSettings.ValidateConnectionString` gives the connection string the same
treatment; it previously surfaced as an Npgsql stack trace on the first request that touched a
table. `CONTRIBUTING.md` has a "Running the server itself" section with both commands.
**Startup order:** `SigningKeySource.Validate` runs after `builder.Build()`. Under minimal hosting
a test host still contributes configuration sources while the builder runs, so validating earlier
judges a half-assembled configuration.
**First red test:** `Login_UnknownUsername_ResponseTimingMatchesKnownUsername`
**Then:** `Login_FiveFailures_LocksAccountForFifteenMinutes`
**Then build:** the password grant, HS256 with `kid`, 15-minute lifetime, claims `sub`/`unm`/`dev`/
`jti`, signing key from configuration and never `appsettings.json`. Dummy password verification on
an unknown username so timing does not leak.
**Refs:** §7.4

### SRV-09 — Refresh tokens: rotation, reuse detection, the grace window ✅
**Status:** done. `refresh_token` with SHA-256 at rest, `family_id` chains and `successor_id`;
`RefreshTokenService` implementing §7.4's three branches; `RefreshTokenGraceCache` for the
10-second idempotency window; `SessionFactory` so registration, the password grant and the refresh
grant all produce one shape. Registration now signs you in, per §7.2.
**Pulled forward from SRV-10:** the `device` table, because `refresh_token.device_id` is a foreign
key to it. Two columns only — SRV-10 still owns the session list and `last_seen_utc`. §7.13 never
defined `device`; the shape chosen is recorded in the design.
**Device ids are server-assigned.** A client sends back the id it was given; one belonging to
somebody else does not match and the installation gets its own. Accepting a client-chosen id would
let a guessed GUID attach a session to another rider's device row.
**The grace window costs a bounded exception to "never store the raw token".** The successor sits
in process memory for ten seconds, because returning *the same* successor is the only way to be
idempotent. A restart empties it; a replay inside the window with nothing cached is answered 401
**without** revoking the family, since a restart is not evidence of theft.
**Watch out:** `issued_utc` is not a total order. The fake clock does not tick unless a test moves
it, so a token and its successor share an instant — ordering a chain by time leaves the tiebreak
to a random primary key. Walk `successor_id` instead.
**First red test:** `Refresh_ValidToken_RotatesAndInvalidatesPredecessor`
**Then:** `Refresh_ReusedToken_RevokesEntireFamily`,
`Refresh_ReusedWithinGraceWindow_ReturnsSameSuccessor`, `Refresh_AfterOneYearIdle_StillSucceeds`
**Then build:** `refresh_token` table, SHA-256 at rest, `family_id` chains, `successor_id`, and the
10-second idempotency window keyed on the successor.
**Watch for:** the grace window is not optional. Without it a client that fires two requests and
refreshes twice revokes its own session, and with permanent sessions that is the *most likely* way
anyone is ever logged out.
**Refs:** §7.4, §7.13

### SRV-10 — Devices, sessions and `last_active_utc` ✅
**Status:** done. `device` gains `name`, `created_utc` and `last_seen_utc`; `AppUser` gains
`last_active_utc` with its index. `GET /auth/sessions` and `DELETE /auth/sessions/{deviceId}`,
both authed. `ActivityTracker` writes both rows on the refresh that already happens at app start,
throttled to one an hour by a set-based `ExecuteUpdate` whose `WHERE` *is* the throttle — so two
launches racing cost at most one write. `NewDeviceNotifier` sends the §7.10 alert when an address
is known, and swallows a transport failure rather than undoing a sign-in that already succeeded.
**Revoke ends every family on the device**, not just the newest. A device that signed in twice has
two chains, and revoking one of them is not what somebody who has lost a phone is asking for.
**404, not 403, for another account's device id.** A distinguishable answer would make this an
oracle for whether a device exists.
**Watch out:** `last_active_utc` is stamped at registration. The column is not nullable, so the
default would be year 0001 — an account that reads as two thousand years idle to §7.11 from the
moment it exists.
**First red test:** `RevokeSession_TargetDeviceCannotRefresh`
**Then:** `Refresh_UpdatesLastActiveUtc`, `Refresh_WithinThrottleWindow_DoesNotRewriteLastActive`
**Then build:** `Device`, the session list and revoke endpoints, and the last-active update
**piggybacked on the refresh that already happens** at app start, throttled to one write an hour.
**Refs:** §7.10

### SRV-11 — Email: confirmation, reset, and two token providers ✅
**Status:** done. `DlrTokenProvider` with `EmailConfirmationTokenProvider` (24 h) and
`PasswordResetTokenProvider` (1 h); `MailKitEmailSender` behind the existing `IEmailSender`;
`AccountEmails` for the two link templates; the six §7.14 endpoints.
**Deviates from §7.7's sketch, and the design has been updated.** That sketch subclasses
Identity's `DataProtectorTokenProvider`, which reads `DateTimeOffset.UtcNow` directly — Identity 10
takes no `TimeProvider` anywhere. Three of this task's tests are lifespan *boundary* tests; against
the framework provider they could only have been sleeping tests or nothing. The provider now
implements `IUserTwoFactorTokenProvider` and takes the project's clock. `IDataProtector` still
seals the payload, so no crypto is reinvented.
**Purpose is guarded twice** — the protector's purpose chain and again inside the payload. Each was
verified to stop a cross-purpose token with the other removed, so neither is decoration.
**Reset needs a *confirmed* address.** An address that was typed but never confirmed may belong to
somebody who mistyped it, and honouring it turns a typo into an account takeover.
**First red test:** `ResetPassword_LifespanIsIndependentOfConfirmationLifespan`
**Then:** `ConfirmEmail_TokenJustUnder24Hours_IsAccepted`, `ConfirmEmail_TokenPast24Hours_IsRejected`,
`ResetPassword_TokenPast1Hour_IsRejected`, `ResetPassword_AccountWithoutEmail_HasNoRecoveryPath`,
`ResetPassword_Success_RevokesAllRefreshTokenFamilies`,
`ChangePassword_Success_KeepsCurrentDeviceSignedIn`
**Then build:** `IEmailSender` + MailKit, the separate `PasswordResetTokenProvider`, and the reset /
confirm endpoints.
**Watch for:** write the *first* test first. `TokenLifespan` is global, and one setting silently
changing both lifespans is exactly the bug this test exists to catch.
**Refs:** §7.7, §7.12

### SRV-12 — Abuse: the IP ladder, rate limits, forwarded headers ✅
**Status:** done. `ForwardedHeadersMiddleware` with `KnownProxies` from configuration;
`RegistrationLadder` counting rows; the `rst` claim and the `NotRestricted` policy;
`RequestThrottle` carrying every row of §7.8's table. Thresholds are in configuration per §14.5.
**Not `AddRateLimiter`, and the table is why.** Three of §7.8's rows key on a *username*, an
*email address* or a *device* — all in the request body, none visible to a middleware
partitioner. The throttle is a `TimeProvider`-driven fixed window enforced in the endpoints, so
every row is expressible and testable. In-memory is right here and wrong for the ladder: these
blunt a burst, the ladder decides whether an account may exist.
**Two tests were only half-writable.** `Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide`
and `Restricted_AfterConfirming_CanJoinRide` need rides (SRV-20) and tracks (SRV-16). What exists
now asserts the policy against the tokens the ladder actually issues; the endpoint halves attach
in SRV-20. **Both are now written**, in `Rides/RideJoinTests.cs` — SRV-20 put
`AuthorizationPolicies.NotRestricted` on create and join, which is the surface they needed.
**Two bugs this task surfaced, both fixed:**
- **Registration never sent a confirmation link.** §7.2's flow ends "if email supplied: send 24 h
  confirmation link", and it was missing — so a ladder-restricted account was restricted with no
  way out.
- **A brand-new account got a "new device signed in" alert.** A first device is not a new device,
  and an alert attached to the act that created the account is noise. Suppressed unless the
  account already has another device.
**Watch out — the harness had a hole.** `TestServer` leaves `RemoteIpAddress` null, and
`ForwardedHeadersMiddleware` then skips its `KnownProxies` check entirely: every `X-Forwarded-For`
is honoured whatever the configuration says. Every per-address test still passed, because they all
*want* the header read. `LoopbackConnection` (an `IStartupFilter` in `DLR.TestSupport`) gives the
test host a connection address so the check is real, and
`ForwardedHeader_FromAnUntrustedHop_IsIgnored` is the test that would otherwise never have existed.
**First red test:** `Registration_LadderUsesForwardedClientIp`
**Then:** `Register_FourthAccountFromSameIpInOneDay_RequiresEmail`,
`Register_FourthAccountFromDifferentIp_DoesNotRequireEmail`,
`Register_LadderCountSurvivesProcessRestart`,
`Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide`,
`Restricted_AfterConfirming_CanJoinRide`,
`RateLimit_SixthLoginAttemptInOneMinute_Returns429`,
`RateLimit_PerIpPartitioning_UsesForwardedClientIp`,
`Register_DuplicateEmail_ReturnsGenericSuccessAndNotifiesOwner`
**Then build:** `ForwardedHeadersMiddleware` with `KnownProxies`, the **row-counting** ladder (not
the in-memory limiter), the `rst` claim and `NotRestricted` policy, and the §7.8 rate-limit table.
**Watch for:** the forwarded-headers test is first for a reason. Get it wrong and every signup looks
like it came from Caddy, so the fourth user ever is asked for an email.
**Refs:** §7.8

### SRV-13 — Profile fields and `SharedProfile` ✅
**Status:** done. The three optional fields and three switches, all defaulting false in the object
*and* in the database; `GET`/`PUT /api/v1/me/profile`; `SharedProfile.For` in `DLR.Core`; the
architecture tests `NoApiSurfaceReturnsAppUser` and `NoContractTypeExposesAppUser`.
**"The only way to construct one" is the compiler, not a convention.** `SharedProfile`'s
properties are `private init` and `For` lives in the same assembly, so no other assembly can
build one at all — no architecture test needed for it. `AppUser` reaches the factory through
`IProfileOwner` in `DLR.Core`, which is also why nothing has to reference the persistence
assembly to describe what a viewer may see.
**Deserialisation is deliberately unsolved.** `private init` means `System.Text.Json` cannot
build one back; nothing needs to yet. When a client does (§5.4's member list), that is the moment
to decide, not now.
**Tested in `DLR.Core.Tests`** — its first tests. The rule is a pure function of six switches and
one boolean, so it needs no database, no HTTP and no ride.
**Deferred, as the task says:** the co-membership half of `Profile_NonCoMember_ReceivesEmptyProfile`
lands in SRV-21. What exists now exercises the rule those tests will drive.
**Watch out — the suite outgrew PostgreSQL's default.** `53300: sorry, too many clients already`,
in whichever test happened to run when the hundredth connection was opened. One database per
factory and one factory per test, each with Npgsql's default ceiling of 100 connectors. Pools are
now capped at 5 and the container runs with `max_connections=300`.
**First red test:** `Profile_FreshAccount_AllThreeSharingSwitchesAreOff`
**Then:** `Profile_WithheldAndUnrecorded_AreIndistinguishableOnTheWire`,
`Profile_NonCoMember_ReceivesEmptyProfile`, `Profile_TurningSharingOff_DoesNotDeleteTheValue`,
`Profile_TurningEmailSharingOff_LeavesRecoveryIntact`,
`Profile_PhoneNumberConfirmed_IsNeverSetTrue`
**Then build:** the three optional fields and three switches, and `SharedProfile.For(owner,
viewerSharesActiveRide)` as the **only** way to construct one.
**Also:** the architecture test `NoApiSurfaceReturnsAppUser`.
**Note:** the co-membership tests land properly in SRV-21 when rides exist; write them then.
**Refs:** §7.3

---

# Milestone C — Tracks

`DLR.Core/Tracks/` is pure logic with no I/O, so most of this milestone is fast unit tests in
`DLR.Core.Tests`. Build it there first and let the server task be thin.

### SRV-14 — GPX reader, writer and the hostile corpus ✅ *(reader; writer is SRV-19)*
**Status:** the reader is done. `DLR.Core/Tracks/` holds `GpxReader`, `TrackGeometry`,
`TrackStats` and `TrackPoint`; `GpxFixtures` in `DLR.TestSupport` builds the corpus in code. 22
tests in `DLR.Core.Tests`. The **writer** is not here — nothing needs to emit GPX until export,
and §15.3 is entirely about reading.
**`TrackStats` arrived early.** SRV-15 nominally owns it, but this task's first named test is
`…CreatesTrackWithComputedStats`, so distance, ascent, duration, max speed and bounds are here.
SRV-15 keeps RDP simplification, `TrackEditor` and the edit-recomputation rules.
**Ascent threshold picked, not inherited.** §15.7 says the importer uses "the recorder's noise
threshold, unchanged" — but the recorder does not exist and the design named no number. 3 m,
against a running reference rather than point to point, recorded in §15.7 for the recorder to
adopt rather than re-decide.
**Watch out — `record struct` defaults are a trap.** `GpxLimits` was one, and `new()` on a record
struct zeroes every field instead of running the primary constructor's defaults: both caps were
**0**, so the reader accepted no tracks at all. It is a class now, which makes
`default(GpxLimits)` impossible to write.
**Watch out — `ReadElementContentAsString` advances past the end tag.** A loop that then calls
`Read()` skips the following sibling, so a `<trkpt>` got its `<ele>` and not its `<time>`.
`ElementText` stops on the end element instead. This one read every *other* child, which is the
kind of bug that produces plausible output.
**First red test:** `Import_GpxWithSingleTrack_CreatesTrackWithComputedStats`
**Then, security first:** `Import_DtdDeclaration_IsRejectedWithoutResolvingIt`,
`Import_NestedEntityExpansion_IsRejected`, `Import_ExternalEntityReference_MakesNoNetworkCall`,
`Import_ExceedsPointCap_AbortsMidStreamWithoutBufferingAll`,
`Import_NotXml_ReturnsProblemDetailsNamingTheProblem`, `Import_TruncatedFile_ReturnsProblemDetails…`
**Then, the format's untidiness:** `Import_GpxWithMultipleTracks_CreatesOnePerTrkUpToCap`,
`Import_GpxRouteElement_ImportsAsTrackWithoutTimestamps`,
`Import_GpxWithoutElevation_LeavesAscentNull`, `Import_GpxWithoutTimestamps_LeavesDurationAndSpeedNull`,
`Import_NonMonotonicTimestamps_PreservesGeometryAndDropsTimeStats`,
`Import_OutOfRangeCoordinates_IsRejected`
**Then build:** the streaming `XmlReader` with `DtdProcessing.Prohibit` and a null `XmlResolver`,
plus a **synthetic** hostile fixture corpus in `DLR.TestSupport`.
**Watch for:** the fixtures are generated, never recorded. A real trace starts at your house.
**Refs:** §15.3, §14.2

### SRV-15 — Track stats, simplification and the editor primitive ✅
**Status:** done. `TrackEditor` (one primitive, three gestures), `TrackSimplifier` (RDP),
`PointRange`. `TrackStats` arrived in SRV-14; this task adds the recompute-on-edit guarantee.
All in `DLR.Core` — no server, no database. 29 new tests.
**The validation errors are a result type, not an exception**, unlike `GpxReader`. A malformed
GPX file is found mid-stream by a parser that throws; an edit is validated up front against
indices the caller chose. Refusing one is an answer, not an accident. SRV-18 maps the error to
its 400.
**RDP tolerance picked, not inherited:** 5 m, recorded in §15.5. Below what a rider can
distinguish at any zoom showing a whole ride and inside consumer GPS error, so the simplified
line is the same shape with the noise gone. Iterative rather than recursive — 40 000 points on a
curve that never straightens would recurse deep enough to overflow a phone's stack.
**Watch out — the ascent test did not test ascent.** Removing the 3 m threshold entirely left the
suite green: the fixture climbed monotonically in 1 m steps, and running-reference accumulation
gives the same total either way. It now uses oscillating altitude — a bike at the lights, which
is what the threshold is *for* — where thresholded reads 20 m and unthresholded reads 25.
**Watch out — RDP output size is not obvious.** Pushing one point 50 m off a straight line keeps
five points, not three: once the corner survives, each half is measured against a line running to
it, so points that were on the original line now deviate from the new one. Asserting an exact
count pins the algorithm's internals rather than the property that matters.
**First red test:** `Edit_NoOpEdit_ProducesIdenticalStats`
**Then:** `Edit_TrimStart_RemovesLeadingPointsAndRecomputesStats`, `Edit_TrimEnd_RemovesTrailingPoints`,
`Edit_RemoveInteriorRange_InsertsSegmentBreak`,
`Edit_RemovedSpan_IsExcludedFromDistanceAndDuration`,
`Edit_RecomputedAscent_UsesRecorderThreshold`, `Edit_OverlappingOrDescendingRanges_Returns400`,
`Edit_RangeOutOfBounds_Returns400`, `Edit_LeavingFewerThanTwoPoints_Returns400`
**Then build:** `TrackStats`, RDP simplification, and `TrackEditor` taking half-open raw index
ranges. All in `DLR.Core` — no server, no database.
**Watch for:** the no-op test first. It is the guard that an edit changes only what it removed.
**Refs:** §15.5, §15.7

### SRV-16 — Blob storage and track upload ✅
**Status:** done. `IBlobStore` + `FileSystemBlobStore` over a volume, the `Track` entity with its
nullable stats columns, `POST /tracks` (idempotent), `GET /tracks`, `GET /tracks/{id}`.
`TrackBlobCodec` in `DLR.Core` writes the points. 11 new tests.
**The blob format is lossless and the wire format will not be.** §15.5's points endpoint sends
the editor an encoded polyline, which quantises to about a tenth of a metre — fine there, because
the editor only sends back *indices*. The blob is what an edit re-reads and re-stats from, so a
format that rounded coordinates would make `Edit_NoOpEdit_ProducesIdenticalStats` false the
moment a track was saved. Two formats, for genuinely different reasons.
**Idempotency is the unique index, not the pre-check.** The read before the write only avoids
doing the work twice in the common case; two drains of one outbox arriving together are decided
by `ux_track_owner_client`, and the loser deletes its own blob rather than leaving one for the
§7.11 orphan sweep. Scoped to the owner, because the client picks the identifier — a global
unique index would let one rider's guid collide with another's upload.
**Watch out — the sort test could not tell the two columns apart.** Sorting on `started_utc`
instead of `created_utc` left it green: the fixture uploaded rides in ride order, and PostgreSQL
puts NULLs first on a descending sort, so even the untimed route landed correctly by accident.
The fixture now uploads in the *opposite* order to when the rides happened.
**First red test:** `Upload_SameClientGuidTwice_IsIdempotent`
**Then:** `Upload_StoresBlobAndComputesContentHash`, `TrackList_SortsOnCreatedUtc_NotStartedUtc`
**Then build:** `IBlobStore` over a filesystem volume (**not** object storage — §9.1), the `Track`
entity with its nullable stats columns, `POST /tracks`, and the list/detail endpoints.
**Refs:** §6.2, §8, §9.1

### SRV-17 — GPX import endpoint ✅
**Status:** done. `POST /tracks/import` multipart with `?dryRun=true`, the §15.8 caps as
settings, per-account rate limits, and Problem Details carrying the reader's own problem name and
position. 14 new tests.
**`TrackStore` extracted.** Upload and import both produce a track, and §15.7 is explicit that
three entry points into one pipeline is how ascent comes out different depending on which door
the points used. Stats, simplified line, content hash and blob are now produced in one place.
**Two caps, two reasons.** `MaxUploadBytes` is checked against `Content-Length` *and* the read
file, because the header can lie; `MaxPointsPerFile` is separate and enforced mid-parse, because
a file can be small and still be pathological. Both answer 413.
**Import is all-or-nothing.** A multi-track file that fails partway discards every staged blob
rather than leaving the rider to work out which of their tracks arrived.
**Waypoints are counted, not created.** The preview reports how many markers a file would make;
SRV-26 creates them, as the task list says.
**Watch out — advancing the clock past 15 minutes expires the access token.** The
duplicate-warning test moves a month forward, which is the case that warning exists for, so it
signs in again exactly as a rider would. Any future test that advances time and then calls an
authed endpoint needs the same.
**First red test:** `Import_DryRun_PersistsNothing`
**Then:** `Import_SameContentTwice_WarnsButProceeds`, `Import_ExceedsSizeCap_Returns413`,
`Import_WaypointsPresent_AreCreatedAsMarkers` *(defer the assertion until SRV-26)*
**Then build:** `POST /tracks/import` multipart, `?dryRun=true`, the per-user rate limits, and
Problem Details that name the actual problem.
**Refs:** §15.3

### SRV-18 — Track editing, versioning and undo ✅
**Status:** done. `POST /tracks/{id}/edit` with optimistic concurrency, `TrackRevision` keyed on
`TrackId` so "exactly one per track" is a shape the database enforces, `edit/undo` and
`DELETE /previous-version`. 14 new tests.
**Undo consumes the revision rather than replacing it.** §15.6 calls it a safety net for the last
action; making the pre-undo state the new revision would be a redo, which is the history feature
that section declines to build. Undo is still an edit — the restored points become a *new*
version, so the chain only moves forward and a device never reasons about going backwards.
**403 for a non-owner edit, not 404** — the one place in this API that distinction runs the other
way (§15.4). A share link makes a track's id legitimately known to people who do not own it.
**Deferred:** the Live-ride precondition from §15.4's table. Rides arrive in SRV-20, and changing
a planned route mid-ride silently moves every rider's position in the gap list.
**Watch out — the token expires before the undo window does.** Two tests advance the clock days
and then call an authed endpoint. `HttpClient.SignInAsync` is now in `DLR.TestSupport` rather than
copied per class; anything that moves time more than fifteen minutes needs it.

**First red test:** `Edit_StaleVersion_Returns409`
**Then:** `Edit_ByNonOwner_Returns403`, `Edit_TrackNotFullyUploaded_Returns409`,
`Edit_IndicesApplyToRawPoints_NotSimplifiedPolyline`,
`Edit_SimplifiedPolylineAndContentHash_AreRegenerated`,
`Undo_WithinWindow_RestoresPreviousPointsAsNewVersion`, `Undo_AfterWindow_Returns404`,
`Undo_SecondEditWithinWindow_ReplacesRetainedOriginal`,
`PurgeNow_DeletesRetainedOriginalImmediately`
**Then build:** `POST /tracks/{id}/edit`, `TrackRevision` with `PurgeAfterUtc`, the undo and
purge-now endpoints.
**Watch for:** `Edit_IndicesApplyToRawPoints…`. Editing against the simplified polyline deletes the
wrong points, silently, only on dense tracks.
**Refs:** §15.4, §15.5, §15.6

### SRV-19 — Full-resolution points endpoint ✅
**Status:** done. `GET /tracks/{id}/points` with `PolylineCodec` in `DLR.Core` — encoded polyline
at precision 6, delta-encoded time offsets and elevations, gzipped by `UseResponseCompression`.
9 codec tests, plus SRV-18's editing tests which read through this endpoint.
**Two formats, and the reason is not symmetry.** This one is lossy to about a tenth of a metre,
and that is safe *because* the editor sends back indices rather than coordinates.
`TrackBlobCodec` keeps the exact doubles and is what an edit re-stats from.
**Missing elevation is `PolylineCodec.MissingElevation`, not zero** — sea level is a measurement.
A track with no timestamps sends `null` rather than a run of zeroes, for the same reason.
**First red test:** `Points_ReturnsEncodedPolylineWithDeltaTimes`
**Then build:** `GET /tracks/{id}/points`, gzipped, in the encoding the editor indexes against.
**Note:** this exists for the web editor; the component that consumes it is a UI task.
**Refs:** §15.5

---

# Milestone D — Group rides ✅

### SRV-20 — Rides, join codes and join requests ✅
**Status:** `Rides/JoinCode.cs` (Core), `Rides/GroupRide.cs` + `GroupRideConfiguration.cs`
(Migrations, migration `AddGroupRides`), `Contracts/Rides/RideContracts.cs`,
`Rides/RideEndpoints.cs` (Server, with `RideJoinOptions` and `RideNotifications`).
16 tests in `Rides/RideJoinTests.cs`; suite 247 green.
**First red test:** `JoinByCode_ApprovalRide_CreatesPendingRequestOnly`
**Then:** `JoinByCode_OpenRide_JoinsImmediately`, `JoinRequest_Approved_AddsMemberAndNotifiesRider`,
`JoinRequest_Declined_WithBlock_CannotRequestAgain`, `JoinRequest_SixthPending_IsRejected`
**Then build:** `GroupRide` with `JoinPolicy`, Crockford base32 join codes, the request table with
its partial unique index, the admit/decline endpoints, and the member cap.
**Also:** the join-code rate limit that §14.5 found missing — per-IP and per-account, counting
failures. Do not ship this endpoint without it.
**Watch out — counting failures is the whole point, and it is invisible in a passing suite.**
Both a failures-only limiter and an all-attempts limiter make
`JoinByCode_RepeatedWrongCodes_AreRateLimited` green. Only
`JoinByCode_SuccessfulJoins_AreNotCountedAgainstTheLimit` tells them apart, and moving the
`TryAcquire` call above the ride lookup was the break that proved it.
**Watch out — a blocked rider gets the same 404 as an unknown code.** Anything else hands them the
one fact the organiser was trying not to have a conversation about.
**Watch out — the join code goes only to the organiser.** It is the ride's entire access control,
so a member's copy carrying it lets any member re-share the group the organiser curated.
`JoinCode_IsNeverSentToAnybodyButTheOrganiser` asserts against the raw response body rather than
the `JoinCode` property, because the rule is "a member never receives the code", not "one field is
null" — and it covers the admitted-by-approval path, which reaches membership a different way.
**Watch out — the organiser is a member row from creation.** Otherwise every "is this person in
the ride" check needs two answers, and one of the call sites will eventually only ask one.
**Note:** create and join carry `AuthorizationPolicies.NotRestricted`, which is what finally let
SRV-12's two deferred §7.15 tests be written — `Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide`
and `Restricted_AfterConfirming_CanJoinRide` now live in `RideJoinTests`.
**Refs:** §5.2, §14.5

### SRV-21 — Sharing consent ✅
**Status:** `GroupRideMember.ShareLocation` and `GroupRide.EndedUtc`, `Positions/RiderPosition.cs`
+ `RiderPositionConfiguration.cs` (migration `AddSharingAndPositions`),
`Contracts/Rides/PositionContracts.cs`, `Positions/PositionStore.cs`,
`Positions/PositionEndpoints.cs`, `Rides/MembershipEndpoints.cs`, and the shared-profile route on
`ProfileEndpoints`. 14 tests in `Rides/SharingTests.cs`; suite 261 green.
**First red test:** `Join_DismissedSharingPrompt_LeavesShareLocationFalse`
**Then:** `Join_SharingDeclined_MemberSeesOthersButPublishesNothing`,
`Publish_ByNonSharingMember_IsRejectedAndStoresNothing`,
`Sharing_TurnedOff_DeletesPersistedRowImmediately`,
`Organiser_CannotEnableSharingOnBehalfOfAMember`
**Then build:** `ShareLocation` defaulting **false**, the sharing endpoint, and deletion-on-off.
**Also now:** the deferred §7.3 tests — `Profile_AfterLeavingRide_SharedFieldsAreNoLongerVisible`,
`Profile_AfterRideCompletes_SharedFieldsAreNoLongerVisible`.
**Watch for:** turning sharing off **deletes the row**. Stopping the broadcast leaves a last-known
position at rest, which is precisely what the rider asked you not to keep.
**Watch out — four routes carry that same obligation, not one.** Turning the switch off, leaving,
being removed, and the ride ending all have to delete. Each one is a separate endpoint and each
one was broken separately in the breaking pass; they funnel through `PositionStore.StopSharingAsync`
/ `ClearRideAsync` rather than writing their own delete, because four copies is how one of them
eventually stops doing it. **`rider_position` has no foreign key to `group_ride_member`**, so
removing a member cascades nothing — the delete is genuinely load-bearing, not belt-and-braces.
**Watch out — `SharedProfile` cannot be deserialised, and a test that tries gets silent nulls.**
Its properties are `private init` so `SharedProfile.For` is the only constructor, which also stops
`System.Text.Json` rehydrating it: `GetFromJsonAsync<SharedProfile>` returns an all-null object
that passes *every* "is no longer visible" assertion. Cost an hour. `SharingTests` reads through a
local `ProfileView` mirror instead, which is what the rule is about anyway — the wire form.
**Watch out — the two revocation tests pass vacuously on their own.** An endpoint that always
returned `Empty` satisfies both. `Profile_CoMemberOfActiveRide_SeesTheSharedFields` is the positive
case they are measured against, and it is not optional.
**Watch out — the sharing route is `/sharing/me`, with no user-id form.** The §5.6 asymmetry (the
organiser controls the ride, the rider controls their location) is expressed by the route surface
rather than by a permission check, so `Organiser_CannotEnableSharingOnBehalfOfAMember` asserts a
routing 404 — there is no guard on it that could later be relaxed.
**Note:** `RideMemberSummary` gained `Sharing` and `HasPosition` as separate fields. *Not sharing*
and *no signal* mean completely different things to somebody waiting at a junction (§5.6).
**Note:** ending a ride takes `Immediate | WindDown` and answers **501** to `WindDown` until
SRV-25. Silently ending immediately would break the promise the §5.6 consent copy makes, and
silently keeping positions would be worse.
**Deferred to SRV-22:** publishing writes through EF directly. `RiderPositionCache` and the raw
`UNNEST` upsert go in front of it there; the durability contract does not change.
**Refs:** §5.6, §7.3, §10.1

### SRV-22 — Position cache, flush and rehydration ✅
**Status:** `Positions/RiderPositionCache.cs`, `PositionWriter.cs`, `PositionFlushService.cs`,
`PositionCacheRehydrator.cs`, `RideOptions.cs`; `PositionStore` now writes through the cache.
`DlrWebApplicationFactory.FlushPositionsAsync()` added for tests. 22 tests across
`Positions/RiderPositionCacheTests.cs`, `PositionFlushTests.cs`, `PositionPersistenceTests.cs`;
suite 281 green.
**First red test:** `Upsert_OlderTimestamp_IsIgnored`
**Then the cache:** `Upsert_NewRider_AddsEntryMarkedDirty`,
`Upsert_UnderParallelLoad_LatestTimestampWins`
**Then the flush:** `Flush_WritesOnlyDirtyEntries`, `Flush_ClearsDirtyFlagsAfterSuccess`,
`Flush_LeavesEntriesDirtyWhenWriteFails`, `Flush_NoDirtyEntries_IssuesNoDatabaseCall`,
`Flush_ManyRiders_IssuesExactlyOneCommand`,
`Flush_DoesNotOverwriteNewerRowInDatabase` *(integration — proves the `WHERE` guard)*,
`Shutdown_FlushesPendingEntries`
**Then rehydration:** `Rehydrate_LoadsLiveAndWindingDownRidesOnly`,
`Rehydrate_SkipsPositionsOlderThanStalenessWindow`, `Rehydrate_LoadedEntriesAreNotDirty`,
`Reads_BlockUntilRehydrationComplete`
**Then build:** `RiderPositionCache`, `PositionFlushService`, `PositionCacheRehydrator`,
`PositionWriter` with the `UNNEST` upsert. The one place raw SQL is allowed.
**Watch for:** `Reads_BlockUntilRehydrationComplete` — the gate lives *inside* the cache, because
Kestrel can serve before hosted services have run.
**Watch out — the gate has to open on the failure path too.** `MarkReady()` is in a `finally`. A
rehydration that throws and leaves the gate shut does not degrade to a blank map, it hangs every
read forever. `Rehydrate_WhenTheDatabaseIsUnreachable_StillOpensTheGate` is that test.
**Watch out — testing the timer is a race, twice over.** `BackgroundService.StartAsync` returns as
soon as `ExecuteAsync` reaches its first await, which is not necessarily after the `PeriodicTimer`
exists — so a single `FakeTimeProvider.Advance` can land before anything is listening and then
wait ten seconds of fake time that never elapse. Spin-on-`Task.Yield` failed ~1 run in 4;
advance-once-then-await-a-signal failed the same way. `Flush_OnItsTimer_RunsWithoutAnybodyCallingIt`
advances in a bounded loop until the writer signals. Run a new timer test six times before trusting it.
**Watch out — SRV-21's four DB assertions needed a flush inserted.** Publishing now lands in the
cache, so `Sharing_TurnedOff_DeletesPersistedRowImmediately` and its three neighbours call
`app.FlushPositionsAsync()` first. This makes them *stronger* — the row they delete is now genuinely
persisted — but a future task that changes the write path will break them the same way.
**Note:** deletes do **not** go through the cache-then-flush path. `StopSharingAsync` and
`ClearRideAsync` hit the database first and evict second, because "gone within ten seconds" is not
what a rider turning sharing off asked for.
**Note:** `RideDetail`'s `HasPosition` reads the cache, not the table — a rider who published four
seconds ago has not been flushed yet, and showing them as *no signal* would be wrong for a whole
flush period.
**Deferred to SRV-25:** the rehydrator loads `Live` rides only. The wind-down half of §5.5's rule 1
needs `SharingEndsUtc`, which SRV-25 adds; until then a `Completed` ride has had its rows deleted
anyway, so the omission is not observable.
**⚠ Known gap, not closed by this task — §13 Q29.** A flush already in flight can re-insert a row a
concurrent delete has just removed. One round trip wide and it needs a delete to land inside it, so
it is rare rather than impossible — but what it leaves behind is exactly the position at rest §10.1
forbids, and the §7.11 nightly sweep is the only backstop. Neither ordering of delete-and-evict
closes it; it needs a tombstone the flush filters against, or a membership join in the upsert.
**Close it before live sharing is on for anyone real.**
**Refs:** §5.5

### SRV-23 — The hub: authorisation and fan-out ✅
**Status:** `Hubs/RideHub.cs` (with `IRideClient`), `Hubs/RideBroadcastService.cs`, the
query-string lift on `JwtBearerRegistration`, `MapHub` in `Program.cs`, `RiderPositionCache.RideIds()`.
`Microsoft.AspNetCore.SignalR.Client` added to the test project (licence gate re-run, exit 0).
7 tests in `Hubs/RideHubTests.cs` with `Hubs/HubClient.cs`; suite 288 green.
**First red test:** `Hub_JoinRide_NonMemberIsRejected`
**Then:** `Hub_JoinRide_PendingRequesterIsRejected`, `Hub_ConnectionWithoutToken_IsRejected`,
`Hub_LongLivedConnection_SurvivesAccessTokenExpiry`
**Then build:** `RideHub`, the query-string token lift scoped to `/hubs/ride` **only**,
`CloseOnAuthenticationExpiration` left `false`, and `RideBroadcastService` sending one batch per
ride per 5 s.
**Watch for:** authentication is not authorisation. The membership check is the only thing between
an account and a stranger's location.
**Watch out — the wrong table passes the obvious test.** Checking `group_ride_join_request`
instead of `group_ride_member` still rejects a total stranger, so
`Hub_JoinRide_NonMemberIsRejected` stays green while every pending requester is admitted to the
live map. `Hub_JoinRide_PendingRequesterIsRejected` is the only thing that catches it, and it was
broken separately to prove it.
**Watch out — testing the hub over `TestServer` needs the token put in the query string by hand.**
The .NET SignalR client sets an `Authorization` header on `ClientWebSocketOptions`, which a custom
`WebSocketFactory` cannot carry, so the handshake arrives anonymous and fails 401. `HubClient`
appends `?access_token=` itself — which is not a workaround but the *right* thing to exercise,
since §7.6's lift exists precisely because a browser cannot set that header.
**Watch out — the lift has two halves and only one is obvious.** That it works on `/hubs/ride` is
tested by every other hub test implicitly; that it is *refused everywhere else* has exactly one
guard, `QueryStringToken_IsAcceptedOnTheHubPathOnly`. Widening the path predicate leaks credentials
into access logs, referrers and browser history for the whole API and nothing else notices.
**Note:** `IRideClient` declares only the messages whose features exist — positions, sharing
changes, ride state. §5.3's markers, comments, reactions and polls arrive with SRV-26 and
Milestone F; declaring them now would be a contract neither side implements.
**Note:** §5.3's server-side publish throttle ("extra pushes dropped") is **not built** — it is in
no task's build list. Cheap to add; worth doing before the hub is load-bearing.
**Refs:** §5.3, §7.6

### SRV-24 — Multi-ride publishing ✅
**Status:** the publish path already carried no ride id from SRV-21; this task added
`RideOptions.MaxConcurrentLiveRidesPerUser` and `POST /group-rides/{id}/start` on
`MembershipEndpoints`. 5 tests in `Rides/MultiRideTests.cs`.
**First red test:** `Publish_SharingInRideAOnly_StoresNoRowForRideB`
**Then:** `Publish_MemberOfThreeLiveRides_WritesToAllThree`,
`LiveRideCap_ExceedingMaxConcurrent_IsRejectedAtRideStart`
**Then build:** `PublishPosition` carrying **no ride id** — the server fans out to every ride where
that rider's own consent flag is set.
**Watch for:** the filter is on the **write**. A rider not sharing with a ride has no row in it at
all — not a hidden pin.
**Watch out — none of SRV-21's sharing tests catch a global consent check.** Replacing the
per-membership `ShareLocation` with "does this rider share with *any* ride" leaves all fourteen
`SharingTests` green, because each of them only ever has one ride in play.
`Publish_SharingInRideAOnly_StoresNoRowForRideB` is the only guard, which is the whole reason this
task is separate from SRV-21.
**Decision — the cap is counted for the organiser starting the ride**, not for every member.
§5.7 says "enforced when a ride goes Live" without saying whose count is checked. Counting all
members would let one rider who is already in five live rides block a ride for fifty other people,
which is a denial of service dressed as a quota. The cost §5.7 is protecting against is a rider's
own downlink, so the actor-scoped reading is the defensible one — but it does mean a member can
still end up in more live rides than the cap by joining rides other people start.
**Note:** `POST /group-rides/{id}/start` is new here. `Publish_RideNotYetLive_StoresNothing` pins
the outer gate: the lifecycle decides whether a ride takes positions at all, and consent decides
whose.
**Refs:** §5.7

### SRV-25 — Ride end and the sharing wind-down ✅
**Status:** `GroupRide.SharingEndsUtc` (migration `AddSharingWindDown`), the wind-down arm of
`EndAsync` on `MembershipEndpoints`, `Positions/SharingWindDownService.cs`; `PositionStore` and
`PositionCacheRehydrator` both widened to "Live **or** inside an unexpired window".
9 tests in `Rides/WindDownTests.cs`; suite 302 green.
**First red test:** `RideEnd_WindDown_ExpiresServerSideWithoutAnyClient`
**Then:** `RideEnd_DefaultChoice_DeletesAllPositionsImmediately`,
`RideEnd_WindDown_KeepsSharingMembersPublishing`, `RideEnd_WindDown_CannotBeExtended`,
`RideEnd_WindDown_OrganiserCanEndItEarlyForEveryone`,
`RideEnd_WindDown_RiderStoppingDeletesOnlyTheirRow`,
`Rehydrate_RideInUnexpiredWindDown_IsLoaded`, `Rehydrate_RideInExpiredWindDown_IsNotLoaded`
**Then build:** `SharingEndsUtc`, the end-state endpoint taking `Immediate | WindDown`, and
`SharingWindDownService` on a `PeriodicTimer`.
**Watch for:** the first test is the whole point. A bounded window that depends on a client to
honour it is an unbounded window, and a flat battery must not leave someone broadcasting.
**Watch out — the wind-down must *continue* consent, never grant it.** The publish filter reads
`ShareLocation && (Live || unexpired window)`. Moving the window clause into the consent half —
`(ShareLocation || unexpired window)` — puts a rider who deliberately had sharing **off** onto the
map the moment the ride ends, which is the exact inverse of what the feature is for. It is a
one-parenthesis mistake and `RideEnd_WindDown_DoesNotStartSharingForSomebodyWhoHadItOff` is the
only thing that catches it.
**Watch out — "cannot be extended" needs the deadline re-read after the refusal.** Asserting only
on the 409 would pass against code that returns Conflict *and* moves `SharingEndsUtc` anyway.
**Watch out — `SharingEndsUtc` is computed from the server clock and the configured cap, never
from anything the caller sends.** That is what makes un-extendability a property of the shape
rather than a validation rule somebody can relax.
**Watch out — ending early and the default ending are the same code path**, deliberately. Both
clear the window, delete every row and switch every member off. `EndedUtc` uses `??=` so an early
stop does not move the moment the ride ended, which §17.6's thirty-day archival counts from.
**Watch out — advancing the clock past 15 minutes expires the access token.** Three of these tests
move time by 30–121 minutes and every client involved needs re-authenticating, including ones that
only *read*. Cost two rounds of 401s. `ReauthenticateAsync` is the local helper.
**Note:** the rehydrator now implements both halves of §5.5's rule 1.
`Rehydrate_RideInExpiredWindDown_IsNotLoaded` covers the gap where a process dies after the
deadline but before the sweep — the rows are still there, and must not come back.
**Refs:** §5.6

---

# Milestone E — Content

### SRV-26 — Markers
**First red test:** `Marker_WithBothParents_IsRejectedByCheckConstraint`
**Then:** `Marker_WithNeitherParent_IsRejectedByCheckConstraint`,
`Marker_NullDirection_IsStoredAsNullNotZero`, `Marker_DirectionOutOfRange_Returns400`,
`Marker_UnknownIconKey_IsStoredAndRendersAsFallback`, `Marker_TitleOverLimit_Returns400`,
`Marker_OnGroupRide_ByNonMember_Returns403`, `Marker_OnGroupRide_AnyMemberMayCreate`,
`Marker_EditByOtherMember_Returns403`, `Marker_DeleteByOrganiser_Succeeds`,
`Marker_ExceedingPerRideCap_Returns409`
**Then build:** the `Marker` table with its exclusive-arc `CHECK`, the create/edit/delete endpoints,
and the GPX waypoint mapping both ways (`Gpx_WaypointsImportAsMarkers`,
`Gpx_MarkerRoundTrip_IsLossless`, `Gpx_WaypointLinkElement_IsIgnoredAndMakesNoRequest`).
**Refs:** §16.1, §16.2, §16.6

### SRV-27 — Photos: the only image decode path
**First red test:** `Photo_ExifGpsTag_IsAbsentFromStoredImage`
**Then:** `Photo_ExifOrientation_IsAppliedBeforeStripping`,
`Photo_AllMetadata_IsAbsentAfterReEncode`, `Photo_DecompressionBomb_IsRejectedBeforeAllocating`,
`Photo_ExceedsByteCap_Returns413`, `Photo_NotAnImage_ReturnsProblemDetails`,
`Photo_ContentTypeLies_IsDetectedBySniffing`, `Photo_LargeImage_IsDownscaledAndThumbnailed`
**Then build:** `DLR.Server/Photos/` with SkiaSharp — decode, apply orientation, downscale,
re-encode with **no metadata written**, generate a thumbnail. `POST /photos` returning a `photoId`.
**Also:** the architecture test that image decoding happens nowhere else.
**Watch for:** the first test is a privacy guarantee, not a feature. EXIF GPS would put back the
exact house §15.6 lets a rider trim off a track.
**Refs:** §16.4

### SRV-28 — Ride content permissions
**First red test:** `Permissions_MarkersOff_MemberPostReturns403`
**Then:** `Permissions_CommentsOff_MemberMayStillReactAndVote`,
`Permissions_PhotosOff_TextCommentStillSucceeds`,
`Permissions_TurnedOff_ExistingContentIsUntouched`,
`Permissions_OrganiserIsNeverRestrictedByOwnSwitches`
**Then build:** the three `Allow*` flags defaulting **true**, the permissions endpoint, and
server-side enforcement on every content write.
**Watch for:** turning a switch off stops new content and deletes nothing.
**Refs:** §5.8

### SRV-29 — The thread: comments, ordering, pinning
**First red test:** `Comment_PostedOffline_OrdersByServerReceiptNotAuthoredTime`
**Then:** `Comment_ClientClockInFuture_IsClampedToReceiptTime`,
`Comment_StaleAuthoredTime_IsSurfacedAlongsidePostedTime`,
`Comment_WithNeitherBodyNorPhoto_Returns400`, `Comment_ByNonMember_Returns403`,
`Comment_EditAfterWindow_Returns409`, `Comment_DeleteByOrganiser_Succeeds`,
`Comment_ExceedingRideCap_Returns409`, `Pin_ByOrganiser_MovesCommentToTopOfThread`,
`Pin_ByOrdinaryMember_Returns403`, `Pin_ExceedingMaxPinned_Returns409`,
`ArchivedRide_ThreadIsReadOnly`, `RideCompleted_DeletesPositionsButKeepsThread`,
`MemberRemoved_KeepsPostsButRevokesAccess`
**Then build:** `RideComment`, cursor pagination with pinned first, the edit window, and the
clamped `CreatedUtc`.
**Refs:** §17.2, §17.3, §17.6

### SRV-30 — Reactions and polls
**First red test:** `Reaction_SecondReactionBySameUser_ReplacesTheFirst`
**Then:** `Reaction_Cleared_RemovesTheRow`,
`Reaction_Response_CarriesAggregateCountsNotIndividualRows`,
`Reaction_ManyInQuickSuccession_CoalescesIntoOneHubMessage`,
`Poll_WithFewerThanTwoOptions_Returns400`, `Poll_SingleSelect_ChangingVoteReplacesIt`,
`Poll_MultiSelect_TogglesOptionsIndependently`, `Poll_VoteAfterClose_Returns409`,
`Poll_ClosesUtcElapsed_RejectsVotesWithoutABackgroundJob`,
`Poll_Results_AreAttributedToVoters`, `Poll_IsPinnableAndReactableLikeAnyComment`
**Then build:** `CommentReaction` keyed `(comment, user)`, the coalescing broadcast timer, and
`Poll`/`PollOption`/`PollVote` hanging off a comment rather than standing alone.
**Watch for:** `Poll_ClosesUtcElapsed_RejectsVotes**WithoutABackgroundJob**` — expiry is evaluated
on read, not swept.
**Refs:** §17.4, §17.5

---

# Milestone F — Operations

### SRV-31 — Moderation: reports and blocking
**First red test:** `Report_SnapshotSurvivesDeletionOfTheComment`
**Then:** `BlockedUser_CommentsAreHiddenFromTheBlocker`
**Then build:** `ContentReport` with its content snapshot, the report endpoints for both markers and
comments, and blocking that hides comments **and** markers.
**Watch for:** this ships **before the first store submission**, not before the first comment
(§11 sequencing note). It is a review requirement, not an optional nicety.
**Refs:** §16.5, §17.7, §10.2

### SRV-32 — The nightly maintenance job
**First red test:** `Cleanup_DryRunEnabled_DeletesNothingButLogsCandidates`
**Then, one per clause:** `Cleanup_EmptyAccountIdle180Days_IsDeleted`,
`Cleanup_AccountWithOneTrack_IsNeverDeleted`, `Cleanup_AccountWithRideMembership_IsNeverDeleted`,
`Cleanup_AccountOwningRide_IsNeverDeleted`, `Cleanup_AccountWithPendingJoinRequest_IsNeverDeleted`,
`Cleanup_IdleAccountAt179Days_IsNotDeleted`
**Then the rest:** `Cleanup_At150Days_SendsWarningWhenEmailKnown`,
`Cleanup_At150Days_SendsNothingWhenNoEmail`, `Cleanup_RespectsMaxDeletesPerRun`,
`Cleanup_ReleasesUsernameForReuse`, `Cleanup_DeletedAccountRefresh_ReturnsDistinguishableReason`,
`Cleanup_NullsRegistrationIpAfter30Days`, `NightlySweep_PurgesRevisionsPastPurgeAfterUtc`,
`NightlySweep_DeletesOrphanedPhotoBlobs`
**Then build:** `NightlyMaintenanceService` carrying all seven sweeps, `DryRun` defaulting **true**,
a kill switch, and a per-run batch cap.
**Watch for:** dry-run first, and every clause of the deletion predicate gets its own test. This is
destructive code on a timer.
**Refs:** §7.11, §15.6, §16.6

### SRV-33 — Export and account deletion
**First red test:** `AccountDeleted_RemovesCommentsReactionsAndVotes`
**Then:** `Photo_AccountDeleted_RemovesBlobsFromObjectStorage`,
`Export_IncludesRetainedRevisionWhileItExists`, `AccountDeletion_CascadesTrackRevisions`
**Then build:** `GET /me/export` and `DELETE /me`, with **explicit blob deletion** — `ON DELETE
CASCADE` does not reach the filesystem.
**Refs:** §6.3, §16.6, §10.1

### SRV-34 — Web session cookie and the MapKit token endpoint
**First red test:** `WebAuth_RefreshTokenIsNotReadableFromJavaScript`
**Then:** `WebAuth_SessionExpiresAfterConfiguredDays`, `MobileAuth_SessionStillNeverExpires`
**Then build:** the `HttpOnly`/`Secure`/`SameSite` refresh cookie for browser callers with
antiforgery on the token endpoint, the 30-day sliding web expiry, and the static-rendered
login/logout/register form posts — **a cookie cannot be set from inside a running WASM client**.
**Also:** `GET /maps/token` minting a short-lived ES256 MapKit JS token. The `.p8` never leaves the
server; an unavailable token must produce a stated error, not a blank map.
**Refs:** §7.5, §18.5, §4.5

### SRV-35 — Deployment, backups and alerts
**First red test:** `Health_ReturnsOkWithMigrationsApplied`
**Then build:** the Dockerfile, `docker-compose.yml` with the `pgdata` and `blobs` volumes, the
`Caddyfile` (TLS, HTTP/3, brotli, `X-Forwarded-For`), `restic` to Backblaze B2 **encrypted
client-side**, and alerting on the nightly run plus **disk usage**.
**Watch for:** verify `ForwardedHeaders` in staging **before the first public signup** — it is
load-bearing for registration, not just rate limiting. And run a restore drill; a backup you have
never restored is a hope.
**Refs:** §9, §9.1, §7.8

---

## What is deliberately not here

- **`DLR.UI` components and both hosts** — the shared Razor library, the two map JS modules, the
  MAUI Blazor Hybrid app and the WASM client. Separate list.
- **Background location, the recording pipeline, the outbox** — device work, and the Phase 0 spike
  that gates it (§4.3, §18.3).
- **Android Auto and CarPlay** — native, Phase 3, and gated on the `androidx.car.app` binding
  question (§4.6).
- **The Phase 0 spikes themselves.** They are not TDD tasks; they are questions with written
  answers, and three of them (car binding, MapKit JS on Android, WebView map battery) can change
  tasks in this list.

## Before the repository goes public

Not a milestone, because it cuts across all of them — but nothing here is optional at the push
(§14.5): `LICENSE` and `LICENSE.exceptions` (SRV-01), `/api/v1/about` with a matching footer link
(SRV-05), the CI licence gate (SRV-03), `SECURITY.md` with a private reporting channel, and the
join-code rate limit (SRV-20).
