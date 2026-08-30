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
| **D — Group rides** ✅ | SRV-20 … SRV-25 | Phase 2 | Join, consent, live positions. **The wind-down was removed again in SRV-36** |
| **E — Content** ✅ | SRV-26 … SRV-30 | Phase 2 | Markers, photos, the thread, polls |
| **F — Operations** ✅ | SRV-31 … SRV-35 | Phase 2–3 | Moderation, the nightly job, deployment, backups |

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

### SRV-07 — Password policy ✅
**Status:** done, and twice revised. v0.22 replaced the original ten-characters-no-composition rule
with six characters plus one each of uppercase / lowercase / digit, so every rejection comes back as
a message the client can render. **v0.23 removed the breach check entirely** at operator request —
`IBreachedPasswordCheck`, `BreachedPasswordValidator`, `PwnedPasswordsClient` and the factory's
`app.Breaches` fake are all gone, and `ApplyPasswordPolicy` in `IdentityRegistration` is now the
whole policy.
**Watch out:** registration no longer calls anything outside the process, so there is no outage
clause left to get wrong — and equally nothing stopping `Passw0rd1`, which the composition rules
alone accept. That is the accepted trade, not an oversight.
**Tests:** `Register_PasswordMissingARequirement_IsRejected` (a theory, one case per rule),
`Register_WeakPassword_IsRejectedAndCreatesNoAccount`,
`Register_PasswordRejection_CarriesPerFieldMessagesTheClientCanRender`,
`Register_PasswordMeetingEveryRequirement_IsAccepted`.
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
**Extended in v0.28 — the home private area is now a sub-resource of `/me`.** `GET`/`PUT`/`DELETE
/api/v1/me/private-area` on the same controller, three nullable columns on `asp_net_users`, and
`PrivateAreaTests` beside `ProfileTests`. It is deliberately **not** three more fields on
`UpdateProfileRequest`: that request replaces the whole profile, so an area carried inside it
would be erased by any client that had not been taught about it, and
`SavingTheProfile_DoesNotDisturbThePrivateArea` is the test that says so. Nothing was added to
`SharedProfile` — `AnotherRider_CannotSeeIt_OnAnyRoute` asserts the payload rather than the type,
so a field added there later fails here rather than shipping (§10.1, §7.14).
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
**The join code goes to every member** *(widened in v0.29; SRV-20 shipped it organiser-only)*.
`JoinCode_IsSentToEveryMember` covers both ways into a ride — joining an open ride with the code,
and being admitted to an approval ride — because they reach membership differently and have to
agree. See §5.2 for why the earlier rule was dropped; the export rule (§6.3) is unchanged.
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
**Note:** ending a ride took `Immediate | WindDown` and answered **501** to `WindDown` until
SRV-25. Silently ending immediately would break the promise the §5.6 consent copy makes, and
silently keeping positions would be worse. ~~Both endings~~ — **superseded by SRV-36: there is no
ending.**
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
**Deferred to SRV-25, then deleted by SRV-36:** the rehydrator loaded `Live` rides only, and the
wind-down half of §5.5's rule 1 needed `SharingEndsUtc`. With no adventure state at all the
rehydrator's filter is the staleness window and nothing else.
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
**Superseded by SRV-36.** The cap had exactly one enforcement point — the start transition — so
deleting the transition left it with none, and `Ride:MaxConcurrentLiveRidesPerUser` went with it.
The decision above is kept because it is the reasoning anybody re-adding a cap has to answer, and
§5.7 already names where it would go instead: the publish fan-out.
**Note:** `POST /group-rides/{id}/start` was new here and is gone in SRV-36.
`Publish_RideNotYetLive_StoresNothing` pinned an outer gate that no longer exists — consent is now
the only gate there is.
**Refs:** §5.7

### SRV-25 — Ride end and the sharing wind-down ✅ — **entirely removed by SRV-36**

> Kept as the record of why the wind-down existed and what it cost, not as a description of the
> code. Nothing below this line is in the product any more. Read SRV-36 for what replaced it.
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

### SRV-26 — Markers ✅
**Status:** `Core/Markers/MarkerIcons.cs` + `MarkerText.cs`, `Contracts/Markers/MarkerContracts.cs`,
`Core/Tracks/GpxWriter.cs` (+ `DlrGpx`, waypoint direction on `GpxReader`),
`Migrations/Markers/Marker.cs` + `MarkerConfiguration.cs` (migration `AddMarkers`),
`Server/Markers/` (`MarkerOptions`, `MarkerEndpoints`, `WaypointImporter`),
`Server/Tracks/TrackExportEndpoint.cs`, and the three `Marker*` messages on `IRideClient`.
21 tests across `Markers/MarkerTests.cs` and `Markers/GpxMarkerTests.cs`; suite 322 green.
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
**Watch out — the round trip does NOT guard the null-direction rule.** Storing `DirectionDeg ?? 0`
still round-trips losslessly (0 goes out, 0 comes back), so
`Marker_NullDirection_IsStoredAsNullNotZero` is the only thing between "no bearing" and every fuel
stop claiming to point due north. Broken separately to prove it.
**Watch out — an unknown icon must survive `ForSymbol`, not just `ToGpxSymbol`.** The first
implementation exported `ferry` correctly and then flattened it to `note` on the way back in,
because the import mapper only preserved keys this version knows. `Gpx_MarkerRoundTrip_IsLossless`
caught it. The rule is: a symbol *shaped* like a key is kept as one; anything else
("Flag, Blue", "Scenic Area") falls back, so foreign files do not become junk icon keys.
**Watch out — `GpxWriter` needs a `StringWriter` that admits to being UTF-8.** The default reports
UTF-16, so `XmlWriter` emits `encoding="utf-16"` into bytes that are UTF-8, and our own reader
rejects the file with "no Unicode byte order mark". Cost one round trip to find.
**Watch out — waypoints-only GPX has nowhere to put its markers**, and that is correct: a marker
needs exactly one parent. `GpxFixtures.WithWaypoints` has no `<trk>`, so it is the wrong fixture
for import tests — `TrackWithWaypoints` was added for that.
**Watch out — do not regenerate a migration by deleting its files.** `migrations add` diffs the
model against `DlrDbContextModelSnapshot.cs`, which still contains the deleted migration's tables,
so it produces an *empty* migration and the table silently never gets created. Every marker test
then fails with `relation "marker" does not exist`. Use `dotnet ef migrations remove`, or roll the
snapshot back from the previous migration's `.Designer.cs` first. To break a schema constraint in
a breaking pass, edit the **migration file's** SQL string instead — no regeneration needed.
**Watch out — a newly generated migration is not formatted.** Run `dotnet format` after
`migrations add` or the IDE0161 file-scoped-namespace check fails the build gate.
**Note:** `EditByOtherMember` and `DeleteByOrganiser` share one guard (`CanWriteAsync`), so
breaking it reddens both — that is the guard working, not a redundant test.
**Deferred to SRV-27:** `Marker.PhotoId`. It is a foreign key to a table that does not exist yet.
**Deferred to SRV-28:** the `AllowMemberMarkers` switch (§5.8) and its enforcement.
**Refs:** §16.1, §16.2, §16.6

### SRV-27 — Photos: the only image decode path ✅
**Status:** `Server/Photos/` (`PhotoOptions`, `ImageIngest`, `PhotoEndpoints`),
`Contracts/Photos/PhotoContracts.cs`, `Migrations/Photos/Photo.cs` + `PhotoConfiguration.cs`
(migration `AddPhotos`), `Marker.PhotoId` and `PATCH /markers/{id}/photo`, and the architecture
rules `ImageDecodingHappensInOnePlaceOnly` / `OnlyTheServerLinksAnImagingLibrary`.
`ImageFixtures` in `DLR.TestSupport` builds the corpus — EXIF assembled byte by byte.
10 tests in `Photos/PhotoTests.cs` plus 2 architecture tests; suite 334 green.
**SkiaSharp 4.151.0 (MIT), plus `NativeAssets.Linux` for CI.** Licence gate re-run, exit 0.
**An ICC colour profile is metadata, and it nearly shipped.** `SKBitmap.Copy` preserves the
decoded colour space and the JPEG encoder writes it straight back out as an `APP2` segment. The
sharp part is *which* path leaked: the one needing neither rotation nor downscaling, because the
rotate and resize routes already build their target from an `SKImageInfo` with no colour space.
That is the small upright photograph — the ordinary case. Caught only because
`Photo_AllMetadata_IsAbsentAfterReEncode` asserts on the file's **segment structure** rather than
on three named tags; "no `Exif` marker" would have been green. §16.4 updated.
**The bomb fixture has to fail two different ways.** A decompression bomb whose image data is
*valid* looks identical from outside whether the pixel cap ran before or after the decode. This
one's stream is deliberately unusable, so decode-first answers `400 DecodeFailed` and header-first
answers `413 TooManyPixels`. Without that, the test is green against a cap that runs too late to
have helped. 69 bytes declaring 30000 × 30000.
**The pixel multiply is a `long`.** 60000 × 60000 is a writable PNG header and overflows an `int`
into a small positive number, which passes a cap written the obvious way.
**Fixtures are generated, never recorded** — the same rule as the GPX corpus, with more force. A
recorded fixture for the GPS test would be a real photograph with real coordinates, committed to a
repository that is going public. The EXIF here is a hand-built TIFF with a fixed layout, and the
test asserts the *exact made-up coordinate bytes* are absent rather than "some GPS tag".
**Watch out — two flat colours compress to nothing.** `Photo_ExceedsByteCap_Returns413` first
asserted a 1600 × 1200 fixture exceeded 20 KB; it is 12 KB. A fixture large enough to exceed a
realistic 12 MB cap would have to be noise, so the cap is lowered to meet the fixture instead.
**Watch out — the architecture rule is scoped to `src/`,** like `SqlRules`. A test has to decode
the stored image to assert a portrait photograph came out portrait; constraining `tests/` would
make the guarantee unassertable. The metadata half (`OnlyTheServerLinksAnImagingLibrary`) covers
what the text rule cannot see — it fails the moment the package reference is added, before any
call site exists.
**Broken deliberately, four times:** the cap ordering, the profile strip, the orientation, and a
second decode path in `Tracks/`. Each reddened exactly one test.
**Deferred to SRV-28:** the `AllowPhotos` switch. **Deferred to SRV-33:** explicit blob deletion
on account deletion — `ON DELETE CASCADE` does not reach the filesystem.
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

### SRV-28 — Ride content permissions ✅ *(two tests deferred — see below)*
**Status:** `GroupRide.AllowMemberMarkers` / `AllowMemberComments` / `AllowMemberPhotos` (migration
`AddContentPermissions`), `Rides/RideContentPermissions.cs`, `PUT /group-rides/{id}/permissions` on
`MembershipEndpoints`, `RidePermissions` on `RideDetail`, and `RidePermissionsChanged` on
`IRideClient`. Enforcement on marker creation and on photo attachment.
5 tests in `Rides/PermissionTests.cs`; suite 339 green.
**Defaults are on in the object *and* in the column.** The property default covers rides this code
creates; `HasDefaultValue(true)` covers the rides that already exist when the migration runs. A
permission defaulting off for existing rows would have silently muted every ride in flight the
moment this shipped. Same treatment SRV-13 gave the profile switches, for the opposite default.
**One method, not a check per endpoint.** Markers, comments and both photo attachments are four
write paths carrying one obligation — the same shape as SRV-21's four delete routes, and four
copies is how one of them eventually stops applying. `RideContentPermissions.Allows` is the only
place that answers, and its unrecognised-content arm **throws** rather than returning true: a new
kind of content nobody wired a switch to must not be permitted by omission.
**The photo switch bites on the attach, not the upload.** `POST /photos` is deliberately ride-less
(§16.4) — the photograph is taken at the top of a hill and uploaded whenever there is signal — so
it has no ride whose switch it could read. The check is on `PATCH /markers/{id}/photo`, and will be
on a comment carrying a `photoId`. §5.8 updated to say so.
**Watch out — `Owner` alone is the wrong exemption.** §5.8 exempts the organiser *and leaders*, and
a check written as "is the owner" passes every other assertion in the file.
`Permissions_OrganiserIsNeverRestrictedByOwnSwitches` promotes a third account to `Leader` for
exactly this reason, and it was broken separately to prove it.
**Watch out — a switch that falls through to a neighbour is invisible.** Making `Photo` read
`AllowMemberMarkers` leaves four of the five tests green;
`Permissions_PhotosOff_MarkerWithoutPhotoStillSucceeds` is the only one that separates them, which
is the whole reason §5.8 makes photos their own switch.
**Watch out — `RideDetail` gained a field**, so anything constructing it positionally needs the
`Permissions` argument.
**Deferred, and the reason is task order.** `Permissions_CommentsOff_MemberMayStillReactAndVote`
and `Permissions_PhotosOff_TextCommentStillSucceeds` both need a thread, which arrives in SRV-29
and SRV-30. The switch and its enforcement hook exist now; **both tests are written in SRV-29/30**,
the same way SRV-12's two ride-dependent tests waited for SRV-20. The marker-shaped half of the
photo rule is asserted now as `Permissions_PhotosOff_MarkerWithoutPhotoStillSucceeds`.
**First red test:** `Permissions_MarkersOff_MemberPostReturns403`
**Then:** `Permissions_CommentsOff_MemberMayStillReactAndVote`,
`Permissions_PhotosOff_TextCommentStillSucceeds`,
`Permissions_TurnedOff_ExistingContentIsUntouched`,
`Permissions_OrganiserIsNeverRestrictedByOwnSwitches`
**Then build:** the three `Allow*` flags defaulting **true**, the permissions endpoint, and
server-side enforcement on every content write.
**Watch for:** turning a switch off stops new content and deletes nothing.
**Refs:** §5.8

### SRV-29 — The thread: comments, ordering, pinning ✅
**Status:** `Migrations/Comments/RideComment.cs` + `RideCommentConfiguration.cs` (migration
`AddRideComments`), `Contracts/Comments/CommentContracts.cs`, `Server/Comments/` (`CommentOptions`,
`CommentEndpoints`), and four `Comment*` messages on `IRideClient`.
16 tests in `Comments/CommentTests.cs`; suite 355 green.
**Watch out — a string-converted enum cannot be cast inside a projection.** `Kind` is
`HasConversion<string>()`, and `(CommentKindDto)comment.Kind` in a translated `Select` compiles
perfectly and then asks PostgreSQL to cast the text `'Text'` to an integer:
`22P02: invalid input syntax for type integer`. Fifteen of sixteen tests failed at once. It is a
comparison now — `comment.Kind == RideCommentKind.Poll ? … : …`. Note `RideEndpoints` does the same
cast safely, because there the entity is materialised first and the cast runs in memory.
**The cursor tiebreaks on `Id`, and that is not belt-and-braces.** The fake clock does not tick
unless a test moves it, so two comments genuinely share a `PostedUtc`; a cursor keyed on time alone
would skip one or serve it twice. Same trap SRV-09 hit with `issued_utc`.
**Pinned posts come back in their own list**, not merged into the page. They render above
everything *regardless of age*, which a single ordered page cannot express without the client
re-sorting — and they are the only posts that survive pagination on first load (§17.6).
**The edit window is measured from receipt, not from the authored time.** Measuring from
`CreatedUtc` would make a post composed offline four hours ago arrive already un-editable, which is
the opposite of what the window is for.
**Idempotency is checked before the throttle and before the cap.** A re-sent post is not a new
post, so charging it against either would let a flaky connection exhaust a rider's own allowance.
The unique index on `(ride, author, client_guid)` is what actually enforces it.
**`Photo` cascades from a comment where it `SetNull`s from a marker**, and the `CHECK` is the
reason: a marker keeps a required title and survives losing its picture; a photo-only comment has
nothing left and would violate "body or photo" the moment the column was nulled.
**Watch out — the edit-window test cannot avoid re-authenticating.** The window is 15 minutes and
so is the access token, so reaching the far side of one is reaching the far side of the other.
`ReauthenticateAsync` is the local helper; `Comment_PostedOffline…` needs it too, and its second
gap was cut to 8 minutes so one sign-in covers the rest.
**Watch out — two response codes are not what they look like.** Publishing a position answers
**200** (it returns the ride list), and ending a ride answers **204**. Both were asserted wrong
first.
**Broken deliberately, twice:** ordering on `CreatedUtc` instead of `PostedUtc`, and trusting the
client's authored time instead of clamping it. Each reddened exactly one test.
**Also landed here:** `Permissions_PhotosOff_TextCommentStillSucceeds`, deferred out of SRV-28 —
photos are their own switch precisely so turning them off leaves conversation alone. Plus
`Comment_SameClientGuidTwice_IsIdempotent`, which §17.3 requires and §17.10 named no test for.
**Deferred to SRV-30:** `Permissions_CommentsOff_MemberMayStillReactAndVote`, which needs
reactions and votes to exist. The `Comment` switch and its enforcement are in place now.
**Not built, and in no task's list:** the §17.6 push table. Nothing here notifies — delivering a
post over the hub is not notifying about it — and §17.1's *"ordinary comments are silent while the
ride is `Live`"* is a client rule with no server half yet. `Notify_*` and `Car_ThreadIsNotRendered…`
are UI tasks.
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

### SRV-30 — Reactions and polls ✅
**Status:** `Core/Comments/ReactionKeys.cs`, `Contracts/Comments/PollContracts.cs`,
`Migrations/Comments/CommentReaction.cs` + `Poll.cs` + `PollConfiguration.cs` (migration
`AddReactionsAndPolls`), `Server/Comments/` (`ReactionEndpoints`, `ReactionBroadcastService`,
`CommentReactions`, `CommentPolls`), `ReactionsUpdated`/`PollUpdated` on `IRideClient`, and
`DlrWebApplicationFactory.FlushReactionsAsync()`. 13 tests in `Comments/ReactionAndPollTests.cs`;
suite 368 green.
**A poll is posted through the ordinary comment endpoint**, as `PostCommentRequest.Poll`. §17.8
sketched it that way and §17.5's whole argument is that a poll is a comment — one posting path
means polls inherit idempotency, the caps, the rate limit, the content switches and the
archived-ride rule with no second copy of any of them. The question is the comment's body, so
there is no second place for text to disagree with itself.
**Coalescing is not batching, and the test now says which.** Five reactions from three riders
produce one message carrying the *tally at flush*, not five deltas replayed — so the two riders who
reacted twice have their first reaction gone from it entirely, and `like` is absent. The assertion
was written the other way first and was simply wrong about the arithmetic; what it asserts now is
the property that matters, because a flush that accumulated events rather than re-reading the table
would produce the count the original assertion expected.
**`ReactionCounts.Mine` is null on the wire.** A group message has one body and "mine" differs per
connection; each client knows what it sent. Stated on `IRideClient` so nobody later "fixes" it.
**Expiry is a comparison, never a stored flag.** `Poll.IsClosed(now)` is computed on every read.
A job that flipped a column would leave a window in which an elapsed poll still took votes — as
wide as the job's interval, and widest exactly when the job is behind.
`Poll_ClosesUtcElapsed_RejectsVotesWithoutABackgroundJob` advances the clock, votes, and then
asserts `ClosedUtc` **is still null in the database** — which is what "without a background job"
actually means and what a test asserting only the 409 would miss.
**The vote request is the full set the voter now holds**, for both kinds. Single-select therefore
replaces and multi-select toggles without two endpoint shapes, and an empty list is the only way to
un-vote. **The key on `(option, user)` cannot express single-select** — that is a rule about how
many options one voter holds *across a poll* — so the endpoint owns it and says so in the config.
**Watch out — hydration is a second pass, not part of the projection.** A tally is a grouped
aggregate and a poll is three joined tables; neither belongs in the translated `Select` that builds
a `CommentDto`. `HydrateAsync` fills both for a whole page in two queries, because fifty posts ×
two queries each is the N+1 that makes a fast feature feel broken.
**Watch out — a fourth account from one address trips SRV-12's ladder.**
`Poll_Results_AreAttributedToVoters` wanted a non-voter to read results and registered a fourth
rider; it got *"An email address is required to register from this connection"*. Restructured to
read as the third rider *before* they vote, which is the same assertion with three accounts.
**Broken deliberately, twice:** `IsClosed` ignoring `ClosesUtc`, and sending a hub message per tap.
Each reddened exactly one test. The coalescing test was then run four times over — it drives
`FlushAsync` directly rather than the `PeriodicTimer`, precisely because SRV-22 established that
advancing a fake clock into a timer is a race twice over.
**Also landed here:** `Permissions_CommentsOff_MemberMayStillReactAndVote`, the last of SRV-28's
two deferrals. Neither reactions nor votes are ever gated by a content switch — a reaction carries
no free text and switching off the ability to answer a poll would break the poll rather than
moderate it. Plus `Reaction_UnknownKey_IsStoredAndRoundTrips`, the §17.4 forward-compatibility rule
§17.10 named no test for; it also pins that free text still cannot arrive through that door.
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

### SRV-31 — Moderation: reports and blocking ✅
**Status:** `Migrations/Moderation/ContentReport.cs` (with `UserBlock`) +
`ModerationConfiguration.cs` (migration `AddModeration`),
`Contracts/Moderation/ModerationContracts.cs`, `Server/Moderation/` (`BlockList`,
`ModerationEndpoints`). Blocking is applied on the thread, the marker list, reaction tallies and
poll results. 6 tests in `Moderation/ModerationTests.cs`; suite 374 green.
**`TargetId` is deliberately not a foreign key.** A report has to outlive the thing it is about —
deleting an abusive comment is exactly what an organiser should do, and it must not destroy the
evidence for the report just filed against it. A foreign key would either cascade the report away
or refuse the deletion, and both are wrong. The snapshot is taken at report time for the same
reason. Broken by storing a pointer instead; `Report_SnapshotSurvivesDeletionOfTheComment` is the
only thing that catches it.
**Blocking hides reactions and votes, not only posts.** §17.7 says so and it is easy to miss: a
tally that still counted a blocked rider would be the one place their presence leaked through, and
a poll whose names and numbers disagreed would read as a bug.
`BlockedUser_ReactionsAndVotesAreHiddenToo` is that guard.
**The filter is on the query, not on the rendered page.** Filtering after `Take` would return short
pages whose length leaked how many blocked authors were in the range.
**The coalesced broadcast applies no block list**, and cannot: a group message has one body and
whose content each connection should not see is per connection. Clients apply their own list on
receipt, exactly as they already do for `Mine`.
**Two different things are called "block".** `UserBlock` is a rider hiding another rider;
`GroupRideJoinRequest.Blocked` is an *organiser* refusing a requester entry to one ride. Same word,
different actor, different mechanism — named apart so the next reader does not conflate them.
**`UserBlock.Blocked` is `NoAction`, not `Cascade`.** Two cascade paths into `asp_net_users`
through one table is a multiple-cascade-path error in PostgreSQL; the blocker's side cascades and
the other is left to SRV-32's sweep.
**A block is silent and one-directional.** Nothing is sent to the person blocked, and their view is
unchanged — a block that announced itself would turn a quiet "I would rather not read this person"
into the confrontation it exists to avoid. Asserted directly.
**Reporting the same content twice answers 200 with the first report**, not a conflict. A rider
tapping report again wants to know it was heard; the unique index on
`(kind, target, reporter)` is what stops a frustrated rider manufacturing a queue.
**Deferred, and stated rather than assumed:** §16.5's *"prevents future co-membership"* is **not
built**. It is in no task's build list, it needs a decision about direction (does my block stop me
joining their ride, or them joining mine, or both), and a naive symmetric check would let one block
keep a rider out of a fifty-person ride. Report-and-block — what store review actually checks —
is complete without it. **Also not built:** the operator's queue for resolving reports, and the
`Moderation:ReportRetentionDays` purge of resolved ones, which is SRV-32's nightly sweep.
**First red test:** `Report_SnapshotSurvivesDeletionOfTheComment`
**Then:** `BlockedUser_CommentsAreHiddenFromTheBlocker`
**Then build:** `ContentReport` with its content snapshot, the report endpoints for both markers and
comments, and blocking that hides comments **and** markers.
**Watch for:** this ships **before the first store submission**, not before the first comment
(§11 sequencing note). It is a review requirement, not an optional nicety.
**Refs:** §16.5, §17.7, §10.2

### SRV-32 — The nightly maintenance job ✅
**Status:** `Server/Maintenance/` (`MaintenanceOptions`, `MaintenanceReport`, `BlobReferences`,
`NightlyMaintenanceService`), `Moderation/ModerationOptions.cs`,
`Migrations/Identity/DeletedAccountToken.cs` and `AppUser.InactivityWarnedUtc` (migration
`AddMaintenance`), `IBlobStore.ListAsync`, `AccountEmails.SendInactivityWarningAsync`, and the
`AccountDeleted` arm on the refresh grant. `DlrWebApplicationFactory.RunMaintenanceAsync()` and
`CapturedLogs` added to `DLR.TestSupport`. 24 tests across `Maintenance/CleanupTests.cs` and
`NightlySweepTests.cs`; suite 398 green.
**`DryRun` gates every sweep, not just the accounts, and the design has been updated.** §7.11
describes it in terms of accounts because that is the sweep worth reading the output of — but an
operator who turns it on has said *show me, do not touch it*, and a dry run still deleting refresh
tokens, positions and photo blobs is a dry run in name only. It also means the whole test suite,
which gets the shipped defaults, cannot have a stray background run destroy anything.
**The kill switch is a different switch and does a different thing.** `DeleteInactiveAccounts`
turns off the 180-day sweep alone and leaves the tidying running, which is what you actually want
at 3 a.m. when the predicate has surprised you and the disk still needs collecting.
**`inactivity_warned_utc` is a new column, and it is not optional.** The warning window is thirty
days wide and the job is nightly, so "warn when idle ≥ 150 days" with nothing recorded is thirty
emails to one person — a blocked sending domain, not a courtesy. Cleared by `ActivityTracker` in the
same `ExecuteUpdate` that moves `last_active_utc`, so a rider who comes back and goes quiet a year
later is warned again rather than deleted in silence. The test runs the sweep three times.
**The distinguishable refusal needed something to recognise, and the cascade had taken it.**
Deleting the account takes `refresh_token` with it, so by the time the device asks there is nothing
left to match — the existing "account no longer exists" branch in `TokenEndpoints` was unreachable
by this path. `deleted_account_token` holds the SHA-256 and a date, nothing else, keyed on the hash
so only the device that really held the token gets the specific answer and a guess still gets
"not valid". Swept on the same horizon as `refresh_token`.
**`user_block.blocked_id` is the one FK the cascade does not cover**, and SRV-31 said so in as many
words. Nothing else in this project deletes an account, so nothing had ever met the constraint. An
unhandled violation there does not skip one account — it aborts the statement and the whole night's
deletions. `Cleanup_AccountSomebodyElseHasBlocked_IsStillDeleted` is that test.
**The orphan blob sweep is the most dangerous code in the project, and it is guarded twice.** A
blob column the sweep does not know about is not a missed tidy-up: every value in it reads as
unreferenced, so the *next run deletes all of them*. `BlobReferences` declares the four, resolves
them through the EF model so a rename throws, and `OrphanSweep_CoversEveryBlobColumnInTheModel`
scans the model for anything blob-shaped the list has missed. Dropping `Photo.ThumbBlobRef`
reddened that test **and** `NightlySweep_DeletesOrphanedPhotoBlobs` — every thumbnail in the store
becomes a candidate.
**Watch out — the grace window is the whole safety of that sweep.** A blob is written before the
row that points at it is committed, so for the width of one request every upload is
indistinguishable from an orphan. `NightlySweep_LeavesUnreferencedBlobsInsideTheGraceWindow` uses a
blob that *nothing ever* points at, so it passes only because of the window.
**Watch out — `ClockRules` caught a real design flaw, not a style violation.** The window first
compared file timestamps against `DateTimeOffset.UtcNow`, because `File.Move` stamps wall-clock
time and the fake clock starts in January. The fix is better than the workaround: `FileSystemBlobStore`
now takes `TimeProvider` and stamps what it writes, so both sides of the comparison are in one frame
and the test needs no filesystem poking at all. An ambient stamp beside a `TimeProvider` horizon
makes every blob look ancient — the window silently not existing.
**Watch out — the boundary is `<`, and §7.11's SQL says so.** An account last active exactly 180
days ago is *at* the horizon, not past it. Three tests failed on this first; the helper now backdates
"at least N days" and the 179-day test pins the other side.
**Watch out — the timer is off in every test, deliberately.** Its period is a day and this suite
advances the clock by a day, or a year, all over the place. Left on, an unbounded number of
destructive runs fire mid-test at a moment decided by a race — SRV-22's lesson, with deletion
attached. `Maintenance:IntervalHours = 0` means no timer, which is also how a `cron`-driven
deployment would run it.
**Watch out — `Cleanup_At150Days_SendsWarningWhenEmailKnown` needs a *confirmed* address.**
Registering with an email does not confirm it, and §7.11 says confirmed — the same reason §7.7 will
not send a reset to an unconfirmed one.
**Broken deliberately, eight times:** the `Pending` filter, the warned-once stamp, the grace window,
the thumbnail column, the `user_block` pre-delete, the position sweep's predicate, the
tombstone lookup, and the `DryRun` gate on deletion. Each reddened exactly the intended test.
**Not built, and stated rather than assumed:** the operator's queue for *resolving* reports. This
sweep purges resolved ones; nothing sets `ResolvedUtc` yet, which SRV-31 already recorded.
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

### SRV-33 — Export and account deletion ✅
**Status:** `Contracts/Account/AccountExport.cs` (Core), `Server/Account/` (`AccountBlobs`,
`AccountExportBuilder`, `AccountEndpoints`). 14 tests across `Account/AccountDeletionTests.cs` and
`AccountExportTests.cs`; suite 412 green. No migration — every table this touches already exists.
**The export is a ZIP, not a JSON body, and §16.6 is why.** It says the export includes markers
*and their photos*; a response listing photo identifiers is not an export of anybody's
photographs, and a track reduced to its distance is not an export of their ride. `export.json`
plus `tracks/{id}.gpx`, `tracks/{id}.previous-version.gpx` and `photos/{id}.jpg`. Points go out as
GPX because that is the format the rest of the product already reads and writes, so the file is
useful somewhere other than here — and it reuses `GpxWriter` and the §16.6 waypoint mapping, so an
exported track re-imports with its markers.
**The switches are exported, not only the values.** What a rider chose to *share* is a decision
about their own privacy; a file showing a phone number without saying who could see it answers a
different question. That is what `Profile_Export_IncludesAllRecordedFieldsAndSwitches` — named in
§7.15 and unbuilt until now — actually asserts.
**The join code is not in the archive**, and the test asserts against the raw manifest text rather
than a property, the same way SRV-20 did. It is the ride's entire access control and goes only to
the organiser; an export handed to a member that carried it would let any member re-share the
group the organiser curated, through a file nobody thinks of as a sharing surface.
**`DELETE /me` requires the current password.** This is the one irreversible action in the API and
a fifteen-minute access token lifted off a shared machine should not be enough to end an account.
Every account has a password (§7.2), so it excludes nobody. Minimal APIs will not infer a body on
`DELETE`, so the parameter is `[FromBody]` — a query string would put the password in Caddy's
access log and the browser's history.
**Watch out — the blob list has to be gathered *before* the rows go.** After the delete there is
nothing left to say which files were this account's. Moving the call below the `ExecuteDelete`
reddens both blob tests, which is how it was verified rather than assumed.
**Watch out — a revision is reached through the track, not through the account.** `TrackRevision`
has no owner column, so a blob query scoped by `OwnerId` alone never sees the retained original —
which is exactly the blob §15.6 promised a rider was gone. `AccountDeletion_CascadesTrackRevisions`
is that test, and the join is the only thing standing between it and every retained original on the
server: `AccountDeletion_LeavesOtherAccountsBlobsAlone` is the guard on the guard.
**Watch out — a photo is two files.** Dropping `ThumbBlobRef` from the list leaves every thumbnail
on the disk being backed up, and nothing else in the suite notices. Same trap SRV-32's orphan sweep
carries, in a second place.
**A real bug the suite caught, not a flaky test.** `OrderBy(Ordinal)` *before* a translated
`GroupBy` does not survive into the result — PostgreSQL returns a group's members in whatever order
it likes, and it varies run to run. A poll whose options come back reversed is a different poll.
It failed about one run in three; the fix fetches flat and groups in memory, and it was then run
six times over.
**Deliberately not built, and stated rather than assumed:** transfer of ride ownership. Deleting an
account cascades `group_ride` and takes every other member's membership with it — that follows from
SRV-20's schema. Refusing the deletion instead is not defensible under §10.2's applicable law, so
the cascade stands and the warning is a UI task. **Also:** self-deletion writes no
`deleted_account_token` tombstone. §7.11's distinguishable reason says "removed after 180 days
without use", which would be a lie here — a rider who deleted their own account knows why their
other device stopped working.
**First red test:** `AccountDeleted_RemovesCommentsReactionsAndVotes`
**Then:** `Photo_AccountDeleted_RemovesBlobsFromObjectStorage`,
`Export_IncludesRetainedRevisionWhileItExists`, `AccountDeletion_CascadesTrackRevisions`
**Then build:** `GET /me/export` and `DELETE /me`, with **explicit blob deletion** — `ON DELETE
CASCADE` does not reach the filesystem.
**Refs:** §6.3, §16.6, §10.1

### SRV-34 — Web session cookie and the MapKit token endpoint ✅ *(map half removed in v0.24)*
**Status:** `Identity/WebSessionCookie.cs`, `WebAuthEndpoints.cs`, `RegistrationService.cs`
(extracted), `Device.Kind` + `JwtOptions.WebSessionDays` (migration `AddWebSessions`),
`RefreshTokenService.RevokeByTokenAsync`; `Maps/MapKitOptions` + `MapKitSigningKey` +
`MapKitEndpoints`, `Contracts/Maps/MapToken.cs`. 14 tests across `Identity/WebSessionTests.cs` and
`Maps/MapKitTokenTests.cs`; suite 426 green. No new packages — the licence gate is untouched.
**`Device.Kind` is how the two session lengths are told apart**, and it is read from the *device*
rather than carried on the token. A successor that came back with a mobile lifetime would convert a
thirty-day browser session into a permanent one by refreshing once — on the very call a client makes
at every start-up. §18.5 already frames a browser as "a device like any other", so this is where the
distinction belongs.
**The kind is server-decided, from which endpoint was reached.** A client-supplied value would let a
browser ask for the permanent session the whole feature exists to withhold. `ResolveDeviceAsync` also
requires the kind to *match* before adopting a claimed device id — otherwise a browser presenting a
phone's id inherits that row and its lifetime.
**`RegistrationService` extracted, because a browser registers too.** The §7.8 ladder, the rate limit
above it, the duplicate-address answer that must not become an enumeration oracle, and the
confirmation link a restricted account needs to stop being restricted — a second copy on the web
route is how one of them ends up subtly different, and the different one is the route an abuser
reaches with a browser.
**Antiforgery is on one endpoint, and §7.5 scopes it that way.** The cost of choosing a cookie over
`localStorage` is "CSRF exposure on exactly one endpoint — the token endpoint". Login and register
carry credentials in the body, so `.DisableAntiforgery()` is said out loud on both; minimal APIs
add the metadata automatically for `[FromForm]`, so declining it has to be deliberate.
**Watch out — `Secure` follows the request, it is not hard-coded on.** A cookie marked `Secure` over
the plain-HTTP loopback a test host and a local `dotnet run` both use is a cookie the browser
discards, and it fails exactly the way §7.5 names: the sign-in appears to work and the next request
is anonymous. `WebAuth_OverHttps_MarksTheCookieSecure` is the other half.
**Watch out — `HttpOnly` is worth nothing on its own.** A cookie the script cannot read, beside the
same token in a JSON body the script just parsed, has protected nothing — and that is exactly where
an XSS would look. `Strip` blanks `RefreshToken` on every web response and the first test asserts the
cookie's value is absent from the body, not merely that a field is empty.
**Watch out — `Max-Age`, not `Expires`.** `Expires` is an absolute instant, so it would need a clock
— and the project's clock is a fake one in tests, which would stamp a cookie dated 2026 against a
browser's real clock. `Max-Age` is a duration, which is what "sliding" actually means.
**Watch out — sign-out has to revoke, not just clear.** Clearing the cookie leaves a working token in
whatever else kept a copy, which on the shared computer that made web sessions expire at all is the
entire scenario. `RevokeByTokenAsync` ends the whole family, and answers the same 204 for an unknown
token so it is not an oracle.
**A real bug the MapKit tests caught, and it succeeds on the first call.** *(Both the code and
the tests were deleted in v0.24; kept because the trap is about IdentityModel, not about MapKit,
and the next `SecurityKey` this project holds will hit it.)* `using ECDsa key =
ECDsa.Create()` inside the request works exactly once: IdentityModel caches the signature provider it
builds around a `SecurityKey`, so the second request reaches a provider whose `ECDsa` the first
already disposed and throws `ObjectDisposedException`. Found by the rate-limit test, which was the
only one to mint twice. `MapKitSigningKey` now builds it once for the process.
**Watch out — the migration's default value.** `AddColumn` backfills a string column with `""`,
which maps back to no `DeviceKind` at all and would make every existing device row unreadable.
`HasDefaultValue(DeviceKind.Mobile)` on the configuration is what makes the generated migration say
`defaultValue: "Mobile"` — the same both-places treatment SRV-13 and SRV-28 needed.
**Broken deliberately, four times:** `HttpOnly`, the body-stripping, the web lifetime, and the
sign-out revocation. Each reddened exactly the intended test.
**Not built, and it is a UI task rather than an omission:** the static-rendered login/logout/register
*pages*. The endpoints they post to are here and are ordinary form posts precisely because a cookie
cannot be set from inside a running WASM client; the Razor around them belongs to the `DLR.Web.Client`
list. **Also not built:** the `AuthenticationStateProvider` that drops a tab to signed-out when a
refresh fails (§7.5) — client-side by definition.
**First red test:** `WebAuth_RefreshTokenIsNotReadableFromJavaScript`
**Then:** `WebAuth_SessionExpiresAfterConfiguredDays`, `MobileAuth_SessionStillNeverExpires`
**Then build:** the `HttpOnly`/`Secure`/`SameSite` refresh cookie for browser callers with
antiforgery on the token endpoint, the 30-day sliding web expiry, and the static-rendered
login/logout/register form posts — **a cookie cannot be set from inside a running WASM client**.
**Also:** `GET /maps/token` minting a short-lived ES256 MapKit JS token. The `.p8` never leaves the
server; an unavailable token must produce a stated error, not a blank map.

> **v0.24 deleted the map half of this task.** `Maps/MapKitOptions`, `MapKitSigningKey`,
> `MapKitEndpoints`, `Contracts/Maps/MapToken.cs`, `RateLimits:MapTokenPerHourPerAddress` and
> `MapKitTokenTests` are gone: every host moved to MapLibre over OSM, which needs no credential,
> so the map is no longer a server dependency (§4.5). **The web-session-cookie half stands
> unchanged** — it is the larger and still-live part of SRV-34. The one rule that outlived the
> endpoint is the stated-error branch in `RideMap.razor`, which now fires for an unreachable CDN
> or tile server instead of a missing token.

**Refs:** §7.5, §18.5, §4.5

### SRV-35 — Deployment, backups and alerts ✅
**Status:** `Api/HealthEndpoints.cs` + `Contracts/Health/HealthReport.cs`, the `--migrate` branch in
`Program.cs`, `MaintenanceOptions.AlertEmail` and the run-summary email; `deploy/` — `Dockerfile`,
`Dockerfile.dockerignore`, `docker-compose.yml`, `Caddyfile`, `backup.sh`, `.env.example`,
`README.md`; a fourth CI job that builds the image and starts it. 3 tests in `Api/HealthTests.cs`;
suite 429 green. No new packages — the licence gate was re-run, exit 0.
**The stack was actually brought up, not written and hoped for.** `docker compose up` against
PostgreSQL, `/healthz` answering `200` with `migrationsApplied: true`, and a write into the mounted
blob volume. Three real bugs came out of doing it rather than reasoning about it, and none of them
would have shown up in the test suite.
**Bug one — nothing applied the schema.** The first run answered `503` with `pendingMigrations: 19`,
which is exactly what the endpoint is for. Fixed with a `--migrate` branch that applies the schema
and exits without starting Kestrel, run as a one-shot compose service the server waits on. Not a
`Migrate()` on the way up: that couples "is this server ready" to "has the schema moved", turns a
failed migration into a crash loop, and makes a second container a race.
**Bug two — the blob volume was root-owned.** The container runs as uid 5000 and Docker seeds a
fresh named volume from the image path, creating it root-owned when the path is absent. That is a
container which starts, answers `/healthz`, and then fails every upload with a permission error
nobody connects to a missing `mkdir`. The image now creates `/blobs` and chowns it.
**Bug three — `HEALTHCHECK` invoked a binary the image did not have.** `curl` is not in
`aspnet:10.0-noble`, and a healthcheck whose command does not exist reports unhealthy forever.
Installed alongside `libfontconfig1`, which SkiaSharp needs even for an app that draws no text.
**`/healthz` is the disk alert §9 asks for**, and it costs nothing extra: it answers `503` when free
space on the blob volume drops below `Health:MinimumFreeMb` (2 GB), so the free uptime pinger that is
already watching the URL *is* the alarm. A full disk stops PostgreSQL **writing**, not merely
stopping uploads — far worse than a slow map, and the limit a 40 GB CX22 reaches first.
**Watch out — the body is anonymous, so it is public.** Booleans and a count; no migration names, no
connection details. `Health_IsReachableWithoutAuthentication` asserts against the *shape* of a
migration identifier rather than the word "migration", because the field names legitimately contain
it — the first version of that assertion failed on its own JSON.
**The Caddy access log had to be filtered, not merely formatted.** §7.6 lifts the SignalR access
token out of a query string because a browser cannot set an `Authorization` header on a websocket,
so the default log writes live credentials into a file that rolls for weeks. Choosing JSON does not
fix it — every format logs `request>uri` intact. There is a `format filter` block deleting
`access_token`, and SRV-23's decision to scope that lift to `/hubs/ride` is what keeps it to one
parameter.
**`Dockerfile.dockerignore`, not `.dockerignore`.** BuildKit looks for `<dockerfile>.dockerignore`
beside the Dockerfile and otherwise for `.dockerignore` at the *context* root. A plain
`.dockerignore` in `deploy/` is silently ignored — the build still succeeds and ships everything it
was meant to exclude. **`.git` is deliberately not excluded:** SourceLink reads the commit out of it
and `/api/v1/about` hands it back, which is an AGPL §13 obligation (§14.6.2).
**CI builds the image and starts it.** Built, never pushed — publishing needs a registry credential
and §14.4's rule is that no job a fork can trigger reads a secret. The job runs `--migrate` and then
waits for `/healthz`, which is the check that would have caught all three bugs above. `fetch-depth: 0`,
because a shallow clone produces a server that cannot state its commit.
**The nightly run emails its summary** (`Maintenance:AlertEmail`), counts and candidate usernames.
§9: *"a destructive job you do not watch is a destructive job you will regret."* The dry-run log is
only read by somebody who goes looking; this arrives. Wrapped in the same per-sweep try/catch, so a
mail transport that is down cannot make a run that did its work report as a failure.
**Backups: one snapshot for the dump and the blobs**, not two. A database restored against blobs
from another night gives you tracks pointing at files that are not there. `restic forget
--keep-daily 14 --keep-weekly 8 --keep-monthly 12` is the number §15.6's privacy copy refers to when
it says a trimmed track's original survives in backups until retention rolls — changing it changes a
privacy statement.
**Not done, and it cannot be done from here:** the restore drill and the staging check of
`ForwardedHeaders`. Both are in `deploy/README.md` as the two things that are not automatic, with
the commands to run. The `restic check --read-data-subset=1%` in `backup.sh` is not a substitute —
it proves the repository is readable, not that anybody can rebuild a server from it.
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

---

### SRV-36 — Delete the adventure lifecycle ✅
**Status:** `GroupRideState`, `GroupRide.State`, `EndedUtc` and `SharingEndsUtc` deleted (migration
`RemoveRideLifecycle`); `RideStateDto`, `RideEndingDto` and `EndRideRequest` off the wire;
`StartAsync` and `EndAsync` gone from `MembershipEndpoints`; `Positions/SharingWindDownService.cs`
deleted with its registration; `IRideClient.RideStateChanged` and `IRideHubClient`'s
`RideStateChanged` / `SharingWindDownStarted` gone. `NightlyMaintenanceService.DeleteStalePositionsAsync`
becomes a call to the new `PositionStore.ClearIdleAsync` / `CountIdleAsync`. 514 server tests green, 1 213 UI, 187 core, 29 architecture.
**First red test:** `NightlySweep_DeletesIdlePositionsAndClearsTheirSharing`
**Then:** `NightlySweep_DryRun_CountsIdlePositionsAndDeletesNothing`,
`SharingOff_DeletesThePositionButKeepsTheThread`,
`SharingOff_DeletesThePositionButKeepsTheMarkers`,
`Delete_WhileSomebodyIsSharing_TakesTheirPositions`
**Then build:** the migration, then the contracts — removing `RideStateDto` breaks the build in
both client projects deliberately, so nothing is missed — then `RideSession`, then the two ride
pages.
**Watch out — it is the cache eviction that makes the delete stick, not the order of the two
writes.** `PositionFlushService` reads `RiderPositionCache.Dirty()` and never looks at
`ShareLocation`, so a flush in flight puts the row back whichever statement ran first; dropping the
cache entry is what stops it. The sweep is therefore `PositionStore.ClearIdleAsync`, beside the
three per-rider paths, ending in the same delete-then-evict pair `StopSharingAsync` uses — the job
calls it rather than restating it.
**Watch out — `GroupRideLive.razor`'s consent trigger is the one place a deleted state test changes
a *privacy* behaviour rather than a cosmetic one.** It read `!Sharing && Ride.State == Open`, and
`Open`-ness was quietly carrying "we have not asked about this adventure yet". Delete the test and
the prompt fires on every load of an adventure somebody has declined; delete the property and leave
the test and nobody is ever asked again. The fact now lives in `IDeviceSettings` under
`dlr.consent.asked.{rideId}`.
**Watch out — five of the six states were never assigned by anything.** `Draft`, `Completed`,
`Archived` and `Cancelled` had guards reading them and no code path writing them, so §17.6's
thirty-day read-only thread has never once fired. Removing the enum exposes that; it did not cause
it. `ThreadAccess.ReadOnly` went with it, along with four dead `if` branches.
**Watch out — `SharingTests.Publish_OlderFix_DoesNotRegressTheStoredPosition` needed a
`FlushPositionsAsync`.** It left a dirty position at teardown, and the shutdown flush can run after
the `LoggerFactory` is disposed — at which point the *logging* of the flush failure becomes the
test's exception. The race is pre-existing and still open; the test no longer walks into it.
**Not done:** nothing about §17.6's read-only thread was rebuilt. If it is still wanted it needs its
own `ArchivedUtc` column, its own sweep and a test that the sweep runs — which is what the state it
replaces never had.
**Refs:** §5.1, §5.6, §5.7, §7.3, §7.11, §10.1, §17.6

### SRV-37 — The not-sharing position sweep, and the track that could not be unlocked ✅
**Status:** `PositionStore.ClearOrphanedAsync` / `CountOrphanedAsync` and a second nightly sweep
reporting `MaintenanceReport.OrphanedPositionsDeleted`; the route detach endpoint now also admits
the track's own owner. 5 tests.
**First red test:** `NightlySweep_DeletesAPositionForARiderWhoIsNotSharing`
**Then:** `NightlySweep_DeletesAPositionLeftBehindByAMembershipThatIsGone`,
`NightlySweep_DryRun_CountsOrphanedPositionsAndDeletesNothing`,
`Detach_ByTheTracksOwner_IsAllowedWithoutBeingTheOrganiser`,
`Detach_ByAMemberWhoDoesNotOwnTheTrack_IsRefused`
**Why, and it is a correction to SRV-36.** The sweep SRV-36 removed did more than reclaim rows for
finished adventures: it was §13 Q29's backstop, catching a position the flush/delete race had
resurrected once the ride ended. The fourteen-day idle rule that replaced it would not have caught
one for a fortnight, so SRV-36 quietly lengthened the exposure on the one thing §10.1 promises does
not exist. This states the invariant directly instead — a position may not exist for a rider who is
not sharing — which is stronger than what was lost, because it does not wait for the adventure to
finish.
**Watch out — this sweep counts a defect, not housekeeping.** Its own number and its own line in
the report, deliberately: added into `PositionsDeleted` it would be one more bit of nightly noise,
and the whole value is that a non-zero count means the race fired. Nothing else in the product
would ever tell you.
**Watch out — the second arm is reachable on its own.** `rider_position` has no foreign key to
`group_ride_member` (§5.6), so a member row going away leaves the position behind with no flag to
test. The predicate is "no member row with `ShareLocation` set", which covers both.
**Watch out — §15.4's edit guard became a permanent lock.** With no lifecycle, "this track is an
adventure's route" never expires, and detaching needed Owner or Leader. A leader who attached their
own track and was later demoted or left could neither edit nor delete their own track, and the
refusal told them to do the one thing they could not. The track's owner may now always withdraw
their own line — §19.2's rule about un-sharing your own row, applied to an attachment.
**Refs:** §5.6, §7.11, §10.1, §13 Q29, §15.4, §19.2

---

## The server list is complete — and three things are owed before real riders

Every task SRV-01 … SRV-37 is marked. 519 server tests green, `dotnet format --verify-no-changes` clean,
architecture tests green, licence gate exit 0, and the image builds and reports healthy. What is
*not* done is deliberately listed here rather than left to be discovered:

- **§13 Q29, the flush/delete race (SRV-22).** A flush already in flight can re-insert a position
  row a concurrent delete has just removed. One round trip wide, so rare rather than impossible —
  but what it leaves behind is exactly the position at rest §10.1 forbids. **Close it before live
  sharing is on for anyone real.** It needs a tombstone the flush filters against, or a membership
  join in the upsert; neither ordering of delete-and-evict closes it. SRV-37's not-sharing sweep is
  a backstop with a number on it, not a fix: it bounds the exposure to a day and makes the race
  visible in the nightly report, which is the first time anybody would know it had happened.
- **The restore drill (SRV-35).** A backup nobody has restored is a hope. The commands are in
  `deploy/README.md`; B2's restore egress is free up to three times the stored volume, so it costs
  nothing but the hour.
- **`ForwardedHeaders` verified in staging (SRV-35, §7.8).** `CADDY_IP` is a guess until somebody
  looks. Get it wrong and the fourth account ever is asked for an email address, because every
  signup appears to come from Caddy. This is load-bearing for *registration*, not just for rate
  limiting.

Two smaller ones, both stated in their own tasks and neither blocking: §5.3's server-side publish
throttle is in no task's build list (SRV-23), and §16.5's "prevents future co-membership" needs a
decision about direction before it can be built (SRV-31).
