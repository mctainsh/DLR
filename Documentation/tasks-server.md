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

---

## Milestones

| Milestone | Tasks | Maps to §11 | Outcome |
|---|---|---|---|
| **A — Skeleton and guards** | SRV-01 … SRV-05 | Phase 0 | An empty solution that already enforces its own rules |
| **B — Identity** | SRV-06 … SRV-13 | Phase 1 | Register, sign in, never sign in again, recover if you left an address |
| **C — Tracks** | SRV-14 … SRV-19 | Phase 1 | Record, upload, import GPX, edit on the web |
| **D — Group rides** | SRV-20 … SRV-25 | Phase 2 | Join, consent, live positions, the wind-down |
| **E — Content** | SRV-26 … SRV-30 | Phase 2 | Markers, photos, the thread, polls |
| **F — Operations** | SRV-31 … SRV-35 | Phase 2–3 | Moderation, the nightly job, deployment, backups |

Milestone A is a prerequisite for everything. Within B–E, the order below is the dependency order;
C and B can overlap once SRV-09 exists, because tracks need an owner but not a full auth story.

---

# Milestone A — Skeleton and guards

The guards come first because they are cheap now and unenforceable later. Every one of these tasks
is a rule the rest of the project leans on.

### SRV-01 — Solution, style and the licence files
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

### SRV-02 — Architecture tests, before there is architecture to break
**First red test:** `Core_ReferencesNoMauiAssembly` — passes trivially, and that is the point: it
fails the first time somebody adds the reference.
**Then build:** `DLR.Architecture.Tests` with the §10.4 list that is checkable today —
no MAUI in `DLR.Core`, no `DateTime.Now`/`UtcNow` outside `DLR.TestSupport`, no `XmlDocument`
anywhere, no raw SQL outside the three permitted folders.
**Done when:** each rule has a test, and you have deliberately broken each one once to watch it go
red. A guard you have never seen fail is a guard you do not have.
**Refs:** §10.4

### SRV-03 — CI: build, test, format, licence gate
**First red test:** a deliberately added package with a non-approved licence fails the gate.
**Then build:** GitHub Actions — restore, build, `dotnet test`, `dotnet format
--verify-no-changes`, and a transitive licence scan that fails on **unknown** as well as
disallowed. `pull_request` (never `pull_request_target`) for fork PRs; no job that touches a
secret runs on one. DCO sign-off check.
**Done when:** a fork PR runs the full suite and sees no secrets.
**Refs:** §14.4, §14.6.3, §14.6.4

### SRV-04 — Test harness: Postgres, clock, email
**First red test:** `Database_Container_StartsAndAppliesMigrations`
**Then build:** in `DLR.TestSupport` — a Testcontainers PostgreSQL fixture shared per collection,
a `WebApplicationFactory` wired to it, `FakeTimeProvider` registered over `TimeProvider`, and a
collecting fake `IEmailSender`. Register `TimeProvider` in DI from day one; retrofitting it is
miserable.
**Done when:** a test can advance the clock six months and assert on a captured email. Everything
in Milestones B–F depends on this and on nothing else external.
**Refs:** §10.4

### SRV-05 — `/api/v1/about` and the AGPL §13 source offer
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

### SRV-06 — `AppUser`, Identity configuration, and the username rules
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

### SRV-07 — Password policy and the breach check
**First red test:** `Register_WeakOrBreachedPassword_IsRejected`
**Then:** `Register_BreachServiceUnavailable_StillAllowsRegistration`
**Then build:** 10-character minimum, no composition rules, Pwned Passwords range API behind an
interface so the test can fake an outage. **A third-party outage must not stop signups.**
**Refs:** §7.2

### SRV-08 — `/auth/token`: JWT access tokens
**First red test:** `Login_UnknownUsername_ResponseTimingMatchesKnownUsername`
**Then:** `Login_FiveFailures_LocksAccountForFifteenMinutes`
**Then build:** the password grant, HS256 with `kid`, 15-minute lifetime, claims `sub`/`unm`/`dev`/
`jti`, signing key from configuration and never `appsettings.json`. Dummy password verification on
an unknown username so timing does not leak.
**Refs:** §7.4

### SRV-09 — Refresh tokens: rotation, reuse detection, the grace window
**First red test:** `Refresh_ValidToken_RotatesAndInvalidatesPredecessor`
**Then:** `Refresh_ReusedToken_RevokesEntireFamily`,
`Refresh_ReusedWithinGraceWindow_ReturnsSameSuccessor`, `Refresh_AfterOneYearIdle_StillSucceeds`
**Then build:** `refresh_token` table, SHA-256 at rest, `family_id` chains, `successor_id`, and the
10-second idempotency window keyed on the successor.
**Watch for:** the grace window is not optional. Without it a client that fires two requests and
refreshes twice revokes its own session, and with permanent sessions that is the *most likely* way
anyone is ever logged out.
**Refs:** §7.4, §7.13

### SRV-10 — Devices, sessions and `last_active_utc`
**First red test:** `RevokeSession_TargetDeviceCannotRefresh`
**Then:** `Refresh_UpdatesLastActiveUtc`, `Refresh_WithinThrottleWindow_DoesNotRewriteLastActive`
**Then build:** `Device`, the session list and revoke endpoints, and the last-active update
**piggybacked on the refresh that already happens** at app start, throttled to one write an hour.
**Refs:** §7.10

### SRV-11 — Email: confirmation, reset, and two token providers
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

### SRV-12 — Abuse: the IP ladder, rate limits, forwarded headers
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

### SRV-13 — Profile fields and `SharedProfile`
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

### SRV-14 — GPX reader, writer and the hostile corpus
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

### SRV-15 — Track stats, simplification and the editor primitive
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

### SRV-16 — Blob storage and track upload
**First red test:** `Upload_SameClientGuidTwice_IsIdempotent`
**Then:** `Upload_StoresBlobAndComputesContentHash`, `TrackList_SortsOnCreatedUtc_NotStartedUtc`
**Then build:** `IBlobStore` over a filesystem volume (**not** object storage — §9.1), the `Track`
entity with its nullable stats columns, `POST /tracks`, and the list/detail endpoints.
**Refs:** §6.2, §8, §9.1

### SRV-17 — GPX import endpoint
**First red test:** `Import_DryRun_PersistsNothing`
**Then:** `Import_SameContentTwice_WarnsButProceeds`, `Import_ExceedsSizeCap_Returns413`,
`Import_WaypointsPresent_AreCreatedAsMarkers` *(defer the assertion until SRV-26)*
**Then build:** `POST /tracks/import` multipart, `?dryRun=true`, the per-user rate limits, and
Problem Details that name the actual problem.
**Refs:** §15.3

### SRV-18 — Track editing, versioning and undo
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

### SRV-19 — Full-resolution points endpoint
**First red test:** `Points_ReturnsEncodedPolylineWithDeltaTimes`
**Then build:** `GET /tracks/{id}/points`, gzipped, in the encoding the editor indexes against.
**Note:** this exists for the web editor; the component that consumes it is a UI task.
**Refs:** §15.5

---

# Milestone D — Group rides

### SRV-20 — Rides, join codes and join requests
**First red test:** `JoinByCode_ApprovalRide_CreatesPendingRequestOnly`
**Then:** `JoinByCode_OpenRide_JoinsImmediately`, `JoinRequest_Approved_AddsMemberAndNotifiesRider`,
`JoinRequest_Declined_WithBlock_CannotRequestAgain`, `JoinRequest_SixthPending_IsRejected`
**Then build:** `GroupRide` with `JoinPolicy`, Crockford base32 join codes, the request table with
its partial unique index, the admit/decline endpoints, and the member cap.
**Also:** the join-code rate limit that §14.5 found missing — per-IP and per-account, counting
failures. Do not ship this endpoint without it.
**Refs:** §5.2, §14.5

### SRV-21 — Sharing consent
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
**Refs:** §5.6, §7.3, §10.1

### SRV-22 — Position cache, flush and rehydration
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
**Refs:** §5.5

### SRV-23 — The hub: authorisation and fan-out
**First red test:** `Hub_JoinRide_NonMemberIsRejected`
**Then:** `Hub_JoinRide_PendingRequesterIsRejected`, `Hub_ConnectionWithoutToken_IsRejected`,
`Hub_LongLivedConnection_SurvivesAccessTokenExpiry`
**Then build:** `RideHub`, the query-string token lift scoped to `/hubs/ride` **only**,
`CloseOnAuthenticationExpiration` left `false`, and `RideBroadcastService` sending one batch per
ride per 5 s.
**Watch for:** authentication is not authorisation. The membership check is the only thing between
an account and a stranger's location.
**Refs:** §5.3, §7.6

### SRV-24 — Multi-ride publishing
**First red test:** `Publish_SharingInRideAOnly_StoresNoRowForRideB`
**Then:** `Publish_MemberOfThreeLiveRides_WritesToAllThree`,
`LiveRideCap_ExceedingMaxConcurrent_IsRejectedAtRideStart`
**Then build:** `PublishPosition` carrying **no ride id** — the server fans out to every ride where
that rider's own consent flag is set.
**Watch for:** the filter is on the **write**. A rider not sharing with a ride has no row in it at
all — not a hidden pin.
**Refs:** §5.7

### SRV-25 — Ride end and the sharing wind-down
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
