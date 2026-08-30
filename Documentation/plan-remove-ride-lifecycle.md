# Plan — remove the adventure start/end lifecycle

**Goal.** An adventure is live from the moment it is created. Anyone who joins can use it
immediately. The owner never presses *Start*; nobody presses *End*; there is no two-hour
wind-down. The **only** control over live position sharing is each rider's own per-adventure
switch, and the only way to finish an adventure is to delete it.

This is a large simplification: it removes one enum, two columns, two endpoints, one background
service, three configuration options, two hub messages, ~14 tests and a whole chapter of two
user-facing documents.

---

## 1. What replaces the guaranteed death of a position row

Today a position row is guaranteed to die: the adventure ends, and the server deletes every row
unconditionally, whether or not any phone is awake. That guarantee is what §1, §10.1, the store
listing and the published privacy policy all promise, in those words:

> Live sharing is scoped to the group ride. It ends when the ride ends … It is never open-ended.

Remove *End* and that guarantee goes with it. A rider who taps *Share*, rides home and forgets the
switch broadcasts **forever** — exactly the "always-on tracking of friends" the product says it is
not. Nothing else in the system deletes that row.

**The decision taken: a fourteen-day idle sweep.** The nightly maintenance job deletes any
`rider_position` whose `RecordedUtc` is older than `Ride:PositionIdleDays` (default **14**) and
clears that member's `ShareLocation`. Server-side and unconditional — it does not depend on any
client being awake, which is the property the wind-down cap had and the thing worth keeping.

**Be clear about what fourteen days is and is not.** It is a backstop against a row nothing else
reclaims — a phone that died, an app that was uninstalled, an adventure nobody deletes. It is
**not** a privacy promise, and the copy must not present it as one. A rider who leaves the switch on
is sharing until they turn it off; the sweep only catches the case where they stopped sending
anyway. Every user-facing sentence that currently says sharing "ends when the adventure ends" must
therefore be rewritten to say it ends **when you turn it off, leave, or are removed** — not to point
at the sweep. The retention table in the privacy policy is the one place the fourteen days belongs,
as the outer bound on a row nobody is updating.

That is a real narrowing of §1's claim, and §1, §10.1, the store listing and the privacy policy all
have to be corrected to match. Recording the change rather than quietly restating it is this
document's own rule (§10.1, and the v0.2 correction that established it).

**Sub-decision — does the sweep clear `ShareLocation` as well as deleting the row?** Recommend yes.
Every other delete path in the system moves the flag and the row together (§5.6's four paths), and a
flag left on means a device that reappears after a month resumes broadcasting without anybody being
asked again. The alternative — delete the row, leave the flag — is defensible on the grounds that
nobody withdrew consent, but it makes the sweep a no-op for exactly the case it exists for.

---

## 2. Knock-on decisions

**2.1 `RideStateDto` comes off the wire.**
No build is in users' hands, so there is no compatibility window to keep and no reason to ship a
field hard-coded to one value. Delete the enum and the `State` property from `RideDetail`,
`RideSummary` and `WaitingRide` outright.

Worth recording *why* this was safe, because it will not be next time: a shipped client
deserialising a response with no `state` gets `default(RideStateDto)` — which is `Draft` — and would
decide the adventure had not started, hide the sharing panel and refuse to bring the GPS up. A
silently wrong default rather than a failure. Any future removal of a non-nullable enum from these
contracts needs the same check.

**2.2 The 30-day `Archived` read-only thread (§17.6).**
`GroupRideState.Archived` is *never assigned anywhere in the codebase* — verified. The thread has
never actually gone read-only; the state exists, the guards that read it exist
(`CommentThreadAccess.cs:106`, `MarkerEndpoints.cs:368`), and nothing has ever set it. Removing the
enum makes that fact visible rather than creating it. If read-only-after-N-days is still wanted, it
comes back later as its own `ArchivedUtc` column and its own sweep — not as a side effect of this
work. Same for `Draft` and `Cancelled`: neither is ever assigned either.

**2.3 `Ride:MaxConcurrentLiveRidesPerUser` (default 5).**
Enforced only at the start transition, so it has no enforcement point left. §5.7 already anticipated
this — *"if that turns out to matter, the place to fix it is the publish fan-out, not the start
transition"*. Recommend deleting the option and its tests; the cost it protected (one inbound batch
per live adventure, on the rider's downlink) is now bounded by how many adventures somebody
deliberately shares with, which is the honest bound.

**2.4 Profile sharing (§7.3).**
Shared profile fields are visible to *current co-members of an adventure that has not completed*
(`ProfileEndpoints.cs:87-89`). With no `Completed`, the second half of that rule evaporates and
sharing lasts as long as co-membership. That is defensible — leaving, being removed and deletion all
still end it — but §7.3, §10.1 and the privacy policy each state the old rule explicitly and must be
corrected.

---

## 3. Behaviour after the change

| Today | After |
|---|---|
| Created `Open`; owner presses *Start* → `Live` | Created live; no transition exists |
| Positions land only while `Live` or inside a wind-down | Positions land whenever the member's `ShareLocation` is true |
| Owner presses *End* → `Completed`, all rows deleted, every switch off | No *End*. Rows die on: switch off, leave, removed, adventure deleted, **or the nightly 14-day idle sweep** |
| Owner may grant a capped 2-hour wind-down | Gone |
| Delete refused while `Live` | Delete always allowed; it is the way to finish an adventure |
| Consent prompt shown on an `Open` adventure the rider does not share with | Shown on **any** adventure the rider does not share with |
| Routes frozen once the adventure ends | Routes always editable by owner/leader |
| Track delete/edit refused while it is a `Live` adventure's route | Refused while it is **any** adventure's route |
| Admin statistic *Live adventures* = count of `Live` rows | Redefine — suggest *adventures with someone sharing*, from the position cache |

---

## 4. Code changes, file by file

### 4.1 Domain — `DLR.Server.Migrations`

`Rides/GroupRide.cs`
- Delete `enum GroupRideState` (whole block, lines 5-26).
- Delete `GroupRide.State`, `GroupRide.EndedUtc`, `GroupRide.SharingEndsUtc` (lines 88, 150, 164 and
  their doc comments).
- Keep `JoinPolicy`, `GroupRideRole`, `JoinRequestStatus`, `GroupRideMember.ShareLocation` — untouched.

`Rides/GroupRideConfiguration.cs`
- Delete the `builder.Property(ride => ride.State).HasConversion<string>()…` line.

**New migration** `RemoveRideLifecycle`:

```
DROP COLUMN group_ride.state
DROP COLUMN group_ride.ended_utc
DROP COLUMN group_ride.sharing_ends_utc
```

`Down` re-adds them with a default of `'Live'` / NULL / NULL, so a rollback leaves every existing
adventure usable. Remember migrations are a one-shot `--migrate` run (§9), not a boot-time
`Migrate()`.

### 4.2 Contracts — `DLR.Core`

`Contracts/Rides/RideContracts.cs`
- Delete `enum RideStateDto` (line 14) and the `State` property from `RideDetail` (96),
  `RideSummary` (207) and `WaitingRide` (244).
- `WaitingRide`'s doc comment for `State` (lines 236-240) argues the field earns its place because a
  waiting rider wants to know *"did it go without me?"* — a question that no longer exists. Delete
  the paragraph rather than leaving it describing a field that is gone.

`Contracts/Rides/PositionContracts.cs`
- Delete `enum RideEndingDto` (line 151) and `record EndRideRequest` (line 167).

`Contracts/Admin/AdminContracts.cs`
- `LiveRides` (line 157) and its doc comment (line 138) — rename or redefine per §3.

`Contracts/Identity/SharedProfile.cs`
- Comment at lines 53-54 explains that profile sharing does not follow the wind-down. Delete it.

### 4.3 Server — `BlazorDLR.Web`

**`Rides/MembershipEndpoints.cs`** — the bulk of the deletion.
- `MembershipEndpoints.StartRouteName` (line 30) and `EndRouteName` (line 27).
- `MembershipController.StartAsync` — lines 55-112, whole method including the concurrency cap.
- `MembershipController.EndAsync` — lines 311-399, whole method.
- Leaves `SetPermissionsAsync`, `SetSharingAsync`, `LeaveAsync`, `RemoveAsync` and
  `HasPositionAsync` alone.

**`Positions/SharingWindDownService.cs`** — **delete the whole file**, and its registration and
tests with it. A fourteen-day window has no business on a sixty-second `PeriodicTimer`; the sweep
belongs in the nightly job that already exists (see `NightlyMaintenanceService` below), already runs
once a day, already honours `Maintenance:DryRun` and already carries three other destructive sweeps
for exactly this reason (§7.11).

**`Positions/RideOptions.cs`**
- Delete `MaxWindDownMinutes` (line 32), `WindDownSweepSeconds` (line 29) and
  `MaxConcurrentLiveRidesPerUser` (line 42).
- Add `PositionIdleDays = 14`. (Days, not minutes — the neighbouring options are cadences measured
  in seconds and minutes, and 20 160 minutes reads as a typo.)
- `BroadcastSeconds`, `FlushSeconds`, `StalenessMinutes`, `MaxRoutesPerRide` unchanged.

**`Positions/PositionStore.cs`** — two identical two-armed filters collapse to one arm.
- `PublishAsync`, lines 72-77 → `member.UserId == userId && member.ShareLocation`.
- `SharedRideIdsAsync`, lines 164-169 → same, and drop the now-unused
  `DateTimeOffset now = cache.Clock.GetUtcNow();`.

**`Positions/PositionCacheRehydrator.cs`** — lines 66-77. The ride-state arm goes; the
`StalenessMinutes` cut-off stays and becomes the whole rule.

**`Positions/RiderPositionCache.cs:54`** — comment referencing the SRV-25 sweep.

**`Maintenance/NightlyMaintenanceService.cs`** — `DeleteStalePositionsAsync`, lines 437-457. **This
is where the idle sweep goes.** The method already exists, already runs nightly, already returns a
count and already respects `DryRun`; only its predicate changes, from "belongs to a ride that is
neither Live nor winding down" to `RecordedUtc < now - PositionIdleDays`. It must also clear
`ShareLocation` for the affected members and evict the cache entries — neither of which the current
version does, because a ride-state sweep never needed to. Rename it `DeleteIdlePositionsAsync`.

**`Rides/RideEndpoints.cs`**
- Line 105: `State = GroupRideState.Open` in the create path — delete.
- Line 179: join refusal `is Cancelled or Archived` — delete the state test, keep `ride is null`.
- Lines 366-393: the `WaitingRide` projection's `row.Ride.State` and the `(RideStateDto)` cast, plus
  the comment at 368-370 explaining why the cast is done outside the query.
- Lines 643-651: the "adventure is in progress" delete refusal — delete the whole guard and its
  comment. Delete becomes the way to end an adventure.

**`Rides/RideRouteEndpoints.cs`** — lines 229-240, the "this adventure has ended" guard on route
attach/detach. Delete.

**`Markers/MarkerEndpoints.cs`** — lines 366-374, the `Archived` read-only guard. Delete.

**`Comments/CommentThreadAccess.cs`** — line 106 `bool archived = …` and every use of `archived` in
the returned `ThreadAccess`. Threads are always writable. Check `ThreadAccess` for a member that is
now always the same value and remove it too.

**`Identity/ProfileEndpoints.cs`** — lines 76-92. The three `State !=` clauses go; the comment above
them (76-80) is rewritten to say sharing lasts while co-membership does.

**`Admin/AdminEndpoints.cs`** — lines 211-213. Redefine per §3, or drop the figure.

**`Account/AccountExportBuilder.cs`** — lines 213-218, the six-way `State` ternary inside the
translated projection. Delete the argument; delete `ExportedRide`'s state field.

**`Tracks/TrackEndpoints.cs`** (line 319) and **`Tracks/TrackEditEndpoints.cs`**
(`IsRouteOfLiveRideAsync`, lines 337-343) — drop `&& route.Ride!.State == GroupRideState.Live` from
both. The guard becomes "this track is a route of an adventure", which is the correct translation now
that every adventure is live. Update the two 409 messages to match.

**`Hubs/RideHub.cs`** — `Task RideStateChanged(RideStateDto state)` (line 71) and its doc.
**Nothing on the server has ever called it** — verified across the whole solution. Delete it.

**`Program.cs`** — delete line 102, `AddSingletonHostedService<SharingWindDownService>()`. The
comment at line 51 mentions the wind-down sweep among the options bound from configuration.

**`Services/InProcessAboutApiClient.cs`** — the two SSR `NotImplementedException` stubs for
`StartRideAsync` / `EndRideAsync` (line 105 area).

### 4.4 Shared UI — `BlazorDLR.Shared`

**`State/RideSession.cs`** — the largest client change.
- `WindDownEndsUtc` property (169-170) and its reset in `LoadAsync` (287).
- `RideCarriesPositions` (176-194) — always true now. Delete the property and its 20-line comment;
  every reader becomes unconditional.
- `SharingPending` (196-201) — always false now. Delete.
- `StartBroadcast`'s `if (!RideCarriesPositions) return;` early exit (528-537) and its comment.
- `StartAsync` (547-548).
- `EndAsync` (633-635).
- `OnRideStateChanged` (901-930) — whole method, plus subscribe/unsubscribe at 730 and 749.
- `OnWindDownStarted` (975-979) — whole method, plus subscribe/unsubscribe at 731 and 750.

**`Services/IRideHubClient.cs`** — `RideStateChanged` (86) and `SharingWindDownStarted` (140-141).
Note `SharingWindDownStarted` **is not even declared on the server's `IRideClient`** — the client has
been subscribing to a message that could never arrive. Both go.

**`Services/SignalRRideHubClient.cs`** — event fields at 71 and 102, `connection.On<…>` registrations
at 132 and 147.

**`Services/Platform/ThrowingRideHubClient.cs`** — stubs at 34 and 49.

**`Services/IApiClient.cs`** — `StartRideAsync` (201) and `EndRideAsync` (203-204).
**`Services/HttpApiClient.cs`** — implementations at 307-315.

**`Pages/GroupRides/GroupRideInfo.razor`**
- Line 42: the state badge in `PageNav.Actions` — remove the `<span class="state …">`.
- Lines 78-86: the wind-down banner.
- Line 344: `&& (ride.State == Live || ride.State == Open)` → just `_broadcast is { } broadcast`.
- Lines 366-383: the whole `_session.SharingPending` branch and its `else`, leaving the
  `broadcast.Describe()` line unconditional. Kills the "your position starts going to the group when
  the organiser starts the adventure" copy.
- Line 397: "…or the adventure ends" → "…or you leave the adventure".
- Lines 456-467: both the *Start adventure* and *End adventure…* buttons.
- Lines 470-495: the entire end-choice dialog.
- `_showEndDialog` field (line 521 area), `EndRideAsync` (781).
- CSS: `.ride-info .wind-down` (890) and the `.state` badge rules.

**`Pages/GroupRides/GroupRideLive.razor`**
- Lines 197-219: the `SharingPending` strip and its comment block.
- Line 221: `&& ride.State is RideStateDto.Open or RideStateDto.Live` → drop the state test; the
  comment at 243-245 about *Open and Live only* goes with it.
- Line 1129: `HasOwnReceiver && _session.Sharing && _session.RideCarriesPositions` → drop the third
  term.
- **Line 1578 — do not miss this one.** `if (!_session.Sharing && _session.Ride?.State ==
  RideStateDto.Open) _showConsent = true;` If the state test is deleted without thought the consent
  prompt shows on every load; if the property is deleted and the test left, it never shows at all
  and **riders stop being asked for consent**. Correct rule: show it once per adventure the rider
  does not yet share with. Check whether an "already declined for this adventure" flag is needed —
  today `Open`-ness was doing part of that job.
- Comment at 1176 referencing "kept only once the ride is Live".

**`State/LaunchRestore.cs`** — lines 105-112. `underway` is now always true; delete the variable, the
`if (!underway)` block and its diagnostic line, keeping the `sharing` check that follows.

**`Components/ConsentPrompt.razor`** — lines 8-9 (comment) and 21-22 (copy). The sentence *"It stops
when the adventure ends — or, if the organiser lets travellers finish getting home, within two hours
of that"* must be replaced with what is actually true under the chosen decision. **This is consent
copy — it is the last place an inaccuracy is acceptable** (the project's own words, §5.6). The
replacement says what the rider controls and does not lean on the sweep:
*"It stops when you turn it off, leave the adventure, or the organiser removes you."*

**`Services/PinExpiry.cs`** — comment at line 9 references the wind-down.

**`Pages/GroupRides/GroupRides.razor`** — `@ride.State` / `@waiting.State` at lines 98, 153, 200.
**`Pages/Settings/Profile.razor`** line 41, **`Pages/Welcome.razor`** line 43,
**`Services/IntroTour.cs`** line 62 — all say sharing stops "the moment the adventure ends". They
become "the moment you stop sharing". Do not substitute the fourteen days here; see §1.
**`Pages/Admin/AdminStatistics.razor`** line 54 — the *Live adventures* tile.

### 4.5 MAUI host — `BlazorDLR`

No lifecycle code. `SharedFrontend.md:175` lists a *sharing wind-down persistent notification*
against `INotificationService`; grep shows no implementation, so nothing to remove — only the doc
line.

### 4.6 Configuration

No `Ride` section exists in `appsettings.json`, so nothing to delete there. Check any deployment
environment for `Ride__MaxWindDownMinutes`, `Ride__WindDownSweepSeconds` or
`Ride__MaxConcurrentLiveRidesPerUser` — see `Documentation/Set-DlrIisEnvironment.ps1`,
`SetupNote-IIS.md`, `SetupNote-Linux.md`.

### 4.7 New work — the fourteen-day idle sweep

- `RideOptions.PositionIdleDays`, default **14**.
- `NightlyMaintenanceService.DeleteIdlePositionsAsync` (the repointed `DeleteStalePositionsAsync`):
  select every `rider_position` with `RecordedUtc < now - PositionIdleDays`; under `DryRun` return
  the count and stop, otherwise delete the rows, clear `ShareLocation` on the matching
  `group_ride_member` pairs, evict each `(rideId, userId)` from `RiderPositionCache`, and return the
  count for the maintenance report.
- **Order matters.** Clear the flag *before* deleting the row, or a flush in flight between the two
  statements writes the row straight back. The existing `PositionStore.StopSharingAsync` already has
  this shape — follow it rather than writing a third copy of the obligation (§5.6's four delete
  paths are now five).
- Driven in tests by the existing `factory.RunMaintenanceAsync()`; no `PeriodicTimer` and no clock
  advance, per the testing model in `CLAUDE.md`.
- Tests: `Idle_PositionOlderThanFourteenDays_IsDeletedAndSharingCleared`,
  `Idle_PositionInsideTheWindow_Survives`,
  `Idle_Sweep_RunsWithoutAnyClientAndHonoursDryRun`.

---

## 5. Tests

### Delete outright
- `tests/DLR.Server.Tests/Rides/WindDownTests.cs` — **all 9 tests, whole file.** It is also the only
  place that resolves `SharingWindDownService` (line 359) and the only reader of
  `RideOptions.StalenessMinutes` in a settings override (line 337), so deleting it clears both.
- `MultiRideTests.LiveRideCap_ExceedingMaxConcurrent_IsRejectedAtRideStart` (124)
- `MultiRideTests.LiveRideCap_DoesNotStopJoiningMoreRides` (163)
- `MultiRideTests.Publish_RideNotYetLive_StoresNothing` (192)
- `RideDeleteTests.Delete_WhileLive_IsRefused_AndChangesNothing` (80)
- `RideDeleteTests.Delete_AfterAWindDown_TakesTheHeldPositions` (106)
- `CommentTests.ArchivedRide_ThreadIsReadOnly` (468)
- `NightlySweepTests.NightlySweep_KeepsPositionsForARideInAnUnexpiredWindDown` (236)
- `GroupRideInfoTests.RideStateChanged_UpdatesTheStateBadge` (261)
- `GroupRideInfoTests.SharingWindDownStarted_ShowsBannerWithCutoffTime` (311)
- `GroupRideInfoTests.Organiser_ClicksStartRide_CallsStartRideAsync` (381)
- `GroupRideInfoTests.StartButton_HiddenForNonOrganiser` (401)
- `GroupRideInfoTests.Organiser_EndRide_ImmediateChoice_SendsImmediateEnding` (415)
- `GroupRideInfoTests.Organiser_EndRide_WindDownChoice_SendsWindDownEnding` (434)

### Rewrite
- `CommentTests.RideCompleted_DeletesPositionsButKeepsThread` (519) and
  `MarkerTests.RideCompleted_DeletesPositionsButKeepsMarkers` (403) — both still make a real point
  (measured location dies, authored content survives). Re-express against *delete the adventure* —
  the fourteen-day sweep is too slow to be the vehicle for a point about immediacy.
- `PositionPersistenceTests.Rehydrate_LoadsLiveRidesOnly` (92) — becomes staleness-only.
- `NightlySweepTests.NightlySweep_DeletesPositionsForRidesNoLongerLive` (205) — becomes the
  fourteen-day idle rule, and gains an assertion that `ShareLocation` was cleared too.
- `GroupRideInfoTests.EventForDifferentRide_IsIgnored` (358) uses `RaiseRideStateChanged` to prove
  cross-ride isolation. Re-point at another hub event — `SharingWindDownStarted` is going too, so use
  `MemberJoined` or `RidePermissionsChanged`.

### Setup-only edits — delete the `POST …/start` call
`CommentTests.cs:529`, `MarkerTests.cs:411`, `MultiRideTests.cs:216`, `RideDeleteTests.cs:241`,
`RideRouteTests.cs:224`, `TrackRenameDeleteTests.cs:333`.

Every `ExecuteUpdateAsync(row => row.SetProperty(x => x.State, …))` helper goes too:
`PositionCounterTests.cs:99,219`, `JoinRequestBroadcastTests.cs:316`,
`MemberJoinedBroadcastTests.cs:204`, `RideHubTests.cs:317`, `PositionPrivacyTests.cs:163`,
`MultiRideTests.cs:279`, `SharingTests.cs:531`, `CommentTests.cs:768` (the `SetStateAsync` helper),
`NightlySweepTests.cs:471`, `PositionPersistenceTests.cs:226,270`, `CleanupTests.cs:505`.

Calls to `POST …/ending` in setup: `CommentTests.cs:557`, `MarkerTests.cs:430`,
`RideRouteTests.cs:270`, `SharingTests.cs:278,395`, `TrackRenameDeleteTests.cs:353`.

### Fakes
- `tests/DLR.UI.Tests/Fakes/FakeApiClient.cs` — `StartRideAsync` (706), `LastEndRide` (713),
  `EndRideAsync` (715).
- `tests/DLR.UI.Tests/Fakes/FakeRideHubClient.cs` — events at 26 and 41, raisers at 161 and 175.
- `tests/DLR.UI.Tests/State/AuthStateTests.cs` — passthroughs at 188-189.
- `CreateRideTests.cs:97`, `MyRidesTests.cs:76,90`, `RideDetailTests.cs:77` carry `EndedUtc: null` in
  fixture literals — these are `TrackSummary`, unrelated to the ride's `EndedUtc`. Check each before
  touching; most likely no change.

**Net: ~14 tests deleted, ~5 rewritten, ~25 setup edits, 3 added.**

---

## 6. Architecture tests

None of the rules in `tests/DLR.Architecture.Tests/` mention ride state, so nothing to widen.
`ClockRules` still applies to the new sweep — it must take `TimeProvider` from DI, never
`DateTimeOffset.UtcNow`.

---

## 7. Documentation

### `Documentation/design-outline.md` — the heaviest edit
- **Revision history (lines 75-97)** — add a new row for this change. Do **not** delete the v0.14,
  v0.15 and v0.17 rows; they are the record of why the wind-down existed, and this document's own
  rule is that a correction is recorded, not erased.
- **§1 line 224** — the headline claim. Rewrite.
- **§5.1 (711-724)** — the whole lifecycle diagram and its four bullets. Replaced by one sentence:
  an adventure is live from creation until it is deleted.
- **§5.3 (750 onward)** — `RideStateChanged` in the `IRideClient` sketch.
- **§5.5 (816, 824, 868, 879, 882)** — the service table row, the rehydrator rule, the wind-down
  deletion trigger, the nightly sweep rule.
- **§5.6 (886-951)** — *"The end of the ride is a choice, not an event"*, the four rules, the fifth
  rule, the `SharingEndsUtc` paragraph and the §1 correction. Roughly 60 lines. What survives:
  join-time consent, default-off, per-adventure scope, *a rider may be in a ride without sharing*,
  the four delete paths, the organiser-cannot-grant-consent asymmetry.
- **§5.7 (985 and the SRV-24 note)** — the concurrency cap.
- **§5.9 (1021-1055)** — the test list: seven `RideEnd_*` names, two `Rehydrate_*`, one
  `LiveRideCap_*`.
- **§7.3 (1246)** — profile sharing's lifecycle.
- **§7.11 (1539, 1554-1556)** — the nightly sweep's rows. Line 1554's *"orphaned `rider_position`
  rows for rides that are neither Live nor in an unexpired wind-down"* becomes *"`rider_position`
  rows with no fix for `Ride:PositionIdleDays`"*, and this is where the fourteen days is written
  down as a retention rule alongside the other three.
- **§8 (1925-1927)** — the `GroupRide` schema block.
- **§10.1 (2035, 2037, 2041, 2053, 2058)** — the privacy statement.
- **§10.3 (2091)** — multi-live-ride downlink budget.
- **§14 (2137)**, **phase table (2214)**, **risks (2235, 2279)**, **open questions (2295, and 2312 —
  Q22 *"wind-down default length"* is answered by deletion)**.
- **§16.5 (2973)**, **§19 (3137, 3208, 3236, 3241, 3391)** — `Archived` read-only.

### `Documentation/tasks-server.md`
- Line 42, phase-D table.
- **SRV-24 (694-714)** — the concurrency cap and `POST /group-rides/{id}/start`.
- **SRV-25 (719-747)** — the whole task entry.
- Lines 565, 647-648, 951, 1129.

### `Documentation/SharedFrontend.md`
- Line 175 — the wind-down notification in the `INotificationService` row.
- Line 336 — `ConsentPrompt.razor`'s description, *"Rendered when a rider joins an Open ride"*.

### User-facing copy — **these are published and must be right**
- **`Documentation/dlr-privacy-policy.html`** — lines 195, 376, 392, **396-400 (delete the whole
  "The wind-down" section)**, 425, and 500-501, the retention table's *Live position* row. That row
  is the **one place the fourteen days is stated to users**: *"Until you stop sharing, leave, or are
  removed. A position nothing has updated for 14 days is deleted regardless. No history is kept at
  any point."* Everywhere else in this file, sharing ends when the rider ends it — see §1.
- **`Documentation/dlr-support.html`** — lines 411-412, 425, and **line 630**, which documents the
  two end-choice buttons by name.
- **`Documentation/store-listing.md`** — line 93, the privacy paragraph.
- **`Documentation/store-release.md`** — lines 287 and 389, release-check steps that exercise ending
  an adventure.

### `CLAUDE.md`
No lifecycle rules in it. No change.

---

## 8. Order of work

Nothing is left to decide — §1 is the fourteen-day idle sweep in the nightly job, §2.1 is a clean
removal of `RideStateDto`.

1. **Server, behind the existing wire.** Delete `StartAsync`/`EndAsync` and
   `SharingWindDownService`, collapse the two-armed position filters, create the adventure with no
   state, drop the guards, repoint `DeleteStalePositionsAsync` at the idle rule. Get
   `dotnet test tests/DLR.Server.Tests` green.
2. **Migration.** `RemoveRideLifecycle`, with a `Down` that restores the columns defaulted to `Live`.
   Run `dotnet run --project BlazorDLR.Web -- --migrate` against a scratch database and confirm the
   template replay in the test fixture still works.
3. **Contracts.** Remove `RideEndingDto`/`EndRideRequest`, then `RideStateDto`. This is the step that
   breaks the build in the two client projects — deliberately, so nothing is missed.
4. **Shared UI.** `RideSession` first (it is where the cascade starts), then the two ride pages, then
   `LaunchRestore` and `ConsentPrompt`. **Pay attention to `GroupRideLive.razor:1578`** — the consent
   prompt's trigger is the one place where deleting a state test silently changes a privacy
   behaviour rather than a cosmetic one.
5. **Hub.** `IRideClient.RideStateChanged`, `IRideHubClient.RideStateChanged` /
   `SharingWindDownStarted`, the SignalR registrations and both fakes.
6. **Tests.** Delete, rewrite, add per §5.
7. **Docs.** §7 in the order listed; the four user-facing files last, so they describe shipped
   behaviour.
8. `dotnet format BlazorDLR.slnx --verify-no-changes`, then the full `dotnet test BlazorDLR.slnx`
   with Docker up.

---

## 9. Rough size

| | Count | Notes |
|---|---|---|
| Deleted outright | 2 files | `SharingWindDownService.cs`, `WindDownTests.cs` |
| Server edits | 16 files | |
| Contract edits | 4 files | |
| Shared UI edits | 13 files | |
| Test edits | ~22 files | |
| Doc edits | 8 files | `design-outline.md` is over half the total |
| New | 1 migration, 1 option, 1 rewritten sweep method, 3 tests | |

Around **300 lines of production code removed** — the idle sweep is a rewritten predicate on a
method that already exists rather than a new service — and **~600 lines of documentation rewritten**.
