# Plan — live positions in memory only

**Goal.** `RiderPositionCache` becomes the only place a live position ever exists. The
`rider_position` table, the write-behind flush, and the startup rehydrator all go. A process
restart loses every pin, and each rider's next push puts theirs back.

**What this is really buying**, in order:

1. **§13 Q29 stops existing.** The flush/delete race is the one thing `tasks-server.md` says to
   close before live sharing is on for anyone real. There is no flush, so nothing can resurrect a
   deleted row. This deletes the bug instead of the ~150 lines of tombstone machinery in the hot
   write path that closing it properly would need.
2. **§10.1's claim gets shorter and stronger.** v0.1 said positions were never persisted; v0.2
   corrected it because the flush made it false, and that correction is the precedent this document
   cites for every later one. This makes the original claim true again — live location never
   touches disk, never enters a nightly backup, never survives a restore.
3. Two hosted services, two config options, two nightly sweeps, a hand-written SQL statement, a
   readiness gate and a table.

---

## 1. The two things that are not obvious

Both were found while sizing this, and both would have been discovered late and awkwardly.

### 1.1 `PositionFlushService` cannot simply be deleted

It has **two** jobs, and only one of them is positions. `FlushAsync` also calls
`meter.DrainRiderCounts()` and hands the result to `IPositionWriter.CountAsync`, which is what
maintains `asp_net_users.positions_recorded` — a durable lifetime counter that survives the ride
and feeds `AdminUserRow.PositionsRecorded` (§14.6).

So the service **narrows** rather than going:

| Type | Keeps | Loses |
|---|---|---|
| `PositionFlushService` | the timer, `StopAsync`'s final drain, the meter drain | `cache.Dirty()`, the batch, `MarkClean` |
| `IPositionWriter` | `CountAsync` | `WriteAsync` |
| `PositionWriter` | the `AddCounts` statement | the `UNNEST` upsert |
| `Ride:FlushSeconds` | — it still paces the counter | nothing |

Rename it `PositionCounterFlushService` (and `IPositionWriter` → `IPositionCounter`) so the name
stops promising durability it no longer provides. `DirtyPosition`, `cache.Dirty()` and
`cache.MarkClean` lose their only caller and go with the batch.

**§5.5's "two independent cadences" table survives with different meaning**: broadcast is still
network fan-out, and the flush is no longer durability — it is *accounting*. Say so, or the next
reader will assume a position is still being written somewhere.

### 1.2 `AdminUserRow.PositionsHeld` is computed inside a translated query

`AdminEndpoints.cs:110` reads `Held = database.Set<RiderPosition>().Count(...)` inside a `Select`
over users that PostgreSQL executes. It cannot become an inline cache read.

The fix is to drop `Held` from the projection and fill it after materialisation, from
`RiderPositionCache`, the same shape the stats endpoint already uses. One extra pass over a page of
at most 200 rows, against a cache that is a dictionary — cheaper than the correlated subquery it
replaces. `AdminUserRow.PositionsHeld` keeps its contract and its meaning (*"fixes currently stored
for this account"*), which is now literally true rather than nearly true.

---

## 2. Decisions this needs

**2.1 What bounds the cache.** Nothing does, once the table goes. Today an entry for a rider who
stopped pushing is removed only by an explicit delete path; the fourteen-day rule swept the *table*.
Recommend: the nightly job keeps calling `cache.RemoveOlderThan(now - Ride:PositionIdleDays)` —
`RemoveOlderThan` already exists (SRV-36) and is the whole sweep now. No database work, no report
row worth keeping.

**The framing changes and the copy must follow.** Fourteen days stops being a retention rule — there
is nothing retained — and becomes "when a forgotten pin leaves memory". The privacy policy's
retention table should say live position is **never stored**, and drop the fourteen days entirely
rather than explaining it.

**Sub-decision: is fourteen days still the right number?** It was chosen so a rider on a three-day
trip with intermittent signal is not quietly dropped. That argument is unchanged. But the cost of
being wrong is now smaller (a restart clears everything anyway) and `PinExpiry` hides a stale pin
client-side from five minutes. Anything from a few hours up is defensible; I would keep 14 days
and rename the option `PositionIdleDays` → keep, since it still means the same thing to the code.

**2.2 `Ride:StalenessMinutes` goes.** Its only reader is the rehydrator (verified: two references,
one being the declaration). Nothing else needs a freshness floor, because nothing is ever loaded
from anywhere.

**2.3 The readiness gate goes.** `ReadyAsync()`, `MarkReady()` and the `TaskCompletionSource` exist
because rehydration was asynchronous and a request could arrive against a half-warm cache. With
nothing to warm, the cache is ready at construction. This also deletes the defect class SRV-22 paid
for once — *"the gate must open on the failure path too"* — because there is no failure path.

**2.4 Scale-out.** §9's path is vertical → per-ride affinity → `LISTEN/NOTIFY`. The table is
currently a shared truth two processes could both read. Per-ride affinity keeps a ride on one
process, so this stays compatible with the documented plan — but "just add a second container"
becomes strictly worse, and §9 should say so rather than leaving it to be discovered.

---

## 3. Behaviour after the change

| Today | After |
|---|---|
| Fix lands in cache, reaches PostgreSQL within 10 s | Fix lands in cache. That is all |
| Restart rehydrates positions < 15 min old | Restart starts empty; each rider's next push restores theirs (~5 s) |
| A rider gone quiet survives a restart for up to 15 min | A rider gone quiet is absent until they push again |
| Turning sharing off deletes a row and evicts the cache | Evicts the cache |
| Nightly sweeps for idle and not-sharing rows | Cache eviction by age; nothing to sweep |
| Positions are in the nightly backup | Positions never touch disk |
| §13 Q29: a flush can resurrect a deleted row | No flush exists |

---

## 4. Code changes, file by file

### 4.1 Delete outright
- `DLR.Server.Migrations/Positions/RiderPosition.cs`
- `DLR.Server.Migrations/Positions/RiderPositionConfiguration.cs`
- `BlazorDLR.Web/Positions/PositionCacheRehydrator.cs` (+ its `Program.cs:100` registration)
- **New migration `RemoveRiderPositions`** — `DROP TABLE rider_position`. `Down` recreates it
  (empty), per §9's one-shot `--migrate` rule.

### 4.2 `BlazorDLR.Web/Positions/`
- **`RiderPositionCache.cs`** — delete `ReadyAsync`, `MarkReady`, the `TaskCompletionSource`,
  `Dirty()`, `MarkClean`, `DirtyPosition`. Keep `Upsert`, `Remove`, `RemoveRide`, `RemoveRider`,
  `RemoveOlderThan`, `ForRide`, `RiderIds`, `RideIds`. `PositionEntry.IsDirty` loses its meaning —
  check whether the field can go with it.
- **`PositionStore.cs`** — the biggest edit. `StopSharingAsync`, `ClearRideAsync` and
  `SetPrivateAsync` become cache-only. **Delete `ClearIdleAsync`, `CountIdleAsync`,
  `ClearOrphanedAsync`, `CountOrphanedAsync`, `OrphanedQuery`, `OrphanedPosition`** — yesterday's
  SRV-37 sweep exists to catch a race that no longer happens. `SnapshotAsync` and `LocatedAsync`
  drop their `await cache.ReadyAsync()`. `HasPositionAsync`'s table read (below) moves here or to
  the cache.
- **`PositionFlushService.cs` / `PositionWriter.cs`** — narrow per §1.1.
- **`RideOptions.cs`** — delete `StalenessMinutes`. Keep `BroadcastSeconds`, `FlushSeconds`,
  `PositionIdleDays`, `MaxRoutesPerRide`.

### 4.3 Elsewhere on the server
- **`Rides/MembershipEndpoints.cs:243-247`** — `HasPositionAsync` becomes a cache read. Note this
  *removes an existing inconsistency*: §5.5 already says `RideDetail`'s `HasPosition` reads the
  cache and not the table, and this one did the opposite.
- **`Admin/AdminEndpoints.cs:110`** — per §1.2.
- **`Maintenance/NightlyMaintenanceService.cs`** — delete `DeleteIdlePositionsAsync` and
  `DeleteOrphanedPositionsAsync`; add the one-line cache eviction per §2.1.
- **`Maintenance/MaintenanceReport.cs`** — delete `PositionsDeleted` and
  `OrphanedPositionsDeleted`, and their lines in the log message and the alert email.
- **`Program.cs`** — drop the rehydrator registration; rename the flush service registration.

### 4.4 Architecture tests
`SqlRules` permits raw SQL in `BlazorDLR.Web/Positions/` and the surviving `AddCounts` statement
still lives there, so **no rule changes**. Worth re-reading `SqlRules`'s doc comment, which names
"the nightly sweep's set-based deletes" as a reason `Maintenance/` is on the list — that reason
is going.

---

## 5. Tests

**The helper whose meaning changes is the sizing driver.** `DlrWebApplicationFactory.FlushPositionsAsync`
(`tests/DLR.TestSupport/Hosting/DlrWebApplicationFactory.cs:220`) currently means *"make the
position durable"*. After this it means *"bank the counter"*. There are **15 call sites across 8
files**; each needs reading to decide which meaning it wanted:

- `Admin/PositionCounterTests.cs` — genuinely wants the counter. Keeps the call.
- `Comments/CommentTests.cs`, `Markers/MarkerTests.cs`, `Rides/RideDeleteTests.cs`,
  `Rides/SharingTests.cs`, `Rides/MultiRideTests.cs` — wanted a row to exist before asserting it
  was deleted. The cache is immediate, so the call goes and the assertion moves from a table count
  to the snapshot endpoint.
- `Positions/PositionPrivacyTests.cs` — same, and its table assertions become API assertions.

**Delete outright**
- `Positions/PositionPersistenceTests.cs` — the whole file is about rehydration and durability.
  Check each test for a surviving point before deleting; `Rehydrate_SkipsPositionsOlderThanStalenessWindow`
  and friends have no subject left.
- `RiderPositionCacheTests` — the `ReadyAsync`/`MarkReady` tests (~136-148).
- `NightlySweepTests` — the two idle tests and the three orphan tests added in SRV-37.

**Rewrite**
- `Positions/PositionFlushTests.cs` — narrows to the counter, and is where "a graceful shutdown
  still banks the counts" should be asserted.
- Add: **`Restart_LosesEveryPin_AndTheNextPushRestoresIt`** — the honest statement of the trade,
  and the one test that would fail if somebody reintroduced a write.
- Add: a cache-eviction-by-age test, if §2.1's nightly call is kept.

**Net: ~10 deleted, ~8 rewritten, ~15 call-site edits, 2 added.**

---

## 6. Documentation

- **`design-outline.md`** — a new revision block. **§5.5 is mostly rewritten**: the schema block,
  the flush statement, the four rehydration rules, the "cost of the trade-off" paragraph and the
  `>` known-gap block all go; the two-cadence table survives with the flush recast as accounting
  (§1.1). **§5.6**'s delete-path list drops the sweep. **§7.11**'s table loses both position rows.
  **§8**'s schema block loses `rider_position`. **§9** gains §2.4's note. **§10.1** is the headline
  edit — the "what is stored, exactly" paragraph becomes *never stored*. **§13 Q16/Q29 close.**
  §14's test list, and the v0.2 correction row gains a pointer that the original claim is true
  again.
- **`tasks-server.md`** — SRV-38; strike Q29 from *"three things are owed before real riders"*,
  leaving two, both of which need a human in staging.
- **`dlr-privacy-policy.html`** — the retention table's *Live position* row becomes "never stored;
  held in memory only while you are sharing, and gone when you stop or the server restarts", and
  the fourteen-day sentence is deleted rather than reworded. This is the second correction to that
  row in two changes, so the wording wants care.
- **`README.md` §2**, **`store-listing.md`**, **`dlr-support.html`** — all currently say positions
  are stored one-row-per-rider. They can now say something better.

---

## 7. Order of work

1. **Narrow the flush service and the writer** (§1.1) while the table still exists — smallest step,
   and it isolates the counter from everything that follows.
2. **`PositionStore` and the cache** — cache-only deletes, drop the sweep methods and the gate.
3. **The three outside consumers** — `HasPositionAsync`, `AdminEndpoints`, the nightly job.
4. **Delete the rehydrator, the entity and the configuration; add the migration.** The build breaks
   here and that is the point.
5. **Tests** — the 15 `FlushPositionsAsync` sites first, since they gate everything else going green.
6. **Docs**, §10.1 and the privacy policy last so they describe shipped behaviour.
7. `dotnet format --verify-no-changes`, then all four suites with Docker up.

---

## 8. Rough size

| | Count |
|---|---|
| Deleted outright | 3 files + 1 migration added |
| Server edits | ~9 files |
| Test edits | ~10 files |
| Doc edits | 6 files, `design-outline.md` §5.5 and §10.1 being most of it |

Around **400 lines of production code removed**, one open privacy bug closed by deletion, and a
privacy claim that gets shorter rather than longer for the first time in the project's history.
