# Dumb Luck Rides — Design Outline

> **Status:** Draft **v0.32** — architecture outline; Milestone A of `tasks-server.md` is built.
> **Assumption:** "Mani" = **.NET MAUI**. Target framework `net10.0-android` / `net10.0-ios`.
> **UI:** one shared Razor component library, hosted by **MAUI Blazor Hybrid** on mobile and **Blazor WebAssembly** on the web (§18).

### Revision history

| Ver | Change | Why |
|---|---|---|
| 0.1 | Initial outline | — |
| 0.2 | Live positions **persisted** as last-known-only, flushed to PostgreSQL every 10 s (§5.5) | Server restarts must come up with a warm cache, not a blank map |
| 0.2 | **Redis removed**; scale-out is vertical → per-ride affinity → `LISTEN/NOTIFY` (§9) | A group ride is a natural shard, so no backplane is needed |
| 0.2 | Privacy claims corrected — v0.1 said positions were "never persisted" (§10.1) | The doc must not misstate what is stored |
| 0.2 | Maps: **device-native first** behind `IMapRenderer`; Mapsui is the planned second renderer (§4.5) | Ship on built-in maps, keep the tile-server swap cheap |
| 0.2 | **Tabs**, with mandatory YAML/Markdown carve-outs (§10.5) | Project convention |
| 0.2 | **TDD** is the delivery unit (§10.4, §11) | Project convention |
| 0.3 | New **§7 Identity, Registration & Login**; old §§7–12 renumbered to 8–13 | Auth was a single table row |
| 0.3 | Not `MapIdentityApi` — custom token endpoint for revocable refresh (§7.1) | "Sign out my stolen phone" is a requirement here |
| 0.4 | Confirmation valid **24 h**, reset **1 h** — needs **two token providers** (§7.7) | `TokenLifespan` is global; one setting silently changes both |
| 0.4 | Email via **Zoho**: Zoho Mail SMTP now, **ZeptoMail** before real users (§7.12) | Domain SPF/DKIM already in place — removes the biggest setup hurdle |
| **0.5** | **Username + password is the account.** Email is optional (§7.2) | Lowest possible signup friction |
| 0.5 | Confirmed-email gate on group rides **removed**; replaced by **organiser consent on both join paths** (§5.2, §10.1) | The organiser always decides who is in the ride — a stronger guarantee than email verification ever gave |
| 0.5 | Two join paths: **code from organiser**, or **request that the organiser admits** (§5.2) | Explicit product decision |
| 0.5 | Per-IP registration ladder: 1–3 username-only, **4+ must confirm an email** (§7.8) | CGNAT-safe throttling with no dead end |
| 0.5 | Sessions **never expire** on a device; refresh tokens are effectively permanent (§7.4) | A person who signs in on a device never signs in again |
| 0.5 | `last_active_utc` recorded at every app start (§7.10); **empty accounts deleted after 180 days** (§7.11) | Replaces time-based session expiry with something better targeted |
| **0.6** | **`DisplayName` removed.** The username *is* the name shown on the map (§7.2) | One identity field, nothing to keep in sync |
| 0.6 | Usernames are **ASCII-only, case-preserved, unique case-insensitively** (§7.2) | A visible unique handle makes homoglyph impersonation a real risk |
| 0.6 | Registration notice also states that an **email-less empty account is deleted after 6 months, with no warning possible** (§7.2) | The 150-day warning email cannot be sent to an account with no address |
| **0.7** | **Usernames are immutable** — chosen once, never changed. No endpoint, no support path (§7.2). Resolves §13 Q9 | A stable handle, and it removes relabel propagation, stale claims and name-recycling entirely |
| 0.7 | Registration **confirms the username** before creating the account (§7.2) | An irreversible choice needs one chance to catch a typo |
| **0.8** | New **§7.3 optional profile fields** — display name, phone, email, each with a sharing switch **off by default**; old §§7.3–7.15 renumbered to 7.4–7.16 | Riders want to swap contact details with people they actually ride with |
| 0.8 | Shared fields are **ride-scoped** to current co-members and revoke when the ride ends (§7.3) | Same lifecycle as position sharing; no profile lookup endpoint exists |
| 0.8 | Map label is **still the username** — a shared display name never replaces it (§7.3) | Keeps usernames cacheable forever and prevents self-chosen labels on others' maps |
| 0.8 | Phone number is **never verified**; `PhoneNumberConfirmed` is permanently false (§7.3) | SMS costs money and an SMS reset path would add takeover surface for no benefit |
| **0.9** | New **§4.6 Android Auto + Apple CarPlay** — template projections with the group on a map, built in Phase 3 | Required capability |
| 0.9 | **Mapsui moves onto the critical path.** The native `Map` control cannot draw into an Android Auto `Surface`, so it cannot serve the car (§4.5, §4.6) | Biggest knock-on: car support forces the second renderer |
| 0.9 | `IMapRenderer` gains **`MapHostKind`** — an unsupported host/renderer pairing fails at startup (§4.6) | A blank car screen is a worse failure than a crash |
| 0.9 | New **`IRideSessionState`** in `DLR.Core`; phone and both car heads project from one snapshot (§4.6) | Car screens must never touch phone ViewModels |
| 0.9 | **CarPlay entitlement requested in Phase 1**; Android Auto binding availability is a Phase 0 spike (§4.6, §11) | Apple approval takes weeks–months and gates shipping, not development |
| **0.10** | New **§14 Open Source and the Repository Boundary** — what is committed, what stays local, and why | The project is going public |
| 0.10 | **Test GPX fixtures must be synthetic.** Real ride traces reveal home addresses (§14.2) | Committing them would publish exactly the data §10.1 promises to protect |
| 0.10 | Gap found while auditing for publication: **`POST /group-rides/join` has no rate limit** (§14.5, §13 Q12) | A 6-char join code is script-guessable without one |
| 0.10 | Abuse thresholds move to **configuration** rather than constants (§14.5) | Tunable against real abuse without a release, and the repo does not reveal current values |
| **0.11** | **Licence decided: the project is distributed under AGPL-3.0** (§14.6). Resolves §13 Q11 | This is a hosted service; network copyleft is the only licence that reaches someone running a modified server |
| 0.11 | AGPL §13 makes a **source offer a runtime requirement** — `GET /api/v1/about` and a web footer link carry the repo URL and the running commit (§14.6, §6.3) | An obligation the deployed server must satisfy, not just a file in the repo |
| 0.11 | LICENSE carries an **additional permission under GPL-3 §7** for app-store distribution and proprietary platform SDK linking (§14.6) | Unmodified AGPL terms conflict with App Store / Play terms and with linking Google Play Services |
| 0.11 | **Inbound = outbound with DCO sign-off**, no CLA; CI **fails on a non-approved dependency licence** (§14.6) | Keeps the §7 permission valid for contributed code, and makes §10.4's Shouldly-over-FluentAssertions rule mechanical |
| **0.12** | New **§15 Tracks — Import, Editing and Versioning**. A track now has **two sources**: recorded on device, or **imported from GPX** on either the app or the web | Required capability. Appended as §15 rather than inserted as a peer of §5 to avoid renumbering §§6–14 and every cross-reference in the document |
| 0.12 | **Web track editor: remove points from the start, the end, or a span in the middle** (§15.5) | Required capability. Trim the ride to the house, cut a lunch stop, delete a GPS spike |
| 0.12 | **§4.4's "tracks are immutable once ended" is replaced by single-writer ownership**: the device writes until upload completes, the server writes forever after (§15.4) | Editing would otherwise reintroduce exactly the sync conflicts immutability was there to prevent |
| 0.12 | All three edits are **one primitive — remove a half-open raw index range** — applied against the full-resolution track, never the simplified one (§15.5) | Editing against a simplified polyline deletes the wrong points; one index space is the only safe design |
| 0.12 | An **interior removal inserts a segment break**; distance and duration are never counted across it (§15.5) | Splicing the ends together would invent a straight-line path the rider never took, and add distance that never happened |
| 0.12 | Edits are **destructive with a 7-day undo window**, plus *"remove the original now"* — and the doc states plainly that **backups still hold it** (§15.6, §10.1) | Trimming your house out of a track is the motivating case; retaining the original forever would defeat it, and pretending backups are instant would repeat v0.1's false privacy claim |
| 0.12 | GPX parsing is **hostile-input handling**: DTD prohibited, streaming reader, size **and** point caps (§15.3) | First untrusted file format in the project — XXE and entity expansion are the whole reason `XmlDocument` is banned here |
| 0.12 | **One codec and one stats implementation in `DLR.Core`**, shared by app, server and editor (§15.7) | The app must import with no signal, the server must re-validate, and a no-op edit must not move the numbers |
| **0.13** | New **§16 Map Markers** — point, optional direction, icon, title, note and one optional photo, attached to **either a track or a group ride** (§16.1) | Required capability. The exclusive arc is the only honest model: the two contexts share a payload but not a lifecycle |
| 0.13 | **Markers force Mapsui onto the phone's critical path too.** The native `Map` control has no custom marker imagery, no marker rotation and no persistent labels (§4.5, §16.3) | Same shape of finding as v0.9's car discovery — and `MapCapabilities` already existed to absorb it |
| 0.13 | **Icons are a curated set keyed by string**, never user-supplied images (§16.2) | No upload surface, no moderation problem, renders offline and on a car screen, and an unknown key from a newer client degrades instead of failing |
| 0.13 | **`Direction` is nullable and is not `0`** (§16.2) | Zero is due north — a real bearing. Most markers have no direction at all |
| 0.13 | **Photo ingest strips all metadata by re-encoding, server-side, always** (§16.4) | EXIF GPS would reinstate exactly the home address §15.6 lets a rider trim off the track — the two features must not fight each other |
| 0.13 | Image decoding is treated as hostile input, with a **decoded-pixel cap** as well as a byte cap (§16.4) | A 40 KB PNG can decode to hundreds of megabytes; a byte cap alone does not bound it |
| 0.13 | **SkiaSharp (MIT) for image processing, not ImageSharp** (§16.4, §14.6.3) | ImageSharp v3+ is under the Six Labors Split Licence, so it needs a deliberate allow-list decision; SkiaSharp is MIT and already arrives with Mapsui |
| 0.13 | Photos shared between riders make this a **UGC app** — Apple requires reporting and blocking (§10.2, §16.5) | A store-review requirement that has nothing to do with the code and bites at submission |
| 0.13 | `<wpt>` waypoints now **import as markers and export from them**, retiring v0.12's "ignored" rule (§15.3, §16.6) | Markers gave waypoints a meaning; the GPX round-trip is now lossless in both directions |
| 0.13 | §5.4's *"regroup here"* pin is **no longer its own feature** — it is a marker with the `regroup` icon (§16.1) | One authored-pin mechanism, not two |
| **0.14** | New **§17 Ride Comments** — a thread per group ride carrying text, photos, pinned posts, reactions and polls | Required capability |
| 0.14 | **While a ride is `Live`, ordinary comments never push.** Only a pinned post from the organiser breaks through (§17.6) | This app is used by people operating vehicles. A notification per comment is a design that asks riders to read their phone mid-corner |
| 0.14 | **A poll is a kind of comment**, not a separate entity (§17.5) | It inherits threading, pinning, reactions, permissions and moderation for free |
| 0.14 | **Reactions are a fixed keyed set, one per user per comment, and are never broadcast one message at a time** (§17.4) | Same forward-compatibility argument as marker icons, and the same batch-don't-relay lesson §5.3 already learned the hard way |
| 0.14 | Poll votes are **attributed, not anonymous** (§17.5) | The question is *"who's coming Saturday?"* — knowing who is the entire point |
| 0.14 | `MarkerReport` **generalised to `ContentReport`**, which snapshots the reported content (§17.7, §16.5) | Comments are now the largest UGC surface; two report tables would have drifted, and hard-deleting a reported comment must not destroy the evidence |
| 0.14 | Thread ordering is by **server receipt**, with the authored time shown when it differs materially (§17.3) | A rider who reconnects after four hours must not inject a stale conversation into the middle of a live one |
| 0.14 | An `Archived` ride's thread becomes **read-only** (§5.1, §17.6) | The existing lifecycle already had the right state; it just had nothing to say about it |
| **0.15** | New **§5.6**: location sharing is **asked at join, defaults to off**, and is revocable at any moment (§5.6) | Joining a ride and consenting to broadcast are two decisions, and the app should not make one imply the other |
| 0.15 | **A rider may be in a ride without sharing.** Asymmetric visibility is allowed, and the member list shows who is sharing (§5.6) | Making sharing the price of admission is coercive; the honest alternative is to make the asymmetry visible and let the group deal with it |
| 0.15 | **Ride end is a choice between two sharing outcomes** — stop everyone now (default), or a bounded **wind-down** in which riders stop themselves (§5.6) | Ending a ride while stragglers are still an hour from home should not blank the map, but "leave it on" cannot mean indefinitely |
| 0.15 | The wind-down is **capped, cannot be extended, and force-stops server-side** (§5.6) | Without a hard backstop, "let them stop themselves" is always-on tracking of whoever forgets — exactly what §1 promises this app is not |
| 0.15 | **§1 and §10.1's headline claim corrected**: sharing ends with the ride *or within a capped wind-down*, not simply "with the ride" | The privacy statement must describe what the code does. Same class of correction as v0.2 |
| 0.15 | New **§5.7**: **a rider can be in several rides at once** — one position publish, fanned out to every ride where that rider's own consent flag is set (§5.7) | Publishing once per ride would multiply battery and data by the number of rides, and per-ride consent is per-ride or it is meaningless |
| 0.15 | `IRideSessionState` gains a **focused ride** plus a list of the others; the car head shows one at a time (§4.6, §5.7) | v0.9 assumed one live ride. The car's ride-picker screen was already there, which is what made this cheap |
| 0.15 | New **§5.8**: the organiser toggles **whether members may add markers, comments, or photos**, at any time (§5.8) | Required capability |
| 0.15 | Turning a content permission off **stops new content and deletes nothing** (§5.8) | Same rule as profile sharing in §7.3: revoking permission is not a delete instruction |
| **0.16** | New **§18 UI Architecture**. **One shared Razor Class Library**, hosted by **MAUI Blazor Hybrid** on mobile and **Blazor WebAssembly** on the web. XAML + MVVM is out (§3, §18.1) | Required architecture. One UI codebase across three surfaces instead of XAML plus Razor plus a JS map |
| 0.16 | **Blazor Server is out; the web is WASM** with static SSR retained for public pages (§6.2, §18.4) | The web client becomes just another API client — and the sticky-session constraint in the scale-out path disappears with it (§9.2) |
| 0.16 | **`NativeMapRenderer` (MAUI Maps) is deleted from the design.** The phone and the web both run **MapLibre GL JS**; Mapsui survives **for the car surfaces only** (§4.5, §18.3) | A native map control cannot be hosted inside a Razor page, and v0.13 had already established that it could not draw markers either |
| 0.16 | **Tile hosting moves from Phase 3 to Phase 1** (§4.5, §11) | The honest cost of the line above: "device-native maps are free" was the reason tiles were deferred, and that option is gone |
| 0.16 | **Web sessions expire; mobile sessions still never do** (§7.5, §18.5) | A browser is a shared device far more often than a phone, and a permanent refresh token in a browser is a different risk from one in a Keychain |
| 0.16 | **Offline-first stays a mobile-only property** — the WASM client is online-only (§18.6) | Shared components must not imply shared behaviour; SQLite is on the phone, and the browser talks to the API |
| 0.16 | Confirmed unchanged: **Android and iOS remain one MAUI project**, and the car heads (§4.6) remain entirely native | Already the design since v0.1; the Blazor change does not touch either |
| 0.16 | **bUnit joins the test stack** — every screen renders in `dotnet test`, no emulator (§18.7, §10.4) | A component written once is tested once; mobile UI testing stops being the thing that never happens |
| **0.17** | **Full-document consistency pass.** No new features; the entries below are corrections where v0.15 and v0.16 left older text saying something no longer true | A design document that contradicts itself is worse than one that is merely incomplete — the reader cannot tell which half is current |
| 0.17 | **`MapHostKind.AppView` removed** (§4.6) | It had no factory after v0.16, so the architecture test asserting every host kind has one would have failed on its own contract |
| 0.17 | **The join-time consent copy corrected** — it still promised sharing "stops when the ride ends" (§5.6) | v0.15 added a two-hour wind-down and left the consent prompt overstating the protection. Consent copy is the last place an inaccuracy is acceptable |
| 0.17 | §10.1's "what is stored" statement now names the **wind-down expiry** as a deletion trigger (§10.1) | Same correction, in the place the privacy policy is written from |
| 0.17 | **Profile-field sharing ends at `Completed` and does *not* follow the position wind-down** — stated explicitly (§7.3, §10.1) | Both sections said "same lifecycle as position sharing", which stopped being true. Watching mates get home is a reason to extend a *position*, not a phone number |
| 0.17 | §7.5's `RevalidatingServerAuthenticationStateProvider` replaced with the **refresh-cycle revocation path** (§7.5) | A Blazor Server API survived into a WASM section; the real bound is the 15-minute access token |
| 0.17 | The nightly job's summary table gained the **three sweeps added since v0.12** — track revisions, orphaned photo blobs, resolved reports (§7.11) | The prose already required them; the table listing "one nightly service, not four" listed four |
| 0.17 | Tiles, the Maps API key, the "degraded pin" fallback and the tile-bandwidth risk all **re-stated for a world with no native map** (§9, §11, §12, §14.2) | Five places still assumed the free device map that v0.16 deleted |
| 0.17 | §10.5 now says what the formatter **cannot** reach: Razor markup, not XAML (§10.5) | The UI is Razor now; the old note guarded a file that barely exists |
| 0.17 | §13 Q5 sharpened: **threads have no retention answer at all** (§13) | Noticed while reconciling lifecycles — every other entity has one |
| **0.18** | **Cloudflare removed from the design entirely.** New **§9.1** replaces it in the three separate jobs it was doing | Not using it. Each job needed its own answer, and two of them turned out not to need a service at all |
| 0.18 | **No CDN in v1.** Caddy serves the WASM bundle, tiles, photos and static assets from the VPS's included 20 TB/month (§9.1) | The whole workload is two to three orders of magnitude inside the allowance. A CDN here would be a dependency bought with no problem to solve; Bunny.net is the named lever if that ever changes |
| 0.18 | **Self-hosted PMTiles served by Caddy over HTTP range requests** — no object store, no edge worker (§9.1, §4.5) | PMTiles is a single file and `file_server` already speaks ranges. The usual R2-plus-Worker recipe exists to solve a problem a VPS with a disk does not have |
| 0.18 | **Blobs move from object storage to a Docker volume**, behind `IBlobStore` (§6.2, §9.1) | One thing to back up, no S3 credentials in the process, no egress bill — and the seam keeps the S3 option a registration change |
| 0.18 | Backups: **`restic` → Backblaze B2, encrypted client-side**, off-provider on purpose (§9.1) | Backups hold last-known positions and email addresses, so a provider breach must not be a user-data breach. Hetzner's Storage Box is fine as a second copy, never the only one — it is the same account as the server |
| 0.18 | **Disk replaces bandwidth as the binding constraint**, with a new High-adjacent risk row (§9.1, §12) | 40 GB shared by Postgres, blobs and tiles — and a full disk stops Postgres *writing*, which is a far worse failure than a slow map |
| **0.19** | **Web uses OpenStreetMap tiles; Android and iOS use Apple Maps via MapKit JS** (§4.5) | Required. Both are free at this scale and neither needs anything hosted |
| 0.19 | **v0.16's "phone and web run the same map code" is withdrawn.** `RideMap.razor` stays one component with **two JS modules** behind one interop contract (§4.5, §18.3) | The honest cost of two providers. The rest of the shared UI is untouched — this is one seam, not a reversal |
| 0.19 | **Tile hosting leaves Phase 1 again**, and the tile extract leaves the VPS disk (§9.1, §11) | v0.16 pulled it forward because nothing free was left; OSM and Apple are both free, so the bill is deferred rather than paid |
| 0.19 | **MapKit JS needs a server-minted ES256 token** — new `GET /api/v1/maps/token`, new `.p8` secret (§4.5, §14.2) | The private key must never reach a client, which makes the map a server dependency for the first time |
| 0.19 | **Offline maps are gone on the phone**, not deferred — MapKit JS has no offline mode (§4.5) | The sharpest consequence: recording still works in a dead zone, but the map behind it goes blank. `MapProvider` keeps an offline option open for later |
| 0.19 | **Shipping Android now depends on an Apple Developer account** (§4.5, §12) | An unusual coupling that deserves to be visible: Apple's terms changing would take the map off *Android* |
| 0.19 | Two Phase 0 questions added: **does MapKit JS render acceptably in an Android WebView, and do Apple's terms permit it there** (§4.5, §11) | Do not plan around an assumption — the same rule §4.6 applied to the `androidx.car.app` binding |
| **0.20** | **`DLR.Server.Migrations` holds the `DbContext` and its entity configurations**, not only the migration files (§3) | Found while building SRV-01. A migrations assembly must reference the model it describes, and the running server must load the migrations assembly — as two projects that is a reference cycle. Making the migrations project the persistence layer breaks it in the one direction that has no downside |
| 0.20 | The `+dirty` marker is stored as `.dirty` in `SourceRevisionId` and **surfaced as `+dirty` on the `commit` field** (§14.6.2) | Semantic version build metadata is dot-separated, so `1.4.0+9f2c1ab.dirty` is well-formed and `1.4.0+9f2c1ab+dirty` is not. The wire format §14.6.2 specifies is unchanged |
| 0.20 | `/api/v1/about`'s `sourceUrl` is **configuration, not a constant** (§14.6.2) | A fork running a modified server owes its users *its own* source. A hard-coded upstream URL would make every downstream deployment non-compliant by construction — the opposite of what §13 is for |
| 0.20 | **An embedded ICC colour profile is metadata, and the re-encode has to drop it too** (§16.4) | Found in SRV-27 by `Photo_AllMetadata_IsAbsentAfterReEncode`. `SKBitmap.Copy` preserves the decoded colour space and the JPEG encoder writes it back out as an `APP2` segment — so the file was not metadata-free after all, on precisely the path a small upright photograph takes |
| 0.20 | **A photo's two objects live on the blob volume, not in object storage** (§16.4, §16.6) | §16.4 still said object storage; v0.18 moved every blob to a Docker volume behind `IBlobStore`. Corrected rather than left for whoever writes the deletion sweep to discover |
| 0.20 | New setting **`Photos:Quality`** (default 85), and `MaxUploadBytes` is checked against `Content-Length` **and** the read file (§16.4, §16.7) | Re-encoding is unconditional, so how hard it re-encodes an already-conformant photograph is a product decision and belongs in configuration. The header can lie, which is the reasoning §15.8's two caps already carry |
| 0.20 | **`Marker.PhotoId` is `ON DELETE SET NULL`**, not a cascade (§16.7) | Losing the photograph must not silently take the marker with it — "gravel across the whole corner" is worth knowing without the picture, and a cascade would delete a hazard warning because a blob sweep ran |
| 0.20 | **`AllowMemberPhotos` is enforced on the attach, not on the upload** (§5.8) | Settled in SRV-28. `POST /photos` is deliberately ride-less (§16.4), so it has no switch to consult; the check belongs where the image is bound to something in a ride |
| 0.20 | The three switches go through **one `RideContentPermissions.Allows`**, and its unrecognised-content arm **throws** rather than allowing (§5.8) | Four write paths carrying one obligation is how one of them stops discharging it — the lesson SRV-21's four delete routes already taught. A permissive default would let a new content type ship ungated by omission |
| 0.20 | `RideDetail` carries `Permissions`, sent to **every** member rather than only the organiser (§5.8) | Unlike the join code. A client that does not know a switch is off draws a compose surface that 403s when used, which reads as a broken app rather than a decision somebody made |
| 0.20 | A comment's **edit window is measured from `PostedUtc`, not `CreatedUtc`** (§17.2, §17.3) | Settled in SRV-29. Measuring from the authored time makes a post composed offline four hours ago arrive already un-editable, which is the opposite of what the window is for |
| 0.20 | `RideComment` gains a **`ClientGuid`** and a unique index on `(ride, author, clientGuid)` (§17.3, §17.9) | §17.3 already required an idempotent post; the schema block had nowhere to put the identifier. Checked *before* the throttle and the cap, so a flaky connection cannot spend a rider's own allowance re-sending |
| 0.20 | A comment's photo **cascades**, where a marker's `SetNull`s (§17.9) | The `CHECK (body OR photo)` is the difference: a marker keeps a required title and survives losing its picture; a photo-only comment has nothing left and would violate the constraint the moment the column was nulled |
| 0.20 | The thread cursor tiebreaks on **`Id` as well as `PostedUtc`** (§17.8) | Two comments genuinely share a receipt instant — the fake clock does not tick unless a test moves it, and a real one has finite resolution. A cursor keyed on time alone skips a post or serves it twice. Same trap as SRV-09's `issued_utc` |
| 0.20 | A poll is created through **`POST …/comments` with a `poll` field**, not an endpoint of its own (§17.5, §17.8) | Settled in SRV-30. §17.5 already said a poll is a comment; making the API agree is what actually delivers the inheritance — idempotency, caps, rate limit, content switches and the archived rule, none of them written twice |
| 0.20 | `ReactionsUpdated` carries **`Mine = null`** of necessity (§17.4) | A group message has one body and "mine" differs per connection. Written down so it is not later read as a bug |
| 0.20 | A vote request is **the full set the voter now holds**, for single- and multi-select alike (§17.5) | One endpoint shape for both, and an empty list becomes the only way to un-vote. The `(option, user)` key cannot express single-select — that is a rule *across* a poll — so the endpoint owns it |
| 0.20 | Coalescing **re-reads the tally**; it does not accumulate events (§17.4) | The distinction is invisible until two people react twice: replayed deltas would report reactions that had since been replaced. `Reaction_ManyInQuickSuccession…` now asserts the replaced keys are absent |
| 0.20 | `ContentReport.TargetId` is **deliberately not a foreign key** (§17.7) | Settled in SRV-31. The report must outlive the content — a foreign key would either cascade the evidence away when an organiser deletes an abusive comment, or refuse the deletion. Both defeat the point of the snapshot |
| 0.20 | Blocking hides a rider's **reactions and poll votes as well as their posts and markers** (§16.5, §17.7) | §17.7 said "reactions" and it is the half most likely to be skipped: a tally that still counted them is the one place their presence leaks through, and a poll whose names and numbers disagree reads as a bug |
| 0.20 | The coalesced `ReactionsUpdated` / `PollUpdated` messages apply **no block list** (§17.4, §17.7) | Same reason `Mine` is null — a group message has one body, and whose content a connection should not see is per connection. The client already holds its own list |
| 0.20 | §16.5's **"prevents future co-membership" is not built** and is now recorded as open (§16.5) | It is in no task's build list, needs a decision about direction, and a symmetric check would let one block keep a rider out of a fifty-person ride. Report-and-block — what review checks — is complete without it |
| 0.20 | **`Maintenance:DryRun` gates every sweep, not only the account deletion** (§7.11) | Settled in SRV-32. §7.11 describes it in terms of accounts because that is the sweep worth reading the output of, but a dry run that still deleted refresh tokens, positions and photo blobs is a dry run in name only. An operator who turns it on has said "show me, do not touch it" |
| 0.20 | New column **`asp_net_users.inactivity_warned_utc`**, cleared whenever the account is heard from (§7.11, §7.13) | The warning window is thirty days wide and the job is nightly, so "warn when idle ≥ 150 days" with nothing recorded emails the same person on thirty consecutive mornings. Clearing it on activity is what stops a rider who came back and went quiet again being deleted with no warning |
| 0.20 | New table **`deleted_account_token`** — a hash-only tombstone for the accounts the sweep deletes (§7.11, §7.13) | §7.11 requires the next refresh to fail with a *distinguishable* reason, and the cascade takes `refresh_token` with the account, so there is nothing left to recognise. Keyed on the hash rather than the account: only the device that actually held the token gets the specific answer, so it is not an oracle |
| 0.20 | The orphaned-blob sweep needs a **grace window** (`Maintenance:OrphanBlobGraceHours`, default 24) (§7.11, §16.6) | A blob is written before the row that points at it is committed, so for the width of one request every new upload is indistinguishable from an orphan. Without the window the sweep deletes photographs out from under the requests uploading them |
| 0.20 | **`IBlobStore` stamps what it writes from `TimeProvider`** (§9.1, §7.11) | Caught by `ClockRules` in SRV-32. The grace window compares a file's timestamp against a horizon, so the two have to be in the same frame — an ambient write time beside a `TimeProvider` horizon makes every blob look ancient, which is the window silently not existing |
| 0.20 | The sweep's set of blob-bearing columns is **declared once and resolved through the EF model** (§7.11, §16.6) | A blob column the sweep does not know about is not a missed tidy-up: every value in it looks unreferenced, so the next run deletes all of them. Resolving through the model makes a rename throw, and a model scan for anything blob-shaped is the test that catches an addition |
| 0.20 | `Moderation:ReportRetentionDays` defaults to **90**, and only **resolved** reports age out (§17.7) | Ageing out an open report turns a backlog into a silent amnesty, and the operator's queue is exactly the thing that gets behind |
| 0.20 | **`GET /me/export` returns a ZIP**, not a JSON body — `export.json` plus the tracks as GPX and the photographs as files (§6.3, §16.6) | Settled in SRV-33. §16.6 says the export includes markers *and their photos*; a response listing identifiers is not an export of anybody's photographs, and a track reduced to its distance is not an export of their ride |
| 0.20 | The export carries the sharing **switches** as well as the profile values (§7.3, §6.3) | What a rider chose to share is a decision about their own privacy. A file showing a phone number without saying who could see it answers a different question |
| 0.20 | The export **never carries a ride's join code** (§5.2, §6.3) | It is the ride's entire access control and goes only to the organiser. An export handed to a member that carried it would let any member re-share the curated group, through a file nobody thinks of as a sharing surface |
| 0.20 | **`DELETE /me` requires the current password**, in the body (§6.3) | The one irreversible action in the API. A fifteen-minute access token lifted off a shared machine should not be enough to end an account, and §7.2 makes a password universal so this excludes nobody. A query string would put it in Caddy's access log |
| 0.20 | Deleting an account **cascades the rides it organises**, and transfer of ownership is **not built** (§6.3, §10.2) | It follows from `group_ride.owner_id ON DELETE CASCADE`. Refusing the erasure instead is not defensible under applicable law, so the cascade stands and the warning is a UI obligation — recorded rather than discovered by an organiser |
| 0.20 | New column **`device.kind`** (`Mobile` \| `Web`), and it is what decides session length (§7.5, §7.13, §18.5) | Settled in SRV-34. §18.5 already framed a browser as a device like any other, so the one thing it does *not* inherit belongs on the device row. Read from the device on every rotation rather than carried on the token — a successor with the wrong lifetime would turn a thirty-day session permanent on the call a client makes at every start-up |
| 0.20 | `device.kind` is **server-decided, from the endpoint reached**, and a claimed device id must match on kind (§7.5) | A client-supplied value would let a browser ask for the permanent session the distinction exists to withhold — and a browser presenting a phone's device id would otherwise adopt that row and inherit its lifetime |
| 0.20 | The web refresh cookie is **`__Host-`-prefixed and `SameSite=Strict`**, with `Secure` following the request (§7.5) | The prefix makes the attributes unforgeable by a subdomain, which a plain cookie name leaves open. `Secure` cannot be hard-coded on: over the plain-HTTP loopback a test host and a local `dotnet run` use, the browser would discard it — §7.5's named failure arriving by a different door |
| 0.20 | Web responses **blank the refresh token in the body** (§7.5) | `HttpOnly` protects nothing if the same value sits in JSON the script just parsed, which is exactly where an XSS would read it |
| 0.20 | **Antiforgery covers the token endpoint only**; the web login and register form posts decline it explicitly (§7.5) | §7.5 scopes the CSRF cost to "exactly one endpoint". The other two carry credentials in the body, so there is nothing to forge without the password — and minimal APIs add the metadata automatically for `[FromForm]`, so declining it has to be deliberate |
| 0.20 | Browser sign-out **revokes the family server-side**, not only the cookie (§7.5, §18.5) | Clearing the cookie leaves a working token in whatever else kept a copy — which on the shared computer that made web sessions expire at all is the entire scenario |
| 0.20 | New setting **`RateLimits:MapTokenPerHourPerAddress`** (default 60) (§4.5, §7.8) | Authentication is the main gate, but a token lasts half an hour and is cached client-side: a real browser needs a handful a day, and anything asking far more often is minting them for somewhere else |
| 0.20 | An unconfigured or unreadable MapKit key answers **503 with a stated title**, never 500 (§4.5) | §4.5 requires a map that cannot get a token to show a stated error rather than a grey rectangle, and a client cannot draw the honest failure from a 500. The two cases answer alike because from a client's side they are the same situation |
| 0.20 | **`GET /healthz` judges the schema and the disk**, not only the process, and answers 503 when either is wrong (§9, §9.1) | Settled in SRV-35. A container whose schema is a migration behind answers every request correctly until one touches the column that is not there yet, and a container on a full disk answers HTTP right up until PostgreSQL cannot write. The free uptime pinger already watching this URL therefore *is* §9's disk alert |
| 0.20 | Migrations are applied by a **one-shot `--migrate` run of the same image**, never on the server's way up (§9) | Migrating at startup couples "is this server ready" to "has the schema moved": a failed migration becomes a crash loop and a second container becomes a race. As its own compose step it either succeeds or stops the deploy |
| 0.20 | The Caddy access log **filters `access_token` out of the query string** (§7.6, §9) | §7.6 lifts the SignalR token into a query string because a browser cannot set an `Authorization` header on a websocket — so the default log writes live credentials into a file that rolls for weeks. Choosing a JSON format does not fix it; every format logs the URI intact |
| 0.20 | The nightly run **emails its summary** (`Maintenance:AlertEmail`) (§7.11, §9) | §9 asks for an alert on the nightly run, and the dry-run log is only read by somebody who goes looking. Counts plus candidate usernames, which is what makes a week of dry runs something an operator does rather than intends to |
| 0.20 | The `pg_dump` and the blob volume go into **one restic snapshot** (§9.1) | A database restored against blobs from another night gives tracks pointing at files that are not there. The retention numbers are also what §15.6's privacy copy refers to, so changing them changes a privacy statement |
| **0.21** | **Per-platform base map, one shared Skia overlay.** iOS uses Apple Maps (MapKit JS), Android uses Google Maps (JavaScript API), the web uses OpenLayers on OSM. Every rider pin, marker and track is drawn by one C# `SkiaMapOverlay` component on top of whichever base map is running (§4.5, §18.3) | Withdraws v0.19's "MapKit JS on both phones" and v0.16's "one map everywhere" together. The shape v0.13 warned about — two providers drifting on marker rendering — is what this closes: the base map is the vendor's problem, and the half we own is one file drawing the same pixels on every surface. Answer recorded in `Documentation/AppleMapKitAndroidQuestion.md` |
| 0.21 | **Google Maps API key added to §14.2's never-commit list.** Browser-type key, referrer-restricted to the deployment's bundle id and origin (§4.5, §14.2) | Same class of secret as the MapKit `.p8` — a leak billed to us, and public-repo scanners find these within hours. The AGPL §7 additional permission (§14.6.5) already covers linking against proprietary platform SDKs, so the licensing question is settled; the operational question is where the key lives |
| 0.21 | `IMapRenderer` and `MapCapabilities` **now describe the car heads only** (§4.5, §4.6, §18.3) | The phone and the web speak `IMapInterop` for the base map and `IMapOverlay` for authored content; the car still speaks `IMapRenderer` because Skia-into-a-Surface is one renderer, not a stack. The startup rule that every `MapHostKind` has a factory still holds |
| **0.22** | **Password policy revised at operator request** (§7.2). Minimum 6 characters, require one uppercase, one lowercase and one digit; no non-alphanumeric requirement. Every rule the sign-up form is measured against surfaces as a specific server-side message the client renders verbatim | Deliberately reverses §7.2's "length over composition" stance. §7.2's original argument still applies as context — composition rules push people toward `Passw0rd!` — but the operator's trade-off is that a signing-up user who cannot get past the form is worse than a predictable-shape password. The Pwned Passwords check remains in place and catches the shape §7.2 was worried about |
| 0.22 | **Client renders `ProblemDetails` bodies verbatim** (§18.2). `HttpApiClient` throws `ApiException` carrying the parsed body; screens list the server's messages one line at a time | `EnsureSuccessStatusCode` was discarding the reasons the server put in the response. A user who types a too-short password now sees "Passwords must be at least 6 characters" from Identity itself, not "The details you entered were rejected". Same shape as §18.2's rule that a shared component crosses the wire with the DTOs from `DLR.Core.Contracts` unchanged — the error DTO gets the same treatment |
| **0.23** | **The breached-password check is removed** (§7.2). `IBreachedPasswordCheck`, `BreachedPasswordValidator` and `PwnedPasswordsClient` are deleted, along with the fake on the test factory; v0.22's composition rules are now the whole password policy | Operator decision: the security impact of a weak password on this application is judged not to be significant, and the check was the registration path's only third-party call. What goes with it is the argument v0.22 leaned on — nothing now stops `Passw0rd!` — and §7.2's outage clause, which no longer has anything to be unavailable |
| 0.23 | **Welcome grows a reveal button on both password fields, and a strength meter on Register** (§7.2, §18.2). One `PasswordField` component; the meter reads `PasswordStrength`, four segments and a word, naming the rules a password still breaks | Partly the removal above — with no corpus lookup left, the sign-up form is the only place a rider is told anything about their password. Partly the field itself: a masked box on a phone keyboard, for an account that may have no email and therefore no reset path, is how somebody is locked out on their first ride. The meter advises and never gates — the server decides what it accepts, and a client that blocks on a rule the server does not have is a rider who cannot register |
| **0.24** | **One base map everywhere: MapLibre GL JS over OpenStreetMap.** `map.mapkit.js`, `map.googlemaps.js` and `map.openlayers.js` are deleted along with `AppleMapsInterop`, `GoogleMapsInterop` and `OpenLayersInterop`; one `MapLibreInterop` in `BlazorDLR.Shared` is registered identically by all three hosts (§4.5, §18.3) | Withdraws v0.21's per-platform split and v0.19's Apple-Maps-on-both-phones, and restores v0.16's "one map everywhere" on a renderer that needs no credential. v0.21 had already moved every marker onto the Skia overlay, which left the three base maps drawing nothing but tiles — three vendor relationships, two secrets and a server dependency, for the one job all three did identically |
| 0.24 | **`GET /api/v1/maps/token` is deleted**, with `MapKitOptions`, `MapKitSigningKey`, the `MapToken` contract and `RateLimits:MapTokenPerHourPerAddress` (§4.5, §7.8) | The map stops being a server dependency, which is what v0.19 introduced and called "the part most likely to be discovered late". It was: a deployment with no key showed *"Map unavailable — Map credentials unavailable"* on the phone, and the diagnosis ran through a token endpoint, an origin claim and a `.p8` before reaching the map |
| 0.24 | **Both map credentials leave §14.2's never-commit list** — the MapKit `.p8` and the Google Maps browser API key (§4.5, §14.2) | Not a relaxation. The rows go because the secrets no longer exist: MapLibre needs no key on the server and none in the app bundle, so there is nothing left to leak or to restrict at a provider. The strongest form of §14.2 is a shorter list |
| 0.24 | **Offline maps are possible again** — v0.19's "gone, not deferred" is withdrawn (§4.5, §13 Q26) | MapKit JS had no offline mode and no tile cache to point at a local file, and for an app whose premise is a trailhead with no signal that was the sharpest thing the decision cost. It is now a tile-source question rather than an SDK limitation, and §13 Q26's PMTiles work is the same work |
| 0.24 | **The Skia overlay moves off `SkiaSharp.Views.Blazor`** — it rasterises off-screen and hands a PNG frame to an `<img>` through `map/overlay.js`, tracking the map with a CSS transform between repaints (§4.5, §16.3) | `SKCanvasView` initialises through `[JSImport]`, which is WebAssembly-only, so on a MAUI `BlazorWebView` it threw on first render — and an unhandled throw in `OnAfterRenderAsync` takes down the Blazor renderer, not just the component. On device that read as a base map that still panned while every button in the app stopped responding. **The overlay had never rendered on a phone**: before v0.24 the map failed first (no MapKit token on iOS, no Google key on Android), so it was never mounted, and §4.5's claim that one C# file drew every pixel on every surface had only ever been true on the web |
| 0.24 | **§13 Q26 is now the only outstanding map decision, and it is on the critical path to launch** (§4.5, §9.1, §13) | Unchanged in substance — OSM's donated tiles never covered a public announcement — but it is no longer competing with three vendor integrations for attention, and the renderer it lands on is already the one shipping |
| **0.25** | **`MapViewport`'s extent is documented as axis-aligned, and `ProjectToCanvas` now divides that out before it rotates** (§4.5) | `map.getBounds()` encloses a turned view rather than tracing it, so the reported box is bigger than the canvas — by `W·|cos θ| + H·|sin θ|` across and `W·|sin θ| + H·|cos θ|` down. Dividing that span by the canvas side gave the two axes *different* scale factors, which is not a projection: the centre pixel stayed correct and everything else was displaced in proportion to its distance from it, coming right again at 180° where the box is the canvas. It also mis-sized the private-area circle and mis-aimed every tap hit test on a turned map, both of which go through the same projection by design |
| 0.25 | **Rotation is allowed and given a compass; pitch is refused everywhere; both are off on the screens where a tap places a point** (§4.5, §10.1, §16.1) | Turning the map the way you are pointing is what a rider following a route wants. Tilting it is not available to give: the overlay projects flat Web Mercator from a viewport with no pitch term, so a tilted base map would draw every pin for a view nobody is looking at — and unlike the bearing there is no term in the contract to correct it with. `AllowRotation` is off on the private-area picker and the marker composer because on those the map is a coordinate entry field, and a rider tapping a turned map is reasoning about a north-up image that is no longer on screen |
| 0.25 | **The overlay frame moves from `<img>` + `data:` URL to `<canvas>` + `createImageBitmap`** (§4.5) | Assigning a new `src` abandons the decode already in flight. A laptop finishes a decode before the next viewport event arrives so the race never opens; a phone does not. The symptom matched the race exactly — nothing on the live map, which repaints on every broadcast, and "sometimes" on the track editor, which repaints only on input |
| 0.25 | **The overlay's drawing lengths scale by `DevicePixelRatio`** (§4.5, §16.3) | Every width, radius and glyph size was authored in CSS pixels and drawn onto a canvas `devicePixelRatio` times larger, so a 3× phone rendered the whole design at a third of its weight. This is also the entirety of the "home circle does not appear on mobile" report — a thin ring over a 20 %-alpha wash, at a third of a pixel |
| 0.25 | **The base map publishes its own projection to `overlay.js`; the overlay no longer follows the map through C#** (§4.5) | Following from C# means the viewport event crosses the bridge, a transform is computed and crosses back — arriving after the frame the finger caused was already painted. The transform is now computed inside MapLibre's own `move` handler, on the same frame as the movement. Nothing about the drawing got faster; the lag was never drawing |
| 0.25 | **`MapGeometryTests` builds the bounds a base map would report for a bearing, and asserts on an oblong canvas** (§4.5, §10.4) | The rotation tests existed and asserted the right invariants, but modelled a turn as `viewport with { HeadingDeg = 37 }` — bounds held still, which is a view that cannot exist — on a 1000 × 1000 fixture where the two wrong scale factors are equal anyway. A test that cannot fail is worse than no test, because it is counted |
| **0.26** | **The `Live`-ride notification silence is removed** (§17.1, §17.6). Ordinary comments and polls now push in every ride state; the `Live` row of §17.6's table reads `Push` across, the thread's *"arrives silently"* note is gone, and `RideThreadMoreTests` now asserts its **absence** | Deliberately reverses v0.14's rule, in the same way v0.22 reversed §7.2's password stance. v0.14's argument — that this app notifies people operating vehicles — is not withdrawn and is still written down in §17.1; what changed is who answers it. The silence was the app deciding for the whole group, and the cost was a ride where the message that actually mattered went unseen because nobody had thought to pin it. Muting is now the rider's own call, made in the operating system — Do Not Disturb, riding and driving focus modes, per-app and per-channel switches — which are the controls the phone already applies to every other app on it |
| 0.26 | **The per-ride mute toggle is cut, not built** (§17.6). Silencing is entirely the operating system's job — Do Not Disturb, riding/driving focus modes, per-app and per-channel switches | Operator decision. An in-app mute would be a second, worse copy of a control every phone already has: it covers one app, hides in a settings screen nobody visits mid-ride, and cannot know the rider is currently moving. The platform's version wins on every axis, so the app ships none and `INotificationService` carries no mute concept at all |
| 0.26 | **Notifications are delivered locally — the app registers with no push service** (§17.6, §18.2). `UNUserNotificationCenter` on iOS, `NotificationManagerCompat` on Android. No APNs key, no `aps-environment` entitlement, no FCM sender key, no `google-services.json`, and no device-token table on the server | The message has *already arrived*: every ride screen holds a SignalR connection (§5.3), and the receiver's foreground service and iOS's `location` background mode keep it alive through a ride (§4.3). A push service would have been a second, slower, credentialed path for something already in memory — and it was the last thing on the §18.2 checklist that could not be closed without store-side credentials. The trade is that nothing is raised while the OS has the app suspended, which is outside a ride |
| 0.26 | `INotificationService` is **rewritten from a push-token registry to a local-notification seam** — `EnsurePermissionAsync` / `ShowAsync` / `CancelAsync` over a `LocalNotification` record, with `CommentNotifier` holding the decision and `NotificationRouting` carrying a tapped notification's route back into Blazor (§17.6) | `RegisterAsync(deviceToken)` / `UnregisterAsync` described a relationship with a push service that no longer exists. The decision half is deliberately in `BlazorDLR.Shared` where `DLR.UI.Tests` can reach it — `CommentNotifierTests` covers the suppression rules (two then, one since v0.27), the summary text and the tag collapsing, none of which should need a device to prove |
| 0.26 | **Android notification importance is `Default`, not `High`** (§17.1, §17.6) | The last of §17.1 that survives as code. `Default` makes a sound and puts a card in the shade; `High` adds a heads-up banner that slides over whatever is on screen — which during a ride is the live map the rider is navigating by. Telling somebody there is a message is the point; covering their map with it is what §17.1 was written about |
| **0.27** | **The live map's *"quiet while live"* label is deleted** (§17.6). It sat beside *Adventure thread* in `GroupRideLive`'s menu and had outlived the rule it described by a whole release, and `GroupRideLiveTests` now asserts no menu copy promises quiet | A stale label is worse than the rule it survived: the app was buzzing riders mid-ride while its own menu told them it would not, so the one visible statement of the behaviour was the one thing that was wrong about it. `RideThreadMoreTests` already guarded the same claim on the thread page — the live map's copy simply had no test, which is exactly why it survived v0.26 |
| **0.27** | **The remaining app-side suppression goes: a post notifies even while the rider is reading that thread** (§17.1, §17.6). `ShouldNotify` is down to *"is it the rider's own post"*, and `AppForegroundState` — with the MAUI window's `Resumed`/`Stopped` wiring — is deleted | Operator decision, and the same reasoning as v0.26 carried to its end: if a notification is to be held back, the rider holds it back, in the operating system. The open-thread rule also needed a second piece of state to stay honest — a page stays mounted after the phone is locked, so without the foreground flag it silenced the rest of the ride — and the cost of removing both is one redundant banner in the one case where the rider is demonstrably already looking at the phone. Opening a thread still withdraws the card in the shade, which is housekeeping rather than silence |
| **0.28** | **The home private area moves from the device to the account** (§10.1, §7.13, §7.14). Three nullable columns on `asp_net_users`, a sub-resource of `/me` — `GET`/`PUT`/`DELETE /api/v1/me/private-area` — and `IDeviceSettings` demoted from the store to a cache of it | Operator decision, and a deliberate reversal of the rule v0.13 wrote down. The device-only argument was that a centre is a precise statement of where somebody lives, so the only copy that cannot leak is the one the server never has. What it did not weigh is how the setting is actually lost: an app update, a reinstall, a cleared browser store or a new handset each wiped it **in silence**, and a rider who believes they have a circle round their house and does not is broadcasting from their doorstep. Losing it fails open; storing it fails closed. The privacy claim is narrowed rather than dropped — it is now *"no other rider can see it"*, which is enforced structurally, instead of *"nobody but this phone has it"*, which was true and kept costing people the feature |
| 0.28 | **It is a sub-resource, not three fields on `PUT /me/profile`** (§7.14) | That endpoint replaces the whole profile — the Profile screen already round-trips values it does not render for exactly this reason — so an area carried inside it would be erased by any client that had not been taught about it. A privacy control must not be deletable as a side effect of saving a display name |
| 0.28 | **The device cache becomes tri-state: a circle, an explicit "none", or "not told yet"** (§10.1) | The gate suppresses until it has an answer, so the two nulls cannot be the same value. Removing the key on *clear* — which is what the device-only design did — would leave a rider who deliberately removed their area indistinguishable from a fresh install, and therefore silently unable to share the next time they opened the app offline |
| 0.28 | **The private area gates publishing only — the rider's own screen keeps working inside it** (§10.1, §4.3). `LocationBroadcastState.OwnFix` no longer answers null while suppressed, and the `LastFix`/`OwnFix` pair is collapsed into one property | Reported from a ride: inside their own circle a rider's mark stopped moving, follow-me had nothing to follow and heading-up stopped turning — the map went dead in the driveway and came back at the edge of the area. The rule was written as *"nothing is recorded and nothing is broadcast"* and the implementation over-read it, blanking the one screen there was nobody to hide the house from: the person reading it is standing in it. The pair of properties differed only in this case, which is exactly the case it got wrong, so keeping both would invite the distinction being reinstated as a fix |
| 0.28 | **The area is in the account export and named as personal data at rest** (§6.3, §10.1, §9) | It is the rider's own data and the export claims completeness. The rest of the cost is stated where the rider reads it rather than only here: the Location screen now says the point and radius are stored on the server and are in the backups, in the same paragraph that says no other traveller can see them |
| **0.27** | **Android importance `Default` → `High`, lock-screen visibility `Private` → `Public`, on a new channel id `dlr.thread.v2`** (§17.6) | Reverses the v0.26 row above by operator decision. The new id is not cosmetic: Android fixes importance at channel *creation* and ignores later changes — correctly, so an app cannot turn its own volume back up — so on every phone that already had the app, raising the constant alone would have changed nothing. `Public` likewise stops the app overriding, for itself and in one direction only, Android's system-wide setting for sensitive lock-screen content. The retired channel is deleted on the first post after upgrade so the settings screen does not show two *"Adventure posts"* rows |
| **0.30** | **A shared route gets a star rating and a thread of its own** (§19, §17). One to five whole stars, one rating per rider per route, replaced rather than accumulated; the average and the count ride on every browse row. Anyone signed in who can see the route can rate it and post to it | Required capability. A catalogue of other people's roads is only useful if it carries other people's verdicts — a browse list sorted by *when it was uploaded*, with a description written by the one person who cannot be objective about it, is a list you have to ride to evaluate |
| **0.30** | **§17.1's *"group rides only in v1"* rule is reversed** (§17.1, §19.3). `RideComment.GroupRideId` becomes nullable, `TrackId` joins it, and a `CHECK ((group_ride_id IS NULL) <> (track_id IS NULL))` makes *exactly one subject* a property of the table (§17.9) | v0.14 refused this on the grounds that a thread on a public track lets people the organiser never admitted post into someone's space, discarding §5.2's consent model. That argument was about **an adventure's** abuse model, and it applied it to a surface that has none to discard: a route on the browse list was **deliberately** put in front of every rider on the service, and organiser consent was never what protected it. What actually protects it is the same machinery §17.7 already built for the harder case — reporting, blocking, the owner as moderator, the §7.8 ladder on posting. v0.14 also predicted the shape of the change correctly: *"adding a `TrackId` parent later is one migration"*, and it was |
| 0.30 | **One comment table, one controller, and a `ThreadAccess` resolver holds the only difference** (§17.8, §19.3). Every endpoint from *edit* downwards — edit, delete, pin, react, vote, close a poll, report — is the code that was already there, unchanged | The two threads differ in exactly one place: who is allowed in and who runs it. A second table would have been a second copy of the reactions, the polls, the edit window, the pinning cap, the block filter and the cursor — and the copy that drifted would have been whichever had fewer tests |
| 0.30 | **A second unique index, `ux_ride_comment_track_client`, carries idempotency for route posts** (§17.9) | Not tidiness — the existing index leads on `group_ride_id`, which is **null** for every route comment, and PostgreSQL treats nulls as distinct in a unique index. Widening the old one would have looked complete and let every drain of an outbox through. Each thread kind gets an index whose leading column is never null for the rows it has to judge |
| 0.30 | **Un-sharing a route hides its thread and keeps the posts; deleting the route cascades both** (§19.2, §19.3) | Un-sharing is reversible and is the owner's own call about their own row; destroying other people's writing over it would be the app punishing a rider for changing their mind. Re-sharing brings the conversation back rather than starting a new one |
| 0.30 | **A rating is anonymous and is therefore *not* filtered by the block list** — the one place §17.7's rule deliberately does not reach (§19.1) | Blocking hides what somebody **wrote**; nothing anywhere records who gave a route three stars. Filtering it would make one rider's average differ from another's for a number they are both being asked to trust — and the difference itself would leak that a blocked rider had rated this route |
| 0.30 | **Clearing a rating deletes the row. There is no zero on the scale** (§19.1) | A stored nought averages in as the worst possible verdict for every rider who tapped a star and thought better of it, which is the opposite of what they meant. Same rule §8 already applies to distance and ascent, applied to a score |
| 0.30 | **Polls are off on a route's thread — an editorial choice in the UI, not a server rule** (§19.2) | A poll is a group deciding something together; a route's thread is riders telling each other what the road is like. The server would accept one either way, and the switch is one component parameter, so this is a default rather than a prohibition |
| 0.30 | **`RideThread.razor` is now a page of chrome over `CommentThreadView`**, and `ReactionBroadcastService` coalesces onto a hub **group name** rather than a ride id (§17.8, §18.2) | The route page had to render exactly the thread the adventure page renders — same reactions, same pinning, same optimistic insert, same block filter — and the only way to guarantee "exactly" is for there to be one of them. The broadcast service holding a group name instead of an id is the same move on the server: the group is decided once, where the change happened, rather than re-derived at flush time in a second file |
| **0.31** | **A rider inside their own private area *disappears* from other riders' maps instead of freezing on them** (§10.1, §5.6). Entering the circle publishes one bit — `POST /api/v1/positions/privacy`, or `PublishPrivacy` on the hub — which **deletes** that rider's stored position in every adventure they share with and fans out `MemberPrivacyChanged`. `RideMemberSummary.Private` carries it in the snapshot, and `MemberPresence` gains a fourth state | §10.1 already claimed *"co-riders see the rider as present in the ride with no position on the map"* and the code did not do it: publishing simply stopped, so the last fix before the driveway stayed in the database and on every other map for the rest of the adventure. A marker parked a few streets from somebody's front door, not moving, is a **better** clue to where they live than most of what the feature withholds — the suppression was creating the exposure it existed to prevent. The correction is the same discipline as v0.2 and v0.15: make the code do what the privacy statement says, rather than soften the statement |
| 0.31 | **The four route figures — range, along, gap, off-route — are not rendered at all for a private rider** (§5.4, §5.6) | Every one of them is derived from a position the adventure no longer holds, so the honest render is four em dashes, and four em dashes read as an app that has lost somebody. The chip beside their name has already given the whole answer, and a row that answers a question and then asks it again four times is worse than one that stops |
| 0.31 | **`MemberPresence` gains `Private` rather than reusing `NoSignal`** (§5.6) | The same rule that split *not sharing* from *no signal* in the first place, applied to a third reason a pin can be missing. A group waits at the junction for *no signal*; it does not wait for somebody who is still in their kitchen, and it does not give up on them either |
| 0.31 | **Nothing changes on the private rider's own screen** (§10.1, §4.3) — their own mark, their own row and all four of its figures are drawn from this device's receiver | The v0.28 correction, carried to the member list. The circle governs what leaves the phone; every figure on the reader's own row is worked out on the phone, for the person holding it, and there is nobody on that screen to hide the house from |
| 0.31 | **Who is private is held in memory only — never a column** (§10.1, §5.5). `RiderPrivacyCache` sits beside `RiderPositionCache`, with the same lifetime and for the same reason | It is live presence, and a durable record of when each account was at home would be a weaker copy of the very thing the feature withholds. A restart forgets, and the device says it again — the client re-states *private* on a slow timer, and any published coordinate clears it server-side, because a coordinate arriving is proof the rider is outside their own circle |
| **0.29** | **The join code is shown to every member, not only the organiser** (§5.2). `RideDetail.JoinCode` and `RideSummary.JoinCode` carry it for anybody in the ride, so *Group adventures* shows the badge on a joined adventure exactly as it does on an organised one | Reported from use: once you have joined, the code is nowhere on any screen, so a rider who wants to bring a friend along has to go back to the organiser for a value they already used. The v0.20 argument was that the code is the ride's whole access control and a member's copy would let them re-share the curated group — but somebody already admitted can name the ride to anybody regardless, so what the rule actually withheld was the ability to invite, not the ability to leak. On an `Approval` ride the organiser still decides every admission and nothing changes; on an `Open` one the trade is deliberate, and the member cap, the member list and *remove member* are what bound it. **The account export still never carries it** (§6.3) — a file nobody thinks of as a sharing surface is a different question from a badge on a screen |
| **0.32** | **The adventure lifecycle is deleted.** No `Draft`/`Open`/`Live`/`Completed`/`Archived`/`Cancelled`, no *Start*, no *End*, no wind-down (§5.1, §5.6). An adventure is live from the moment it is created until somebody deletes it, and the **only** control over live position sharing is each rider's own per-adventure switch | Five of the six states were never assigned by any code path, and the two that were bought one thing — a moment at which the server deleted everybody's positions — at the cost of an enum on the wire, two columns, two endpoints, a background service, three configuration options, two hub messages and a state test in front of nearly every guard in the product. The organiser pressing *Start* was a step between joining an adventure and using it that answered no question a rider had asked |
| 0.32 | **`RideStateDto` comes off the wire outright** (§5.3) | No build is in users' hands, so there is no compatibility window to keep and no reason to ship a field hard-coded to one value. Worth recording because it will not be true next time: a shipped client deserialising a response with no `state` gets `default(RideStateDto)`, which is `Draft` — it would have decided the adventure had not started, hidden the sharing panel and refused to bring the GPS up. A silently wrong default rather than a failure |
| 0.32 | **A fourteen-day idle sweep replaces the guaranteed death of a `rider_position` row** (§5.6, §7.11). The nightly job deletes any position with no fix for `Ride:PositionIdleDays` and clears that member's `ShareLocation` | *End* was the only thing that reclaimed a row unconditionally, and removing it without a replacement means a rider who taps *Share*, rides home and forgets the switch broadcasts for ever — exactly the always-on tracking of friends §1 promises this app is not. It is a **backstop, not a privacy promise**: the phone that died, the app uninstalled, the adventure nobody deletes. A rider still sending fixes is still sharing, and every user-facing sentence now says so |
| 0.32 | **§1 and §10.1's headline claim corrected again**: sharing ends when *you* turn it off, leave, or are removed — not when the adventure ends (§1, §10.1) | There is no longer an end for it to happen at, and v0.15's own rule applies: a privacy statement that describes an earlier version of the code is worse than none. The fourteen days belongs in the retention table as an outer bound, and nowhere else |
| 0.32 | **Deleting an adventure is the way to finish one**, and is no longer refused while it is running (§5.1, §5.6) | The refusal existed because *End* was the gentler verb for that moment and this is the destructive one. With no *End* the refusal is a lock on a door with no other way out |
| 0.32 | **The consent prompt shows once per adventure per device**, remembered in `IDeviceSettings` (§5.6, §18.6) | `Open`-ness was quietly doing half this job — an adventure that had not started was the only one that asked. Deleting the state test without writing the fact down either shows the prompt on every load of an adventure somebody has deliberately declined, or stops asking anybody. Neither is acceptable for a consent gate |
| 0.32 | **`Ride:MaxConcurrentLiveRidesPerUser` deleted** (§5.7) | It was enforced only at the start transition, so it had no enforcement point left. §5.7 already anticipated this: *"if that turns out to matter, the place to fix it is the publish fan-out, not the start transition"* |
| 0.32 | **Profile sharing lasts as long as co-membership** (§7.3, §10.1) | It ended at `Completed`, and there is no `Completed`. Leaving, being removed and deletion all still end it, which is the rule that was doing the work anyway |
| 0.32 | **§17.6's thirty-day read-only thread is gone with the `Archived` state that carried it** (§17.6, §19) | The state was never assigned by anything, so the thread has never actually gone read-only — the guards existed, the sweep did not. Removing the enum makes that visible rather than causing it. If read-only-after-N-days is still wanted it comes back as its own `ArchivedUtc` column and its own sweep, not as a side effect |

---

## 1. Product Summary

A group-ride companion for motorcyclists / cyclists / drivers:

- **Record** GPS tracks on-device, even with no signal — the rider turns *save track* on and off.
- **Import** existing GPX files from either the app or the website, so a track can come from another app, a planning tool, or a mate (§15).
- **Edit** a track on the website — trim points off the start or end, or remove a span in the middle (§15.5).
- **Mark up the map** — drop a marker with an icon, a title, a note, an optional facing direction and an optional photo, on a recorded track or live in a group ride (§16).
- **Talk about the ride** — one thread per group ride with text, photos, pinned posts, reactions and polls, which stays quiet while people are actually riding (§17).
- **Share** completed tracks with other users (and export GPX).
- **Create group rides** — a time-boxed container that a set of users join. Inside a group ride, members see **each other's live location on a map** and a **shared planned route (GPS track)**.
- Live sharing is **asked for when you join, off unless you say yes, and revocable at any moment**. It stops when you turn it off, leave the adventure, or the organiser removes you — nobody else can turn it on for you (§5.6). No history is kept at any point. No always-on tracking of friends.
- **You can be in several rides at once**, with a separate sharing decision for each (§5.7).

### Core entities in one sentence
A **User** records **or imports** **Tracks**. A **GroupRide** has a planned **Route** (a Track), a set of **Members**, and a live window during which members publish **Positions**.

---

## 2. System Architecture

```
   ONE MAUI PROJECT (Android + iOS)          BROWSER
┌───────────────────────────────────┐   ┌──────────────────┐
│  DLR.App — MAUI Blazor Hybrid     │   │ DLR.Web.Client   │
│  ┌─────────────────────────────┐  │   │ Blazor WASM      │
│  │  BlazorWebView              │  │   │ ┌──────────────┐ │
│  │  ┌───────────────────────┐  │  │   │ │  DLR.UI      │ │ ← the SAME
│  │  │  DLR.UI (Razor RCL)   │◄─┼──┼───┼─┤  (Razor RCL) │ │   components
│  │  │  + MapLibre + OSM     │  │  │   │ │  + MapLibre  │ │   (§18)
│  │  │                       │  │  │   │ │    + OSM     │ │   (§4.5)
│  │  └───────────────────────┘  │  │   │ └──────────────┘ │
│  └─────────────────────────────┘  │   └────────┬─────────┘
│  DLR.Core — domain/sync/SQLite    │            │
│  FG Service / CoreLocation        │ ← GPS      │
│  Keystore / Keychain              │ ← token    │  HttpOnly
│  Android Auto / CarPlay (native,  │            │  cookie
│    Mapsui → raw Surface, §4.6)    │            │  (§18.5)
└────────┬──────────────────────────┘            │
         │ HTTPS (REST) + WSS (SignalR)          │
         └──────────────────┬────────────────────┘
                            ▼
        ┌────────────────────────────────────────────┐
        │          DLR.Server (ASP.NET Core)         │
        │  Minimal APIs   │  SignalR RideHub         │
        │  Static-SSR public pages + WASM host       │
        │  Identity + JWT (§7)                       │
        │  RiderPositionCache  (in-memory, dirty-    │
        │    tracked, write-behind every 10 s)       │
        │  NightlyMaintenanceService (§7.11)         │
        └───────────────┬────────────────────────────┘
                        ▼
        ┌────────────────────────────────────────────┐
        │  PostgreSQL                                │
        │   • domain tables (EF Core)                │
        │   • asp_net_* — username-based user store  │
        │   • refresh_token — rotating, revocable    │
        │   • rider_position — last known only,      │
        │     one row per (ride, rider), no history  │
        │  Blob volume — track blobs + photos (§9.1) │
        └────────────────────────────────────────────┘
```

**One UI codebase, three surfaces.** `DLR.UI` is a Razor Class Library compiled into both hosts, so a screen is written once and appears on Android, iOS and the web (§18). The car heads are the deliberate exception: they are native template code, because Android Auto and CarPlay have no browser (§4.6).

**One deployable server, one datastore.** The WASM client, REST API, and the realtime hub are served from the *same* ASP.NET Core process, backed by PostgreSQL alone. This is the biggest cost lever — no separate API tier, no managed SignalR service, no Redis, no serverless per-invocation billing on a chatty websocket workload.

---

## 3. Solution / Project Layout

The repository root is not the solution root. The root carries what is shared by everything in the
tree; `Web/` holds the server and web solution.

```
<repository root>
├─ .editorconfig             tabs (§10.5)
├─ Directory.Build.props     shared TFM/analyzer/style settings
├─ Directory.Build.targets   dirty-tree marker + build timestamp (§14.6.2)
├─ global.json               SDK pin
├─ .config/dotnet-tools.json nuget-license, dotnet-ef
├─ build/licences/           the licence gate, as data (§14.6.3)
├─ Documentation/
└─ Web/                      the server and the web client

Web/DLR.sln
├─ src/
│  ├─ DLR.Core/              net10.0 — no platform deps, no MAUI reference
│  │   ├─ Domain/            Ride, GroupRide, TrackPoint, Member, Marker, enums
│  │   ├─ Contracts/         DTOs + SignalR hub interfaces (shared w/ server)
│  │   ├─ Tracks/            GPX/encoded-polyline codecs, simplification, stats,
│  │   │                     TrackEditor (range removal) — one impl, app + server (§15)
│  │   ├─ Maps/              IMapRenderer + platform-free map POCOs — car only (§4.5)
│  │   └─ Abstractions/      ILocationProvider, IRideRepository, IApiClient,
│  │                         ITokenStore
│  ├─ DLR.UI/                ★ Razor Class Library — THE shared UI (§18)
│  │   ├─ Pages/             every screen in §4.1, written once
│  │   ├─ Components/        RideMap (one component, two JS modules §4.5),
│  │   │                     Thread, MarkerEditor,
│  │   │                     TrackEditor, PollCard …
│  │   ├─ ViewModels/        presentation-neutral, testable without a renderer
│  │   └─ wwwroot/           maplibre-gl.js, component CSS, icon sprites (§16.2)
│  │                         — no MAUI reference, enforced by test (§10.4)
│  ├─ DLR.App/               MAUI single project (Android + iOS) — Blazor Hybrid
│  │   ├─ MainPage.xaml      one BlazorWebView hosting DLR.UI. Effectively all
│  │   │                     the XAML in the project
│  │   ├─ Auth/              SecureTokenStore, AuthHandler, single-flight refresh
│  │   ├─ Maps/              MapsuiMapRenderer — CAR SURFACES ONLY (§4.6)
│  │   └─ Platforms/
│  │       ├─ Android/       ILocationProvider, foreground service
│  │       │   └─ Auto/      CarAppService, Session, Screens (§4.6) — native
│  │       └─ iOS/           ILocationProvider, CoreLocation
│  │           └─ CarPlay/   CPTemplateApplicationSceneDelegate, templates
│  ├─ DLR.Web.Client/        ★ Blazor WASM host for DLR.UI (§18.4)
│  ├─ DLR.Server/            ASP.NET Core: APIs + Hub + static-SSR shell
│  │   ├─ Api/ Hubs/ Components/   REST endpoints incl. /api/v1/about (§14.6.2);
│  │   │                     public pages, statically rendered (§18.4)
│  │   ├─ Identity/          token endpoint, RefreshTokenService, email sender,
│  │   │                     registration throttle, rate-limit policies (§7)
│  │   ├─ Maintenance/       NightlyMaintenanceService (§7.11, §15.6)
│  │   ├─ Tracks/            GpxImportEndpoint, TrackEditService, BlobStore (§15)
│  │   ├─ Photos/            the ONLY image decode path — re-encode + strip (§16.4)
│  │   └─ Positions/         RiderPositionCache, PositionFlushService,
│  │                         PositionCacheRehydrator, PositionWriter (§5.5)
│  └─ DLR.Server.Migrations/ DlrDbContext, entity configurations, EF Core migrations.
│                            Persistence lives with the migrations that describe it —
│                            as two projects it is a reference cycle (v0.20)
└─ tests/
   ├─ DLR.Core.Tests/        codecs, simplification, sync state machine
   ├─ DLR.UI.Tests/          bUnit — components render once, tested once (§18.7)
   ├─ DLR.Server.Tests/      WebApplicationFactory + Testcontainers Postgres
   ├─ DLR.Architecture.Tests/ layering + map-isolation rules (§10.4)
   └─ DLR.TestSupport/        GPX replay harness, FakeTimeProvider, fake email sink
```

`DLR.Core.Contracts` is referenced by **every host and the server** — the MAUI app, the WASM client and `DLR.Server` all compile against one set of DTOs and one hub interface, so a breaking change is a build error rather than a runtime surprise. Since v0.16 that guarantee covers the website too, which it did not when the web tier rendered server-side.

---

## 4. Mobile Apps (.NET MAUI)

### 4.1 Screens

*Every screen below is a Razor component in `DLR.UI`, written once and rendered by both the MAUI app and the website (§18). The car screens in §4.6 are the exception — they are native templates.*

| Screen | Purpose |
|---|---|
| Welcome | Create account (username + password) or sign in — **skippable** (§7.9) |
| Home / Ride | Big start-stop, live stats (speed, distance, elapsed, ascent), map |
| My Rides | Local + synced track list, detail view, GPX export, share, **GPX import** (§15.2) |
| Group Rides | Joined rides, create, enter a join code, request to join (§5.2) |
| Ride Requests | *Organiser only* — pending join requests, admit or decline |
| Group Ride Live | Map with member pins + planned route + **markers (§16)** + member list & ETA/gap |
| Add Marker | Long-press the map or a big *drop marker* button: icon, title, note, optional direction, optional photo (§16) |
| Ride Thread | Comments, photos, pinned posts, reactions, polls — silent while the ride is Live (§17) |
| Route Planner | Import GPX (§15.2) / pick a past ride / draw simple route → attach to group ride |
| Settings | Units, GPS profile, map provider, signed-in devices, account |
| Settings → Profile | Display name, phone, email + a sharing switch each (all off), and a "what other riders see" preview (§7.3) |

### 4.2 Recording pipeline

```
GPS fix → filter → buffer (in-memory) → SQLite append → batch upload
          ▲
          └─ accuracy gate, speed sanity, update-rate gate
```

- **Update rate — three numbers the rider sets, not a named profile.** *Update distance* (5 / 10 / 25 / 50 / 100 / 500 m, default 25): travel this far and the new position goes. *Maximum update time* (10 / 30 / 60 / 120 s, 5 / 10 min, default 60 s): nothing sent for this long and the current position goes anyway. *Minimum update time* (2 / 5 / 10 / 30 / 60 s, default 5 s): never two sends closer together than this — a distance trigger inside the window is held, and what goes when it lifts is the latest fix, not the one that came due. The maximum is always greater than the minimum, enforced in the type.
- The three replaced `Eco` / `Balanced` / `Precise`, which were fixed pairs of the first two with the third hidden. A stored profile name still decodes to the matching rate, so an existing choice survives the upgrade.
- The **accuracy gate** is not a rider setting: it is four times the update distance, clamped to 30–50 m. Asking for coarse updates is not asking to be drawn in the wrong place.
- The platform receiver is asked for something finer than the wire carries — half the minimum (capped at 5 s) and a fifth of the update distance (capped at 10 m) — so the OS's own filters can never be what decides when a rider is seen. A parked phone whose receiver goes quiet is covered by the maximum, which the device restates on a timer.
- Points appended to SQLite in transactions of ~20 so a crash loses seconds, not the ride.
- Track simplification (Ramer–Douglas–Peucker) applied **only** to the copy sent to the server for display; the raw track is preserved locally and uploaded in full on Wi-Fi.

**Recording is one of two ways a track comes into existence.** The rider turning *save track* on and off produces a track; so does importing a GPX file from the app or the website. Both land in the same entity, the same list and the same stats pipeline — see **§15**, which also covers editing. The one asymmetry worth knowing here: a recorded track always has timestamps and accuracy per point, and an imported one frequently has neither (§15.3).

### 4.3 Background location — the hard part

Highest-risk area of the whole project. Spike it **first** (Phase 0).

**Android**
- Foreground `Service` with `android:foregroundServiceType="location"`, persistent notification, `START_STICKY`.
- Permissions: `ACCESS_FINE_LOCATION`, `ACCESS_BACKGROUND_LOCATION`, `FOREGROUND_SERVICE_LOCATION`, `POST_NOTIFICATIONS`.
- Use **Fused Location Provider** (Google Play Services), not `LocationManager`.
- Battery: offer the `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` prompt with an explanation. Expect OEM aggression (Xiaomi/Huawei/Samsung) — ship a "recording stopped unexpectedly?" help page.

**iOS**
- `UIBackgroundModes: location`, `NSLocationAlwaysAndWhenInUseUsageDescription`.
- `CLLocationManager` with `allowsBackgroundLocationUpdates = true`, `activityType = .automotiveNavigation` (or `.fitness`).
- `pausesLocationUpdatesAutomatically = false` while a ride is active.
- Consider the iOS 17+ `CLLocationUpdate` live-updates API for a cleaner async stream.

**Shared surface**

```csharp
public interface ILocationProvider
{
	IAsyncEnumerable<LocationFix> Watch(AccuracyProfile profile, CancellationToken ct);
	Task<PermissionState> EnsurePermissionsAsync();
	bool IsRecording { get; }
}
```

ViewModels only ever see this interface; both platform implementations stay thin. The GPX replay harness (§10.4) is a third implementation used by tests.

### 4.4 Offline-first sync

- **SQLite** is the source of truth on-device.
- Every mutable row carries `LocalId (guid)`, `ServerId?`, `SyncState` (`Local | Pending | Synced | Conflict`), `UpdatedUtc`.
- Client-generated GUIDs as primary keys → uploads are idempotent, retries are safe, no ID reconciliation.
- Outbox pattern: a queue of pending operations drained by a background sync loop with exponential backoff.
- Conflict rule: **a track has exactly one writer at any moment** — the recording device until the full-resolution upload completes, the server from then on *(restated in v0.12; through v0.11 this read "tracks are append-only and immutable once ended", which web editing would contradict)*. Points are still append-only while recording, and the device never edits, so the two writers can never overlap. The only real conflicts remain on group-ride metadata — last-write-wins on the server, owner wins ties.
- Once the server owns a track, the device's copy is a **cache**: an edit (§15.5) bumps `Version`, and the next sync replaces the local copy wholesale rather than merging. Merging point lists is a problem this design never has to solve, and that is deliberate.

### 4.5 Maps — MapLibre over OpenStreetMap, everywhere; Mapsui for the car

The renderer set, as of **v0.24**:

| Surface | Renderer | Tiles | Cost |
|---|---|---|---|
| **iOS + Android + Web** | **MapLibre GL JS** in the `BlazorWebView` / the browser | **OpenStreetMap**, *"to begin with"* | Free. No key, no account, no server dependency |
| **Android Auto / CarPlay** | **Mapsui / SkiaSharp** into a raw `Surface` | Whatever it is given | Unchanged since v0.9 (§4.6) |

**One module, one interop class, three hosts.** `map.maplibre.js` and `MapLibreInterop` live in `BlazorDLR.Shared`, and every host registers the same line:

```csharp
builder.Services.AddTransient<IMapInterop, MapLibreInterop>();
```

Base-map role only — tiles, camera, rotation, attribution. **Every rider pin, marker and track is drawn by `SkiaMapOverlay`** (v0.21, unchanged and still the design's centre of gravity here).

#### Why three base maps became one

v0.21 left the project with Apple Maps on iOS, Google Maps on Android and OpenLayers on the web. That was a defensible answer to a question that had stopped being asked, because v0.21 itself had already changed the terms:

- **v0.21 moved every marker, pin and route onto the Skia overlay.** After that the base map draws tiles and handles gestures. Nothing else.
- So the three SDKs were being kept for the one job all three do identically, and each was charging for it separately: MapKit JS wanted a `.p8`, an ES256 token endpoint and an origin claim; Google Maps wanted a browser API key shipped inside the app bundle and restricted at the provider; OpenLayers wanted neither but only ran on the web.
- **Two of the three made the map a server dependency or a secret-management problem, and neither bought a pixel the third could not draw.**

What that cost in practice is worth recording, because it is the kind of bill that arrives late. A deployment with no MapKit key showed *"Map unavailable — Map credentials unavailable"* on the phone; diagnosing it ran through a token endpoint, a rate limiter, an options object, a signing key and an origin claim before arriving at "this server was never given a `.p8`". None of that machinery was drawing anything.

#### The overlay had never run on a phone

Worth recording separately, because it is the more embarrassing half of the same story and it was found by shipping v0.24 rather than by reasoning.

`SkiaMapOverlay` was built on `SKCanvasView` from `SkiaSharp.Views.Blazor`. That package initialises through `[JSImport]` — **WebAssembly-only** interop — so on a MAUI `BlazorWebView`, where the runtime is Mono, the overlay's first render throws `System.Runtime.InteropServices.JavaScript is not supported on this platform`. An unhandled exception in a child's `OnAfterRenderAsync` does not merely lose that child: **it takes down the Blazor renderer for the whole application.** The observed symptom was exact — the base map still panned, because it is pure JS inside the WebView, and every button on every page stopped responding, because nothing Blazor was running any more.

This was not introduced by the consolidation. It was *revealed* by it. Before v0.24 the base map failed first on both phones — no MapKit token on iOS, no Google browser key on Android — so `RideMap` never reached the branch that mounts the overlay. v0.21's headline claim, *one C# file drawing the same pixels on every surface*, had only ever been exercised on the web.

The fix keeps the drawing code and replaces the surface:

- **`PaintOverlay(SKCanvas)` is unchanged.** All ~1,100 lines of pin, route, label and circle drawing take an `SKCanvas` and always did, which is why this cost a surface rather than a rendering rewrite.
- It draws into an off-screen `SKSurface`, encodes PNG, and presents the frame through `map/overlay.js`. Plain SkiaSharp runs natively on Android and iOS — only the *canvas binding* was browser-only.
- **Repaints are coalesced, and the frame is CSS-transformed between them.** A repaint costs a rasterise, an encode and a bridge hop; viewport events arrive once per animation frame during a drag. A pan, a zoom and a turn are all changes a `translate`/`scale`/`rotate` can express, so the frame already on screen follows the map for free, and a real repaint is started only for what it cannot express: new content, or a resize.
- `BlazorDLR.Shared` now references plain `SkiaSharp`. Each host names its own native assets: the MAUI SDK for Android/iOS, `SkiaSharp.NativeAssets.WebAssembly` in `BlazorDLR.Web.Client`, `SkiaSharp.NativeAssets.Linux` in `BlazorDLR.Web`.

`RideMap` also wraps the overlay in an `ErrorBoundary`. It catches nothing today, and it stays because the blast radius of this class of failure is the whole application rather than one component. Note that an `ErrorBoundary` does **not** catch failures during component instantiation — a missing DI service throws before the boundary applies — so it is not a general safety net.

**The regression test runs on the desktop CLR**, which is the same not-wasm situation as the phone: `Overlay_RasterisesAFrame_OnARuntimeThatIsNotWebAssembly` mounts the real overlay and asserts a real PNG reaches `present()`. It failed before the fix and passes after it. Every *other* map test in the suite forces `InitException`, which is precisely how the overlay reached production having never been mounted outside a browser.

#### Then the phone found four more (v0.25)

Mounting the overlay on a device was not the same as drawing correctly on one. Each of the four below was invisible in a browser, and three of them were invisible on a *square-ish* test fixture as well — worth recording as a set, because the common cause is that the web host is the easy case for every one of them.

**1. The frame's decode was being cancelled.** `overlay.js` presented each PNG by assigning a `data:` URL to an `<img>`. That decode is asynchronous, and assigning a new `src` **abandons the one in flight**. On a laptop a frame decodes faster than the next viewport event arrives, so the race never opens; on a phone it does not, and the *observed* symptom was exactly the shape of the race — the live map, which repaints on every position broadcast, showed nothing at all, while the track editor, which repaints only on user input, showed the track "sometimes". The overlay is now a `<canvas>`, and frames arrive through `createImageBitmap` on a `Blob`, which is a decode that completes or throws rather than one that can be silently superseded.

**2. Every length was authored in CSS pixels on a canvas sized in device pixels.** Line widths, ring radii, glyph sizes and label padding were `const float`s, and the canvas is `devicePixelRatio` times bigger than the box it fills — so on a 3× phone the whole design was drawn at a third of its intended weight. `Viewport.DevicePixelRatio` now scales the twenty-odd length constants at paint time. This is also the whole of the "home circle does not appear on mobile" report: the private-area circle is a thin ring over a 20 %-alpha wash, and at a third-of-a-pixel stroke there was genuinely nothing to see.

**3. The overlay lagged the map by a round trip.** Following the base map from C# means the base map raises a viewport event, the event crosses the JS/.NET bridge, C# computes a transform and sends it back — after the frame the finger caused has already been painted. The base map's own projection is now published to `overlay.js` through a small tracker registry in `interop.js`, and the overlay reads it inside MapLibre's `move` handler. **The transform is computed on the same frame as the movement it is following**, and no repaint is involved. Nothing about the drawing got faster; the lag was never drawing.

**4. Off-centre content was misplaced on a turned map.** `map.getBounds()` is **axis-aligned**: with a bearing applied it *encloses* the rotated view rather than tracing it, so the reported box is wider and taller than the canvas — `W·|cos θ| + H·|sin θ|` across, `W·|sin θ| + H·|cos θ|` down. `ProjectToCanvas` was dividing that inflated span by the canvas side to get its scale, which gives the two axes **different** scale factors and is therefore not a projection at all away from the cardinal bearings. The scale is now recovered from the inflated box before the rotation is applied.

> **This is the load-bearing fact about `MapViewport`, and it was undocumented.** The contract carries an axis-aligned extent *plus* `HeadingDeg`, and the two are only usable together. Anything that derives a scale from the extent alone is correct at 0° and 180°, where the box is the canvas, and wrong at every other bearing — with the error growing in proportion to distance from the centre, so it reads as "the middle is fine". Any future base map bound to `IMapInterop` must report the same axis-aligned box, or report that it cannot.

That last one is worth a note on how it survived. `MapGeometryTests` did cover rotation, and its assertions were the right ones — the centre is the fixed point, a turn is an isometry. But it modelled a rotation as `viewport with { HeadingDeg = 37 }`, holding the bounds still, which is a view that **cannot exist**; and its fixture was a 1000 × 1000 canvas, on which the two wrong scale factors are equal and the bug is invisible even so. The tests now build the bounds a base map would actually report for a bearing, and assert on an 800 × 400 canvas where a quarter turn swaps two different numbers.

#### Rotation is allowed; pitch is not (v0.25)

A rider following a route wants the map turned the way they are pointing. That is now permitted, with a compass in the top-left that appears only once the map is off north — a compass that always reads "north is up" on a map that is always north-up is decoration. It is sized and positioned from `--dlr-map-control-*` in `app.css`, the same tokens the live map's menu button uses, so the two controls cannot drift apart.

**Pitch is refused on every map, and this is structural rather than a preference.** The Skia overlay projects flat Web Mercator from a `MapViewport` that has no pitch term. A tilted base map would leave every pin, track and circle drawn for a view nobody is looking at, and unlike the bearing there is no term in the contract to correct it with. `maxPitch: 0`, `pitchWithRotate: false`, `touchPitch: false`.

**Rotation is off on the two screens where a tap *places* something** — the private-area picker (§10.1) and the marker composer (§16.1). On those the map is a coordinate entry field: a rider who has turned it and then taps is reasoning about a north-up mental image that is no longer on screen, and the point lands somewhere they did not mean. `MapOptions.AllowRotation`, defaulting to on.

#### What the consolidation removes

| Gone | Was |
|---|---|
| `map.mapkit.js`, `map.googlemaps.js`, `map.openlayers.js` | Three base-map modules implementing one contract |
| `AppleMapsInterop`, `GoogleMapsInterop`, `OpenLayersInterop` | Three interops, two of them host-specific |
| `GET /api/v1/maps/token`, `MapKitOptions`, `MapKitSigningKey`, `MapToken` | The map as a server dependency |
| `RateLimits:MapTokenPerHourPerAddress` | A ceiling on an endpoint that no longer exists |
| The MapKit `.p8` and the Google browser API key | Two rows on §14.2's never-commit list |
| The `#if IOS / #elif ANDROID` in `MauiProgram.cs` | A platform conditional selecting between credentials |

`IMapInterop`, `MapProvider` and `MapBridge` **survive**. The seam is what let three providers be deleted and one added without touching a screen, and §13 Q26's offline renderer is the case it exists for again. `MapProvider` is down to one member and stays an enum because the attribution obligation is per tile source, not per app.

#### Offline maps are possible again

v0.19 recorded this bluntly and it deserves the same treatment on the way back:

> **MapKit JS has no offline mode**, and there is no tile cache to point at a local file… A rider in a dead zone sees their track and their position over a blank background.

That was the sharpest thing the Apple decision cost, for an app whose premise is a trailhead with no signal. It is now a **tile-source** question rather than an SDK limitation — MapLibre will render from a local PMTiles archive as readily as from a tile server — which means §13 Q26's work and an offline map pack are the same work, not two projects.

This is not shipped. It is unblocked, which is a different claim and the only one being made here.

#### OpenStreetMap — and the words "to begin with"

Unchanged from v0.19, and now the **only** outstanding map decision:

- **OSM's tile usage policy is a real constraint, not a formality.** `tile.openstreetmap.org` is a donated service: it forbids bulk downloading and heavy or commercial use, and it requires an identifying `User-Agent`. It is appropriate for development and a handful of friends; it is **not** appropriate for a public launch, and continuing to lean on it at scale would be taking something that was given for a different purpose.
- **Attribution is mandatory and permanent** — "© OpenStreetMap contributors" under ODbL. It is declared on the tile source inside `map.maplibre.js`, so MapLibre's own `AttributionControl` renders it and removing the credit means removing the tiles.

So *"to begin with"* is still load-bearing: **before the web app is publicly announced, the tile source moves** to self-hosted PMTiles (§9.1) or a paid tier. Recorded as §13 Q26, and now on the critical path to launch rather than competing with three vendor integrations for attention.

#### What this costs

Honest accounting, because the previous three revisions each claimed a win here:

- **The tile bill is no longer deferrable by leaning on a vendor.** v0.19 got tile hosting *out* of Phase 1 by letting Apple serve the phone and OSM serve the web. Half of that is now gone: there is no vendor serving anyone, and §13 Q26 is the whole answer. A regional extract is a few GB for Australia against ~100 GB for the planet (§9.1) — real disk on a 40 GB VPS, and a real decision about which region.
- **Cartography is a step down on the phone.** Apple Maps is better-looking than raster OSM, and an iPhone user knows what Apple Maps looks like. The overlay draws everything this app authors, so what is lost is the base tiles' polish — worth stating rather than pretending the consolidation is free.
- **The CDN is still a network dependency.** `map.maplibre.js` loads the library from jsDelivr and the tiles from OSM. Neither works offline today; the difference from MapKit is that neither *has* to stay remote.

#### One thing this gives back

Every host now fails the same way, so the stated-error branch in `RideMap.razor` has one shape to get right instead of three, and a map bug reproduces on a laptop. §14.2's list is two rows shorter, and shipping the Android build no longer depends on an Apple Developer account — the coupling v0.19 flagged as "unusual enough to deserve being visible" is simply gone.

---

*The v0.2–v0.15 reasoning, retained because it explains the trajectory:*

**Phase 1 was to use the device's built-in maps:** `Microsoft.Maui.Controls.Maps` via `UseMauiMaps()` → Google Maps on Android, MapKit on iOS. Nothing to host, nothing to pay for, and it looks native.

Its limits were worth stating up front because they **shaped the abstraction** — and in the end they are what killed it:

| Capability | Built-in `Map` |
|---|---|
| Route polyline | ✅ `MapElements` → `Polyline` / `Polygon` / `Circle` |
| Rider pins | ⚠️ `Map.Pins` only — **no cross-platform custom marker imagery** |
| Map markers (§16) | ❌ no custom icon, no rotation, no persistent label — title only in a tap callout |
| Offline tiles | ❌ |
| Custom tile source | ❌ |
| Long polylines | ⚠️ degrades — simplify hard for display |
| Android setup | Maps SDK key in the manifest (free for map display) |
| iOS setup | None — MapKit needs no key |

**Structure** (contracts live in `DLR.Core`, so they carry zero MAUI types):

```
DLR.Core/Maps/          MapCoordinate, MapBounds, MapCamera, RouteOverlay,
                        RiderMarker, MapCapabilities, IMapRenderer
DLR.UI/Components/      RideMap.razor      ← one component, one C# surface
DLR.UI/wwwroot/         map.mapkit.js      ← Apple Maps: Android + iOS   (v0.19)
                        map.maplibre.js    ← MapLibre + OSM: web         (v0.19)
DLR.App/Maps/           MapsuiMapRenderer + IMapRendererFactory
                                              CAR SURFACES ONLY (§4.6)
DLR.Server/Maps/        MapKitTokenService ← ES256, .p8 never leaves here (v0.19)
```

*(v0.16: `NativeMapRenderer` and `RideMapHost : ContentView` are gone — there is no MAUI view layer left to host them. v0.19: one JS module became two.)*

```csharp
public interface IMapRenderer
{
	MapCapabilities Capabilities { get; }

	void SetCamera(MapCamera camera);
	void SetRoute(RouteOverlay? route);

	// Incremental, never "replace the whole collection".
	void UpsertRiderMarker(RiderMarker marker);
	void RemoveRiderMarker(Guid riderId);
	void ClearRiderMarkers();
}
```

**Four rules that decided whether the swap would be cheap — and it was, which is why v0.16 could make it at all:**

1. ~~ViewModels bind to `IMapRenderer` and the Core POCOs only~~ — **still true, now for the car path.** The rule that `Microsoft.Maui.Controls.Maps` appears in exactly one folder is what made deleting that folder a contained change rather than a rewrite (§10.4).
2. **Incremental marker updates.** `UpsertRiderMarker` / `RemoveRiderMarker`, never a wholesale pin rebuild. Rebuilding pins every 5 s flickers visibly on native maps, and baking that shape into the contract makes it unfixable later.
3. **`MapCapabilities` flags** — `CustomMarkerImages`, `OfflineTiles`, `Rotation`, `BreadcrumbTrails`, `CustomTileSource`, plus `RotatedMarkers` and `PersistentLabels` from v0.13 (§16.3). Richer renderers advertise extras and the UI degrades gracefully, instead of the contract permanently sinking to the weakest provider. This is the rule that absorbed the marker feature without a redesign.
4. **Own coordinate types.** No `Location` / `MapSpan` in `DLR.Core`.

**The `MapProvider` setting**, as of v0.19: `Apple | Osm | Offline` — defaulting to `Apple` on the phone and `Osm` on the web, with `Offline` present but not selectable until a PMTiles pack exists. The setting has survived three renderer changes without its shape altering, which is the point of having had it since v0.2.

**[Mapsui](https://mapsui.com/) remains, scoped to the car.** It is the only renderer that can draw into an Android Auto `Surface` or a CarPlay window (§4.6), which was true in v0.9 and is unaffected by anything since — head units have no browser, and MapLibre GL JS is a browser SDK. It keeps `IMapRenderer` alive as a contract with exactly one implementation and two hosts.

**Tiles are somebody else's problem again** (v0.19): Apple serves the phone, OSM serves the web. Self-hosted **PMTiles** — a regional extract served off the VPS by Caddy over HTTP range requests (§9.1) — is now the *planned replacement* for OSM before public launch, and the route to an offline option, rather than Phase 1 work.

### 4.6 Android Auto and Apple CarPlay

Both are **template-based projections**, not a port of the phone UI. The phone app does all the work; the head unit renders a constrained UI the platform controls. Arbitrary layouts are impossible, and driver-distraction rules are enforced by the template APIs themselves rather than only at review.

What ships on the car screen — the group on a map, the gap list, and two one-tap actions:

```
┌───────────────────────────┬────────────────┐
│                           │ DaveH          │
│   map: rider pins         │    1.2 km back │
│        planned route      │ SarahK         │
│        own position       │    0.4 km up   │
│                           │ Regroup  8 km  │
│                           ├────────────────┤
│                           │ [Stop] [Stopped]│
└───────────────────────────┴────────────────┘
```

| | **Android Auto** | **Apple CarPlay** |
|---|---|---|
| Library | Android for Cars App Library (`androidx.car.app`) | `CarPlay.framework` |
| Category / entitlement | **Navigation** category (`androidx.car.app.category.NAVIGATION`) | **`com.apple.developer.carplay-maps`** |
| Entry point | `CarAppService` → `Session` → `Screen` | `CPTemplateApplicationSceneDelegate` |
| Map surface | `NavigationTemplate` + `SurfaceCallback` — you draw into a raw `Surface` | Root view controller inside the `CPWindow` |
| Lists / actions | `ItemList`, `ActionStrip`, `MessageTemplate` | `CPMapTemplate` bars, `CPListTemplate` |
| Local testing | Desktop Head Unit (DHU) | CarPlay Simulator (Xcode external display) |
| Approval | Play review against Auto quality guidelines | **Entitlement request to Apple**, then App Review |

#### The consequence for §4.5

Android Auto navigation apps render the map by drawing into a `Surface` handed over by the host. A platform *view* cannot draw into that Surface — which ruled out `Microsoft.Maui.Controls.Maps` in v0.9, and rules out a `BlazorWebView` running MapLibre just as firmly in v0.16. So **car map support requires the Skia/tile renderer**, and `MapsuiMapRenderer` is a hard dependency of car support rather than the Phase 3 offline-packs nicety it started as. The phone's renderer has changed twice since; the car's conclusion has not moved.

`IMapRenderer`'s imperative contract is unchanged, but a renderer must declare which surfaces it can attach to:

```csharp
[Flags]
public enum MapHostKind
{
	None			= 0,
	AndroidSurface	= 1 << 0,	// Android Auto NavigationTemplate
	CarWindow		= 1 << 1	// CarPlay CPWindow
	// v0.16 removed AppView: the phone map is a Razor component, not an
	// IMapRenderer, so leaving the flag would have failed the architecture
	// test that every MapHostKind has a factory (§10.4, §18.3).
}

public interface IMapRendererFactory
{
	MapHostKind Supported { get; }
	IMapRenderer Create(MapHost host);
}
```

`MapsuiMapRenderer.Supported` is `AndroidSurface | CarWindow` — and since v0.16 those are the only two hosts `IMapRenderer` serves at all, because `AppView` is now a Razor component running a JS map rather than an `IMapRenderer` implementation (§4.5, §18.3). Asking for a renderer that cannot serve a given host is still a **startup failure, not a silently blank map**, and an architecture test asserts every `MapHostKind` has at least one factory (§10.4).

#### Two heads over one session, not two apps

The car UI is a third presentation over the same running process — the recording foreground service, the SignalR connection and the position cache client are all shared. Car screens must not reach into phone ViewModels, so both heads observe one presentation-neutral state object in `DLR.Core`:

```csharp
public interface IRideSessionState
{
	RideSnapshot Current { get; }
	IAsyncEnumerable<RideSnapshot> Watch(CancellationToken ct);

	Task StartRecordingAsync();
	Task StopRecordingAsync();
	Task FlagStoppedAsync();		// the "I've stopped" action, §5.4
}
```

*(v0.15: this interface gained a **focused ride** plus `OtherLiveRides` and `FocusAsync`, because a rider can be live in several rides at once — §5.7. The car head renders the focused one and picks between them on the second screen it already had.)*

`RideSnapshot` carries elapsed time, distance, speed, the member gap list (§5.4), the next regroup point and the off-route flag. Phone ViewModels and car `Screen`s both project from it, so a change to gap calculation reaches both screens at once and is unit-testable without either platform present.

#### No authentication on the car screen

Sign-in never happens on a head unit. Android Auto ships a `SignInTemplate`, but the car UI simply *requires* an already-authenticated session — which permanent sessions (§7.4) make effectively always true. With no account, the car screen shows a single `MessageTemplate` / `CPInformationTemplate` reading *"Open Dumb Luck Rides on your phone"* and nothing else.

Shared profile fields (§7.3) are **never rendered on the car screen.** A phone number is useless while driving, and templates are the wrong place for contact details.

The same reasoning caps what markers (§16) show here: **icons on the map, and at most one row of "next marker ahead" in the list.** No titles, no notes, no photos, no tapping — see §16.3.

**The ride thread (§17) does not appear on a car screen at all** — not truncated, not as an unread badge. It is the clearest case in the product of something whose right amount on a head unit is zero.

#### Distraction limits shape the design, not just the polish

- Template depth is capped, so the car UI is at most two screens deep: the ride map, and a list to pick which ride.
- Visible list rows are limited while driving — the gap list shows the nearest few riders, never all 50.
- No free text entry, no scrolling text, no custom tap targets. Entering a join code on the car screen is not possible and is not attempted.
- Every action is one tap: stop recording, flag stopped, acknowledge a regroup pin.

#### MAUI has no car support — both heads are platform code

This is the largest platform-specific surface in the project and none of it is shared.

- **iOS is the more tractable side.** `CarPlay.framework` is part of the iOS SDK and therefore already bound by .NET for iOS, so `CPTemplateApplicationSceneDelegate`, `CPMapTemplate` and friends are callable directly. Needs a `UIApplicationSceneManifest` scene declaration plus the entitlement.
- **Android needs a binding, and this is the biggest unknown in the car story.** `androidx.car.app` has no MAUI abstraction. **Phase 0 must verify whether a maintained `Xamarin.AndroidX.Car.App` package exists at a usable version.** If it does not, this becomes a .NET for Android binding project over the AAR — a real piece of work. Do not plan around an assumption here; check it before committing to a date.

#### Entitlements are the long pole

Apple's CarPlay entitlement is a request-and-wait process, selective for navigation apps, measured in weeks to months. It gates nothing during development — the simulator works without it — but it gates *shipping*, so **the request goes in during Phase 1**, as soon as there is enough of a product to describe. Android Auto has no pre-approval, but Play review against the Auto quality guidelines happens at submission, where a rejection costs a whole cycle.

The **Desktop Head Unit** and **CarPlay Simulator** are both free and both mandatory before any real hardware time. Neither substitutes for one real head unit before submission — per-manufacturer projection quirks are real.

---

## 5. Group Rides — Realtime Design

### 5.1 Lifecycle

**There isn't one.** An adventure is live from the moment it is created until somebody deletes
it: joinable, route visible, thread active, positions flowing for whoever has said yes. The
organiser never presses *Start*, nobody presses *End*, and the only way to finish one is to
delete it (§5.6).

Through v0.31 there were six states — `Draft`, `Open`, `Live`, `Completed`, `Archived`,
`Cancelled`. Five of them were never assigned by any code path in the product's life, and the
one transition that carried weight bought a single thing: a moment at which the server deleted
everybody's positions. §5.6 says what replaces it. What the states cost, in return, was an enum
on the wire, two columns, two endpoints, a background service and a state test in front of nearly
every guard in the product — including several that had been quietly dead since they were written.

The consequence worth naming: **the rider's own switch is now the whole of the control surface.**
There is no organiser action that turns anybody's sharing on, and none that turns it off but
removing them from the adventure.

### 5.2 Joining — the organiser always decides

There are exactly **two ways into a ride**, and the organiser controls both:

| Path | Flow | Organiser's role |
|---|---|---|
| **1. Join code** | Organiser shares a code / link → rider enters it → joins | Chose who to give the code to |
| **2. Join request** | Rider opens the ride and requests to join → organiser admits or declines | Explicitly admits each rider |

- 6-character human-friendly **join code** (Crockford base32, no ambiguous characters), plus deep link `dlr://ride/AB3K9Z` and an `https://` universal-link fallback that also renders in the web app.
- `GroupRide.JoinPolicy` replaces the old `RequiresApproval` boolean:
  - `Open` — a valid code joins immediately.
  - `Approval` — a valid code, or the ride link, creates a **pending request**; nobody enters until the organiser admits them.
- **Both paths end at the location-sharing prompt** (§5.6). Joining a ride and agreeing to broadcast are separate decisions, and the second one defaults to *off*.
- Hard member cap per ride (default 50). Organiser can remove a member, and decline-and-block a requester so they cannot ask again. **Removing a member deletes their position row immediately, revokes their access to the thread and markers, and leaves their existing posts in place** (§5.5, §17.6) — deleting half a conversation makes the other half nonsense, and the organiser can delete posts explicitly if that is what they actually want.
- **The join code is returned to every member.** *(Organiser-only in SRV-20; widened in v0.29.)* `RideDetail.JoinCode` and `RideSummary.JoinCode` carry the code for anybody in the ride, so the *Group adventures* list shows the badge on a joined ride exactly as it does on an organised one. The earlier rule withheld it because the code is the ride's access control — but a rider who has already been let in can name the ride to anybody anyway, and withholding the code only stopped them telling a friend how to follow along, which is what they most want to do at the start line. On an `Approval` ride the organiser still decides every admission, so nothing about who gets in changes; on an `Open` ride the code is now a convenience the people already in the ride are trusted with. **The export still never carries it** (§6.3) — a file nobody thinks of as a sharing surface is a different question from a badge on a screen.
- **A blocked rider gets the same 404 as an unknown code.** *(Implemented in SRV-20.)* A distinct "you are blocked" response hands them the one fact the organiser was trying not to have a conversation about.
- Pending requests notify the organiser by push; decisions notify the rider. *(SRV-20 ships this as **email**, not push — push is Phase 2, and email is what exists. It reaches only accounts with a known address, which is another line in §7.2's trade-off. `RideNotifications` is where push attaches.)*
- **Request spam limits:** at most 5 pending requests per user at once, and 20 requests per user per day. Without this, "request to join" is an invitation to pester every ride in the system.

**This is now the whole abuse story for group rides, and it is a stronger one than email verification.** Email verification only ever proved that somebody could read a mailbox — it never proved the organiser wanted them on the ride. Under both paths above, a rider reaches another person's live location **only** because the organiser handed out a code or pressed *Admit*. That is why the confirmed-email gate was removed in v0.5 (§7.2, §10.1).

Discovery — how a rider finds a ride to request in the first place — is deliberately left narrow for now: an organiser-shared link. Open browsing of nearby rides is §13 Q8.

### 5.3 SignalR hub

```csharp
public interface IRideClient                          // server → client
{
	Task PositionsUpdated(PositionBatch batch);
	Task MemberJoined(MemberDto member);
	Task MemberLeft(Guid memberId);
	Task RouteUpdated(RouteRef route);
	Task JoinRequestReceived(JoinRequestDto request);     // organiser only
	Task JoinRequestDecided(JoinRequestDecision decision);
	Task MarkerAdded(MarkerDto marker);                   // §16.6 — discrete,
	Task MarkerUpdated(MarkerDto marker);                 //   never folded into
	Task MarkerRemoved(Guid markerId);                    //   the position batch
	Task CommentPosted(CommentDto comment);               // §17.8
	Task CommentEdited(CommentDto comment);
	Task CommentRemoved(Guid commentId);
	Task CommentPinChanged(Guid commentId, bool isPinned);
	Task ReactionsUpdated(Guid id, ReactionCounts counts); // coalesced, §17.4
	Task PollUpdated(Guid commentId, PollResults results); // coalesced
	Task RidePermissionsChanged(RidePermissions perms);   // §5.8
	Task MemberSharingChanged(Guid memberId, bool sharing);
}

[Authorize]                                           // + membership check, §7.6
public class RideHub : Hub<IRideClient>               // client → server
{
	Task JoinRide(Guid rideId);                       // → Groups.AddToGroupAsync
	Task PublishPosition(PositionUpdate update);      // once, not per ride — §5.7
	Task LeaveRide(Guid rideId);
}
```

**Fan-out strategy — batch, don't relay.** Naïvely relaying every fix is O(n²) messages. Instead:

- Clients push their position every **5 s** while Live — throttled server-side, extra pushes dropped. **One push covers every ride the rider is live in** (§5.7); the server decides which of them it lands in, by that ride's own consent flag.
- Server holds last-known positions in `RiderPositionCache` — an in-memory **write-behind cache**, flushed to PostgreSQL every 10 s (§5.5).
- A single hosted `RideBroadcastService` ticks every **5 s per active ride** and sends **one batch** containing all members' latest positions to that ride's SignalR group.
- Result: `1 message × n members` per tick, not `n × n`. Payload is a compact array of `[memberId, lat, lon, speed, heading, ts]` with lat/lon as integers scaled 1e-5 (~1 m).

**The same lesson is applied to reactions** (§17.4), which are the other high-frequency, low-value event in the product: coalesced per comment on a short timer rather than relayed per tap. Markers and comments themselves are discrete authored events and *are* sent individually — dropping one is data loss, not a skipped frame.

**Transport realities**
- Prefer WebSockets; SignalR falls back to SSE / long-polling automatically.
- Survive tunnels and dead zones: auto-reconnect with jitter, and on reconnect fetch a `GET /rides/{id}/positions` snapshot rather than replaying history.
- Authentication on a long-lived connection has its own rules — see §7.6.

### 5.4 In-ride features worth having (v1.1+)
- Gap/order list: distance along route per member → "who's behind".
- ~~"Regroup here" pin dropped by the ride leader~~ — **absorbed into map markers (§16.1)**: a marker with the `regroup` icon, pushed to all. Same for the "I've stopped" flag below, which is the `stopped` icon.
- Breadcrumb trail: last 10 minutes of each member's path *(client-side only — the server keeps no history)*.
- Off-route warning (distance from planned polyline > threshold).
- SOS / "I've stopped" flag — genuinely useful on remote rides.

### 5.5 Position durability — in-memory cache, 10 s write-behind

The server keeps live positions in memory for fan-out speed, and writes **only the last known position per rider** to PostgreSQL every 10 s so that a restarted process rehydrates a warm cache instead of showing a blank map until every rider's next push.

**Two independent cadences.** These are easy to conflate and must not be:

| Timer | Period | Purpose | Config key |
|---|---|---|---|
| `RideBroadcastService` | 5 s | Network fan-out to SignalR groups | `Ride:BroadcastSeconds` |
| `PositionFlushService` | 10 s | Durability / cache rehydration | `Ride:FlushSeconds` |

**Collaborators** (all in `DLR.Server/Positions/`):

| Type | Responsibility |
|---|---|
| `RiderPositionCache` | `ConcurrentDictionary<Guid rideId, ConcurrentDictionary<Guid userId, PositionEntry>>`. `Upsert` rejects an older `RecordedUtc` and sets `IsDirty`. Exposes `ReadyAsync()`. |
| `PositionFlushService` | `BackgroundService` on `PeriodicTimer(FlushSeconds, TimeProvider)`. Drains dirty entries → one upsert. Also flushes on `StopAsync`. |
| `PositionCacheRehydrator` | Loads every position fresher than `Ride:StalenessMinutes` into the cache exactly once at startup, gated by `Lazy<Task>`. |
| `PositionWriter` | Raw-Npgsql upsert and delete statements. The one place SQL is hand-written. |

**Schema** (EF Core migration, snake_case via `UseSnakeCaseNamingConvention`):

```sql
CREATE TABLE rider_position (
	group_ride_id	uuid		NOT NULL REFERENCES group_ride(id) ON DELETE CASCADE,
	user_id			uuid		NOT NULL REFERENCES asp_net_users(id),
	lat				integer		NOT NULL,	-- 1e-5 deg, ~1 m
	lon				integer		NOT NULL,
	speed_mps		smallint	NULL,
	heading_deg		smallint	NULL,
	accuracy_m		smallint	NULL,
	recorded_utc	timestamptz	NOT NULL,
	PRIMARY KEY (group_ride_id, user_id)
);

CREATE INDEX ix_rider_position_recorded ON rider_position (recorded_utc);
```

lat/lon are stored as scaled `integer` — same representation as the wire format in §5.3, no float drift, roughly half the row width. **There is no history table.** The row is overwritten in place, and the composite primary key makes "one row per rider per ride" a database invariant rather than a convention.

**Flush statement** — one round trip regardless of rider count, via array parameters:

```sql
INSERT INTO rider_position
	(group_ride_id, user_id, lat, lon, speed_mps, heading_deg, accuracy_m, recorded_utc)
SELECT * FROM UNNEST (
	@rideIds, @userIds, @lats, @lons, @speeds, @headings, @accuracies, @times)
ON CONFLICT (group_ride_id, user_id) DO UPDATE SET
	lat				= excluded.lat,
	lon				= excluded.lon,
	speed_mps		= excluded.speed_mps,
	heading_deg		= excluded.heading_deg,
	accuracy_m		= excluded.accuracy_m,
	recorded_utc	= excluded.recorded_utc
WHERE excluded.recorded_utc > rider_position.recorded_utc;
```

The `WHERE` guard is load-bearing: it makes the flush **idempotent** and stops an out-of-order or retried batch from regressing a newer row.

**Rehydration rules** — all three matter; each one omitted is a defect:

1. **Freshness gate:** only rows with `recorded_utc > now - Ride:StalenessMinutes` (default 15). A stale point must not reappear on the map as if it were current. Since v0.32 this is the *whole* filter — there is no adventure state for a second arm to test (§5.1).
2. **Loaded entries are marked clean.** Otherwise startup immediately schedules a pointless write of everything it just read.
3. **Reads await `ReadyAsync()`.** Hub reads and `GET /positions` block until rehydration completes, so no client can observe a half-warm cache. The gate lives *inside the cache* rather than relying on hosted-service ordering, because Kestrel's `GenericWebHostService` can start serving before custom hosted services have run. The rehydrator also kicks the task off eagerly so the cache is warm before the first request arrives. **The gate must open on the failure path too** *(SRV-22)* — `MarkReady()` belongs in a `finally`. A rehydration that throws and leaves it shut does not degrade to a blank map; it hangs every read for the life of the process.

**Deletes do not go through the write-behind** *(SRV-22)*. Publishing is cache-first and reaches PostgreSQL on the next tick; `StopSharingAsync` and `ClearRideAsync` delete from the database directly and evict the cache. "Gone within ten seconds" is not what a rider turning sharing off asked for (§5.6).

> **Known gap — a flush in flight can resurrect a deleted row.** A flush that has already snapshotted its batch, and completes its write *after* a concurrent delete, puts that rider's position back. The window is one round trip and needs a delete to land inside it, so it is rare — but it is rare, not impossible, and what it leaves behind is exactly the position at rest §10.1 says must not exist. Neither ordering of *delete-then-evict* and *evict-then-delete* closes it; the fix is a tombstone the flush filters its batch against immediately before writing, or a membership join in the upsert. **The §7.11 nightly sweep is the current backstop, which means the exposure is up to a day.** Worth closing properly — see §13 Q16.

**Lifecycle and cleanup**
- Member sets `ShareLocation = false`, or leaves, or is removed by the organiser: delete that member's row immediately — stopping the broadcast is not sufficient (§10.1).
- Adventure deletion is covered by `ON DELETE CASCADE`, plus an explicit `ClearRideAsync` first, because the cascade clears the table and not the cache in front of it.
- **Nightly idle sweep** — any row with no fix for `Ride:PositionIdleDays` (default 14) is deleted and that member's `ShareLocation` cleared (§7.11). Since v0.32 this is the only unconditional reclamation there is, and it exists because *End* used to be (§5.6).

**Cost of the trade-off, stated plainly:** a hard process kill loses up to 10 s of movement. On restart the cache rehydrates slightly stale and self-corrects on each rider's next 5 s push, so the worst observable symptom is a pin that lags for a few seconds. A graceful shutdown loses nothing. At 500 concurrent riders the flush is ~50 rows/s in a single statement — negligible on the €4 VPS (§9).

### 5.6 Consent to share, and what makes it stop

**Joining a ride and agreeing to broadcast your position are two separate decisions**, and the app treats them that way.

#### At join

Both join paths (§5.2) end at the same prompt, before the rider is in:

> **Share your location with this ride?**
> Members of *Saturday Coast Run* will see where you are. You can turn this off at any time. It stops when you turn it off, leave the adventure, or the organiser removes you.
>
> **[ Share ]  [ Not now ]**

*(The last sentence names three things and no fourth, and that is the whole of the care in it. It said "it stops when the ride ends" through v0.14, which the wind-down made untrue; it named the wind-down from v0.17, which v0.32 made untrue. There is now no end for sharing to stop at, so the copy promises only what the rider themselves controls. **It must not point at the fourteen-day sweep** (§7.11) — that is a garbage collector for rows nothing is updating, not a limit on somebody who is still riding, and dressing it as one would be the third version of the same mistake.)*

**Shown once per adventure, per device**, remembered through `IDeviceSettings` (§18.6). That fact used to be free: an adventure that had not started was the only one that asked, so `Open`-ness was doing half the job. With no lifecycle the fact has to be written down, because the two ways of not writing it down are *ask on every load of an adventure somebody has deliberately declined* and *never ask anybody*.

- **Dismissing is "not now", and the flag defaults to `false`.** A prompt that treats a swipe-away as consent is not a consent prompt. This matches §7.3's structural default-off for profile fields, for the same reason: an accidental "on" cannot be un-shared.
- The choice is **per ride**, stored on `GroupRideMember.ShareLocation`. A rider who shares with their regular Sunday group and not with a charity ride full of strangers is expressing something sensible, and one global switch could not express it.
- Turning it on later is one tap from the ride screen, and the ride screen makes the current state obvious rather than burying it in settings.

#### A rider may be in a ride without sharing

This is allowed deliberately, and it is worth defending because the alternative is tempting: making sharing the price of seeing the map would be simpler, and it would be coercive. Someone joining a big organised ride to follow the route, a pillion, an organiser driving a support van — all have reason to watch without broadcasting.

The control is **visibility, not enforcement**: the member list shows each rider's state — *sharing*, *not sharing*, *no signal*, and since v0.31 *private* (§10.1) — so a group that cares can see the asymmetry and say something. That is a social problem with a social fix, and the app's job is to make the fact legible rather than to compel.

**"No signal" and "not sharing" must be distinguishable in the UI.** They mean completely different things to somebody waiting at a junction, and collapsing them into one grey pin is the kind of small ambiguity that gets someone left behind.

#### Turning it off, at any time

Unchanged from §5.5 and worth restating because it is the load-bearing part: setting `ShareLocation = false` **deletes the persisted row immediately** and evicts the cache entry. Stopping the broadcast alone would leave a last-known position at rest in the database — precisely what a rider turning sharing off is asking you not to do. Leaving the ride and being removed by the organiser do the same thing.

#### There is no end of the ride

Through v0.31 the organiser pressed *End* and chose between stopping everybody's sharing at once
and a bounded two-hour wind-down in which riders stopped themselves. Both are gone with the
lifecycle (§5.1). What went with them is worth naming precisely, because it was the only one of
its kind: **the guaranteed death of a `rider_position` row.** A rider who taps *Share*, rides home
and forgets the switch was caught by the end of the adventure whether or not their phone was awake.

Nothing else in the system deletes that row, so something had to replace it.

**The replacement is a fourteen-day idle sweep**, in the nightly job that already exists (§7.11).
Any `rider_position` whose `recorded_utc` is older than `Ride:PositionIdleDays` — default **14** —
is deleted and that member's `ShareLocation` cleared. Server-side and unconditional: it does not
depend on any client being awake, which is the property the wind-down cap had and the only part of
it worth keeping. A fortnight rather than a few hours because it is not trying to be a stop button;
a rider on a three-day trip with intermittent signal must not be quietly unsubscribed.

**Be exact about what fourteen days is and is not.** It is a backstop against a row nothing else
reclaims — a phone that died, an app uninstalled, an adventure nobody deletes. It is **not a
privacy promise, and no user-facing sentence may present it as one.** A rider who leaves the switch
on is sharing until they turn it off; the sweep only catches the case where they stopped sending
anyway. The one place it is written down for users is the retention table in the privacy policy,
as the outer bound on a row nobody is updating.

**The eviction is what makes the delete stick, not the order of the two writes.** A flush already
in flight reads `RiderPositionCache` and never consults `ShareLocation`, so clearing the flag first
would not stop it putting the row back — dropping the cache entry is what does. The sweep therefore
lives on `PositionStore` beside the three per-rider paths and ends with the same delete-then-evict
pair `StopSharingAsync` uses, rather than restating the obligation in the maintenance job.

**Deleting the adventure is what finishes one**, and it is no longer refused while people are on
the road. That refusal existed because *End* was the gentler verb for that moment; with no *End* it
was a lock on a door with no other way out. It takes the thread and the markers with it, which is
the honest cost and is why it is confirmed.

**The organiser cannot switch a rider's sharing back on**, and since v0.32 they cannot switch it off
either except by removing them from the adventure. That asymmetry is the whole point — the organiser
controls the *adventure*, the rider controls their *location* — and it is now the entire control
surface rather than one half of it.

*(SRV-21 makes that structural rather than checked: the route is `PUT /group-rides/{id}/sharing/**me**`, and there is no user-id form of it. An endpoint that could express "set another rider's sharing" would need a guard, and a guard is a thing that can be removed by someone who does not know why it is there. The route cannot be relaxed by accident.)*

**Four routes carry the delete obligation, not one** *(SRV-21, SRV-36)*. Turning the switch off, leaving, being removed, and the nightly idle sweep must all delete the position row, and `rider_position` has **no foreign key to `group_ride_member`** — so removing a member cascades nothing and the explicit delete is the only thing doing the work. All four live on `PositionStore`, because four copies of an obligation is how one of them eventually stops meeting it. The sweep is set-based rather than a loop over `StopSharingAsync` — it is a backstop over the whole table — but it is a method on the same type, not a second implementation in the job that calls it.

#### Which means §1's headline claim needed correcting, again

v0.14 said sharing *"is scoped to the group ride and ends with it"*; v0.15 corrected that to name the
wind-down. Neither is true now, and this document's rule is that a correction is recorded rather
than quietly restated (§10.1, and the v0.2 correction that established it). The accurate claim, now
used in both places:

> Live sharing is scoped to the group adventure, off unless you turn it on, and stops the moment you
> turn it off, leave, or are removed. Nobody else can turn it on for you, and there is no history —
> one row per rider, overwritten in place, deleted when you stop.

Note what the claim no longer says: that sharing has an end you did not choose. It does not, and
pretending otherwise on the strength of the fourteen-day sweep would be the mistake v0.15 and v0.17
each corrected once already.

### 5.7 Being in several rides at once

A rider can be a member of any number of rides, and more than one of them can be `Live` simultaneously. A weekend away with a big organised event running inside a small group of mates is the ordinary case, not a corner one.

Most of the storage design already supports this without change, which is worth noticing before adding anything: `RiderPositionCache` is keyed ride-first (§5.5), `rider_position` has primary key `(group_ride_id, user_id)`, and `GroupRideMember` is unique per pair. Multi-ride was never excluded by the data model; it was only ever excluded by assumptions in the client.

**One publish, many fan-outs.** The client sends its position **once** per 5 s tick, not once per ride. The server writes it into every ride where that rider is a member **and** `ShareLocation` is true for *that* ride:

```csharp
Task PublishPosition(PositionUpdate update);   // no rideId — see below
```

Two reasons the update carries no ride id. First, cost: publishing per ride multiplies the rider's uplink and battery by the number of rides they are in, for data the server can trivially copy. Second, correctness: the rider's own consent is per ride (§5.6), so the *server* must be the thing that applies it — a client deciding which rides to publish to is a client that can get it wrong in the direction that leaks.

**Consent is filtered on the write, not on the read.** A rider sharing with ride A and not ride B has no row in B at all. Broadcasting to B and having its members' apps hide the pin would leave the position in the cache, in the flush, and in the batch on the wire — three places it has no business being.

**On the client, one ride is *focused*.** The map, the stats and the car head all render the focused ride; the others run in the background contributing nothing but their own inbound batches. `IRideSessionState` (§4.6) therefore grows a little:

```csharp
public interface IRideSessionState
{
	RideSnapshot Current { get; }              // the focused ride
	IReadOnlyList<RideSummary> OtherLiveRides { get; }
	Task FocusAsync(Guid rideId);
	// … unchanged: Watch, StartRecordingAsync, StopRecordingAsync, FlagStoppedAsync
}
```

The car story needs nothing new: §4.6 already specified a second screen for picking which ride, because template depth is capped at two. That screen now has a reason to exist beyond choosing among rides you might join.

**One recording, several rides.** Recording is a device activity, not a ride activity — a rider records one track and may attach it to each ride's summary afterwards (`GroupRideMember.RecordedTrackId` is per membership, so this already works).

**No longer bounded by a cap.** `Ride:MaxConcurrentLiveRidesPerUser` was enforced at the start transition and nowhere else, so with no transition it has no enforcement point and is deleted (v0.32). This section already anticipated that: *"if that turns out to matter, the place to fix it is the publish fan-out, not the start transition"*. The honest bound now is how many adventures a rider has deliberately turned sharing on for, which is a decision they made rather than a number the server picked.

> **Whose count is checked** *(decided in SRV-24)*: the **organiser performing the transition**, not every member. Counting all members would let one rider who is already in five live rides block a ride for fifty other people — a denial of service dressed as a quota. The cost this cap protects is a rider's own downlink, so the actor-scoped reading is the defensible one. The consequence, stated plainly: a member can still end up in more live rides than the cap by joining rides other people start. If that turns out to matter, the place to fix it is the publish fan-out, not the start transition.

**The data budget multiplies on the downlink** (§10.3). One 5 s batch per live ride means three live rides is three times the inbound traffic — the uplink stays flat, which is the half that matters for battery. Non-focused rides can drop to a slower batch cadence; that is an optimisation, not a v1 requirement, and it is recorded as such rather than designed now.

### 5.8 What members may add — the organiser's content switches

The ride creator decides whether members may contribute markers, comments and photos, and can change that **at any time** during the ride's life.

| Switch | Default | Off means |
|---|---|---|
| `AllowMemberMarkers` | on | Only the organiser and leaders may add markers (§16) |
| `AllowMemberComments` | on | Only the organiser and leaders may post; everyone can still read, react and vote |
| `AllowMemberPhotos` | on | No photo attachments from members, on comments or markers. Text still works |

**Defaults are on.** A group ride is a group of people the organiser chose (§5.2); starting from silence would be a strange default for a product whose point is riding together. The switches exist for the ride where they are needed — a large public charity ride, or one that has gone sideways.

**Photos are their own switch, not a consequence of the comment switch**, because they are the expensive and awkward half: storage (§16.4), moderation (§17.7), and the one thing an organiser is most likely to want to stop while leaving conversation alone.

**Turning a switch off deletes nothing.** It stops new content; existing markers and comments stay exactly where they are. This is the same rule as §7.3's profile sharing, and for the same reason — revoking a permission is not an instruction to destroy what was already permitted. An organiser who wants something gone deletes it explicitly (§17.7).

**Never restricted:** the organiser and leaders themselves, and **reactions and poll votes**. Reactions carry no free text, no image and no storage cost worth naming, and switching off the ability to answer a poll would break the poll rather than moderate it.

**Enforcement is server-side; the UI merely agrees.** A member whose permission was revoked mid-compose gets a `403` with a distinguishable reason, and the client disables the compose surface on the `RidePermissionsChanged` hub message. The message is a courtesy so the UI does not lie; the check is what makes it true.

**`AllowMemberPhotos` is enforced on the *attach*, not on the upload** *(settled in SRV-28)*. A photo is a standalone resource with no ride context — it is taken at the top of a hill and uploaded whenever there is signal, which is the whole reason §16.4 separates the two requests. `POST /photos` therefore has no ride whose switch it could consult, and the check lands where the image is bound to something in that ride: `PATCH /markers/{id}/photo`, and a comment carrying a `photoId`. The consequence worth knowing is that a member can still *upload* against their own quota while photos are off for a ride; they simply cannot attach it there.

**All three checks go through one method**, `RideContentPermissions.Allows`, for the reason SRV-21 learned the hard way about the four delete paths: markers, comments and both photo attachments are separate write paths carrying the same obligation, and four copies of a rule is how one of them eventually stops applying it. The switch arm for an unrecognised content type **throws** rather than defaulting permissive — a new kind of content that nobody wired a switch to must not be allowed everywhere by omission (the same mistake the breach-outcome switch §7.2 used to carry already made this project pay for once, before v0.23 removed that check).

```csharp
Task RidePermissionsChanged(RidePermissions permissions);   // → IRideClient, §5.3
```

**Changes are visible in the thread**, as a system line — *"DaveH turned off photos"*. A permission that changes silently reads as a bug to whoever just lost the button, and support questions about "the camera icon disappeared" are entirely avoidable.

### 5.9 Tests for §§5.6–5.8

Consent and sharing are the parts of this product where a defect is a privacy incident rather than a bug, so they are written first:

```
Join_DismissedSharingPrompt_LeavesShareLocationFalse
Join_SharingDeclined_MemberSeesOthersButPublishesNothing
Publish_ByNonSharingMember_IsRejectedAndStoresNothing
Sharing_TurnedOff_DeletesPersistedRowImmediately
Sharing_TurnedOffMidRide_RemovesPinForOtherMembers
MemberList_DistinguishesNotSharingFromNoSignal

SharingOff_DeletesThePositionButKeepsTheThread
SharingOff_DeletesThePositionButKeepsTheMarkers
Delete_WhileSomebodyIsSharing_TakesTheirPositions
NightlySweep_DeletesIdlePositionsAndClearsTheirSharing
NightlySweep_DryRun_CountsIdlePositionsAndDeletesNothing
Rehydrate_SkipsPositionsOlderThanStalenessWindow
Organiser_CannotEnableSharingOnBehalfOfAMember

Publish_MemberOfThreeLiveRides_WritesToAllThree
Publish_SharingInRideAOnly_StoresNoRowForRideB
Publish_OneMessage_ProducesNoAdditionalUplinkPerRide
Focus_SwitchingRides_DoesNotInterruptRecordingOrPublishing
LiveRideCap_ExceedingMaxConcurrent_IsRejectedAtRideStart

Permissions_MarkersOff_MemberPostReturns403
Permissions_CommentsOff_MemberMayStillReactAndVote
Permissions_PhotosOff_TextCommentStillSucceeds
Permissions_TurnedOff_ExistingContentIsUntouched
Permissions_OrganiserIsNeverRestrictedByOwnSwitches
Permissions_Changed_IsBroadcastAndRecordedInTheThread
```

---

## 6. Web App (ASP.NET Core)

### 6.1 Responsibilities
1. **Public surface:** landing page, shared-ride links, shared-track pages (SEO-friendly, works without the app installed).
2. **Ride planning on a big screen** — GPX import, route drawing, invites, and handling join requests.
3. **Track editing** — the only place a track can be edited: trim the start, trim the end, remove a span in the middle (§15.5). It needs a mouse and a big map, which is why it is web-only rather than a phone screen.
4. **Live spectator view** — read-only map of an in-progress group ride for people not riding (family tracking the group, event organiser). See §13 Q3.
5. **Account management** — signed-in devices, password change, add/verify a recovery email, data export, deletion (§7.10).
6. **Auth landing pages** — email confirmation and password reset links must work in a browser even for app-only users (§7.7).
7. **The AGPL §13 source offer** — every page footer, public and authed, carries the licence, a link to the source, and the commit the server is running (§14.6.2). A licence obligation the web tier discharges on behalf of the whole deployment.

### 6.2 Stack

| Concern | Choice | Rationale |
|---|---|---|
| Rendering | **Blazor WebAssembly** for the signed-in app, from the shared `DLR.UI` library; **static SSR** for public and auth-landing pages | One component set with the mobile app (§18); SSR keeps the public pages fast and indexable |
| Map | **MapLibre GL JS + OpenStreetMap tiles** via a small JS interop wrapper (§4.5) | Free and hosted by someone else, which is the right trade for getting onto a map early. *"To begin with"* is doing real work in that sentence — OSM's usage policy does not cover a public launch (§13 Q26). Since v0.24 the phone runs the same **component and the same module** — one base map on every host, and no credential on any of them |
| Auth | ASP.NET Core Identity — **HttpOnly refresh cookie** for web, JWT + rotating refresh in the keychain for mobile | Full design in **§7**; the divergence and its reasoning in §18.5 |
| Data | **PostgreSQL** + EF Core | Free, portable, JSONB for flexible bits; PostGIS optional |
| Live positions | Raw **Npgsql** (§5.5) | Deliberate second idiom — see below |
| Track storage | Compressed blobs (encoded polyline / protobuf, gzip) on the **blob volume** behind `IBlobStore` (§9.1) | Track points are write-once, read-whole — a row per point is a mistake. Editing rewrites the blob whole (§15.5); it never mutates points in place. The interface keeps S3-compatible storage a registration change if the disk ever argues |
| Migrations | EF Core migrations applied on startup behind a flag | No extra deploy tooling |

**Two data-access idioms, on purpose.** EF Core owns all domain work; the 10 s position flush uses raw Npgsql. Change tracking is wasted effort on a hot loop, and the `ON CONFLICT … WHERE excluded.recorded_utc > …` guard has no first-class EF expression — attempting it in EF ends in raw SQL anyway, just less honestly. The hand-written SQL is confined to `PositionWriter`.

> **The Blazor Server note that used to sit here is resolved rather than answered.** Through v0.15 this warned that Blazor Server holds a live websocket per browser tab, pinning the web tier to one instance, and suggested converting the authed pages to WASM if that ever became a problem. **v0.16 did exactly that, for a different reason** — sharing one component set with the mobile app (§18) — and the hosting benefit comes along free: no circuits, no sticky sessions, and a bundle Caddy can serve and cache like any other static file (§9.1, §9.2).

### 6.3 API sketch

```
POST   /api/v1/tracks                          upload track (idempotent on client guid)
GET    /api/v1/tracks/{id}                     metadata + polyline
PATCH  /api/v1/tracks/{id}                     { name } — rename; carries no version (§15.1)
DELETE /api/v1/tracks/{id}                     delete the track, its markers and its blobs
GET    /api/v1/tracks/{id}/points              full-resolution points — the editor's source (§15.5)
GET    /api/v1/tracks/{id}/gpx                 GPX export
POST   /api/v1/tracks/{id}/share               → share link/visibility
POST   /api/v1/tracks/import                   multipart GPX; ?dryRun=true previews (§15.3)
POST   /api/v1/tracks/{id}/edit                { version, removals: [[from,to)] }   (§15.5)
POST   /api/v1/tracks/{id}/edit/undo           within the undo window (§15.6)
DELETE /api/v1/tracks/{id}/previous-version    purge the retained original now (§15.6)
POST   /api/v1/tracks/{id}/markers             add a marker to a track (§16)
POST   /api/v1/group-rides/{id}/markers        add a marker to a ride — any member
PATCH  /api/v1/markers/{id}                    author or organiser
DELETE /api/v1/markers/{id}                    author or organiser
POST   /api/v1/markers/{id}/report             UGC report → ContentReport (§17.7)
GET    /api/v1/group-rides/{id}/comments       thread, cursor-paginated, pinned first (§17)
POST   /api/v1/group-rides/{id}/comments       { body?, photoId?, poll? }
GET    /api/v1/tracks/{id}/comments            a shared route's thread — same shape (§19.2)
POST   /api/v1/tracks/{id}/comments            { body?, photoId? } — any signed-in rider
GET    /api/v1/tracks/{id}/rating              { average?, count, mine? }        (§19.1)
PUT    /api/v1/tracks/{id}/rating              { stars } — 1..5, replaces the caller's
DELETE /api/v1/tracks/{id}/rating              withdraw; never a zero (§19.1)
PATCH  /api/v1/comments/{id}                   author, within the edit window
DELETE /api/v1/comments/{id}                   author or organiser
POST   /api/v1/comments/{id}/pin               organiser or leader; { pinned }
PUT    /api/v1/comments/{id}/reaction          { reaction } — null clears
POST   /api/v1/comments/{id}/votes             { optionIds }
POST   /api/v1/comments/{id}/close-poll        author or organiser
POST   /api/v1/comments/{id}/report            → ContentReport (§17.7)
POST   /api/v1/photos                          multipart → { photoId }; re-encoded, stripped
GET    /api/v1/photos/{id}                     full image
GET    /api/v1/photos/{id}/thumb               callout thumbnail
POST   /api/v1/group-rides                     create
GET    /api/v1/group-rides/{id}                detail + members + route
POST   /api/v1/group-rides/join                { code } → join or create request
POST   /api/v1/group-rides/{id}/join-requests  request to join (path 2, §5.2)
GET    /api/v1/group-rides/{id}/join-requests  organiser: pending list
POST   /api/v1/join-requests/{id}/approve      organiser
POST   /api/v1/join-requests/{id}/decline      organiser, { block? }
DELETE /api/v1/group-rides/{id}/members/{uid}  organiser: remove a member
DELETE /api/v1/group-rides/{id}                owner: the only way to finish one (§5.6)
PUT    /api/v1/group-rides/{id}/permissions    owner/leader: { markers, comments, photos } (§5.8)
GET    /api/v1/group-rides/{id}/positions      snapshot (awaits cache ready)
PUT    /api/v1/group-rides/{id}/route          attach/replace planned route
PUT    /api/v1/group-rides/{id}/sharing        { shareLocation } — false deletes the row
GET    /api/v1/me/export                       full data export — a ZIP: export.json, tracks
                                               as GPX, photographs as files (§16.6)
DELETE /api/v1/me                              account + data deletion; body carries the
                                               current password, and blobs go explicitly (§16.6)
GET    /api/v1/about                           licence, source URL, running commit — anon (§14.6.2)
WS     /hubs/ride                              SignalR
```

Auth endpoints are listed separately in §7.14. Cursor-paginated lists. Problem Details (RFC 7807) for errors.

---

## 7. Identity, Registration & Login

### 7.1 Scope and the shaping decisions

**An account is a unique username and a password. Nothing else is required.** Email is optional, used for recovery and as the escalation lever when one IP registers unusually many accounts (§7.8). No social providers and no passwordless in v1.

**Sessions do not expire.** A person who registers or signs in on a device never signs in again on that device (§7.4). Time-based expiry is replaced by deleting genuinely empty dormant accounts (§7.11) — better targeted, since it only removes accounts holding nothing.

**We do not use `MapIdentityApi<TUser>()`.** .NET ships it and it would give `/register`, `/login`, `/refresh`, `/confirmEmail`, `/forgotPassword` and `/resetPassword` for almost free — but its bearer tokens are stateless Data Protection payloads, so **they cannot be revoked server-side**. For an app that broadcasts a user's live location, "sign out my stolen phone, now" is a requirement, not a nice-to-have. So:

| Use Identity for | Hand-roll |
|---|---|
| User store, `UserManager`/`SignInManager` | `/auth/token` endpoint issuing JWT access tokens |
| Password hashing (PBKDF2-HMAC-SHA512, current iteration defaults) | Opaque, hashed, **revocable, permanent refresh tokens** (§7.4) |
| Lockout, security stamp, unique normalised username | Session/device listing and revocation (§7.10) |
| Email-confirmation and password-reset token providers | Registration throttle and rate-limit policies (§7.8) |
| Cookie auth for the web app (§7.5) | Inactivity cleanup (§7.11) |

Password hashing, confirm/reset token generation, and lockout are exactly the parts you should never write yourself; token lifetime and revocation are exactly the parts this app needs to control.

### 7.2 Registration

```
username + password   [+ optional email]
	→ username availability check
	→ policy: >= 6 chars, one each of upper / lower / digit (§7.2)
	→ per-IP ladder: 4th+ account from this IP today REQUIRES an email (§7.8)
	→ UserManager.CreateAsync
	→ if email supplied: send 24 h confirmation link (§7.7)
	→ issue access + permanent refresh token — signed in immediately
```

**One field is the account.** `UserName` is the login identifier **and** the name other riders see on the map. There is no separate display name — nothing to default, nothing to keep in sync, and no decision about which one to render.

| Field | Unique | Required | Notes |
|---|---|---|---|
| `UserName` | ✅ | ✅ | Login identifier *and* map label. 3–20 chars, ASCII `A–Z a–z 0–9 - . _`, stored as typed, unique case-insensitively, reserved names blocked |
| `Email` | ✅ *when present* | ❌ | Recovery only; also required by the IP ladder (§7.8) |

The cost is that **map names are unique**: two riders called Dave cannot both be `Dave`, so the second becomes `DaveH` or `Dave2`. That is the familiar handle model, and it buys a security property described below.

**The username is permanent.** It is chosen once at registration and can never be changed — no endpoint, no settings screen, no support path. That makes a handle a stable identity for everyone who rides with that person, and it deletes a surprising amount of work: nothing to propagate when a name changes, no stale `unm` claim sitting in an already-issued access token, no old-name reservation window, and no impersonation-by-recycling.

Because the choice is irreversible, the app **confirms the username before creating the account**:

> You're registering as **DaveSmith**.
>
> This is permanent — it cannot be changed later, and it is the name other riders see on the map.

That confirmation is not ceremony. A typo is forever, and this is the only chance to catch one.

Immutability also pays off on the client: a username can be **cached and denormalised freely** — onto a local SQLite member row, into a stored ride summary, into an exported GPX — because it can never go stale. No invalidation logic anywhere.

**Case is preserved; uniqueness is not case-sensitive.** Identity stores `UserName` exactly as typed and `NormalizedUserName` upper-cased for the unique index — so `DaveSmith` renders as `DaveSmith` on a map pin while `davesmith` cannot also register. `AllowedUserNameCharacters` **must therefore include uppercase**; the lowercase-only charset that suits a hidden login handle would reject `DaveSmith` outright, which is unacceptable for a name people read (§7.7).

**ASCII-only is now a security choice, not merely simplicity.** Because the unique handle is also the visible label, allowing Unicode would enable homoglyph impersonation — `DaveSmıth` with a dotless i reads as `DaveSmith` on a pin at a glance, and the two are distinct strings so both can exist. Restricting to ASCII letters, digits, `-`, `.` and `_` removes that whole class of attack. Reserved names — `admin`, `support`, `help`, `dlr`, `root`, `no-reply`, `system` and similar — are blocked for the same reason: nothing should be able to pose as the service on someone else's map.

**The registration screen must state the recovery trade-off plainly**, not in fine print:

> **No email means no recovery and no warning.**
>
> If you forget your password or lose this device, this account and everything in it is gone — a password cannot be reset without an email address.
>
> An unused account holding no saved rides is **deleted after 6 months**, and with no email address we cannot warn you before it happens.
>
> You can add an email any time in Settings.

Both halves of that notice are consequences the user cannot discover on their own, so they are stated at the only moment that matters. The second half exists because the 150-day inactivity warning is an *email* (§7.11) — an account with no address is deleted silently, and that is only fair if it was disclosed up front.

One wording precision worth keeping in the UI copy: deletion requires the account to be **empty as well as idle** (§7.11). "Holding no saved rides" is the honest short form — an account with even one recorded ride is never auto-deleted, so promising otherwise would be both inaccurate and needlessly alarming. Copy says *6 months*; the implementation constant is 180 days.

The app also prompts to add a recovery email at two natural moments — after the first ride is saved, and when creating a group ride — because those are the points at which the account starts holding something worth losing, and the point at which it stops being deletion-eligible.

**Username enumeration is unavoidable and accepted.** Uniqueness means registration has to tell you whether a name is taken. This is a deliberate reversal of the enumeration-resistance stance in §7.8 for *this one endpoint*: a username is a public handle shown on the map, not a private identifier like an email. Login stays generic ("invalid username or password"), and forgot-password stays generic on the email address.

**Password policy** *(revised in v0.22, narrowed again in v0.23, both at operator request).* Minimum 6 characters and one each of uppercase / lowercase / digit; no non-alphanumeric requirement. **There is no breached-password check** — v0.23 removed it, so this list is the entire policy and registration makes no third-party call.

The pre-v0.22 stance argued the opposite — length over composition — because composition rules do not measure strength; they measure compliance, and what people comply with is `Passw0rd!`, a string that satisfies every rule and is in every breach corpus. That argument still stands as context. The v0.22 trade-off accepted pushing toward that predictable shape in exchange for a form where every rejected password comes back with a specific, actionable message ("must have at least one uppercase letter") rather than a generic *"password too weak"*, and leaned on the Pwned Passwords lookup to stop the shape actually landing in the database. v0.23 removed that lookup as unnecessary for an application whose security impact the operator judges not to be significant, which means the predictable shape now lands: `Passw0rd1` is an accepted password. Worth knowing when weighing it, because for an email-less account the password is the *only* credential and there is no reset path.

**The sign-up form is where the rider is told this** *(v0.23)*. The Register password box carries a four-segment strength meter that states its verdict as a word — Weak / Fair / Good / Strong — and names any rule still unmet ("still needs an uppercase letter, a digit"). It is scored on length and character variety, with repetition discounted; a passphrase reaches the top of the bar without a symbol in it, because §7.2 does not require one. It is **advice, never a gate**: the server decides what it accepts, and nothing in the client disables the button. Both password fields — Register and Sign in — also carry a reveal button. Revealing is per-field and per-visit: switching tabs re-masks.

**No email gate on group rides.** v0.4 required a confirmed email to create or join a ride; v0.5 removes that entirely, because the organiser now controls both entry paths (§5.2) — a stronger guarantee than email verification. The one remaining use of confirmation is the IP ladder in §7.8.

### 7.3 Optional profile fields and sharing

Three optional fields, each with an independent sharing switch that is **off by default**:

| Field | Stored | Shared by default | Verified | Also used for |
|---|---|---|---|---|
| Display name | optional | ❌ off | — | nothing else |
| Phone number | optional | ❌ off | ❌ **never** | nothing else |
| Email address | optional | ❌ off | ✅ 24 h link | password recovery (§7.7), IP ladder (§7.8) |

**Recording and sharing are separate decisions.** A value can be stored and not shared — that is the default for all three. Turning sharing off never deletes the value, which matters most for email: it remains the recovery address even while hidden from other riders.

**The map label does not change.** Pins and the position batch always carry the immutable username (§7.2). A shared display name appears in the ride member list *beside* the username, never instead of it. That preserves v0.7's property that a username can be cached and denormalised forever with no invalidation — display names are editable, usernames are not — and it stops a rider labelling themselves `RideLeader` on someone else's map.

**Sharing is ride-scoped and revokes itself.** A shared field is visible to riders who are **currently co-members of a group adventure** with the owner, surfacing in that adventure's member list. Leaving, being removed by the organiser, and the adventure being deleted all end access — the same lifecycle as live position sharing (§5.5), for the same reason. Co-membership is now the whole of the rule: v0.15 to v0.31 also ended it the moment the ride was `Completed`, deliberately not following the position wind-down, and neither of those states exists any more (§5.1). A rider who has never joined an adventure has no audience at all, whatever their switches say.

**The phone number is not verified and is not a recovery path.** SMS verification needs a paid provider the €4 budget (§9) does not want, and an SMS reset path would add an account-takeover surface for no benefit. Identity's `PhoneNumber` column is reused, but **`PhoneNumberConfirmed` stays permanently `false` and must never be used as a gate** — a future contributor who sees that column will otherwise assume verification happened somewhere. The field is a convenience for mates on a ride, nothing more; tapping to call a rider mid-ride is the obvious use (§5.4).

**Sharing an email exposes the account's recovery address.** Worth one line of UI copy, because a rider who shares it is telling a ride full of people which mailbox to attack in order to reset the password. Not a reason to forbid it — a reason to say so plainly next to the switch.

#### Default-off has to be structural, not conventional

Three booleans defaulting to false are trivial to get right at creation and easy to get wrong on a read path. One forgetful DTO mapper leaks a phone number, and there is no way to un-leak it. So the defence is structural:

- **No endpoint ever projects the user entity.** Shared fields reach the wire through exactly one factory, which cannot be called without stating the viewer's relationship to the owner. *(A consequence worth knowing before it costs somebody an hour: `private init` also stops `System.Text.Json` **deserialising** a `SharedProfile`. A test that reads a response into one gets an all-null object and therefore passes every "is no longer visible" assertion it makes, whatever the server actually sent. Read the wire form instead — SRV-21's `SharingTests` uses a local mirror record.)*

```csharp
public sealed record SharedProfile
{
	public string? DisplayName { get; private init; }
	public string? PhoneNumber { get; private init; }
	public string? Email { get; private init; }

	// The only way to build one. A viewer with no shared active ride gets Empty.
	public static SharedProfile For(AppUser owner, bool viewerSharesActiveRide) =>
		!viewerSharesActiveRide ? Empty : new SharedProfile
		{
			DisplayName	= owner.ShareDisplayName ? owner.DisplayName : null,
			PhoneNumber	= owner.SharePhoneNumber ? owner.PhoneNumber : null,
			Email		= owner.ShareEmail       ? owner.Email       : null
		};

	public static readonly SharedProfile Empty = new();
}
```

- **"Not shared" and "not recorded" must be indistinguishable on the wire.** Serialise with `JsonIgnoreCondition.WhenWritingNull` so an omitted property covers both. Emitting `phone: null` for withheld while omitting it for absent would leak the *existence* of a phone number — a small leak, and a completely avoidable one.
- An architecture test asserts no API surface returns `AppUser` (§10.4).

#### UI

Settings → **Profile**: each field is a value input plus a *"Share with riders in my group rides"* switch, off, with the scope stated inline rather than buried in a help page. A **"what other riders see"** preview renders the exact `SharedProfile` the server would emit — cheap to build, and the only way a user can confirm for themselves that the defaults are what they believe.

### 7.4 Login and the mobile token model

```
POST /api/v1/auth/token   { grantType: "password", username, password }
	→ 200 { accessToken, expiresIn: 900,
			refreshToken,
			user: { id, userName, hasEmail, emailConfirmed } }
```

| Token | Form | Lifetime | Stored |
|---|---|---|---|
| Access | JWT, HS256, claims `sub`, `unm`, `dev`, `jti`, `rst`? | **15 min** | Memory only |
| Refresh | 256-bit opaque random, base64url | **Effectively permanent** | `SecureStorage`; **SHA-256 hash** server-side |

The refresh token is deliberately *not* a JWT. It must be revocable, and it is only ever presented to one endpoint, so there is nothing to gain from making it self-describing — and a real cost if it leaks into a log.

**Sessions never expire.** `expires_utc` is retained in the schema and set to `issued + Auth:RefreshTokenYears` (default **10 years**) rather than made nullable — queries and indexes stay simple, and there is still an escape hatch if a blanket expiry is ever needed. There is no sliding-window logic and no re-authentication prompt.

**The exhaustive list of things that *do* force a fresh sign-in** — worth stating, because "never log in again" needs its exceptions written down:

| Trigger | Scope |
|---|---|
| User signs out | That device |
| User revokes a device in Settings (§7.10) | That device |
| Password reset completed (§7.7) | **All** devices |
| Refresh-token reuse detected outside the grace window | **All** devices in that family — theft response |
| `SecureStorage` decrypt failure (§7.4, below) | That device |
| Account deleted by inactivity cleanup (§7.11) | All — and the account is gone |

**Rotation with reuse detection.** Every refresh issues a new token and marks the old one used, within a `family_id` chain:

```
POST /api/v1/auth/token   { grantType: "refresh", refreshToken }

	hash → look up row
	├─ not found / revoked            → 401
	├─ used_utc IS NULL               → rotate: mark used, issue successor
	└─ used_utc IS NOT NULL           → REPLAY
		 ├─ within grace window AND successor unused
		 │     → return the same successor (idempotent)
		 └─ otherwise
			   → revoke the entire family, 401, security email if address known
```

**The grace window is not optional.** A mobile client that fires two requests, gets two 401s, and refreshes twice will legitimately replay its own token and — with naive rotation — revoke its own session and dump the user at the login screen mid-ride. With permanent sessions this is now the *most likely* way a user is ever logged out, so both mitigations are required:

- **Client side:** single-flight refresh. One shared `Task`, every other caller awaits it. Implemented once in `DLR.App/Auth/AuthHandler`.
- **Server side:** a 10-second idempotency window keyed on `successor_id`. A replay inside the window returns the same successor rather than treating it as theft.

Outside that window, a reused token really does mean the refresh token exists in two places, and revoking the family is the correct, aggressive response.

**Storage at rest.** MAUI `SecureStorage` → iOS Keychain / Android Keystore. Two platform realities to handle rather than discover:

- Use the *this-device-only* Keychain accessibility class so a device restored from another phone's backup does not carry a working permanent token onto new hardware.
- Android `SecureStorage` can throw on decrypt after a backup/restore or key invalidation. **Treat a decrypt failure as "signed out"**, never as a crash — wrap every read. With permanent sessions this is the main residual re-login path, and for an email-less account it is unrecoverable, which is another reason the §7.2 warning matters.

**Signing key.** ≥ 32 bytes, from an environment variable or Docker secret, never `appsettings.json`. Include `kid` in the JWT header and accept two keys during a rotation window, so rotating the key does not invalidate every access token at once. Refresh tokens are unaffected by rotation — they are database rows, not signed blobs.

### 7.5 Web sessions (Blazor WebAssembly)

*(Rewritten in v0.16 — the web client is WASM, not Blazor Server. Full reasoning in §18.5.)*

The browser holds its **refresh token in an `HttpOnly`, `Secure`, `SameSite` cookie** and its access token in memory only. The WASM client is otherwise an ordinary API client: same `/auth/token` endpoint, same rotation and reuse detection (§7.4), same device list and revocation (§7.10).

**Why a cookie rather than `localStorage`.** §7.4 makes refresh tokens effectively permanent, so an XSS bug in a browser build would hand over an account outright rather than a session. A token JavaScript cannot read is the difference between a bad day and an unrecoverable one. The cost is CSRF exposure on exactly one endpoint — the token endpoint — which gets antiforgery treatment accordingly.

**Web sessions expire; mobile sessions still do not.** `Auth:WebSessionDays`, default **30**, sliding. v0.5's "sign in once, never again" was reasoned about a personal phone behind a device passcode, and a browser is frequently a shared computer. Applying the conclusion outside the argument that produced it would be the mistake.

**The trap that remains, in a new form:** login, logout and registration still **must** be static-rendered form posts or minimal-API endpoints issuing a redirect, because a cookie cannot be set from inside an already-running WASM client any more than it could over a live Blazor Server circuit. It fails the same confusing way — the sign-in appears to work, the next request is anonymous.

**Revocation reaches an open tab through the refresh cycle, not a circuit teardown.** Blazor Server's `RevalidatingServerAuthenticationStateProvider` — which is what v0.15 specified here — has nothing to revalidate in WASM. Instead, a security-stamp change (password reset, device revocation) makes the next refresh fail, and a custom `AuthenticationStateProvider` drops the tab to signed-out when it does. The exposure window is therefore the access-token lifetime, **15 minutes at most** (§7.4), which is the same bound the mobile app has always had.

### 7.6 SignalR authentication

Three things specific to authenticating a *long-lived* connection:

1. **Token transport.** Browsers cannot set headers on a WebSocket handshake, so SignalR sends the token as `?access_token=`. The server lifts it via `JwtBearerEvents.OnMessageReceived` — **scoped to the `/hubs/ride` path only.** Accepting query-string tokens globally would scatter credentials through access logs and referrers.

2. **Leave `CloseOnAuthenticationExpiration` at its default of `false`.** SignalR validates the token at connect time; with expiration-closing enabled, a 15-minute access token would kill a 2-hour ride's connection every quarter hour. The MAUI client sets `AccessTokenProvider` so that *reconnects* pick up a freshly rotated token automatically. Recorded here so nobody later "fixes" the apparent oversight.

3. **Authentication is not authorization.** `JoinRide(rideId)` must verify the caller is an **admitted member** of that ride (§5.2). A valid token proves who the user is, not which rides they belong to — without the membership check, any authenticated user could subscribe to any ride's live positions by guessing an id. This check is now the *only* thing standing between an account and a stranger's location, since the email gate is gone, so it is not optional and it is tested directly (`Hub_JoinRide_NonMemberIsRejected`, `Hub_JoinRide_PendingRequesterIsRejected`).

> **Why the second test is not redundant** *(SRV-23)*: checking `group_ride_join_request` instead of `group_ride_member` still rejects a total stranger, so the first test stays green while every pending requester — precisely the people the organiser has *not* decided about — is admitted to the live map. The refusal is also deliberately indistinguishable from a ride that does not exist, for the reason §5.2 gives about join codes: a ride id is shareable, and a distinguishable error turns the method into an oracle for who is in which ride.

### 7.7 Password reset and recovery

Reset requires a **confirmed email**. An account without one cannot be recovered — that is the trade-off §7.2 surfaces at registration.

- `POST /auth/forgot-password` takes an email address and **always** returns `202`, whether or not it exists (§7.8).
- Identity's `GeneratePasswordResetTokenAsync`, lifetime **1 hour**.
- Links are `https://` universal links with a web fallback page, so reset works whether or not the app is installed — the reason §6.1 needs auth landing pages.
- **On successful reset: update the security stamp and revoke every refresh-token family.** Every device signs in again. This is the one place permanent sessions are deliberately broken, because if the reset was triggered by a compromise, leaving other sessions alive defeats the point.
- `change-password` (authed, requires the current password) revokes *other* families but keeps the current device signed in.
- Adding an email later (`POST /auth/email`) sends a 24 h confirmation link and, once confirmed, enables recovery from that point on.

#### Two lifespans need two token providers

Email confirmation is valid **24 hours**; password reset **1 hour**. These cannot both come from configuration, because **`DataProtectionTokenProviderOptions.TokenLifespan` is global** — it governs *every* `DataProtectorTokenProvider` at once (confirm email, reset password, change email). Setting it to one hour for reset silently drops confirmation to one hour too, and nothing warns you.

> **Implemented differently, and the reason is §10.4.** The sketch below subclasses Identity's `DataProtectorTokenProvider`. That type reads `DateTimeOffset.UtcNow` directly — ASP.NET Core Identity 10 takes **no `TimeProvider` anywhere**, the same limitation §7.8's lockout runs into — so the two lifespans could be asserted as configuration but never as behaviour, and `ConfirmEmail_TokenJustUnder24Hours_IsAccepted`, `ConfirmEmail_TokenPast24Hours_IsRejected` and `ResetPassword_TokenPast1Hour_IsRejected` are all boundary tests. Against the framework provider they could only have been written as sleeping tests or not at all.
>
> So `DlrTokenProvider` implements `IUserTwoFactorTokenProvider<AppUser>` directly and takes the project's clock. **Nothing cryptographic is reinvented**: the payload is sealed with `IDataProtector` — the same vetted primitive Identity uses — and carries the issue time, the user id, the purpose and the security stamp. The shape below is otherwise unchanged: two providers, two lifespans, `DlrPasswordReset` selected by name.
>
> The purpose ends up guarded **twice**, in the protector's purpose chain and again inside the payload. Each was verified to stop a cross-purpose token with the other removed. The chain is the stronger — a foreign token fails to decrypt rather than being decrypted and then judged — and the payload check is what survives somebody later collapsing the protector to a single purpose string.

The sketch that shape came from:

```csharp
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
	public PasswordResetTokenProviderOptions()
	{
		Name = "DlrPasswordReset";
		TokenLifespan = TimeSpan.FromHours(1);
	}
}

public sealed class PasswordResetTokenProvider<TUser>(
		IDataProtectionProvider protection,
		IOptions<PasswordResetTokenProviderOptions> options,
		ILogger<DataProtectorTokenProvider<TUser>> logger)
	: DataProtectorTokenProvider<TUser>(protection, options, logger)
	where TUser : class;
```

```csharp
// Global lifespan — email confirmation. Explicit even though 24 h is the
// framework default, so a future tweak cannot silently change it.
services.Configure<DataProtectionTokenProviderOptions>(o =>
	o.TokenLifespan = TimeSpan.FromHours(24));

services.AddIdentity<AppUser, IdentityRole<Guid>>(o =>
	{
		o.User.RequireUniqueEmail = false;		// null emails must be allowed (§7.13)
		o.User.AllowedUserNameCharacters =			// uppercase is required: the
			"ABCDEFGHIJKLMNOPQRSTUVWXYZ" +			// username is the map label
			"abcdefghijklmnopqrstuvwxyz0123456789-._";
		o.Password.RequiredLength = 10;
		o.Password.RequireNonAlphanumeric = false;
		o.Tokens.PasswordResetTokenProvider = "DlrPasswordReset";
	})
	.AddTokenProvider<PasswordResetTokenProvider<AppUser>>("DlrPasswordReset");
```

`ResetPassword_LifespanIsIndependentOfConfirmationLifespan` (§7.15) exists specifically to catch a later refactor collapsing the two back into one setting.

Note these tokens are **stateless** — validity derives from the security stamp and the protected payload, not a database row. A token is invalidated early by anything that rolls the security stamp (a completed reset, a password change), but there is no "used" flag to inspect and no way to expire one on demand.

### 7.8 Abuse resistance

**Enumeration.** Username enumeration is unavoidable (§7.2). Everything else stays closed:

| Situation | Response |
|---|---|
| Register with a taken **username** | Explicit "that username is taken" — unavoidable, and a username is a public handle |
| Register with an existing **email** | Generic success. Email the existing owner: "someone tried to register with your address" |
| Login, unknown username | Generic "invalid username or password" — and still run a dummy password verification, so timing does not leak existence |
| Forgot password, unknown email | `202`, no email sent |

**The per-IP registration ladder.** Registrations from one IP address in a rolling 24 hours:

| Accounts today from this IP | Requirement |
|---|---|
| 1–3 | Username + password only |
| 4 and beyond | **An email address, which must be confirmed** before the account can create or join rides |

There is deliberately **no hard cap**. Carrier-grade NAT means an entire mobile network can present as one address, so a flat block would silently refuse legitimate signups on mobile data with no path forward. The ladder gives a real person an obvious next step — verify an email — while an abuser needs N distinct working mailboxes. Crossing the threshold also logs an alert.

Accounts created past the threshold carry `requires_email_confirmation = true`. The access token gains a `rst` ("restricted") claim while `requires_email_confirmation AND NOT email_confirmed`, and a `NotRestricted` policy guards ride creation and joining. Restricted accounts can still record solo — the restriction targets the social surface, which is what abuse would be after.

```csharp
options.AddPolicy("NotRestricted", policy =>
	policy.RequireAuthenticatedUser()
		  .RequireAssertion(ctx => !ctx.User.HasClaim(c => c.Type == "rst")));
```

**The counter must be persistent, not the in-memory rate limiter.** `AddRateLimiter` partitions live in process memory: they reset on every deploy and are per-instance, so an attacker just waits for a restart. The ladder therefore counts rows:

```sql
SELECT count(*) FROM asp_net_users
WHERE created_by_ip = @ip
	AND created_utc > now() - interval '24 hours';
```

with `CREATE INDEX ix_users_created_ip ON asp_net_users (created_by_ip, created_utc)`. The registration IP is personal data, so the nightly job (§7.11) **nulls `created_by_ip` after 30 days** — long enough to be useful for throttling, short enough not to be a standing record of where people signed up.

**Conventional rate limits** still apply on top, in memory — but *not* via `AddRateLimiter`, and the table below is the reason. Three of these rows are keyed on a **username**, an **email address** or a **device**, all of which arrive in the request body; a middleware partitioner sees the URL and the connection and nothing else, so it could have enforced only the two per-address rows. `RequestThrottle` is a `TimeProvider`-driven fixed window applied in the endpoints, where those keys are actually in hand.

In-memory is right here and wrong one paragraph above, and the distinction is worth keeping straight: these limits blunt a burst, so losing them on deploy costs seconds of protection. The ladder decides whether an account may exist at all, so losing it on deploy is a bypass an attacker can simply wait for.

| Endpoint | Limit |
|---|---|
| `/auth/token` (password grant) | 5/min per IP **and** 10/hour per username |
| `/auth/register` | 10/hour per IP (a ceiling above the ladder, not a substitute) |
| `/auth/forgot-password` | 3/hour per email, 10/hour per IP |
| `/auth/token` (refresh grant) | 30/min per device |
| `/group-rides/{id}/join-requests` | 5 pending, 20/day per user (§5.2) |
| `/tracks/import` | 20/hour and 100/day per user, on top of the size and point caps (§15.3) |
| `/tracks/{id}/edit` | 30/hour per user — each edit rewrites and re-stats a blob (§15.5) |

Plus Identity lockout: 5 failed attempts → 15-minute lockout (`lockoutOnFailure: true`).

> **Gotcha with teeth, now load-bearing:** per-IP logic requires `ForwardedHeadersMiddleware` configured with `KnownProxies`/`KnownNetworks`. Without it every request appears to originate from Caddy — so the registration ladder would see *all* signups as coming from one address and demand an email from the fourth user ever. In v0.4 this bug merely weakened rate limiting; in v0.5 it breaks registration for everybody. Covered by `RateLimit_PerIpPartitioning_UsesForwardedClientIp` and `Registration_LadderUsesForwardedClientIp`.

**Transactional email** is the weakest external dependency in the stack and gets its own section — see **§7.12**. One rule belongs here: never send SMTP straight from the VPS, because datacentre IP reputation means mail lands in spam or is dropped silently.

### 7.9 Offline behaviour — the part specific to this app

An offline-first riding app has auth requirements a normal web app does not.

- **Recording works before any account exists.** A fresh install at a trailhead with no signal must still record. Pre-account rides are stored locally with a null owner and **claimed by the first account that signs in on that device.** Without this the offline-first architecture has a hole exactly where riders need it most. The Welcome screen is skippable.
- **Never sign out on a 401 while offline.** On-device auth state is *"a refresh token exists"*, not *"the access token is valid"*. A failed request with no connectivity is a network condition, not a credential failure — treating it otherwise would log riders out mid-ride, in the middle of nowhere.
- **Access-token expiry must not interrupt recording.** GPS capture and local SQLite writes never touch the network; uploads queue in the outbox (§4.4) and drain when signal returns.
- **No expiry warnings, ever.** Sessions are permanent (§7.4), so there is no 90-day countdown and nothing to nag about. A rider who skips an entire season opens the app and is simply still signed in — provided the account was not empty enough to be cleaned up (§7.11).

### 7.10 Devices, sessions and activity

`refresh_token` rows reference the existing `Device` row, which turns session management into a feature rather than an afterthought:

- Settings → **Signed-in devices**: "iPhone 15 — Sydney — last seen 2 hours ago", with revoke. This matters more now that sessions are permanent: revocation is the *only* thing that ends one.
- Revoking a device revokes its whole token family; its next refresh fails and the app signs out locally.
- A sign-in from a new device emails a security alert **when an address is known** — silently impossible otherwise, which is another line in the §7.2 trade-off.
- Sign-out revokes the family for that device and clears `SecureStorage`. **Locally recorded rides are kept** — they are the user's data, not session state.

**Recording last activity.** Every app start updates `asp_net_users.last_active_utc` and `device.last_seen_utc`. Two implementation notes that keep this free:

- **Piggyback on the refresh that already happens at app start.** The client refreshes its access token on launch anyway, so `POST /auth/token` (refresh grant) does the update. No extra endpoint, no extra round trip, no client work beyond what it already does.
- **Throttle the write.** Skip it when `now - last_active_utc < 1 hour`, so opening the app five times in a morning is one `UPDATE`, not five.

Because the update rides on a server call, `last_active_utc` means *"the last time the server heard from this account"* — which is exactly the semantics the cleanup job needs. A rider who is active but permanently offline is an edge case bounded by the fact that their tracks eventually sync, and a track makes the account ineligible for deletion anyway.

### 7.11 Account lifecycle and inactivity cleanup

Dormant accounts that hold **nothing** are deleted after **180 days** of inactivity. This is what replaces time-based session expiry: rather than logging people out on a timer, remove accounts that would cost nothing to lose.

**All of these must be true.** The conjunction is the safety property — an account with a single saved ride is never touched:

```
last_active_utc < now() - 180 days
AND NOT EXISTS (track          WHERE owner_id = u.id)
AND NOT EXISTS (group_ride_member WHERE user_id = u.id)
AND NOT EXISTS (group_ride     WHERE owner_id = u.id)
AND NOT EXISTS (group_ride_join_request WHERE user_id = u.id AND status = 'Pending')
```

The comparison is **strict**. An account last active exactly 180 days ago is *at* the horizon, not past it, and the pair of boundary tests is written that way round deliberately.

- **Warned at 150 days** by email, if a confirmed address is known. Without one there is no way to warn — the same gap as password recovery, and a further reason the §7.2 notice matters. **`asp_net_users.inactivity_warned_utc` records that it went**, because the window is thirty days wide and the job is nightly: without it the courtesy is thirty emails to the same person and a blocked sending domain. It is **cleared whenever the account is next heard from** (§7.10), so a rider who comes back and goes quiet a year later is warned again rather than deleted in silence. Stamped only on a successful send, so a transport failure is retried tomorrow instead of swallowing somebody's only notice.
- The warning uses the **same emptiness predicate** as the deletion. Two predicates would mean an account either warned and then kept — noise — or deleted having been told nothing.
- **The warning carries no link and no token.** Signing in is what saves the account, and an unsolicited "click here to keep your account" is indistinguishable from the phishing message somebody will eventually send in our name.
- Hard delete. `ON DELETE CASCADE` clears devices, refresh tokens and any residual rows; there is nothing worth soft-deleting given the criteria. **One exception, and it is load-bearing:** `user_block.blocked_id` is `NO ACTION` rather than a cascade — two cascade paths into `asp_net_users` through one table is an error in PostgreSQL (§16.5) — so the sweep deletes those rows itself first. Nothing else in the project deletes an account, so nothing else has ever met that constraint, and an unhandled violation does not skip one account: it aborts the statement and the whole night's deletions with it.
- **Batched** — at most `Maintenance:MaxDeletesPerRun` (default 500) per night, so one run can never take a long lock.
- Username is released back to the pool on deletion. This does not undermine the permanence rule in §7.2, and the deletion criteria are what make it safe: an eligible account has **never joined a ride**, so it never appeared on anyone's map and no rider can have formed an association with that name. There is no reputation to inherit and nothing to impersonate. Names that were ever visible to another rider belong to accounts that are never auto-deleted.

**The same nightly job carries three other sweeps**, all small and all destructive in their own way: nulling `created_by_ip` after 30 days (§7.8), deleting position rows for rides that are no longer Live (§5.5), and **purging expired `TrackRevision` originals past their undo window** (§15.6). They share the job because they share the requirement below — a destructive timer nobody watches.

**A destructive automated job needs brakes.** Two non-negotiables:

- **`Maintenance:DryRun` defaults to `true`.** It logs exactly which accounts *would* go — **named, one per line, not counted**, because "seven accounts would be deleted" is not something anybody can check. Run it that way for at least a week and read the output before enabling deletion for real. **It gates every sweep in the table below, not only the account deletion**: an operator who turns it on has said *show me, do not touch it*, and a dry run that still deleted refresh tokens, positions and photo blobs would be a dry run in name only.
- **A kill switch** (`Maintenance:DeleteInactiveAccounts`) that disables the 180-day sweep **alone**, without a redeploy. That is what distinguishes it from `DryRun`, and it is the setting to reach for at 3 a.m. when the predicate has done something surprising and the disk still needs collecting.

**Client handling.** When an account has been deleted, the device's next refresh fails. The response carries a distinguishable reason so the app can say *"This account was removed after 180 days without use"* and offer to create a new one — not a generic sign-in error, which would look like a bug and be indistinguishable from a bad password.

**Which needs something to recognise, and the cascade has taken it.** The account is gone, its refresh tokens went with it, and its username is back in the pool, so there is nothing left to point at. `deleted_account_token` (§7.13) holds the SHA-256 of each token the deleted account still had, and nothing else — a hash of a value that no longer opens anything, and a date. Keyed on the hash rather than on the account, so only the device that actually held the token gets the specific answer and a guessed token still gets *"that refresh token is not valid"*. It is **not an oracle** for whether an account ever existed. Swept on the same horizon as `refresh_token` itself: a device not opened in a month will be told to sign in, and that is answer enough.

**One nightly service**, not seven. `NightlyMaintenanceService` consolidates:

| Sweep | Reference |
|---|---|
| `rider_position` rows with no fix for `Ride:PositionIdleDays` (default **14**) — the row deleted and that member's `ShareLocation` cleared | §5.5, §5.6 |
| `refresh_token` rows expired or revoked > 30 days, and `deleted_account_token` on the same horizon | §7.13 |
| Null `created_by_ip` on users older than 30 days | §7.8 |
| `TrackRevision` originals past their undo window — **the blob as well as the row** | §15.6 |
| Orphaned blobs on the volume — `ON DELETE CASCADE` does not reach a filesystem | §16.6 |
| Resolved `ContentReport` rows and their content snapshots past retention | §17.7 |
| Warn at 150 days, delete empty accounts at 180 | this section |

**Each sweep is its own transaction and its own failure.** One that throws is logged and the rest still run: a blob volume that has gone read-only must not be the reason nobody's `created_by_ip` was cleared for a fortnight.

**The orphaned-blob sweep is the one that can destroy data, and it has two safeguards.**

- **A grace window** (`Maintenance:OrphanBlobGraceHours`, default 24). A blob is written before the row that points at it is committed, so for the width of one request every new upload is indistinguishable from an orphan. Without the window the sweep deletes photographs out from under the requests uploading them. It is measured against the file's own timestamp — which `IBlobStore` **stamps from `TimeProvider`** on write, precisely so that the two sides of that comparison are in the same frame (§9.1).
- **One declared set of blob-bearing columns**, resolved through the EF model so a rename throws rather than silently dropping out. A column the sweep does not know about is not a missed tidy-up: every value in it looks unreferenced, so the next run deletes all of them. `Track.BlobRef`, `TrackRevision.BlobRef`, `Photo.BlobRef` and `Photo.ThumbBlobRef` — four, and a photo's thumbnail is the one most easily forgotten.

**The job also runs on demand.** `Maintenance:IntervalHours` (default 24) turns the timer off entirely at zero, for a deployment that would rather drive it from `cron` — and for the test suite, which calls it directly rather than advancing a fake clock a whole day into a `PeriodicTimer` (§5.5's lesson).

Automatic deletion of dormant data is also the right posture for **APP 11.2** and GDPR storage limitation — it is a compliance asset, not just tidiness, and belongs in the privacy policy (§10.2).

### 7.12 Email delivery — Zoho

Zoho has two relevant products and the distinction decides how far you can push it:

| | **Zoho Mail** (mailbox) | **ZeptoMail** (transactional) |
|---|---|---|
| Built for | A human reading mail | App-generated mail |
| SMTP host | `smtp.zoho.com` — **region-specific** (`.com.au`, `.eu`, `.in`) | `smtp.zeptomail.com` |
| Credentials | Mailbox address + **app-specific password** | Mail Agent token / `Zoho-enczapikey` |
| Volume | Mailbox quota; bursts get throttled | Credit-based, ~US$2.50 per 10k, credits don't expire |
| Bounce handling | Bounce notices land in the mailbox | Webhooks, suppression list, delivery events |
| Domain auth | **Already done** if the domain runs Zoho Mail | **Separate** domain verification + DKIM in the ZeptoMail console |
| Sanctioned for app mail | Discouraged by ToS | Yes — this is the product for it |

**Plan: Zoho Mail SMTP for Phase 0–1, ZeptoMail before real users.**

Using the existing Zoho account is the right call, and the reason is precisely the risk §12 ranks worst: **the hard part of transactional email is domain authentication, and for Zoho Mail it is already in place and warm.** SPF and DKIM are configured, the domain has sending history, and Phase 1 volume is you and a few mates — comfortably inside a mailbox's limits.

Email volume is also lower in v0.5 than it was in v0.4: most accounts never supply an address, so confirmation mail is the exception rather than the rule. Sends are now confirmation and reset for users who opt in, security alerts, and 150-day inactivity warnings.

**Setup prerequisites.** Verify all four before writing the sender; each fails in a way that does not describe its own cause:

- **The SMTP host is region-specific.** An account in the Australian datacentre uses `smtp.zoho.com.au`. Pointing at `smtp.zoho.com` surfaces as an *authentication* failure, not a region mismatch — which sends you hunting for a credential bug that isn't there.
- **SMTP access needs a paid plan.** Zoho's free tier is web-access-only for newer signups: no IMAP/POP/SMTP. Confirm the plan includes it rather than assuming.
- **An app-specific password is required** once 2FA is on, as it should be. The normal mailbox password simply will not authenticate.
- **`From` must be the authenticated mailbox or a verified alias.** Zoho rejects arbitrary senders, so `no-reply@…` has to exist as a real alias — decide the address now, because it ends up in every template and in users' allow-lists.

**Use MailKit, not `System.Net.Mail.SmtpClient`** (obsolete for new development). This is also what makes the eventual migration free: **ZeptoMail speaks SMTP too**, so moving to it is a host-and-credential change in configuration, with no code change behind `IEmailSender`.

```csharp
public interface IEmailSender
{
	Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
```

```ini
# The entire cutover, once ZeptoMail's domain + DKIM are verified:
# Email:Host         smtp.zoho.com.au    →  smtp.zeptomail.com
# Email:Port         587
# Email:UserName     no-reply@example    →  emailapikey
# Email:Password     <app password>      →  <ZeptoMail send-mail token>
# Email:FromAddress  no-reply@example       (unchanged)
# Email:FromName     Dumb Luck Routes       (unchanged)
```

**Cut over when any of these is true:** store submission is close, sends exceed ~100/day, or you need to know whether mail actually arrived. A mailbox gives no bounce tracking, no suppression list and no delivery events — so a hard-bouncing address is retried forever, silently, and Zoho's own terms steer automated sending to ZeptoMail regardless. Treat the ZeptoMail DKIM records as a distinct task from the Zoho Mail ones; sharing a vendor does not mean sharing DNS setup.

**Templates:** confirm address, reset password, new-device sign-in alert, registration-attempt-on-existing-address, and 150-day inactivity warning (§7.11).

**In tests**, `IEmailSender` is faked to a collecting sink (§10.4) — no test ever touches Zoho, and confirmation and reset tokens stay readable.

### 7.13 Schema additions

```sql
-- asp_net_users (IdentityUser<Guid>) gains:
--   user_name / normalized_user_name   unique (Identity does this already)
--   email / normalized_email           NULLABLE, unique when present
--   (user_name is the map label; display_name below is profile only, §7.3)
--   display_name         varchar(60) NULL      -- §7.3, optional
--   phone_number         varchar(20) NULL      -- Identity column, never verified
--   phone_number_confirmed  boolean            -- ALWAYS false; not a gate (§7.3)
--   share_display_name   boolean NOT NULL DEFAULT false   -- §7.3
--   share_phone_number   boolean NOT NULL DEFAULT false   -- §7.3
--   share_email          boolean NOT NULL DEFAULT false   -- §7.3
--   marker_colour        varchar(7)  NULL      -- §16.3, #rrggbb, no sharing switch
--   private_area_lat     double precision NULL -- §10.1, home private area; v0.28
--   private_area_lon     double precision NULL -- §10.1, all three move together
--   private_area_radius_m  double precision NULL -- §10.1, clamped to 100 m … 10 km
--   (the three private_area_* columns are personal data at rest: they name where the
--    rider lives. No index, no query, and no route that reads another account's.
--    double precision, not the scaled ints positions use — the rider types this one
--    into a box and lines the circle up with their own roof, §10.1)
--   last_active_utc    timestamptz  NOT NULL   -- §7.10
--   inactivity_warned_utc  timestamptz NULL    -- §7.11, cleared on activity
--   created_by_ip      inet         NULL       -- §7.8, nulled after 30 days
--   requires_email_confirmation  boolean NOT NULL DEFAULT false   -- §7.8 ladder
--   is_guest           boolean      NOT NULL DEFAULT false        -- §7.16
--   avatar_url, units, map_provider, created_utc

-- RequireUniqueEmail must stay FALSE in IdentityOptions, because Identity's
-- validator rejects a null email when it is true. Uniqueness is enforced here:
CREATE UNIQUE INDEX ux_users_email
	ON asp_net_users (normalized_email)
	WHERE normalized_email IS NOT NULL;

CREATE INDEX ix_users_created_ip	ON asp_net_users (created_by_ip, created_utc);
CREATE INDEX ix_users_last_active	ON asp_net_users (last_active_utc);

-- The device a refresh-token family belongs to (§7.10). The id is assigned by the
-- server and never accepted from the client: a client sends back the id it was given,
-- and one belonging to another account simply does not match, so the installation gets
-- one of its own. Accepting a client-chosen id would let a guessed GUID attach a
-- session to somebody else's device row.
CREATE TABLE device (
	id				uuid		PRIMARY KEY,
	user_id			uuid		NOT NULL REFERENCES asp_net_users(id) ON DELETE CASCADE,
	name			varchar(60)	NULL,		-- "iPhone 15"; client-supplied, never verified
	kind			varchar(20)	NOT NULL DEFAULT 'Mobile',	-- Mobile|Web (§7.5); server-decided
	created_utc		timestamptz	NOT NULL,
	last_seen_utc	timestamptz	NOT NULL	-- throttled to one write an hour (§7.10)
);

CREATE INDEX ix_device_user ON device (user_id);

CREATE TABLE refresh_token (
	id				uuid		PRIMARY KEY,
	user_id			uuid		NOT NULL REFERENCES asp_net_users(id) ON DELETE CASCADE,
	device_id		uuid		NOT NULL REFERENCES device(id) ON DELETE CASCADE,
	family_id		uuid		NOT NULL,
	token_hash		bytea		NOT NULL,	-- SHA-256 of the opaque token
	successor_id	uuid		NULL REFERENCES refresh_token(id),
	issued_utc		timestamptz	NOT NULL,
	expires_utc		timestamptz	NOT NULL,	-- issued + 10 years (§7.4)
	used_utc		timestamptz	NULL,
	revoked_utc		timestamptz	NULL,
	revoked_reason	text		NULL,
	created_by_ip	inet		NULL,
	user_agent		text		NULL
);

CREATE UNIQUE INDEX ux_refresh_token_hash		ON refresh_token (token_hash);
CREATE INDEX ix_refresh_token_family			ON refresh_token (family_id);
CREATE INDEX ix_refresh_token_user_device		ON refresh_token (user_id, device_id);
CREATE INDEX ix_refresh_token_expires			ON refresh_token (expires_utc);

CREATE TABLE group_ride_join_request (
	id				uuid		PRIMARY KEY,
	group_ride_id	uuid		NOT NULL REFERENCES group_ride(id) ON DELETE CASCADE,
	user_id			uuid		NOT NULL REFERENCES asp_net_users(id) ON DELETE CASCADE,
	status			text		NOT NULL,	-- Pending|Approved|Declined|Withdrawn
	message			varchar(200)NULL,
	requested_utc	timestamptz	NOT NULL,
	decided_utc		timestamptz	NULL,
	decided_by		uuid		NULL REFERENCES asp_net_users(id),
	blocked			boolean		NOT NULL DEFAULT false
);

-- One live request per rider per ride; history is kept for decided rows.
CREATE UNIQUE INDEX ux_join_request_pending
	ON group_ride_join_request (group_ride_id, user_id)
	WHERE status = 'Pending';

-- What is left of an account the §7.11 sweep deleted, and it is deliberately not much:
-- a hash of a token that no longer opens anything, and a date. It exists so the next
-- refresh from that device can be answered with a *reason* rather than a shrug. There
-- is no user_id and no username — the account is gone and the name is back in the pool,
-- so there is nothing to point at and nothing here that identifies a person.
CREATE TABLE deleted_account_token (
	token_hash		bytea		PRIMARY KEY,	-- SHA-256, as refresh_token held it
	deleted_utc		timestamptz	NOT NULL
);

CREATE INDEX ix_deleted_account_token_deleted ON deleted_account_token (deleted_utc);
```

The raw refresh token is never stored — only its SHA-256. `successor_id` is what makes the idempotent replay window in §7.4 possible.

**One bounded exception to that, and it is deliberate.** Returning *the same* successor to a replay means being able to reproduce a token the database only holds a hash of, so the successor is held in **process memory for the length of the grace window** — ten seconds, never written down. A restart empties it, and a replay arriving inside the window with nothing cached is answered `401` **without revoking the family**: a process restart is not evidence that a token exists in two places, and treating it as theft would sign people out for a deploy.

### 7.14 Endpoints

```
POST   /api/v1/auth/register              { userName, password, email? } → tokens
POST   /api/v1/auth/token                 { grantType: password | refresh, … } → tokens
GET    /api/v1/auth/username-available    ?u=…  → { available }
POST   /api/v1/auth/logout                { refreshToken } → revoke family
POST   /api/v1/auth/email                 authed, { email } → send 24 h confirmation
POST   /api/v1/auth/confirm-email         { userId, token } → FRESH tokens
POST   /api/v1/auth/resend-confirmation   authed
POST   /api/v1/auth/forgot-password       { email } → always 202
POST   /api/v1/auth/reset-password        { userId, token, newPassword }
POST   /api/v1/auth/change-password       authed, { current, new }
GET    /api/v1/auth/sessions              authed → device list
DELETE /api/v1/auth/sessions/{deviceId}   authed → revoke that device
GET    /api/v1/me/profile                 authed → own values + all three switches
PUT    /api/v1/me/profile                 authed, { displayName?, phoneNumber?,
                                                    shareDisplayName, sharePhoneNumber,
                                                    shareEmail, markerColour? }
GET    /api/v1/me/private-area            authed → { area: {lat, lon, radiusM} | null }
PUT    /api/v1/me/private-area            authed, { latitude, longitude, radiusM }
DELETE /api/v1/me/private-area            authed → { area: null }
```

**The private area is a sub-resource of `/me`, not three more fields on the profile (§10.1, v0.28).** `PUT /me/profile` replaces the whole profile — the Profile screen round-trips values it does not even render for that reason — so an area carried inside it would be erased by any client that had not been taught about it. A privacy control must not be deletable as a side effect of saving a display name. The route carries no user id: there is no way to ask for anybody else's, which is the whole of the "no other rider can see it" guarantee.

`PUT` clamps the radius to the offered range and refuses a centre that is not on the earth — the same `PrivateAreaSettings.Normalised` the client calls before it sends, so the two cannot disagree about what was kept, and the response carries the *stored* values rather than the posted ones. `DELETE` is idempotent and nulls all three columns together.

Setting an email address stays on `POST /auth/email` because it requires confirmation; `/me/profile` only controls whether a *confirmed* address is shared (§7.3). Shared values surface to other riders through the member list on `GET /group-rides/{id}`, never through a profile lookup — there is no endpoint that resolves a username to a profile.

There is deliberately **no username-change endpoint** — usernames are immutable (§7.2). Any profile-update surface added later must reject or ignore the field rather than quietly accepting it.

Web equivalents are static-rendered Razor endpoints, not Blazor components (§7.5).

### 7.15 Tests to write first

Per §10.4, TDD. `IEmailSender` is faked to a collecting sink so confirmation and reset tokens are readable, and `FakeTimeProvider` drives every window.

```
Register_UsernameAndPasswordOnly_Succeeds
Register_NoEmail_AccountIsFullyUsable
Register_DuplicateUsername_IsRejectedCaseInsensitively
Register_ReservedUsername_IsRejected
Register_MixedCaseUsername_IsStoredAndReturnedAsTyped
Register_UsernameDifferingOnlyByCase_IsRejected
Register_NonAsciiUsername_IsRejected
Register_UsernameOutsideLengthBounds_IsRejected
Username_CannotBeChangedByAnyEndpoint
Register_CanReuseUsernameOfDeletedAccount
Register_DuplicateEmail_ReturnsGenericSuccessAndNotifiesOwner
Register_NullEmails_DoNotCollideOnUniqueIndex
Register_WeakPassword_IsRejectedAndCreatesNoAccount
Register_FourthAccountFromSameIpInOneDay_RequiresEmail
Register_FourthAccountFromDifferentIp_DoesNotRequireEmail
Register_LadderCountSurvivesProcessRestart
Register_LadderUsesForwardedClientIp
Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide
Restricted_AfterConfirming_CanJoinRide
Login_UnknownUsername_ResponseTimingMatchesKnownUsername
Login_FiveFailures_LocksAccountForFifteenMinutes
Login_AccessTokenCarriesTheClaimsAndKeyIdFromSevenPointFour
AccessToken_SignedWithTheOutgoingKey_StillValidatesDuringRotation
SigningKey_InAFileThatShipsWithTheCode_RefusesToStart
ConfirmEmail_TokenJustUnder24Hours_IsAccepted
ConfirmEmail_TokenPast24Hours_IsRejected
ResetPassword_TokenPast1Hour_IsRejected
ResetPassword_LifespanIsIndependentOfConfirmationLifespan
ResetPassword_AccountWithoutEmail_HasNoRecoveryPath
ResetPassword_Success_RevokesAllRefreshTokenFamilies
ChangePassword_Success_KeepsCurrentDeviceSignedIn
Refresh_AfterOneYearIdle_StillSucceeds
Refresh_ValidToken_RotatesAndInvalidatesPredecessor
Refresh_ReusedToken_RevokesEntireFamily
Refresh_ReusedWithinGraceWindow_ReturnsSameSuccessor
Refresh_UpdatesLastActiveUtc
Refresh_WithinThrottleWindow_DoesNotRewriteLastActive
RevokeSession_TargetDeviceCannotRefresh
Hub_ConnectionWithoutToken_IsRejected
Hub_JoinRide_NonMemberIsRejected
Hub_JoinRide_PendingRequesterIsRejected
Hub_LongLivedConnection_SurvivesAccessTokenExpiry
JoinByCode_OpenRide_JoinsImmediately
JoinByCode_ApprovalRide_CreatesPendingRequestOnly
JoinRequest_Approved_AddsMemberAndNotifiesRider
JoinRequest_Declined_WithBlock_CannotRequestAgain
JoinRequest_SixthPending_IsRejected
Cleanup_EmptyAccountIdle180Days_IsDeleted
Cleanup_AccountWithOneTrack_IsNeverDeleted
Cleanup_AccountWithRideMembership_IsNeverDeleted
Cleanup_AccountOwningRide_IsNeverDeleted
Cleanup_AccountWithPendingJoinRequest_IsNeverDeleted
Cleanup_IdleAccountAt179Days_IsNotDeleted
Cleanup_At150Days_SendsWarningWhenEmailKnown
Cleanup_At150Days_SendsNothingWhenNoEmail
Cleanup_DryRunEnabled_DeletesNothingButLogsCandidates
Cleanup_RespectsMaxDeletesPerRun
Cleanup_ReleasesUsernameForReuse
Cleanup_DeletedAccountRefresh_ReturnsDistinguishableReason
Cleanup_NullsRegistrationIpAfter30Days
Profile_FreshAccount_AllThreeSharingSwitchesAreOff
Profile_DisplayNameNotShared_IsOmittedFromMemberList
Profile_PhoneNotShared_IsOmittedFromMemberList
Profile_EmailNotShared_IsOmittedFromMemberList
Profile_WithheldAndUnrecorded_AreIndistinguishableOnTheWire
Profile_EachSwitchGovernsOnlyItsOwnField
Profile_BlankValues_AreTreatedAsAbsent
Profile_NonCoMember_ReceivesEmptyProfile
Profile_AfterLeavingRide_SharedFieldsAreNoLongerVisible
Profile_AfterRideCompletes_SharedFieldsAreNoLongerVisible
Profile_SharedDisplayName_DoesNotChangeMapLabelOrPositionBatch
Profile_TurningSharingOff_DoesNotDeleteTheValue
Profile_TurningEmailSharingOff_LeavesRecoveryIntact
Profile_PhoneNumberConfirmed_IsNeverSetTrue
Profile_Export_IncludesAllRecordedFieldsAndSwitches
Offline_NoAccount_CanStillRecordLocally
Offline_401WithNoConnectivity_DoesNotSignUserOut
FirstSignIn_ClaimsPreAccountLocalRides
SecureStorage_DecryptFailure_IsTreatedAsSignedOut
RateLimit_SixthLoginAttemptInOneMinute_Returns429
RateLimit_PerIpPartitioning_UsesForwardedClientIp
```

### 7.16 Deferred to Phase 3

**Social sign-in — Apple and Google ship together.** Offering Google on iOS makes Sign in with Apple mandatory, so they are one work item, not two. Use **native** flows (`ASWebAuthenticationSession` on iOS, Credential Manager on Android) — Google blocks embedded webviews — and POST the resulting ID token to `/api/v1/auth/external`, where the server verifies its signature against the provider's JWKS.

Account linking needs one rule stated up front: link a provider identity to an existing account **only when the provider asserts a verified email that matches a confirmed address**. Otherwise require the password first. Skipping this is a straightforward account-takeover route via an unverified provider email.

Social sign-in is also the cleanest answer to the recovery gap in §7.2 — a linked Apple or Google identity is a recovery path that costs the user no typing.

**Guest riders** *(§13 Q4)*. With email already optional and usernames the only required field, the remaining gap between "account" and "guest" is small: `is_guest` is present in the schema so a device-bound, password-less participant can be added without a migration, and upgraded in place by setting a password.

**TOTP two-factor.** Identity's scaffolding already supports it; keep it in place and expose it once there are users worth protecting.

---

## 8. Data Model (initial)

```
AspNetUsers(Id, UserName!, NormalizedUserName!, Email?, EmailConfirmed,
            PasswordHash, SecurityStamp, AvatarUrl, Units,
            MapProvider, IsGuest, RequiresEmailConfirmation, CreatedUtc,
            CreatedByIp?, LastActiveUtc, AccessFailedCount, LockoutEnd,
            DisplayName?, PhoneNumber?, PhoneNumberConfirmed,
            ShareDisplayName, SharePhoneNumber, ShareEmail)               -- §7.13
            -- UserName is the immutable map label (§7.2).
            -- DisplayName is optional profile data, never the map label (§7.3).
            -- All three Share* flags default false. PhoneNumberConfirmed is
            -- always false — no SMS verification exists (§7.3).
RefreshToken(Id, UserId, DeviceId, FamilyId, TokenHash, SuccessorId?,
             IssuedUtc, ExpiresUtc, UsedUtc?, RevokedUtc?, RevokedReason,
             CreatedByIp, UserAgent)                                      -- §7.13
Device(Id, UserId, Platform, AppVersion, LastSeenUtc)
             -- PushToken removed in v0.26: notifications are local (§17.6), so the
             -- server has nothing to address and no token to keep in step.

Track(Id, OwnerId, Name, CreatedUtc, StartedUtc?, EndedUtc?, DistanceM,
      DurationS?, AscentM?, MaxSpeedMps?, BoundsMinLat/MinLon/MaxLat/MaxLon,
      PointCount, SegmentCount, Visibility, BlobRef, SimplifiedPolyline,
      Source{Recorded,Imported}, Version, EditedUtc?, ContentHash,
      ImportedFileName?, ImportedFormat?)                              -- §15.8
      -- StartedUtc/EndedUtc/DurationS/MaxSpeedMps are NULL for an imported
      --   route with no <time> elements; AscentM is NULL with no <ele> (§15.3).
      -- Version increments on every edit; the device replaces its cached
      --   copy rather than merging (§4.4, §15.4).
TrackRevision(TrackId, Version, BlobRef, ReplacedUtc, PurgeAfterUtc)   -- §15.6
      -- Exactly one row per track at most: the pre-edit original, kept for
      --   the undo window, then purged by the nightly job (§7.11).
TrackShare(TrackId, GrantedToUserId?, LinkToken?, ExpiresUtc?)

Marker(Id, TrackId?, GroupRideId?, CreatedByUserId, Lat, Lon, DirectionDeg?,
       Icon, Title, Note?, PhotoId?, CreatedUtc, UpdatedUtc)             -- §16.7
       -- CHECK ((TrackId IS NULL) <> (GroupRideId IS NULL)) — exactly one parent.
       -- DirectionDeg NULL means "no direction"; 0 is due north (§16.2).
Photo(Id, OwnerId, BlobRef, ThumbBlobRef, WidthPx, HeightPx, ByteSize,
      ContentHash, CreatedUtc)                                           -- §16.4
       -- Re-encoded on ingest, so metadata-free by construction, not by policy.
RideComment(Id, GroupRideId, AuthorId, Kind{Text,Poll}, Body?, PhotoId?,
            IsPinned, PinnedByUserId?, PinnedUtc?,
            CreatedUtc, PostedUtc, EditedUtc?)                        -- §17.9
       -- CHECK (Body IS NOT NULL OR PhotoId IS NOT NULL).
       -- Thread order is PostedUtc; CreatedUtc is clamped to it (§17.3).
CommentReaction(CommentId, UserId, Reaction)   -- PK (CommentId, UserId)
Poll(CommentId, AllowMultiple, ClosesUtc?, ClosedUtc?, ClosedByUserId?)
PollOption(Id, CommentId, Ordinal, Text)
PollVote(PollOptionId, UserId)                 -- PK (PollOptionId, UserId)
ContentReport(Id, TargetKind{Marker,Comment}, TargetId, ReportedByUserId,
              Reason, ContentSnapshot, CreatedUtc, ResolvedUtc?)      -- §17.7

GroupRide(Id, OwnerId, Name, Description, StartUtc,
          JoinCode, JoinPolicy{Open,Approval}, MemberCap,
          PlannedRouteTrackId?, MeetPointLat/Lon, CreatedUtc,
          AllowMemberMarkers, AllowMemberComments, AllowMemberPhotos)    -- §5.8
          -- No State, EndedUtc or SharingEndsUtc since v0.32: an adventure
          --   is live from creation until it is deleted (§5.1).
          -- The three Allow* flags default true and are toggleable at any
          --   time; turning one off deletes nothing (§5.8).
GroupRideMember(GroupRideId, UserId, Role{Owner,Leader,Rider,Spectator},
                JoinedUtc, RecordedTrackId?, ShareLocation)
                -- ShareLocation defaults FALSE — consent is asked at join
                --   and a dismissed prompt is a no (§5.6).
GroupRideJoinRequest(Id, GroupRideId, UserId, Status, Message?,
                     RequestedUtc, DecidedUtc?, DecidedBy?, Blocked)      -- §7.13
GroupRideInvite(Id, GroupRideId, InvitedEmail?, LinkToken, ExpiresUtc)

RiderPosition(GroupRideId, UserId, Lat, Lon, SpeedMps, HeadingDeg,
              AccuracyM, RecordedUtc)          -- PK (GroupRideId, UserId)
                                               -- last known only, no history (§5.5)
```

**Deliberately not a table:** individual track points — they live in compressed blobs, since they are written once and always read whole. **Markers are the opposite** and therefore *are* a table: a handful per ride, individually created, edited and deleted, and queried by parent.

`JoinPolicy` replaces v0.4's `RequiresApproval` boolean, which could not express the two-path model in §5.2.

Indexes: `GroupRide(JoinCode)` unique, `GroupRideMember(UserId, GroupRideId)` unique, **`Track(OwnerId, CreatedUtc desc)`**, `Track(OwnerId, ContentHash)`, `TrackRevision(PurgeAfterUtc)`, `Marker(GroupRideId)`, `Marker(TrackId)`, `Photo(OwnerId)`, `RideComment(GroupRideId, PostedUtc desc)` plus a partial index on pinned, `PollVote(PollOptionId)`, `RiderPosition(RecordedUtc)`, plus everything in §7.13.

**Why the track list sorts on `CreatedUtc`, not `StartedUtc`** *(changed in v0.12)*: an imported route has no start time at all (§15.3), and sorting a nullable column puts every route in one lump at whichever end the database prefers. `CreatedUtc` is always known — it is when the row appeared, recorded or imported — so it is what the index and the list ordering use. `StartedUtc` remains the right thing to *display* when it exists.

---

## 9. Hosting — "Very Cheaply"

Ranked for this workload (persistent websockets, small always-on footprint, single Postgres):

| Option | ~Monthly | Verdict |
|---|---|---|
| **Hetzner CX22 VPS** (2 vCPU / 4 GB) + Docker Compose + Caddy + Postgres | **~€4** | ✅ **Recommended.** Cheapest per unit of capability; websockets and always-on are free; Caddy does TLS automatically |
| Fly.io shared-cpu-1x + Fly Postgres | ~$5–10 | Good middle ground; nicer deploys, less sysadmin; watch bandwidth |
| Oracle Cloud Always Free (4× Arm, 24 GB) | **$0** | Absurd value if you tolerate the capacity/account risk; ARM64 .NET is fine |
| Azure App Service B1 + Postgres Flexible B1ms | ~$28+ | Only if you want Azure specifically |
| Azure Container Apps (scale-to-zero) | variable | Scale-to-zero still conflicts with persistent SignalR hubs — though **not** with the web tier any more, which is static WASM as of v0.16 (§18.4) |
| Any per-request serverless | ✗ | Long-lived websockets are the wrong shape for it |

**Recommended deployment**

```
Caddy (TLS, HTTP/3, brotli, X-Forwarded-For → §7.8)
  ├── /              → dlr-server      (ASP.NET Core container: API, hub, SSR shell)
  ├── /_framework/*  → WASM bundle, immutable cache headers        (§18.4)
  └── /tiles/*.pmtiles → file_server with range requests           (§9.1)

Volumes:  pgdata (postgres:17)   blobs (tracks + photos)   tiles (PMTiles extract)
Nightly:  pg_dump + blobs  →  restic (encrypted)  →  Backblaze B2
```

- Single container, `dotnet publish` → distroless/Alpine image, GitHub Actions builds and pushes.
- Backups: nightly **`restic` to Backblaze B2** — `pg_dump` plus the blob volume, **encrypted client-side** so the storage provider never holds plaintext positions or email addresses. Off-provider on purpose (§9.1). Backups matter more in v0.5: for an email-less account, the database row *is* the only proof the account exists.
- **No CDN. Caddy serves everything** — the WASM bundle (§18.4), map tiles, photos and static assets — out of the VPS's included traffic allowance (§9.1). Caddy already does TLS, HTTP/3, compression and caching; a CDN at this scale would be a dependency bought with no problem to solve.
- Observability on the cheap: Serilog → file + Seq (free single-user), or OpenTelemetry → Grafana Cloud free tier. `/healthz` plus a free uptime pinger.
- **Alert on the nightly maintenance run** (§7.11) — deletion counts, dry-run candidates, and registration-ladder trips. A destructive job you do not watch is a destructive job you will regret.
- ~~Push notifications: FCM (free) for Android and, via FCM's APNs bridge, iOS.~~ **Not needed as of v0.26** — notifications are raised locally by the app on a hub message it already has (§17.6). No FCM project, no APNs key, no operational surface at all.
- **Secrets** (JWT signing key, email credentials, Postgres password) as Docker secrets or environment variables — never in the image or in `appsettings.json`.
- **`ForwardedHeaders` is now load-bearing, not hygiene** (§7.8). Verify it in staging before the first public signup.

### 9.1 Bandwidth, blobs and backups — without a CDN

**No Cloudflare, and no CDN at all in v1.** This is a decision rather than an omission, and the numbers are what make it defensible.

**Traffic.** A Hetzner CX22 includes **20 TB of egress a month**. Against that:

| Load | Per unit | 1 000 of them |
|---|---|---|
| WASM bundle, first visit (brotli, then cached) | ~4 MB | ~4 GB |
| Vector tiles for one map session | ~5–15 MB | ~15 GB |
| Ride position batches, 2 h × 10 members (§10.3) | ~3 MB | ~3 GB |
| Photo view, thumbnail + full (§16.4) | ~0.5 MB | ~0.5 GB |

The whole workload is two to three orders of magnitude inside the allowance. **A CDN solves a problem this project does not have**, and adding one buys an account, a cache-invalidation story and a dependency to explain in §14. Caddy already terminates TLS, speaks HTTP/3, compresses with zstd and gzip, and sets cache headers.

*The lever, if it is ever needed:* a plain pull-CDN in front of `/tiles` and `/_framework` — **Bunny.net** at roughly $0.01/GB is the obvious pick. It is a configuration change and a DNS record, not a redesign, precisely because nothing in the design depends on edge behaviour.

**Map tiles — not ours to serve, for now** *(revised in v0.19)*. Apple serves the phone's tiles and OSM serves the web's (§4.5), so **no tile extract sits on this disk in Phase 1** and the traffic table above overstates the tile row until that changes.

When it does change — before the web app is publicly announced, because OSM's usage policy does not cover that (§13 Q26) — the answer is already designed: self-hosted **PMTiles, served straight off the VPS by Caddy.** PMTiles is a single file read over HTTP range requests, which Caddy's `file_server` handles natively, so the usual "PMTiles needs object storage plus a Worker" setup is not required here. A regional extract is the practical unit: a few GB for Australia, versus ~100 GB for the planet. That is also the route to an offline map pack — which v0.19 through v0.23 could not have had at any price, because MapKit JS has no offline mode, and which v0.24 unblocked by putting every host on a renderer that will read a local PMTiles archive (§4.5).

**Blobs.** Track blobs and photos live on a **Docker volume on the VPS**, not in object storage. One place to back up, no S3 credentials in the running process, and no egress bill. `IBlobStore` keeps the seam, so moving to S3-compatible storage later is a registration change — but doing it now would add a dependency to save nothing.

**The constraint that replaces bandwidth is disk.** A CX22 has 40 GB. Tiles take a few GB of it, Postgres and blobs share the rest, and **a full disk stops Postgres writing, not just uploads** — a much worse failure than a slow map. So:

- Per-account storage quotas are already required (§13 Q13, §15.8) and this is the reason they are not optional.
- **Alert on disk usage**, alongside the nightly-maintenance alert already in §9.
- Expansion is a Hetzner volume at about €0.05/GB per month — cheap, and one command, provided somebody noticed in time.

**Backups go off-provider, deliberately.** **Backblaze B2** via `restic`: dedup, and **encryption before it leaves the machine**, so a backup provider breach is not a user-data breach — which matters more here than usual, since backups contain last-known positions and email addresses (§10.1). Restore egress from B2 is free up to three times the stored volume, so a restore drill costs nothing and should therefore actually be run. Hetzner's own Storage Box is cheaper and faster, but it is the same account as the server: fine as a *second* copy, never as the only one.

### 9.2 Scale-out path (no Redis)

**Single-instance is a design constraint, not a preference.** The in-memory `RiderPositionCache`, SignalR's in-process group tracking, and the in-memory rate limiter (§7.8) all assume one process. Postgres covers position durability (§5.5), refresh-token state and the registration ladder (§7.13), so the storage tier scales; only fan-out and the conventional rate limits are pinned. The ladder deliberately counts database rows precisely so that it keeps working across restarts and would keep working across instances. The ladder, in order:

1. **Vertical scale.** One VPS core handles thousands of concurrent websockets. Measure before assuming otherwise — a load test with simulated riders belongs in Phase 3.
2. **Per-ride affinity.** A group ride is a natural shard: no message ever crosses ride boundaries. Consistent-hash `rideId` to an instance and route at the proxy. No shared backplane required, and the cache stays in-process.
3. **PostgreSQL `LISTEN`/`NOTIFY` as a SignalR backplane** — only if (2) proves insufficient. Reuses the datastore already present.

~~Blazor Server circuits need sticky sessions the moment there is more than one instance~~ — **removed in v0.16.** The web client is WebAssembly (§18.4): static files Caddy serves and caches, no server-side circuit, no per-tab memory, no affinity requirement. The web tier is now genuinely stateless, and what pins the design to a single instance is only the position cache and the in-memory rate limiter — both already covered by steps (1) and (2) above.

---

## 10. Cross-Cutting Concerns

### 10.1 Privacy — the headline feature, stated accurately

Live location is shared **only** within a group adventure, **only** with its admitted members, **only if the rider said yes** (§5.6), and **stops the moment they turn it off, leave, or are removed**.

*Corrected in v0.32, and this is the third version of the sentence: v0.14 said sharing ended with the ride, v0.15 added the wind-down, and v0.32 removed the end it was measured from. Same discipline as the v0.2 correction below — a privacy statement that describes an earlier version of the code is worse than no statement, because people rely on it. Note what the sentence deliberately does **not** claim: an end the rider did not choose. The fourteen-day idle sweep (§5.6, §7.11) is a garbage collector for rows nothing is updating and must never be sold as a limit on somebody still riding.*

**What is stored, precisely** *(corrected in v0.2 — v0.1 claimed positions were never persisted, which the 10 s flush makes false)*:

> Exactly **one row per rider per adventure**, overwritten in place. **No location history is ever stored** — there is no positions table to accumulate, no trail, no replay. A row is **deleted when the rider stops sharing, leaves, is removed, or the adventure is deleted** (§5.6), and as a backstop when nothing has updated it for fourteen days (§7.11). Recorded tracks are a separate, opt-in artefact.

**Measured location is deleted; authored content is kept.** Markers (§16) are locations too, and the ride thread (§17) is a record of who said what to whom — both survive the ride precisely because a person chose to write them. The distinction is worth naming so the promise above stays exact: the app deletes what it *observed* about where you were, and keeps what you *wrote down* about where something is. A marker is visible to whoever its parent is visible to — a track's audience, or a ride's admitted members — and no wider.

**Who can see a rider's live position** *(rewritten in v0.5 — the confirmed-email gate is gone)*:

> Only members of an adventure the **organiser** admitted them to. There are two ways in: the join code, or the organiser pressing *Admit* on a request (§5.2). Since v0.29 any member can pass the code on, so on an open-join adventure the organiser no longer controls both paths by themselves — the cap, the member list and *remove member* are what bound it.

This is a stronger statement than v0.4's email gate. Confirming an email only ever proved that somebody could read a mailbox; it said nothing about whether the organiser wanted them on the ride. The membership check in the hub (§7.6) is what enforces it, which is why that check is tested directly rather than assumed.

- **Consent is asked at join and defaults to off** (§5.6). A rider can be in a ride without sharing, and the member list shows who is and is not — visible rather than enforced.
- Per-ride `ShareLocation` toggle. Setting it false, or leaving the ride, **deletes the persisted row** — merely stopping the broadcast would leave a last-known point at rest in the database, which is exactly what a user turning sharing off is asking you not to do.
- **The organiser has no switch over anybody's sharing.** They can remove a member, which deletes that member's row; they can never turn sharing on or off on somebody's behalf (§5.6). Since v0.32 the rider's own toggle is the entire control surface.
- An organiser can remove a member mid-ride; their position row is deleted immediately.
- **Several concurrent rides mean several independent consents** (§5.7), and a rider sharing with one ride and not another has no stored position in the second at all — the filter is applied on the write, not on the read.
- Revoking a device (§7.10) cuts that device's ability to read positions — its next refresh fails.
- **Minimal collection by default:** a working account is a username and a password hash. Email, phone number and display name are all optional, and all three are **shared with nobody unless the user switches them on** (§7.3).
- **Shared profile fields are adventure-scoped**, visible only to current co-members, and access ends when co-membership does — leaving, being removed, or the adventure being deleted (§7.3). There is no profile lookup endpoint and no way to resolve a username to a person's details.
- Registration IP is kept 30 days for abuse throttling, then nulled (§7.8).
- Dormant empty accounts are deleted after 180 days (§7.11) — data minimisation by construction.
- Public share links are unguessable tokens, revocable, optionally expiring.
- Optional "hide start/end" radius on shared tracks — don't publish home addresses. This is a **display** rule: the points are still stored, and they are still in the owner's own export and GPX.
- **A home private area — a point and a radius on the rider's account, inside which nothing is broadcast.** Unlike the display rule above this one is *collection*: the fix never reaches the hub and never becomes a stored position row, so there is nothing to hide later. Co-riders see the rider as present in the ride with no position on the map. Four properties are load-bearing and are asserted rather than assumed:
  - **No other rider can see it, by construction.** There is no route that answers with somebody else's area — `/api/v1/me/private-area` has no user id in it — `SharedProfile` has no field for one, and nothing published to a ride carries it. This is the guarantee the feature actually rests on, and it is enforced by the shape of the API rather than by a switch.
  - **The server can see it, and that is said out loud.** *(changed in v0.28 — this reverses v0.13.)* The centre is a precise statement of where somebody lives, and it is now a column on `asp_net_users`, in the nightly backups (§9) and in the account export (§6.3). The device-only version was strictly better on this axis and strictly worse on the one that matters more: it was wiped in silence by app updates, reinstalls and new handsets, leaving riders broadcasting from their doorstep believing they were not. Losing the setting fails open; storing it fails closed. The Location screen states both halves in the same paragraph — where it is kept, and who else can read it — rather than only the flattering one.
  - **Suppression, not obfuscation.** No jittered or edge-snapped point is published in place of the real one; several such points would bound the true centre, which is the one number this protects.
  - **The rider comes off the map, and the stored row is deleted with them** *(corrected in v0.31 — the sentence above this list always said this and the code did not do it)*. Entering the circle sends one bit and no coordinate; the server deletes that rider's position in every adventure they are sharing with — immediately, not on the next flush — and tells those adventures. Merely ceasing to publish left the last fix before the driveway sitting in the database and on every co-rider's map for the rest of the ride, which is a **sharper** disclosure than most of what the feature withholds: a marker that has stopped moving a few streets from a house is a good guess at the house. The member list still shows them, labelled *private*, and the four route figures are absent rather than dashed — there is no position to derive them from.
  - **It suppresses *publishing*, and nothing else** *(corrected in v0.28)*. The rider's own map keeps drawing them, follow-me keeps following and heading-up keeps turning while they are inside the circle; the recorder keeps the points too, on the same phone, and the choice about them is offered again at save time (§15.1). This is a rule about what other people can see, and the earlier reading of it — blanking the owner's own screen — protected nobody: the only person it hid the house from was the one standing in it, at the moment they were most likely to be looking at the map.
  - **The gate is closed until there is an answer.** "The account has no area", "this device has not been told yet" and "we could not ask" are three states, not two, and only the first is safe to publish from. The device store caches the account's answer — including an explicit *none* — so a phone with no signal enforces yesterday's circle; a phone that has never been told suppresses everything, which costs nothing real, because publishing needs the same network the read just failed on.
- **Trimming a track in the web editor is the destructive counterpart** (§15.5): the removed points are gone from the stored track, from exports, and from every share link. Two caveats are stated in the UI rather than buried here, because a user trimming their house off a ride is making a privacy decision and deserves the truth about it:
  - the pre-edit original is retained for a **7-day undo window** unless the user chooses *remove the original now* (§15.6);
  - **nightly backups (§9) still contain it** until they roll out of retention. Deleting from the live database is not deleting from a backup, and saying otherwise would be the same class of error as v0.1's "positions are never persisted".
- **Photo metadata is destroyed on upload, not on display** (§16.4). Every image is re-encoded server-side with no metadata written, because an EXIF GPS tag in a photo taken at home would reinstate the exact address the trim above just removed — in a file handed to every member of the ride. The two features are one decision, and getting one right without the other is worth nothing.
- Full export and hard delete endpoints. Deleting an account cascades refresh tokens, devices, join requests and positions.
- Applicable law: **Australian Privacy Act / APPs**, and **GDPR** if EU users.

### 10.2 Store compliance (plan for it — it bites late)
- **Google Play:** background-location declaration plus prominent in-app disclosure *before* the permission prompt; a demo video is typically required. The **Data Safety form must declare that location is stored, not merely transmitted** (§5.5). It must also now declare **name, email address and phone number as collected-but-optional**, and — because §7.3 lets riders show them to each other — that some personal information is **visible to other users**, which is a distinct disclosure from sharing with third parties (which the app does not do). Note that Play and Apple both ask whether optional data is *required* to use the app; here the honest answer is no, which is worth stating precisely rather than approximately.
- **The home private area is itself stored location** *(new in v0.28)*, and the forms have to say so. It is not a fix — it is a point the rider typed — but it is a coordinate held on the server against an account, it is *precise*, and on both forms it belongs under location as collected and stored rather than being tacitly covered by the position rows. It is **not** visible to other users, which is the distinct disclosure the profile fields needed, so that box stays unticked for it (§10.1).
- **Apple:** `PrivacyInfo.xcprivacy` privacy manifest, clear purpose strings, App Privacy details covering location **collection and storage**. Background-location apps get extra scrutiny, so the ride-sharing purpose must be visible in the UI.
- **User-generated content changes what review asks for** *(new in v0.13, and larger in v0.14)*. Marker photos, titles and notes (§16.5) — and now a full comment thread with photos, reactions and polls (§17) — are visible to other riders, which puts the app under Apple's UGC rules and Play's equivalent: a way to **report objectionable content**, a way to **block a user**, and a stated commitment to act on reports. Small audiences do not exempt you — reviewers check that the mechanisms exist. Build them with the feature, not after a rejection.
- The Data Safety and App Privacy forms must also declare **photos and in-app messages** as collected, stored, and **visible to other users** — the same distinct disclosure the profile fields needed, for the same reason. "Messages" is its own category on both forms and is not covered by declaring photos.
- **A messaging surface raises the age-rating question.** Unmoderated user communication pushes the rating up on both stores; declare it accurately rather than discovering it at submission (§17.7).
- **Account deletion must be reachable from inside the app** on both stores — `DELETE /api/v1/me` needs a UI entry point, not just an endpoint.
- **The 180-day deletion policy belongs in the privacy policy** (§7.11), stated as retention, along with the fact that accounts holding rides are never auto-deleted.
- If and when social sign-in lands (§7.16), **Sign in with Apple becomes mandatory** on iOS.

### 10.3 Battery & data budget
- Target **< 8 %/hour** battery for `Balanced` recording with the screen off. **Screen-on with the MapLibre map live is the number v0.16 put at risk** and the one the Phase 0 spike must produce (§18.3) — a vector map in a WebView is not the same power profile as a native one.
- Position payload ≈ 24 bytes; a 5 s cadence ≈ 17 KB/hour up. A 2-hour ride with 10 members ≈ ~3 MB down. Acceptable on mobile data; keep the cadence configurable and back off when a member is stationary.
- **Several live rides multiply the downlink, not the uplink** (§5.7): one publish regardless, but one inbound batch per ride. Three live rides is ~9 MB down over two hours. Slowing the batch cadence for non-focused rides is the obvious lever if it ever matters, and is deliberately not built yet.
- Track uploads deferred to Wi-Fi by default.

### 10.4 Test-driven development

**TDD is the delivery unit.** Every phase in §11 names the failing test written first; no production type is introduced without a red test that demands it.

**Stack**

| Concern | Choice |
|---|---|
| Runner | xUnit |
| Components | **bUnit** (MIT) — every screen renders in `dotnet test`, no emulator, no browser (§18.7) |
| Assertions | **Shouldly** — permissive licence. *FluentAssertions v8+ requires a paid commercial licence; avoided deliberately, and under AGPL this is now enforced rather than intended (§14.6.3).* |
| Mocks | NSubstitute |
| Database | Testcontainers for PostgreSQL — real Postgres, real `ON CONFLICT` and partial-index semantics |
| Server | `WebApplicationFactory` |
| Clock | `Microsoft.Extensions.TimeProvider.Testing` → `FakeTimeProvider` |
| Email | fake `IEmailSender` collecting sink (§7.12) |

**Two decisions that make the timing-heavy parts testable at all:**

- **`TimeProvider` everywhere; never `DateTime.UtcNow`.** The 10 s flush, the 5 s broadcast, the staleness window, access-token expiry, lockout duration, the refresh grace window, the 24 h/1 h token lifespans, the 1-hour last-active throttle, the 24-hour registration ladder and the **180-day inactivity sweep** are all verified by *advancing a fake clock* — no `Task.Delay`, no flaky sleeps, and no test that would otherwise take six months. Construct timers as `new PeriodicTimer(period, timeProvider)` (available on `net10.0`).
- **SignalR against `TestServer`** needs `Server.CreateHandler()` wired into the `HubConnection`'s `HttpMessageHandlerFactory`, otherwise negotiation fails silently and the test hangs rather than failing usefully.

**Position cache** test list:

```
Upsert_NewRider_AddsEntryMarkedDirty
Upsert_OlderTimestamp_IsIgnored
Upsert_UnderParallelLoad_LatestTimestampWins
Flush_WritesOnlyDirtyEntries
Flush_ClearsDirtyFlagsAfterSuccess
Flush_LeavesEntriesDirtyWhenWriteFails
Flush_NoDirtyEntries_IssuesNoDatabaseCall
Flush_ManyRiders_IssuesExactlyOneCommand
Flush_DoesNotOverwriteNewerRowInDatabase          (integration — proves the WHERE guard)
Rehydrate_LoadsLiveAndWindingDownRidesOnly              (§5.6 — see also §5.9)
Rehydrate_SkipsPositionsOlderThanStalenessWindow
Rehydrate_LoadedEntriesAreNotDirty
Reads_BlockUntilRehydrationComplete
Shutdown_FlushesPendingEntries
SharingOff_DeletesThePositionButKeepsTheThread
MemberStopsSharing_DeletesPersistedRow
```

**Sharing consent, the idle sweep, multi-adventure publishing and the organiser's content switches** have their own list in §5.9. `NightlySweep_DeletesIdlePositionsAndClearsTheirSharing` is the one that matters most — it is the only unconditional reclamation left, and it has to work with every phone in the adventure switched off.

**Identity, joining and account lifecycle** have their own list in §7.15 — it is the largest single block of tests in the project, which is appropriate given that the membership check is now the only thing protecting a rider's location.

**Ride comments, reactions and polls** have their own list in §17.10. Since v0.26 the notification rules are client-side and unit-tested rather than server-side and integration-tested — `CommentNotifierTests` (`DLR.UI.Tests`) is where they live, because a local notification never involves the server at all. The two worth writing first are `ARidersOwnPost_NeverNotifiesThem` and `AThreadLeftOpenOnABackgroundedPhone_DoesNotSuppressAnything`: the first is the rule whose absence would notify every rider about everything they said, and the second is the one whose absence would quietly reinstate the `Live` silence through a page left mounted in a tank bag. `Car_ThreadIsNotRenderedAtAll` is the one safety rule that is still structural and still server-adjacent, and it has not moved.

**Map markers and photos** have their own list in §16.8. The EXIF assertions are the ones to write first — `Photo_ExifGpsTag_IsAbsentFromStoredImage` is a privacy guarantee that no amount of careful code review substitutes for.

**GPX import and track editing** have their own list in §15.9, including a hostile-input corpus. Import is the only place the server parses an untrusted file format, and editing is the only place it destroys user data on purpose — both earn the coverage.

**Architecture tests** (`DLR.Architecture.Tests`) — conventions that are only real if a build enforces them:
- `DLR.Core` references no MAUI assembly.
- **`DLR.UI` references no MAUI assembly and no platform API** — it must compile into WebAssembly (§18.2). This is now the single most load-bearing rule in the list: break it and the shared UI silently becomes mobile-only.
- **No `#if` platform symbols inside `DLR.UI` components** (§18.2). Conditional compilation in a shared library is two libraries wearing one name.
- ~~`Microsoft.Maui.Controls.Maps` is referenced only under `DLR.App/Maps/Native/`~~ — **the assertion is now that it is referenced nowhere at all** (§18.3). The rule earned its keep: because the reference lived in exactly one folder, deleting it in v0.16 was a contained change.
- No `DateTime.Now` / `DateTime.UtcNow` outside `DLR.TestSupport`.
- No raw SQL outside `DLR.Server/Positions/PositionWriter`, `DLR.Server/Identity/` and `DLR.Server/Maintenance/`.
- **Exactly one GPX parser and one stats implementation, both in `DLR.Core/Tracks/`** — no second codec in `DLR.Server` or `DLR.App` (§15.7).
- **No `XmlDocument`, and no `XmlReaderSettings` with `DtdProcessing` other than `Prohibit`**, anywhere in the solution (§15.3). This is a one-line test that closes an XXE hole permanently.
- **Image decoding happens only in `DLR.Server/Photos/`** (§16.4) — one ingest path, so metadata stripping cannot be bypassed by a second one.
- **No API surface returns `AppUser`** — shared profile data leaves the server only via `SharedProfile.For` (§7.3).
- **Every `MapHostKind` has at least one registered factory** — a car screen must fail at startup, not render blank (§4.6).
- Car screens reference `IRideSessionState`, never a phone ViewModel (§4.6).
- **The reported commit matches the running assembly's `InformationalVersion`** — the AGPL §13 source pointer cannot be allowed to drift from the build (§14.6.2).

**Licence gate in CI** — not a test project, but the same kind of enforcement: a scan over the full transitive package graph that fails on any licence outside the allow-list, or on a package whose licence is unknown (§14.6.3). This is what makes "permissive dependencies only" survive a deadline, in the same way the map-isolation rule needed a build to enforce it.

**GPX replay harness** — a `FakeLocationProvider` driven from `.gpx` files, built in Phase 0 as a first-class fixture. It shares the §15.7 codec, so the harness and the import feature are exercised by each other's tests. It makes the recording pipeline testable in the simulator without going for a ride, and it pays for itself the first afternoon.

**Where coverage matters:** high on `DLR.Core` (pure logic), the flush/rehydrate path, every auth branch, and **every clause of the deletion predicate** (§7.11) — that is destructive code operating on a timer, so each `NOT EXISTS` gets its own test.

### 10.5 Code style — tabs

Indentation is **tabs**, width 4. A root `.editorconfig`:

```ini
root = true

[*]
indent_style = tab
indent_size = 4
end_of_line = crlf
charset = utf-8
insert_final_newline = true
trim_trailing_whitespace = true

# YAML forbids tab indentation outright.
[*.{yml,yaml}]
indent_style = space
indent_size = 2

# Leading tabs in Markdown are parsed as code blocks.
[*.md]
indent_style = space
indent_size = 2

[*.cs]
csharp_prefer_braces = true:warning
dotnet_diagnostic.IDE0055.severity = warning
```

The YAML and Markdown carve-outs are **mandatory, not stylistic** — tabs are invalid in YAML and change meaning in Markdown, so a blanket `indent_style = tab` would break `docker-compose.yml` and the CI workflow.

Enforced by `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` in `Directory.Build.props` and `dotnet format --verify-no-changes` in CI.

**What the formatter does not reach** *(revised in v0.16)*: `dotnet format` handles the C# inside a `.razor` file but not its markup, and there is now a great deal more Razor than there was XAML — `DLR.UI` is most of the UI (§18). So Razor markup indentation is convention plus review, and the one remaining `MainPage.xaml` is not worth tooling for. Add `[*.razor] indent_style = tab` to the `.editorconfig` above so editors at least agree, and accept that CI cannot verify it.

---

## 11. Delivery Plan

Each phase leads with the first failing test.

| Phase | First failing test | Scope | Exit criterion |
|---|---|---|---|
| **0 — Spikes** (1–2 wk) | `Replay_KnownGpx_ProducesExpectedDistanceAndAscent` | GPX replay harness; background GPS on both platforms; **`DLR.UI` skeleton rendering in both a `BlazorWebView` and WASM (§18)**; **MapLibre GL JS in the WebView on both phones, 20 pins updating every 5 s — measure battery (§4.5)**; SignalR through Caddy; **verify an `androidx.car.app` .NET binding exists** (§4.6) | A 2-hour ride recorded with the screen off on a real iPhone **and** a real Android, no gaps — plus a written answer on the Android Auto binding, and a battery number for the WebView map against §10.3's 8 %/hour |
| **1 — Solo** | `Register_UsernameAndPasswordOnly_Succeeds` | Username/password registration, permanent refresh tokens, IP ladder, optional email + confirm/reset, `last_active_utc`. Record, store, list, view, GPX export. Track upload. **GPX import on app and web, with the full hostile-input corpus (§15.3)**. **`DLR.UI` shared components in both hosts; one map module — MapLibre + OSM — on every host, with no credential and no token endpoint (§4.5)**. Web track view. **`LICENSE` + `/api/v1/about` + footer source link, and the CI licence gate** (§14.6) | Install on your own phone and stop using anything else — including a reinstall that signs straight back in without typing a password |
| **2 — Group rides** | `JoinByCode_ApprovalRide_CreatesPendingRequestOnly` | Both join paths + admit/decline, **join-time sharing consent and the per-adventure toggle (§5.6)**, **multi-ride membership and publishing (§5.7)**, **organiser content switches (§5.8)**, planned route, live map, member list, batched fan-out, position cache + 10 s flush (§5.5), hub membership authz. **Web track editor + undo window (§15.5–15.6)**. **Markers with photos (§16)**, rendering fully — MapLibre draws icons, rotation and labels from Phase 1, so v0.13's degraded-pin fallback never has to ship (§18.3). **Ride thread: text, photos, pinning, reactions, and the notification rules — uniform across every ride state since v0.26 (§17.1, §17.6)** | 4 people, 1 real ride, all pins moving; one joined by code, one admitted from a request; kill and restart the server mid-ride and watch the map come back warm. **One rider joins without sharing and stays invisible on the map while still seeing everyone; another turns sharing off mid-ride and watch their pin go from all three other phones at once.** **Trim your own house off a real recorded ride, watch the distance change, undo it, then purge the original.** **Drop a hazard marker with a photo mid-ride and have it appear on three other phones; confirm the stored image carries no EXIF GPS** |
| **3 — Polish + car** | `Snapshot_GapList_OrdersRidersAlongRoute` | `IRideSessionState` + gap list, **Mapsui renderer** (on the critical path for both the car *and* markers, §16.3), **full marker rendering — icons, rotation, labels**, **Android Auto + CarPlay heads (§4.6)**, inactivity cleanup behind dry-run, ~~push notifications~~ *(shipped early and locally in v0.26 — §17.6 — since it needed no store-side credential)*, **polls (§17.5)**, **report/block moderation (§17.7)**, off-route alerts, ride summaries, load test, **social sign-in + guest riders (§7.16)** | Store submission; a real ride navigated from a head unit; a week of dry-run deletion logs read |
| **4 — Beyond** | — | Ride photos on the timeline, leader hand-off, public ride discovery, TOTP 2FA, Wear OS / watchOS glances | — |

Ship Phase 1 to yourself before building anything in Phase 2.

**Sequencing note on UGC.** Photos, markers and the thread land in Phase 2, while report-and-block lands in Phase 3. That is deliberate, not an oversight: Phase 2's audience is four people who know each other, and Phase 3's exit criterion is *store submission*, which is the moment the moderation tools stop being optional (§16.5, §17.7). The rule is that moderation ships **before the first submission**, not before the first comment.

**Sequencing note.** Registration got *simpler* in v0.5 (one required field, no gate), but account lifecycle got more complex. Keep the cleanup job in Phase 3 with `DryRun = true` so real deletion only ever happens after there is data worth being careful about — and never enable it in the same deploy that introduces it.

---

## 12. Key Risks

| Risk | Severity | Mitigation |
|---|---|---|
| **An account with no email is unrecoverable** | **High** | Prominent warning at registration, in the user's own words (§7.2); prompts to add an email after the first saved ride and on ride creation; permanent sessions mean the password is rarely needed; social sign-in in Phase 3 gives a no-typing recovery path (§7.16). This is an accepted product trade-off, not a bug — but it must stay visible in the UI |
| Android OEM battery managers killing the foreground service | **High** | Foreground service + battery-exemption prompt + in-app troubleshooting page; test on Samsung and Xiaomi specifically |
| Play Store rejection over background location | High | Prominent disclosure, demo video, and a Data Safety form that declares location **storage** (§10.2) from the first submission |
| **`ForwardedHeaders` misconfigured** | **High** *(was Medium)* | Now breaks registration outright, not just rate limiting: every signup looks like it comes from Caddy, so the fourth user ever is asked for an email. `Registration_LadderUsesForwardedClientIp` plus a staging check before first public signup (§7.8) |
| **Automated deletion removes an account it shouldn't** | **High** | Five-clause `NOT EXISTS` conjunction, a test per clause, `DryRun` default-on, a kill switch, per-run batch cap, and alerting on counts (§7.11) |
| **A forgotten switch becomes indefinite sharing** | **High** | The risk v0.32 inherited when it deleted the adventure's end (§5.6). Nothing but the rider now stops it, so the app's obligation is to keep the fact in front of them: a persistent notification while the receiver runs, a red strip on the live map whenever sharing is off *or* on, and the member list showing every rider's state to the whole group. The fourteen-day sweep is the floor under a phone that has stopped sending, **not** a limit on one that has not |
| **A rider believes they are not sharing when they are** (or the reverse) | **High** | Consent defaults to off and is asked explicitly at join (§5.6); the ride screen shows the current state rather than hiding it in settings; the member list distinguishes *not sharing* from *no signal*; and several concurrent rides each show their own state (§5.7). Ambiguity here is the failure mode, not a leak in the transport |
| Per-ride consent applied in the wrong place with several live rides | **High** | Filtered on the **write**, never on the read (§5.7): a rider not sharing with a ride has no row in it at all, so no cache entry, no flush and nothing on the wire. `Publish_SharingInRideAOnly_StoresNoRowForRideB` is the assertion |
| Hub membership check is now the only barrier to a stranger's location | Medium | Email verification never protected this anyway; organiser consent on both paths (§5.2) plus `Hub_JoinRide_NonMemberIsRejected` and `Hub_JoinRide_PendingRequesterIsRejected` (§7.6) |
| Permanent refresh token on a lost device | Medium | Hashed at rest, this-device-only keystore, rotation with reuse detection, per-device revocation from any other device (§7.10). Accepted deliberately — it is what "never sign in again" costs |
| Refresh rotation locks users out via a self-inflicted replay | Medium | Client-side single-flight refresh **and** a 10 s server-side idempotency window. Now the *most likely* logout path, so both halves are mandatory (§7.4) |
| Transactional email deliverability | Medium | SPF/DKIM already warm on the Zoho domain (§7.12); volume is lower now that most accounts have no address |
| Mailbox throttling or ToS action once volume grows | Medium | Cut over to ZeptoMail at store submission or ~100 sends/day (§7.12) |
| Username squatting and impersonation | Medium | The handle is the only visible name and it is permanent, so a desirable name is first-come-forever and the owner cannot rename out of a bad choice either. ASCII-only kills homoglyphs, reserved names are blocked, and report-and-remove is the only remaining lever — removal frees the name (§7.2, §7.11) |
| A user regrets their permanent username | Low–Med | Confirmation step before account creation (§7.2). The escape hatch is to abandon the account and register again — cheap while it holds nothing, expensive once it holds rides. Accepted deliberately as the price of immutability |
| **A profile field leaks because one read path forgot a switch** | **High** | Cannot be un-leaked, so the control is structural rather than careful: a single `SharedProfile.For` factory that requires the viewer relationship, an architecture test banning `AppUser` on the wire, omit-when-null so withheld and unrecorded look identical, and a test per field (§7.3) |
| Shared phone number becomes a harassment vector | Medium | Off by default, adventure-scoped, and revoked when co-membership ends — leaving, being removed, or the adventure being deleted. The organiser controls who is in the adventure at all (§5.2), so the audience is never strangers-at-large. Unverified by design, so it is also never an identity claim |
| **Apple refuses the CarPlay entitlement** | **High** | Request filed in Phase 1 so the wait overlaps other work (§4.6). No engineering mitigation exists — if refused, iOS ships phone-only and the `carplay-driving-task` entitlement is the fallback to investigate. Do not promise CarPlay in store copy until it is granted |
| **No usable .NET binding for `androidx.car.app`** | **High** | Phase 0 spike answers this before any planning depends on it (§4.6). Fallback is a binding project over the AAR — real work, and the reason it is a spike rather than an assumption |
| Mapsui is now on the critical path, not optional | Medium | Car support cannot use the native map control at all (§4.6). `IMapRenderer` was designed for exactly this swap, and `MapHostKind` makes an unsupported pairing a startup failure rather than a blank screen |
| Auto/CarPlay review rejection on distraction grounds | Medium | Templates enforce most rules mechanically; two-screen depth, capped list rows and single-tap actions are design constraints from the start (§4.6), not late fixes. DHU and CarPlay Simulator before hardware, one real head unit before submission |
| ~~Built-in map's pin limits force an early Mapsui swap~~ | — | **Retired in v0.16.** There is no built-in map in the design any more (§18.3) |
| **A JS map in a WebView misses the battery or frame-rate target** | **High** | The genuine unknown since v0.16, and the one map risk v0.24 did not retire — it made it easier to measure. Every host now runs **MapLibre GL JS**, so one **Phase 0 spike** with a number attached (§10.3's 8 %/hour) answers for all three instead of one per provider. There is no fallback module any more: the fallback is the tile source (raster → vector, or a lighter style), which is §13 Q26's decision anyway |
| ~~**MapKit JS is not licensed, or not usable, in an Android WebView**~~ | — | **Retired in v0.24.** Closed twice over: v0.21 stopped putting MapKit on Android, and v0.24 removed MapKit entirely. MapLibre GL JS is BSD-2-Clause and carries no per-platform licensing question at all (§4.5) |
| ~~**Shipping Android depends on an Apple Developer account**~~ | — | **Retired in v0.24.** No host depends on an Apple credential for its map. The account is still required to ship iOS and CarPlay (§4.6) — what is gone is the coupling that let an Apple terms change take the map off *Android* |
| **Offline maps are still not shipped** | Medium | v0.24 withdrew v0.19's "lost, not deferred": MapLibre will render from a local PMTiles archive, so this is a tile-source question again rather than an SDK wall (§4.5). It remains a *risk* because nothing has been built — recording, markers and the thread work in a dead zone; the map behind them still does not, until §13 Q26 lands |
| OSM's tile usage policy stops covering the web app | Medium | It is a donated service and the policy forbids heavy or commercial use. Fine for development and a handful of friends, not for a public launch — so the move to self-hosted PMTiles is scheduled against the announcement, not against a complaint (§13 Q26) |
| Two map modules drift in behaviour | Low–Med | Back in scope as of v0.19, having been eliminated by v0.16. `MapCapabilities` declared per module keeps differences explicit, and §18.8 tests both. The marker rendering in §16.3 is the most likely place to diverge |
| Shared components drift into two implementations | Medium | `#if ANDROID` in `DLR.UI` is the failure mode, and it starts as one harmless line. Banned by architecture test (§10.4, §18.2); the correct move is always an interface with a per-host registration |
| Blazor Hybrid maturity on a phone-shaped surface | Medium | Gestures, keyboard avoidance, scroll behaviour and safe-area insets in a `BlazorWebView` are less polished than native XAML. Mitigation is the Phase 0 skeleton spike — build the two hardest screens (live map, thread) before committing, not after |
| WASM first-load payload | Low–Med | A few MB on first visit. Trimming, brotli from Caddy, long-lived immutable cache headers on fingerprinted assets, and static SSR on exactly the pages a first-time visitor hits (§18.4). Returning users are cached, and the traffic budget is not the issue (§9.1) — first-paint latency for a new visitor is |
| MAUI maturity on background/long-running work | Medium | Keep platform code thin behind `ILocationProvider`; be ready to write real Java/Swift interop |
| Websocket churn in poor coverage | Medium | Aggressive reconnect + snapshot-on-reconnect; never assume ordered delivery |
| Two token lifespans collapse into one | Low–Med | `TokenLifespan` is global; separate provider plus `ResetPassword_LifespanIsIndependentOfConfirmationLifespan` (§7.7) |
| 10 s flush becomes a write hot spot | Low–Med | Single `UNNEST` upsert — one round trip regardless of rider count; verified by the Phase 3 load test |
| Single VPS holds live ride state and all session state | Medium | Positions are recoverable-by-design; refresh tokens and accounts are in Postgres and covered by the nightly backup — which is now the only record of an email-less account |
| Map tile bandwidth cost | Low *(re-assessed in v0.18 and again in v0.19)* | Not ours to pay for now — Apple serves the phone, OSM serves the web (§4.5). Returns when self-hosted PMTiles does, and even then the VPS's 20 TB/month covers it many times over (§9.1) |
| **The VPS disk fills** | **Medium** | New in v0.18, and nastier than it sounds: a full disk stops **Postgres writing**, not just uploads. Tiles, blobs and the database share 40 GB. Per-account quotas (§13 Q13), a disk-usage alert next to the maintenance alert, and a Hetzner volume as the one-command escape (§9.1). The failure mode is silent until it is total |
| **AGPL terms conflict with App Store / Play distribution** | **High** | Real, not theoretical — it is why VLC left the App Store. Mitigated by the GPL-3 §7 additional permission covering store distribution and proprietary platform SDK linking (§14.6.5), granted repo-wide so forks inherit it, with inbound = outbound keeping it valid for contributed code. Get the wording reviewed before the first public push, while you are the only copyright holder |
| **The deployed build's source offer drifts from the repo** | Medium | AGPL §13 is about the *running* version, so a stale or hand-maintained commit string is a licence breach that looks like a cosmetic bug. Commit embedded at build time via SourceLink, asserted by `About_CommitMatchesAssemblyInformationalVersion`, and CI refuses to publish an image built from an unpushed tree (§14.6.2) |
| **A hostile GPX file** — XXE, entity expansion, or a million points | **High** | The first untrusted file format in the project, and one the *server* parses. `XmlReader` with `DtdProcessing = Prohibit` and a null `XmlResolver`, streaming rather than `XDocument.Load`, hard size **and** point caps enforced mid-parse, and an architecture test banning `XmlDocument` outright (§15.3, §10.4). A synthetic hostile corpus ships with the tests |
| **An edit destroys ride data the user wanted** | **High** | Editing is deliberately destructive, so the brakes are procedural: a preview showing the new distance and duration before saving, an explicit confirm, a **7-day undo window** with the original retained, and the removal expressed as ranges the user can see highlighted on the map (§15.5–15.6). The nightly purge is the same job that already needs watching (§7.11) |
| Unbounded imports exhaust storage or backup bandwidth | Medium | A €4 VPS with 40 GB of disk (§9.1) has no headroom for someone uploading 25 MB files in a loop. Per-file size and point caps, per-account track and storage quotas, and an import rate limit (§15.3, §7.8) — all configuration, all tunable without a release (§14.5) |
| Stats drift between recording, import and editing | Low–Med | Three entry points into one pipeline is how ascent quietly comes out different depending on where a track came from. One implementation in `DLR.Core/Tracks/` (§15.7), an architecture test forbidding a second, and `Edit_NoOpEdit_ProducesIdenticalStats` as the guard that a rewrite changed nothing it should not have |
| **A photo's EXIF GPS republishes what a track trim removed** | **High** | The two features would otherwise cancel out (§15.6, §16.4). Mitigated structurally, not carefully: every image is re-encoded server-side with no metadata written, there is exactly one ingest path enforced by an architecture test, and `Photo_ExifGpsTag_IsAbsentFromStoredImage` is the assertion that keeps it true after the next refactor |
| **A malicious or malformed image** — decompression bomb, hostile decoder input | **High** | Byte cap *and* decoded-pixel cap, dimensions checked from the header before any allocation, format by content sniffing rather than extension, and re-encoding rather than passing the original through (§16.4). Same posture as GPX (§15.3), for the same reason |
| **A ride thread encourages phone use while riding** | **High** | The product risk in this feature. **v0.26 removed the strongest mitigation against it** — comments push while the ride is `Live` — and **v0.27 removed what was left**: importance is now `High`, so a banner does slide over the live map, and the app no longer holds a notification back even for the thread on screen. One structural mitigation survives, and it is the one the app does not control: the thread never renders on a car head unit (`Car_ThreadIsNotRenderedAtAll`, §4.6). Everything else is the rider's own — Do Not Disturb, a riding or driving focus mode, the per-app and per-channel switches (§17.1, §17.6). This row keeps its **High** rating on purpose and the rating is now doing more work than it was: the risk did not fall, the control left the app entirely, and a rider who has never set up a focus mode is unprotected by design. If real rides show mid-ride reading, the answer is to reinstate the `Live` row of §17.6's table — or the channel's `Default` importance — not to add a warning dialog or an in-app mute |
| Notification storms from an active thread | Medium | Coalesced reactions (§17.4), no notification per reaction or vote at all, and — since v0.26 replaced the `Live` silence — **one card per adventure**: every post shares a tag, so the newest replaces the last rather than stacking twenty entries a rider has to dismiss at the lights (`EveryPostInOneAdventure_SharesATagSoTheNewestReplacesTheLast`). Beyond that it is the platform's Do Not Disturb. Twelve riders on a wet Sunday generate a lot of chat |
| Moderation load once the app is public | Medium | Report-and-block with a content snapshot (§17.7), organiser deletion inside their own ride, and audiences bounded by organiser consent (§5.2) so no comment ever reaches strangers-at-large. Proactive scanning is deliberately out of scope and recorded as §13 Q17 |
| Thread storage grows without bound | Low–Med | Caps per adventure and photos already quota'd (§16.4). Text is cheap; the photos attached to it are not. The `Archived` read-only state that used to be listed here never existed in the code and is gone (v0.32, §17.6) |
| **UGC rules bite at store review** | Medium | Photos and notes visible to other riders make this a UGC app: Apple and Play require reporting, blocking, and a response commitment (§10.2, §16.5). Cheap to build with the feature, a whole review cycle to add afterwards |
| Photo storage outgrows the €4 budget | Medium | Photos are an order of magnitude larger than tracks, and they land on the same 40 GB disk as everything else (§9.1). Downscale to 2048 px, thumbnails for callouts, per-account quotas (§13 Q13), and Caddy caching the reads. Uploads go through the VPS deliberately (§16.4) — that cost is accepted to keep metadata stripping non-optional |
| ~~Marker icons cannot render on the Phase 1 native map~~ | — | **Retired in v0.16**, and closed for good by v0.21: every icon, rotation and label is drawn by `SkiaMapOverlay`, not by a base map (§16.3). v0.13's degradation path now applies only to the car renderer |
| Orphaned photo blobs after a delete | Low–Med | `ON DELETE CASCADE` does not reach object storage, so blob deletion is explicit and the nightly job sweeps orphans as a backstop (§16.6, §7.11). An orphan here is a privacy failure wearing a storage bill's clothes |
| **Trimmed points survive in backups** | Medium | Unavoidable and therefore disclosed rather than mitigated: the UI and privacy policy say the removed points leave the live database immediately (or after the undo window) but persist in nightly backups until retention rolls (§10.1, §15.6). Bounded backup retention is the only real control |
| A dependency arrives under a licence that cannot ship in an AGPL project | Low–Med | Allow-list plus a CI licence scan that also fails on *unknown* (§14.6.3). Cheap to fix at PR time, expensive to unpick after release |

---

## 13. Open Questions

1. **Primary audience** — motorcycles, bicycles, or 4WD? It changes accuracy profiles, ascent handling, and whether "speed" or "cadence" is the hero stat. *Worth settling before Phase 1.*
2. **Voice/audio in-ride?** Intercom-adjacent features are a much bigger project; explicitly in or out.
3. **Spectator links** — should a non-member watch a live ride via a link? Positions are persisted, so a spectator link is a standing read grant over stored location data. Sharper now that organiser consent is the whole access model: a spectator link is the one way into a ride's data that is *not* a per-person admission, so it needs its own expiry and revocation.
4. ~~**Anonymous use**~~ — **largely resolved (v0.5):** with email optional and username-only registration, an account is already nearly frictionless. `is_guest` remains in the schema for a device-bound password-less participant in Phase 3 (§7.16).
5. **Retention** — how long are adventures, their recorded tracks and their threads kept? (Live positions: deleted when the rider stops, leaves or is removed, and at fourteen days idle as a backstop, §5.6. Empty dormant accounts: 180 days, §7.11. An adventure and its thread live until somebody deletes them, §5.1 — which makes this the one entity in the product with no retention answer at all, and v0.32 sharpened the question by making deletion the only ending there is.)
6. **New-device email alerts** — every new device, or only a new device in an unusual location? Every-time is simpler and safer; it also trains people to ignore them. Moot for accounts with no address.
7. ~~**Email provider**~~ — **resolved (v0.4):** Zoho Mail SMTP for Phase 0–1, ZeptoMail before real users (§7.12). Three setup facts to confirm, none of them design decisions: the Zoho **datacentre region** (it sets the SMTP host), whether the plan **includes SMTP access**, and which **`no-reply@` alias** to send as.
8. **Ride discovery for join requests** *(new in v0.5)* — path 2 in §5.2 needs the rider to reach the ride somehow. v0.5 assumes an organiser-shared link. Should there also be browsable nearby/public rides? That is a much larger surface: discovery plus request spam plus the privacy question of listing rides at all.
9. ~~**Username changes**~~ — **resolved (v0.7): never.** Usernames are immutable, with a confirmation step at registration as the only safeguard (§7.2). Deleted accounts release their name, which is safe because such an account was never in a ride (§7.11).
10. **Should the app auto-create an account on first launch** *(reshaped by v0.7)* — immutability largely answers this. A silently generated handle would be **permanent**, so the user would be stuck as `rider_8f21` forever, and prompting for a name instead is just registration by another route. The remaining variant worth considering is deferring account creation entirely: record locally with no account (§7.9 already supports this) and ask for a username only when the rider first needs the network — uploading, or joining a ride. That keeps first launch free of any signup without ever assigning a name the user did not choose.
11. ~~**Licence**~~ — **resolved (v0.11): AGPL-3.0-only**, plus an additional permission under GPL-3 §7 for app-store distribution and proprietary platform SDK linking (§14.6). Network copyleft is the only licence that reaches someone running a modified server, which is the only way this software is ever "used". Settled now precisely because inbound = outbound with no CLA (§14.6.4) means a relicence would later need every contributor's agreement.
12. ~~**Rate limit on join-code submission**~~ *(new in v0.10)* — **resolved (SRV-20)**: 10 failed attempts per minute per address and 30 per hour per account, both configurable under `Rides:`. The decision "how strict" turned out to be secondary to **what is counted**: the limiter counts *failures only*, so a rider joining ten rides in a minute is unaffected while a client trying ten codes meets it immediately. That distinction is invisible in a passing suite — an all-attempts limiter satisfies the obvious test too — so it has its own guard, `JoinByCode_SuccessfulJoins_AreNotCountedAgainstTheLimit`.
13. **Per-account storage quota** *(new in v0.12)* — §15.3 says imports must be bounded and makes the caps configuration, but the actual numbers are unset. What is a fair ceiling on tracks per account and megabytes per account, given a 40 GB VPS disk shared with Postgres and the tile extract (§9.1)? Cheap to set now, awkward to lower once people are over it.
14. **Editing beyond removal** *(new in v0.12)* — v1 removes points and nothing else: no splitting one track into two, no merging, no moving or inserting a point, no redrawing a span. Splitting is the most likely next ask (one file containing a whole weekend), and it is the only one that needs no new geometry — a split is two range removals over a copy. Moving or inserting points is a different feature: it makes the track a drawing rather than a record, which is a product decision, not a technical one.
15. **Should the app be able to edit too?** *(new in v0.12)* — deliberately web-only in v1 (§6.1). The domain code is in `DLR.Core` and therefore already available to MAUI, so this is a UI question — whether trimming is workable on a phone-sized map — not an architectural one.
16. **Marker visibility on a shared track** *(new in v0.13)* — a track's markers are visible to whoever the track is (§16.1). Should a marker be individually private, so a rider can annotate *"awful surface, don't come back"* on a ride they also share publicly? It is one `IsPrivate` flag, but it needs UI that makes the state obvious at a glance, and a wrong default here is a leak rather than an inconvenience.
17. **Does UGC need moderation beyond report-and-remove?** *(new in v0.13, widened in v0.14)* — §17.7 builds reporting and blocking because the stores require them. Proactive scanning (hash matching against known illegal material) is a different order of cost and commitment, and small organiser-admitted audiences make it hard to justify today. The honest answer changes if public ride discovery (Q8) ever ships.
18. **Comments on shared tracks** *(new in v0.14)* — §17.1 confines the thread to group rides, because commenting on a public share link would let people the organiser never admitted post into someone's space. Is a track-scoped thread wanted at all, and if so is it members-only, or open to anyone with the link plus a moderation story that does not exist today?
19. **@mentions** *(new in v0.14)* — a natural fit that v1 skips. Immutable usernames (§7.2) make it unusually cheap here: a mention can be stored as plain text and still resolve forever, with no rename propagation and no stale reference. The open part used to be notification behaviour, which collided head-on with §17.6's `Live` silence; v0.26 removed that silence, so a mention now has nothing to erode and simply pushes like any other post.
20. **Threaded replies** *(new in v0.14)* — v1 is a flat thread. Replies change deletion semantics (an orphaned reply chain), ordering, and the pagination contract, so it is a real feature rather than a field.
21. **Should the sharing toggle reach the car screen?** *(new in v0.15)* — §4.6 caps the head unit at one-tap actions and v1 leaves sharing off it, so stopping mid-ride means stopping the bike. Defensible for a privacy control that deserves a moment's thought, and arguably wrong if someone wants to drop out of a ride while moving. One template action either way.
22. **Wind-down default length** *(new in v0.15)* — 120 minutes is a guess that sounds right for a day ride. Too short strands the people it exists for; too long is tracking. Worth revisiting after one real season, and it is configuration (§14.5), so it moves without a release.
23. **Non-focused ride cadence** *(new in v0.15)* — §5.7 sends every live ride's batch at the full 5 s rate. Slowing the ones a rider is not looking at is the obvious saving and is deliberately not built; the question is whether anyone is ever in enough simultaneous live rides for it to matter.
24. **Does the web need offline at all?** *(new in v0.16)* — §18.6 makes the WASM client online-only. A service worker plus IndexedDB would give the planning surface some resilience, but it duplicates the sync engine that already exists for mobile (§4.4) in a second, weaker form. Probably not worth it — worth asking once rather than drifting into it.
25. **Native fallback for the live map** *(new in v0.16)* — if the Phase 0 WebView battery spike (§18.3) fails, does the live ride screen become a native MAUI page while everything else stays shared? That is the designed retreat, and it is worth knowing in advance which screens would follow it down.
26. **When does the web leave OSM tiles?** *(new in v0.19)* — "to begin with" needs a trigger, not a hope. `tile.openstreetmap.org` is donated infrastructure whose policy does not cover a public launch (§4.5), so the honest answer is *before the web app is announced to anyone outside the test group*, with self-hosted PMTiles (§9.1) as the destination. The open part is only whether that is worth doing earlier, since it also unlocks the offline map option.
27. **Do riders actually miss offline maps?** *(new in v0.19, reopened in v0.24)* — no longer blocked on an SDK: MapLibre reads a local PMTiles archive, so the cost is a few GB on the phone and the packaging around it, not a third renderer (§4.5). Still worth answering with real riders in a real dead zone rather than in advance, because it decides whether the §13 Q26 archive gets a phone-side download or stays server-only.
28. **Should CarPlay use native MapKit instead of Mapsui?** *(new in v0.19, weakened by v0.24)* — the argument was that Apple Maps was already the phone's provider, so a native `MKMapView` inside the `CPWindow` was the natural pairing. That pairing is gone: the phone is on MapLibre, so native MapKit on CarPlay would now be a *second* Apple dependency rather than a consistent one, on top of Mapsui for Android Auto. Weaker than when it was asked; revisit only if CarPlay quality becomes the thing being judged.
29. **Closing the flush-versus-delete race** *(new in SRV-22)* — a write-behind flush already in flight can re-insert a position row that a concurrent delete has just removed (§5.5). One round trip wide, and it needs a delete to land inside it, but what it leaves behind is the position at rest §10.1 forbids, and with the §7.11 nightly sweep as the only backstop the exposure is up to a day. Two candidate fixes, neither expensive: a tombstone set the flush filters its batch against immediately before writing, or a join to `group_ride_member` in the upsert so the statement itself cannot write a row for a rider who has stopped sharing. The second is self-maintaining and costs the hot path a join; the first is cheaper and needs its tombstones expired somewhere. **Worth closing before live sharing is switched on for anyone real.**

---

## 14. Open Source and the Repository Boundary

The project is public and **distributed under AGPL-3.0** (§14.6). The rule for what goes in it is simple and worth stating before the table:

> **Everything that describes *how the system works* is committed. Everything that *grants access to a running instance* stays local.**

Design, schema, migrations, infrastructure definitions and abuse-control logic are all public. Keys, tokens, passwords, signing material and real user data are not.

Note that the licence choice makes part of this a *product* requirement rather than a repository one: AGPL §13 obliges the running server to offer the source of the exact build it is running, which is why `GET /api/v1/about` and the web footer exist (§14.6.2).

### 14.1 Commit these

| Item | Notes |
|---|---|
| All source — `DLR.Core`, `DLR.UI`, `DLR.App`, `DLR.Web.Client`, `DLR.Server`, all test projects | The point of the exercise |
| `.sln`, `.csproj`, `Directory.Build.props`, `.editorconfig` | Including the `UserSecretsId` GUID — it is a reference, not a secret |
| EF Core migrations | Schema, not data. Contributors need them to stand up a local database |
| `docker-compose.yml`, `Caddyfile` | With environment-variable *references*, never values (§14.3) |
| GitHub Actions workflows | Secret **names** are committed; values live in GitHub Secrets (§14.4) |
| `appsettings.json` | Non-secret defaults and placeholders only |
| `*.template.json`, `.env.example` | The documented shape of every local file a contributor must create |
| `AndroidManifest.xml`, `Info.plist`, `*.entitlements` | Including the CarPlay entitlement declaration (§4.6) — declaring a capability is not a credential |
| Synthetic GPX fixtures | Generated or scrubbed — see §14.2, this one has teeth |
| `Documentation/`, `README.md`, `SECURITY.md`, `CONTRIBUTING.md` | This document included |
| `LICENSE`, `LICENSE.exceptions` | Verbatim **AGPL-3.0** text plus the §7 additional permission — required, not optional; an unlicensed public repo grants nobody anything (§14.6) |

### 14.2 Never commit these

| Item | Why it matters here |
|---|---|
| **JWT signing key** (§7.4) | Forges an access token for any user. The single worst leak in the project |
| **Zoho app-specific password / ZeptoMail token** (§7.12) | Sends mail as your domain — phishing with valid SPF and DKIM |
| **PostgreSQL password / full connection string** | Everything: accounts, positions, tokens |
| **Android upload and release keystore** (`*.jks`, `*.keystore`) plus passwords | Effectively unrotatable. A leaked signing identity is the one mistake here with no clean recovery |
| **Apple signing material** — `*.p12`, `*.p8`, `*.mobileprovision`, App Store Connect API keys | Ships builds as you |
| ~~**FCM service-account JSON / APNs key**~~ | **Gone in v0.26** — not a relaxation, the secrets no longer exist. Notifications are local (§17.6), so there is no push credential to leak or to restrict at a provider. The strongest form of this list is a shorter one |
| **Backblaze B2 credentials and the `restic` repository password** (§9.1) | The credentials read every backup; the password decrypts them. Store them apart — an encrypted backup whose key sits beside it is an unencrypted backup |
| ~~**`google-services.json`, `GoogleService-Info.plist`**~~ | **Gone in v0.26**, with the FCM row above. These were Firebase's config files; v0.24 had already removed the Google Maps key that was the other reason to hold one. The app now ships with no Google or Apple service configuration of any kind |
| **Map tile API key**, if a paid tier ever replaces OSM (§4.5) | See the note below. *(This row has outlived three map decisions. Through v0.15 it named the Google Maps Android key; v0.16 removed the native map; v0.19 replaced it with the MapKit `.p8`; v0.21 added the Google browser key beside it. **v0.24 deleted both** — MapLibre over OSM authenticates with nothing — so the row is again a placeholder against §13 Q26 choosing a paid tile tier.)* |
| `appsettings.Development.json`, `appsettings.Production.json`, `.env` | Wherever the real values actually live |
| **`pg_dump` output, any `*.sql.gz`, `backups/`** | Real user data, including last-known positions and email addresses |
| Server SSH keys, Caddy's `data` volume (ACME account keys) | Shell and certificate control |
| Log files | Serilog output can contain coordinates and usernames |
| **Real `.gpx` files from your own rides** | See below — the sharpest trap in this list |

**On the tile API key.** It ships inside the app and the WASM bundle and is therefore extractable, which tempts people to commit it. Don't: a key in a public repo is harvested by bots and billed to you. The control is **restricting the key at the provider** — to your bundle IDs and to your web origin — which makes a scraped key useless, and watching the usage graph. Keep it out of the repo the same way as every other secret (§14.3) and commit a template beside it.

**Self-hosting PMTiles removes this row entirely** (§9.1), which is a point in its favour beyond cost: there is no key to leak when the tiles are a file on your own disk.

**On real GPX files — the trap specific to this project.** §10.4 mandates a GPX replay harness fed from `.gpx` files, and also asks for "one deliberately awful test route" recorded in the real world. Those traces start and end at your house. §10.1 offers a "hide start/end radius" feature precisely because start and end points reveal where someone lives — so committing your own ride traces to a public repository publishes exactly the data this app promises to protect, about yourself, permanently.

So **test fixtures are synthetic**, generated programmatically. That is better for tests anyway: deterministic, no binary blobs, and you can construct the pathological cases — tunnel gaps, GPS spikes, zero-movement stretches — rather than hoping to ride into them. GPX import (§15.3) sharpens this both ways: it needs a *larger* fixture corpus, including deliberately hostile files, and every one of those is synthetic by nature — an XXE payload or a million-point file is written, not ridden. Real traces stay in a gitignored folder; if one is ever needed to reproduce a bug, scrub it first by trimming the first and last few hundred metres, offsetting all coordinates into a different region, and making timestamps relative.

### 14.3 Where local secrets actually live

| Context | Mechanism |
|---|---|
| Local development | **.NET User Secrets** — `dotnet user-secrets set`. Stored under `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`, physically outside the repo, so it cannot be committed by accident |
| CI | GitHub Secrets, referenced by name from the committed workflow |
| Production | Docker secrets or environment variables on the host (§9) |
| Mobile signing | Local keychain / keystore, backed up somewhere that is not Git |

`.gitignore` — the essential entries beyond the standard .NET template, which already covers `bin/`, `obj/` and `.vs/`:

```gitignore
# Secrets and local config
.env
.env.*
!.env.example
appsettings.Development.json
appsettings.Production.json
appsettings.*.Local.json
secrets.json
**/Resources/values/secrets.xml
google-services.json
GoogleService-Info.plist

# Signing material — unrotatable, treat as radioactive
*.keystore
*.jks
*.p12
*.p8
*.cer
*.certSigningRequest
*.mobileprovision
*.provisionprofile

# Infrastructure and data
docker-compose.override.yml
*.pgdump
*.sql.gz
backups/
logs/
*.pem

# pg_dump output is data; SQL under src/ and tests/ is source
*.sql
!Web/src/**/*.sql
!Web/tests/**/*.sql

# Build logs and OS noise
*.binlog
*.trx
.DS_Store
Thumbs.db

# Real ride traces contain home addresses (see 14.2)
**/fixtures/private/
*.private.gpx
```

Three of those entries are less obvious than they look, and each was a near miss rather than a
precaution:

- **`*.pem` is unanchored on purpose.** A pattern with an interior slash matches only from the
  `.gitignore`'s own directory, so the natural-looking `deploy/*.pem` silently misses
  `Web/deploy/` — and §14.2 rates signing material the one mistake with no clean recovery.
- **`*.sql` needs the two negations.** §10.4 permits raw SQL in three server folders, so the
  first `.sql` file committed as an embedded resource would otherwise be skipped by `git add`
  without a warning and break the build for everyone who cloned.
- **`*.binlog` belongs with the secrets, not the noise.** An MSBuild binary log embeds the full
  environment block, which on this project means the connection string and the §7.4 signing key.

### 14.4 Public-repo CI has its own hazard

The workflow file is committed; the secret values are not. But on a public repository, **pull requests from forks must never see secrets**. Use `pull_request` rather than `pull_request_target` for untrusted contributions, and gate any job that touches a credential — deploy, container push, signing — on the branch being yours rather than on a PR event.

A build-and-test job needs no secrets at all, and that is worth drawing out: **the testing strategy already makes open source painless.** Every test runs against a throwaway PostgreSQL container and a fake `IEmailSender` (§10.4), so a contributor needs no production access, no Zoho credentials and no seeded data to run the entire suite. That was chosen for test isolation, and it happens to be exactly what an outside contributor needs.

### 14.5 What publishing the code reveals

Publishing the abuse controls means they have to work without secrecy. Most of the design holds up. Going through it honestly surfaces one real gap and two things that should move to configuration:

- **Gap: `POST /group-rides/join` has no rate limit.** §7.8 limits the auth endpoints and join *requests*, but never join-*code* submission. A 6-character Crockford base32 code is roughly 1.07 billion combinations — impractical to guess at human speed, entirely practical for a script, and publishing the code format makes that obvious to anyone reading. This needs a limit before the repo goes public, per-IP and per-account, counting failures rather than successes. Recorded as §13 Q12.
- **Move thresholds to configuration**, not constants: the IP registration ladder's 1–3/4+ boundary (§7.8), the rate-limit windows, and the 180-day inactivity horizon (§7.11). Not because the numbers are secret — they are printed in this document — but because you want to change them in response to real abuse without shipping a release, and a reader of the repo should not learn your *current* values.
- **Everything else is fine to publish by design.** Passwords are hashed with a published algorithm, refresh tokens are random and stored hashed, positions are protected by the hub membership check, and organiser consent is the access model (§5.2). None of it depends on an attacker not knowing how it works — which is the standard the code has to meet regardless of whether the repo is public.

Also needed before the first public push: a **`SECURITY.md`** with a private reporting channel. A location-sharing app with authentication will attract reports, and the alternative to a documented channel is someone disclosing publicly first.

And, from §14.6, three more things that must be in place *at* the push rather than shortly after: the **`LICENSE`** and **`LICENSE.exceptions`** files (a public repo with no licence grants nobody anything, whatever the README implies), the **`/api/v1/about`** source offer with a matching footer link, and the **CI licence gate**. The first is legally load-bearing; the second is a live obligation the moment the server is reachable.

### 14.6 Licence — AGPL-3.0 *(decided in v0.11)*

**Dumb Luck Rides is distributed under the GNU Affero General Public License, version 3.** SPDX identifier **`AGPL-3.0-only`**. This is a design requirement, not a preference: it constrains dependency choice (§14.6.3), adds a runtime obligation to the server (§14.6.2), and shapes how contributions are accepted (§14.6.4).

`-only` rather than `-or-later`: the terms the project ships under are the terms that were read, and as sole copyright holder the option to move to a later version at any time is retained anyway. Choosing `-or-later` would delegate that decision to a future document nobody has seen.

Why it wins here, against the two alternatives that were on the table:

| Licence | Effect here | Verdict |
|---|---|---|
| **MIT** | Simplest, maximum adoption. Anyone may run a commercial hosted clone with no obligation to share improvements | Rejected — the entire product *is* a hosted service, so this gives away the only thing worth protecting |
| **Apache-2.0** | Same freedoms plus an explicit patent grant and attribution requirements. The usual default for a project that may attract corporate contributors | Rejected — the patent grant is genuinely better, but it is still permissive on the axis that matters |
| **AGPL-3.0** | Copyleft that reaches network use: anyone running a modified server must publish their modifications | **Chosen** |

The reasoning is narrow and worth stating plainly: **a plain GPL would not do anything here.** Nobody distributes this server — they run it. Section 13 of the AGPL is the only clause in common use that treats "let the public talk to my modified copy over a network" as a trigger for the source obligation, and that is exactly the shape of a hosted group-ride service. AGPL-3.0 also inherits GPL-3's patent provisions, so the Apache-2.0 advantage largely disappears.

The accepted cost: some organisations refuse AGPL dependencies outright, and this project will therefore never be embedded in a proprietary product. Since the intent is a public app and a public server rather than a component other people ship, that cost is close to zero. Nothing about AGPL restricts *using* the app or running an unmodified instance.

#### 14.6.1 What the repository must contain

| File | Content |
|---|---|
| `LICENSE` | The **verbatim** AGPL-3.0 text. Never edited, never trimmed — a modified copy of the licence is not the licence |
| `LICENSE.exceptions` | The additional permission under GPL-3 §7 (§14.6.5), referenced from `LICENSE`'s neighbourhood rather than spliced into it |
| `README.md` | A licence section stating AGPL-3.0, the §7 permission, and a link to the source — the informal half of the §13 offer |
| `CONTRIBUTING.md` | Inbound = outbound, DCO sign-off (§14.6.4) |
| `Directory.Build.props` | `<PackageLicenseExpression>AGPL-3.0-only</PackageLicenseExpression>`, `<Copyright>`, and SourceLink enabled so the commit is embedded in every assembly (§14.6.2) |

**No per-file SPDX headers.** They are a maintenance tax on hundreds of C# files for a project with one copyright holder, and `LICENSE` plus assembly-level metadata carries the same information. If contributions ever come from multiple organisations, revisit — that is when per-file provenance starts earning its keep.

#### 14.6.2 AGPL §13 is a runtime requirement, not a repository one

This is the part of choosing AGPL that shows up in the code, and the part that is easy to miss because everything else about licensing is a file at the repo root.

Section 13 obliges anyone who lets users interact with a modified version *remotely* to offer those users the Corresponding Source of **that running version**. Publishing the repository is not sufficient on its own, and "there is a GitHub link on the marketing page" is not sufficient either if the deployed build is ahead of it. The server therefore has to be able to say what it is running:

```
GET /api/v1/about        (unauthenticated, no rate limit beyond the global one)
	→ 200 {
		licence:    "AGPL-3.0-only",
		sourceUrl:  "https://github.com/<owner>/dlr",
		commit:     "9f2c1ab…",          // exact commit of this build
		version:    "1.4.0+9f2c1ab",
		builtUtc:   "2026-07-30T04:11:22Z"
	}
```

- **The commit is embedded at build time**, from `InformationalVersion` via SourceLink / `SourceRevisionId` — never hand-maintained in a constant, because a hand-maintained constant is wrong within a week and a wrong source pointer is worse than none.
- **The web app renders the same three facts in its footer** on every page, public and authed (§6.1). A human-reachable offer matters as much as a machine-readable one.
- **The mobile app surfaces it too** — Settings → About shows the app's own version and commit plus a link to the source. Not strictly required of a client the user was given a copy of, but it costs one screen and removes the question.
- **A dirty or unpushed build must be visible as such.** If the build is not from a pushed commit, `commit` reports the SHA with a `+dirty` marker rather than pretending. A deploy from an unpushed working tree is a §13 breach in progress, and CI refusing to publish such an image is the real fix.
- The car heads render none of this — templates are the wrong surface, and §4.6 already forbids incidental text there.

Tests, in §7.15's style:

```
About_ReturnsSourceUrlAndCommitOfRunningBuild
About_IsReachableWithoutAuthentication
About_CommitMatchesAssemblyInformationalVersion
Web_FooterRendersSourceLinkOnPublicAndAuthedPages
```

#### 14.6.3 Every dependency must be AGPL-compatible — and CI checks it

AGPL-3.0 can absorb permissive dependencies but not the reverse, so the standing rule is: **permissive (MIT, BSD, Apache-2.0, PostgreSQL, MS-PL) or nothing.** No GPL-incompatible copyleft, no source-available-but-not-open licences, and no paid-licence packages.

The current set is clean: Mapsui (MIT), **SkiaSharp (MIT)**, **MapLibre GL JS (BSD-3-Clause)**, Npgsql (PostgreSQL licence), EF Core / SignalR / MAUI (MIT), xUnit (Apache-2.0), **bUnit (MIT)**, NSubstitute (BSD), Testcontainers (MIT), Shouldly (permissive).

*(MapLibre matters here beyond the licence line: it is the community fork created when Mapbox GL JS v2 moved to a proprietary licence. Taking the fork rather than the original is the same decision as Shouldly over FluentAssertions, made about a JavaScript dependency — and the CI gate scans NuGet, not npm, so this one is a review-time judgement rather than an automated one.)*

**The rule has already earned its keep once.** Image processing for marker photos (§16.4) would default to ImageSharp for most .NET developers — but ImageSharp v3+ ships under the Six Labors Split Licence, which grants free use to open-source projects under conditions rather than being plainly permissive, so it is an allow-list decision rather than an assumption. SkiaSharp is MIT and already in the graph via Mapsui, so the feature added a capability and no new licence question. That evaluation happened *because* a gate exists to force it.

This makes **§10.4's choice of Shouldly over FluentAssertions structural rather than incidental.** It was picked because FluentAssertions v8+ requires a paid commercial licence; under AGPL that reasoning hardens — a licence-gated test dependency means every outside contributor must buy something before running `dotnet test`, which is incompatible with the point of publishing. So the convention gets a build step rather than a paragraph:

- A CI job runs a licence scan (`nuget-license` or equivalent) over the full transitive graph and **fails on any licence not in the allow-list**, including a package whose licence is merely *unknown*.
- The allow-list lives in the repo as data, and adding to it is a reviewed change — which is precisely the moment to think, rather than six months later during a legal review.

**One unavoidable wrinkle: the Android build links proprietary Google Play Services** — Fused Location Provider (§4.3) and the Maps SDK (§4.5). These are not free software and not system libraries in the GPL sense. It is a real interaction, not a technicality, and it is covered by the §7 permission below rather than ignored.

#### 14.6.4 Contributions: inbound = outbound, DCO, no CLA

`CONTRIBUTING.md` states that contributions are licensed under the project's outbound terms — AGPL-3.0-only **including** the §7 additional permission — and requires a `Signed-off-by` line (`git commit -s`) asserting the Developer Certificate of Origin. CI enforces the sign-off.

**Why not a CLA:** a CLA exists to let the maintainer relicense other people's work, which is a real ask for a small project to make of a drive-by contributor and adds friction to exactly the contributions most likely to arrive. Inbound = outbound achieves what is actually needed here — that every line in the tree carries the same terms, including the app-store permission, so no contributed file can quietly make store distribution unsound. The trade-off accepted: **a future relicence would need every contributor's agreement.** That is the correct default; wanting to relicense later is not a plan, it is an escape hatch, and building a CLA to preserve it taxes every contribution in the meantime.

#### 14.6.5 The app-store problem, and the §7 permission that solves it

**Unmodified AGPL terms and the Apple App Store do not coexist.** The store's terms impose usage restrictions and device limits on users of the binary that GPL-family licences forbid adding — the reason VLC was pulled from the App Store years ago, and the reason this cannot be waved through. Google Play is less pointed but not clean either, and the Play Services linkage in §14.6.3 is the same class of problem.

Two mechanisms were considered:

1. **Split licensing** — AGPL-3.0 for `DLR.Server`, Apache-2.0 for `DLR.App` and `DLR.Core`. Legally tidy, and it puts copyleft exactly where the value is. Rejected because `DLR.Core` is shared by both (§3), so the boundary would have to be policed forever by an architecture test, and a contributor would have to know which licence their patch lands under.
2. **An additional permission under GPL-3 section 7** — chosen. Section 7 exists for this: the licensor may grant permissions *beyond* the licence's terms without forking the licence text.

The permission, stated in substance and living in `LICENSE.exceptions`:

> As an additional permission under section 7 of the GNU AGPL version 3, the copyright holders grant permission to distribute compiled binaries of the client application through application distribution platforms (including the Apple App Store and Google Play), subject to those platforms' terms, and to link the application against the proprietary platform SDKs required to function on the target device (including Google Play Services and Apple's CarPlay framework). This permission applies to distribution of the compiled application only; the Corresponding Source remains available under the AGPL, and the network-use obligation of section 13 is unaffected.

Three properties that make it work:

- **It is a grant, not a restriction** — §7 permits added permissions and forbids added restrictions, so this direction is the legal one.
- **It travels with the code.** Because it is part of the outbound terms that inbound contributions match (§14.6.4), a downstream fork can also ship to the stores. A permission the maintainer alone enjoys would be a de-facto proprietary carve-out, which is not the intent.
- **It touches only distribution of the client.** Server modifications and the §13 source offer are explicitly untouched — that clause is the whole reason AGPL was chosen and nothing here is allowed to erode it.

**One honest caveat.** This section is engineering judgement about licence mechanics, not legal advice, and the App Store interaction in particular is an area where reasonable people have landed differently. The design commits to AGPL-3.0 plus a §7 store permission; **have the exception wording reviewed by someone qualified before the first public push**, since it is far cheaper to get right while the only copyright holder is you (§13 Q11 notes why the timing matters). If review says the wording needs work, the wording changes — the licence choice does not.

### 14.7 If a secret does get committed

Rotate it. Do not try to delete it.

Git history is permanent in practice: once a public repository has been cloned, forked, or indexed by a caching proxy, `git rm` and even a full history rewrite do not recall the value. Assume anything pushed to a public repo is compromised from the moment of the push, and treat rotation as the only real remedy — new signing key, new SMTP token, new database password, new API key with fresh restrictions.

This is the argument for enabling **GitHub secret scanning with push protection** on day one, and for running `gitleaks` as a pre-commit hook. Both are free, and both are cheaper than a rotation. The one leak with no rotation path is the **Android release keystore**, which is why it sits near the top of §14.2.

---

## 15. Tracks — Import, Editing and Versioning

> **Section placement:** this is domain design and belongs beside §5, not after the repository boundary. It is numbered §15 to avoid renumbering §§6–14 and the several hundred cross-references pointing at them — the same trade-off §7.3 made when it was inserted, resolved the other way because the cost here is far higher.

### 15.1 Two sources, one entity

A track is created in exactly two ways:

| Source | Where | What it always has | What it may lack |
|---|---|---|---|
| **Recorded** | App only — the rider turns *save track* on and off (§4.2) | Per-point timestamp and accuracy, elevation where the device supplies it | — |
| **Imported** | App **and** website, from a `.gpx` file (§15.2) | Coordinates | Timestamps, elevation, accuracy — all three commonly absent |

Everything downstream treats them identically: one list, one detail screen, one share model, one GPX export, and either can be attached to a group ride as a planned route (§5.2). `Track.Source` records which is which for display and for support, never for authorisation.

**The one distinction that leaks into the product: a track with no timestamps is a route, not a ride.** It has a distance and a shape but no duration, no speed and no start time. Consequences, stated once so they are not rediscovered per-screen:

- `DurationS`, `MaxSpeedMps`, `StartedUtc` and `EndedUtc` are **nullable** and rendered as "—", never as `0` (§8). Zero implies a measurement; null says there was none.
- Timeless tracks are excluded from any "total distance this month" style aggregate, because mixing a planned route into a total of rides actually ridden makes the number a lie.
- `AscentM` is null when the file has no `<ele>`, and **no elevation is invented.** A DEM lookup service is a paid third-party dependency and a new failure mode for a number nobody is checking.

**Naming, renaming and deleting are properties of the entity, not of the source.**

- **A recorded track is named before it can be saved.** The Location screen's save button is off until the box has something in it, and `TrackRecordingState.SaveAsync` refuses a blank name as well — a disabled button is a courtesy, and this is the one path that takes a recorded ride off the device. The rider is asked while the ride is fresh, because the alternative is naming it weeks later against a date and a distance, and a list in which three rows read "Untitled" is a list nobody can use.
- **An imported track is named from the file** — `<name>`, or the filename when the element is absent — and that name is *clamped* to the column rather than refused. Nobody typed it, and rejecting a file because a planning tool wrote a sentence into that element would be damaging the import over a column width.
- **`PATCH /tracks/{id}`** renames either kind. It carries no `Version`: a rename moves no point, so it cannot conflict with an edit the way one edit conflicts with another (§15.5), and bumping the version would refuse an editor open in another tab over a change that could not have invalidated it.
- **`DELETE /tracks/{id}`** removes the track, its retained original (§15.6), its markers (§16.1) and any ride attachment, and deletes both blobs in the request rather than leaving them to the nightly sweep — a cascade reaches rows and not a filesystem (§16.6). It meets §15.4's live-ride precondition for a stronger reason than an edit does: an edit moves the line a ride in progress is measured against, and a delete takes it away entirely.
- Both are owner-scoped and answer **404** to anybody else, the same as the detail read — a distinguishable answer would be a way to ask whether a track id exists.

### 15.2 Where the import happens — both, with one parser

| Client | Flow |
|---|---|
| **App** | System file picker (and share-sheet / "Open with" registration for `.gpx`) → **parsed on-device** by `DLR.Core/Tracks/` → appears in My Rides immediately → uploaded through the normal outbox (§4.4) |
| **Web** | File input or drag-and-drop → multipart `POST /api/v1/tracks/import` → parsed server-side → appears in the list |

**Importing on the app must work with no signal.** That is not a nicety in an app whose whole premise is a trailhead with no coverage: a rider handed a GPX route by a mate over Bluetooth at the start of a ride needs it on the map now, not when they get home. So the app parses locally and the result is a perfectly ordinary local track that syncs later.

**The server re-parses and re-validates everything the app sends**, because a client-supplied point list is untrusted input regardless of which of our own clients produced it. It is the same code path — see §15.7.

Two small platform pieces that are easy to forget until someone taps a `.gpx` attachment in a mail app and nothing happens: an **Android intent filter** for the `application/gpx+xml` MIME type and the `.gpx` extension, and an **iOS `CFBundleDocumentTypes` / exported UTI** declaration. Both live in files §14.1 already commits, and both are the difference between "import" meaning *a button inside the app* and meaning *the way GPX files behave on this phone*.

### 15.3 Reading GPX from strangers

This is the first time the project parses a user-supplied file format, and GPX is XML, which means the failure modes are the classic ones rather than anything cycling-specific.

**Hostile input first.** The rules are absolute and enforced by an architecture test (§10.4):

```csharp
var settings = new XmlReaderSettings
{
	DtdProcessing	= DtdProcessing.Prohibit,	// XXE and billion-laughs, both
	XmlResolver		= null,						// no external entity ever resolves
	IgnoreComments	= true,
	IgnoreWhitespace = true,
	MaxCharactersFromEntities = 0
};
```

- **`XmlReader`, streaming — never `XDocument.Load` or `XmlDocument` on the request stream.** Buffering the document first is what turns a 25 MB upload into a several-hundred-megabyte allocation on a 4 GB VPS.
- **Two independent caps, both enforced mid-parse:** `Tracks:MaxUploadBytes` (default 25 MB, rejected at the request level with `413`) and `Tracks:MaxPointsPerFile` (default 500 000, aborting the read the moment it is exceeded rather than after). A file can be small and still be pathological; a size cap alone does not bound the point count.
- **The extension and the client's content type are hints, not facts.** Validation is by parsing.
- Errors return Problem Details that **name the actual problem** — element and line number where the reader knows them. "Invalid file" is useless to someone whose exporter emits something slightly unusual, and this is a feature people will hit with files from a dozen different tools.

**Then the format's own untidiness.** GPX in the wild is looser than the schema suggests:

| In the file | Rule |
|---|---|
| Multiple `<trk>` | One track per `<trk>`, capped at `Tracks:MaxTracksPerFile` (default 20). The preview lists them and the user picks |
| Multiple `<trkseg>` in a `<trk>` | Kept as **segment breaks** — a pause or a signal gap. Distance and duration are never summed across a break |
| `<wpt>` waypoints | **Imported as markers** *(v0.13 — v0.12 ignored them)*. Name → title, description → note, `<sym>` → icon. Mapping in §16.6 |
| `<rte>` instead of `<trk>` | Imported as a track with no timestamps. Planning tools emit routes, and rejecting them would fail the most common import there is |
| ~~`<wpt>` waypoints ignored~~ | Superseded by the row above. The v0.12 rule was "ignore them, but say so in the preview" — markers made that unnecessary, and the preview now reports how many will be created |
| Missing `<ele>` | `AscentM` null. No interpolation, no DEM lookup |
| Missing `<time>` | Timeless track (§15.1) |
| Non-monotonic `<time>` | **Geometry is preserved in file order; the time-derived stats are dropped instead.** Reordering points to satisfy the clock would silently change the shape of the ride, which is a far worse outcome than losing a duration |
| Coordinates out of range, or `NaN` | Rejected — the file is malformed, not merely odd |
| Implausible speed between points | Flagged in the preview as a likely GPS spike, **not auto-removed**. The editor (§15.5) is the tool for that, and a rider dropping off a ferry legitimately looks like a spike |

**Preview is `?dryRun=true`, not server-side staging.** The same endpoint parses and returns exactly what *would* be created — track count, points, distance, what was ignored — without persisting anything, and the client re-posts to commit. The alternative, holding a parsed result server-side between two calls, needs its own storage, its own expiry sweep and its own orphan cleanup: a whole mechanism to save one re-upload of a file that is capped at 25 MB. On the app, preview costs nothing at all, because the parse already happened locally.

**Duplicate detection, not duplicate prevention.** Every track carries a `ContentHash` over its normalised point stream. Importing a file whose hash matches an existing track *of the same owner* warns — *"You imported this on 3 June"* — and proceeds if the user says so. Re-importing on purpose is legitimate (a second copy to edit differently); doing it by accident is the common case, and a warning serves both.

### 15.4 Who may write a track

Editing is what forces this to be stated precisely, and it is worth stating in one place because §4.4's sync design depends on it:

> **A track has exactly one writer at any moment.** The recording device owns it until the full-resolution upload completes. The server owns it from then on, forever. The device never edits.

That single rule preserves everything v0.2's offline-first design bought:

- No merge of two divergent point lists — a genuinely hard problem this design simply never encounters.
- No last-write-wins on track *content*; LWW stays confined to group-ride metadata.
- An edited track reaches the device as a **replacement**, keyed on `Version`, not as a patch to reconcile.

Three preconditions fall directly out of it, each a `409` with a distinguishable reason rather than a generic failure:

| Precondition | Why |
|---|---|
| The track is **fully uploaded** at full resolution | The server would otherwise be editing the simplified display copy while the device still holds the real one. The UI says *"still uploading — you can edit once this finishes"* |
| The track is **not the planned route of a `Live` ride** | Off-route warnings and the gap list compute distance-along-route (§5.4); changing the route mid-ride silently moves every rider's position in the list. For an `Open` ride editing is allowed and fires `RouteUpdated` (§5.3) |
| The caller **owns** the track | Not the group-ride organiser, even for a route they were handed. A recipient who wants their own variant exports the GPX and re-imports it — the round-trip *is* the copy feature, and it needs no endpoint of its own |

**Share links point at the track, not at a snapshot.** Someone who opened a shared link before an edit sees the edited version afterwards. That is the behaviour people expect from a link, but it is worth writing down, because the alternative — pinning each share to a version — would mean the trimmed points stay readable through an old link, which defeats the main reason anyone trims (§10.1).

### 15.5 The editor — one primitive, three gestures

The requirement is to remove points from the start, from the end, or from along the track. All three are the same operation:

> **Remove a half-open range of raw point indices, `[from, to)`.** Trim-start is a range anchored at 0; trim-end is a range ending at `PointCount`; an interior cut is neither.

```
POST /api/v1/tracks/{id}/edit
{
	version:  7,                                  // optimistic concurrency
	removals: [ [0, 118], [4512, 4520], [43180, 43266] ]
}
	→ 200 { version: 8, pointCount, segmentCount, distanceM, durationS, ascentM, … }
	→ 409  version stale / still uploading / route of a Live ride
```

**Edits are expressed against the full-resolution index space, never the simplified one.** This is the single most important implementation constraint in the section. The map draws a simplified polyline for performance (§4.2, §6.2), and if the browser sent *"delete the 412th point I am displaying"* it would delete a different point on the server — plausibly hundreds of metres away, invisibly, and only on tracks dense enough for simplification to have done anything. So the editor loads the real thing:

```
GET /api/v1/tracks/{id}/points   → encoded polyline + delta-encoded times, gzipped
```

A 12-hour tour at 1 Hz is ~43 000 points, roughly 200 KB gzipped in that encoding. That was sized for a desktop browser, back when that was the only place the editor ran; the editor now runs on every host, so on a phone it is 200 KB over whatever connection the rider has at the end of a ride — acceptable for a deliberate act, and the reason the fetch happens on opening the editor rather than on opening the ride. The base map simplifies for *rendering* client-side; the indices the editor manipulates stay the server's indices throughout. One index space, no mapping layer, no class of bug.

**Validation** — all `400`, all with the offending range named:

- ranges ascending, disjoint, non-empty, within `[0, PointCount)`;
- at least **2 points** must survive, or it is not a line (deleting the whole thing is `DELETE /tracks/{id}`, which already exists);
- `version` must match, or `409` — two browser tabs editing the same track is the realistic case, and silently applying stale indices would cut the wrong span.

**An interior removal inserts a segment break.** The alternative — splicing the neighbours together — would draw a straight line across the gap and add its length to the distance, inventing a path the rider never took. So the removed span leaves a genuine discontinuity, distance and duration are summed within segments only, and `SegmentCount` increments. This is the same mechanism as a multi-`<trkseg>` import (§15.3), which is a good sign the concept is right rather than bolted on. For the common case — cutting a three-point GPS spike out of a suburban street — the resulting gap is a few metres and invisible at any sane zoom.

Trimming the start or the end creates no break: there is nothing on the outside to disconnect from.

**The display tolerance is 5 m** (`TrackSimplifier.DefaultToleranceM`). That is below what a rider can distinguish at any zoom showing a whole ride and comfortably inside consumer GPS error, so the simplified line is not a different shape — it is the same shape with the noise taken out. Nothing derived is computed from it: distance, ascent and duration all come from the raw points (§15.7). The implementation is iterative rather than recursive, because 40 000 points along a curve that never straightens recurses deep enough to overflow a phone's stack, and the failure is a crash rather than a slow render.

**Everything derived is recomputed, not adjusted.** Distance, duration, ascent, max speed, bounds, point count, segment count, the simplified polyline and the content hash are all rebuilt from the surviving points by the same code that computes them at record and import time (§15.7). Incrementally patching stats — subtracting the removed span's distance — is how an edited track ends up with numbers that no longer describe it after the third edit.

**What the user sees before committing**, because this is destructive:

- the removal highlighted on the map, in place, before saving;
- the new distance, duration and ascent **beside the old ones** — the number changing is the whole point of trimming a lunch stop;
- an explicit confirm that says what will happen to the removed points (§15.6);
- for the privacy case, a *"trim to hide the start/end"* shortcut that pre-selects a radius around the first and last points — the destructive sibling of §10.1's display-only hide radius, and the reason most people will open this editor at all.

### 15.6 Undo, and being honest about deletion

Edits are destructive, and the motivating use case is privacy, which pulls in two directions: keeping the original forever makes trimming your house meaningless, and keeping nothing makes a misclick unrecoverable. The resolution:

- On save, the pre-edit blob moves to **`TrackRevision`** with `PurgeAfterUtc = now + Tracks:EditUndoDays` (default **7**).
- **`POST /tracks/{id}/edit/undo`** restores it within that window, as a new `Version` — undo is itself an edit, not a rewind, so the version chain only ever moves forward.
- **Exactly one revision per track.** A second edit inside the window replaces the retained original and restarts the clock. Undo is a safety net for the last action, not a history feature, and unbounded revisions would quietly triple storage on the €4 VPS.
- **`DELETE /tracks/{id}/previous-version`** — *"remove the original now"* — for the user who just trimmed their home address and does not want a seven-day wait.
- The nightly job purges expired revisions (§7.11).

**And the part that must be said out loud:** nightly backups (§9) still contain the original until they roll out of retention. The privacy copy says the removed points are gone from the app, from exports and from every share link — and that backups keep a copy for the backup retention period. This document has form here: v0.2 exists because v0.1 claimed positions were never persisted when a 10 s flush made that false (§10.1). Getting it right in the UI at the moment of the trim is cheaper than correcting it later.

Data export (`GET /api/v1/me/export`) and account deletion (`DELETE /api/v1/me`) both cover `TrackRevision`: a retained original is the user's data while it exists, so it is exported with the track, and it is deleted with the account.

### 15.7 One codec, one stats implementation

There are three entry points into the same pipeline — record, import, edit — and three copies of "compute the stats" is how a track's ascent comes out different depending on where it came from.

**`DLR.Core/Tracks/` holds the only GPX reader/writer, the only simplifier, the only stats calculator and the only `TrackEditor`.** The app uses them to import offline; the server uses them to re-validate and to edit; the GPX replay harness (§10.4) uses them for tests. `DLR.Core` has no platform dependencies (§3), so this costs nothing structurally — it just has to be enforced, hence the architecture test.

Two consequences worth naming:

- **`Import_AppAndServerParsers_ProduceIdenticalTracks`** is a real test, not a tautology, because it is the thing that guarantees the offline path and the web path agree about a file.
- **`Edit_NoOpEdit_ProducesIdenticalStats`** — an edit that removes nothing must reproduce the stored numbers exactly. If a rewrite shifts ascent by a metre on an untouched track, the ascent algorithm is order-dependent or accumulating error, and this test says so before a user notices their ride grew a hill.

**The ascent noise threshold is 3 m** (`TrackStats.AscentNoiseThresholdM`), tracked against a running reference rather than point to point. GPS altitude wanders by several metres while standing still, so summing every rise produces a number that grows with how long the ride took rather than with how much of it was uphill — and a per-point threshold would instead discard a long steady climb made of half-metre steps. The recorder adopts this figure when it arrives rather than choosing its own.

Ascent in particular uses the recorder's noise threshold, unchanged. An edited track whose *untouched* half reports different climbing is a bug that reads as data corruption to the person who owns the ride.

### 15.8 Schema and configuration

The `Track` columns added in v0.12 are listed in §8. Configuration, per §14.5's rule that thresholds are settings rather than constants:

| Key | Default | Purpose |
|---|---|---|
| `Tracks:MaxUploadBytes` | 25 MB | Request-level cap; `413` beyond it |
| `Tracks:MaxPointsPerFile` | 500 000 | Enforced mid-parse, aborts the read |
| `Tracks:MaxTracksPerFile` | 20 | `<trk>` elements accepted from one file |
| `Tracks:MaxTracksPerAccount` | *(§13 Q13)* | Storage quota — value deliberately unset |
| `Tracks:MaxStorageMbPerAccount` | *(§13 Q13)* | As above |
| `Tracks:EditUndoDays` | 7 | Retention of the pre-edit original |
| `Tracks:MinPointsAfterEdit` | 2 | Below this an edit is rejected |

### 15.9 Tests to write first

Import — parsing, normalisation and hostility:

```
Import_GpxWithSingleTrack_CreatesTrackWithComputedStats
Import_GpxWithMultipleTracks_CreatesOnePerTrkUpToCap
Import_GpxRouteElement_ImportsAsTrackWithoutTimestamps
Import_GpxWithoutElevation_LeavesAscentNull
Import_GpxWithoutTimestamps_LeavesDurationAndSpeedNull
Import_MultipleSegments_DoesNotCountDistanceAcrossGaps
Import_NonMonotonicTimestamps_PreservesGeometryAndDropsTimeStats
Import_OutOfRangeCoordinates_IsRejected
Import_WaypointsPresent_AreCreatedAsMarkers                 — §16.6
Import_DryRun_PersistsNothing
Import_SameContentTwice_WarnsButProceeds
Import_OnDeviceWithNoNetwork_SucceedsAndQueuesUpload
Import_AppAndServerParsers_ProduceIdenticalTracks

Import_DtdDeclaration_IsRejectedWithoutResolvingIt          — XXE
Import_NestedEntityExpansion_IsRejected                     — billion laughs
Import_ExternalEntityReference_MakesNoNetworkCall
Import_ExceedsPointCap_AbortsMidStreamWithoutBufferingAll
Import_ExceedsSizeCap_Returns413
Import_NotXml_ReturnsProblemDetailsNamingTheProblem
Import_TruncatedFile_ReturnsProblemDetailsNotAnUnhandledException
```

Editing — the destructive half:

```
Edit_TrimStart_RemovesLeadingPointsAndRecomputesStats
Edit_TrimEnd_RemovesTrailingPoints
Edit_RemoveInteriorRange_InsertsSegmentBreak
Edit_RemovedSpan_IsExcludedFromDistanceAndDuration
Edit_NoOpEdit_ProducesIdenticalStats
Edit_RecomputedAscent_UsesRecorderThreshold
Edit_OverlappingOrDescendingRanges_Returns400
Edit_RangeOutOfBounds_Returns400
Edit_LeavingFewerThanTwoPoints_Returns400
Edit_StaleVersion_Returns409
Edit_ByNonOwner_Returns403
Edit_TrackNotFullyUploaded_Returns409
Edit_TrackIsRouteOfLiveRide_Returns409
Edit_TrackIsRouteOfOpenRide_FiresRouteUpdated
Edit_IndicesApplyToRawPoints_NotSimplifiedPolyline
Edit_SimplifiedPolylineAndContentHash_AreRegenerated

Undo_WithinWindow_RestoresPreviousPointsAsNewVersion
Undo_AfterWindow_Returns404
Undo_SecondEditWithinWindow_ReplacesRetainedOriginal
PurgeNow_DeletesRetainedOriginalImmediately
NightlySweep_PurgesRevisionsPastPurgeAfterUtc
Export_IncludesRetainedRevisionWhileItExists
AccountDeletion_CascadesTrackRevisions
Sync_EditedTrack_ReplacesLocalCopyRatherThanMerging
```

---

## 16. Map Markers

### 16.1 What a marker is, and what it hangs off

A marker is an **authored point of interest on the map**: a position, an optional direction, an icon, a short title rendered beside that icon, a longer note revealed on tap, and optionally one photo.

Authored is the word that matters. Everything else this app puts on a map is *measured* — a recorded track, a live position — and measured data is governed by the rules in §10.1 that delete it the moment its rider stops sharing. A marker is something a person deliberately placed and typed, so it lives as long as the thing it is attached to, and it is visible to whoever that thing is visible to. Two different lifecycles, and conflating them is how a "privacy-first" app quietly starts retaining locations.

**Markers attach to exactly one of two parents**, because "the ride" means both things in this product and the requirement is served by both:

| Parent | The use case | Lifecycle | Realtime |
|---|---|---|---|
| **`Track`** | Annotating a ride you recorded or imported — *"viewpoint, worth the detour"*, a photo at the summit, a hazard on a route you are about to share | Lives and dies with the track; visible wherever the track is (owner, share links, attached ride summary) | None |
| **`GroupRide`** | Anything the group needs *now* — regroup point, fuel stop, gravel section, an obstacle someone just hit | Lives and dies with the ride; visible to admitted members (§5.2) | Fans out over the hub (§16.6) |

```sql
CHECK ((track_id IS NULL) <> (group_ride_id IS NULL))     -- exactly one parent
```

An exclusive arc rather than two tables: the payload, the validation, the photo pipeline, the editor UI and the GPX mapping are identical across both, and duplicating all of that to avoid one `CHECK` constraint trades a database invariant for two copies of everything that can drift. What differs — who may write one, when it is deleted, whether it broadcasts — is behaviour, and behaviour is the thing that belongs in different code paths anyway.

**§5.4's "regroup here" pin stops being its own feature.** It was listed as a v1.1 idea; it is now a marker with the `regroup` icon dropped by the ride leader, and the "I've stopped" flag (§4.6, §5.4) is a marker with the `stopped` icon. One authored-pin mechanism, one fan-out path, one set of tests.

### 16.2 The fields

| Field | Type | Required | Rules |
|---|---|---|---|
| **Point** | lat/lon, scaled `integer` 1e-5 | ✅ | Same representation as `rider_position` (§5.5) — one coordinate encoding in the database, no float drift |
| **Direction** | `smallint` degrees, 0–359, true north | ❌ | **Nullable, and null is not zero.** Zero is due north, a perfectly good bearing. A fuel stop has no direction; a *"blind crest, hazard on the right"* marker does |
| **Icon** | `varchar(32)` key from a curated set | ✅ | See below. Never a user-supplied image |
| **Title** | `varchar(40)` | ✅ | Rendered on the map beside the icon, so length is a rendering constraint, not a database one — 40 characters is about what fits before a label starts covering the road it refers to |
| **Note** | `varchar(500)` | ❌ | Shown on tap. Plain text; never rendered as HTML or Markdown |
| **Photo** | `PhotoId?` | ❌ | One, optional (§16.4) |

**Icons are a curated set, keyed by string.** Roughly twenty: `hazard`, `gravel`, `water-crossing`, `gate`, `fuel`, `food`, `coffee`, `water`, `toilet`, `camping`, `parking`, `viewpoint`, `photo`, `repair`, `medical`, `regroup`, `stopped`, `start`, `finish`, `turn`, `note`.

Three reasons this is not "upload your own icon", and they are all load-bearing:

- **No new upload surface** beyond photos, which already cost a whole subsection to make safe.
- **It renders anywhere** — vector assets shipped in the app work offline, at any zoom, on a car screen, and in the web app, which a user-supplied PNG does not.
- **A string key degrades.** An app one version ahead sends `ferry`; a server that has never heard of it stores it, and an older client that cannot draw it falls back to `note`. An enum ordinal would need a migration in lockstep across two app stores' release cadences, which is not a thing that happens.

Unknown icon keys are therefore **stored, not rejected** — the server validates length and character set, not membership. The client owns the drawing.

> **The GPX mapping has to honour that in both directions** *(SRV-26)*. Exporting an unknown key correctly and then flattening it to `note` on re-import destroys it on any round trip through an older server — which is exactly the version-skew case the string key exists to survive. So `<sym>` is mapped through the table, and anything not in the table that is *shaped* like a key (lowercase, digits, hyphens) is kept as one. Symbols that are not key-shaped — `Flag, Blue`, `Scenic Area` — still fall back, so a foreign file's symbols never become junk icon keys.

**Title and Note are user text shown to other people.** Blazor escapes by default and MAUI labels are not HTML, so this is not an XSS story so much as a discipline one: no rich-text rendering is ever added here, and the GPX exporter escapes properly (§16.6). Both fields are trimmed, control characters stripped, and normalised to NFC.

### 16.3 Rendering — the feature the native map could not draw

> **Updated in v0.16 and v0.19.** This section originally concluded that markers forced **Mapsui** onto the phone's critical path. The UI change in §18 overtook it, and v0.19 changed the provider again: the phone renders **MapKit JS** and the web **MapLibre**, both of which do custom icons and rotation natively (§4.5). Markers are no longer what drives the renderer choice. The table below stands as the record of *why* the built-in control was never going to survive this feature — and Mapsui is still what draws markers on a car screen.

The built-in `Map` control cannot draw this feature. §4.5's capability table already said so in v0.2, before there was a feature that needed it:

| Marker needs | Built-in `Map` | Mapsui *(car)* | MapLibre *(web)* | MapKit JS *(mobile, v0.19)* |
|---|---|---|---|---|
| Custom icon imagery | ❌ `Map.Pins` only | ✅ | ✅ | ✅ custom annotation |
| Icon rotated to a bearing | ❌ | ✅ | ✅ | ✅ via the annotation's own DOM |
| Title drawn persistently beside the icon | ❌ — the title appears in a callout **on tap** | ✅ | ✅ with decluttering | ⚠️ needs a custom annotation, not `MarkerAnnotation` |
| Note on tap | ⚠️ callout text only | ✅ | ✅ | ✅ callout delegate |
| Photo thumbnail in the callout | ❌ | ✅ | ✅ | ✅ callout is arbitrary DOM |

**That whole table is now history.** It compares what four *base maps* could draw, and since v0.21 no base map draws a marker at all — `SkiaMapOverlay` does, in one C# file, identically on every host (§4.5). v0.24 went further and left one base map anyway. The table is retained because it is why the overlay exists: every row in it is a difference that would otherwise have had to be reconciled per provider, and `MapCapabilities` — which was built to declare those differences — now describes the car heads only (§4.6).

So **v0.9 promoted Mapsui to the critical path for the car, and v0.13 promoted it for the phone as well** — which is where this section stood until §18 replaced the phone's renderer entirely. The through-line is that the built-in control was ruled out three separate times, by three unrelated requirements, before it was finally removed.

**The degradation is defined rather than accidental**, which is what `MapCapabilities` was built for (§4.5 rule 3). Two flags join it — still meaningful, because the car renderer and any future one must still declare what they can draw:

```csharp
[Flags]
public enum MapCapabilities
{
	// … existing: CustomMarkerImages, OfflineTiles, Rotation,
	//             BreadcrumbTrails, CustomTileSource
	RotatedMarkers		= 1 << 5,	// icon can be drawn at a bearing
	PersistentLabels	= 1 << 6	// title drawn beside the icon, not on tap
}
```

On a renderer without them, a marker becomes a plain pin whose **title and note collapse into the tap callout**, and the direction is stated as text in that callout (*"facing NE"*) rather than drawn. It is a worse experience and an honest one; the alternative — silently dropping the direction — would let a hazard marker lose the half of its meaning that says which way to look.

**Two rendering rules that are easy to get wrong and expensive to fix later:**

- **On a rotating map, the icon rotates with the map and the label does not.** A heading-up map that rotates the text renders half the titles upside-down. The bearing is relative to true north, and the renderer composes it with the current map rotation — the marker's stored value never changes.
- **Labels collide.** Twenty markers around a car park draw twenty overlapping titles. Mapsui declutters, and the rule is: at low zoom, icons only; titles appear past a zoom threshold; the tap target is the icon, never the label. Worth stating because "why did my titles disappear" otherwise reads as a bug.

**On the car screen** (§4.6), markers render as **icons on the map and nothing else**. No titles, no notes, no photos, no tapping. §4.6's distraction rules are not negotiable and a photo on a head unit is indefensible. The one concession is the existing gap list, which may show the next marker ahead on the route as one row — *"Hazard 1.2 km"* — because that is navigation information, which is what the car head is for.

### 16.4 Photos — the largest new attack and cost surface in the project

One optional photo per marker. That "one" is a decision: a gallery per marker multiplies storage, needs ordering and a carousel UI, and none of it serves *"here is the pothole"*.

#### Ingest is hostile-input handling, again

Image decoders are a classic remote-code-execution and denial-of-service surface, and the same discipline §15.3 applies to GPX applies here:

- **Cap bytes *and* decoded pixels.** `Photos:MaxUploadBytes` (default 12 MB) bounds the transfer, checked against `Content-Length` *and* against the file that was actually read, because the header can lie; `Photos:MaxDecodedPixels` (default 40 MP) bounds the decompression bomb — a 40 KB PNG can expand to hundreds of megabytes of bitmap, and a byte cap alone does not see it coming. Read the header, check the dimensions, and refuse *before* allocating. The pixel multiply is done in a `long`: 60000 × 60000 is a writable PNG header and overflows an `int` into a small positive number, which passes a cap written the obvious way.
  - **Testing the ordering needs a fixture that fails two different ways** *(settled in SRV-27)*. A bomb whose image data is *valid* looks identical from outside whether the cap ran before or after the decode. The fixture's stream is therefore deliberately unusable, so decode-first answers `400 DecodeFailed` and header-first answers `413 TooManyPixels` — two different statuses and two different problem names. Without that, `Photo_DecompressionBomb_IsRejectedBeforeAllocating` would be green against a cap that runs too late to help.
- **Accept JPEG, PNG, HEIC, WebP by content sniffing**, never by extension or client-supplied content type.
- **Everything is re-encoded**, always, even when the original is already a conformant JPEG — see below.
- Failures return Problem Details, not an unhandled decoder exception.

#### Re-encoding is how metadata is stripped, and stripping is mandatory

**Photos carry GPS coordinates, timestamps, device serials and sometimes a thumbnail of the *unedited* original.** In this app specifically that is not a generic privacy nicety — it is a direct contradiction of a feature shipped one section ago:

> §15.6 lets a rider trim the first 400 m off a track so the ride does not start at their house. If they then attach a photo taken in their driveway, the EXIF GPS tag puts the house back — in a file handed to every member of the ride.

So: **decode, apply the EXIF orientation, re-encode to JPEG, write no metadata.** Re-encoding rather than running a metadata-stripping pass is deliberate — strippers work on the tags they know about, and the failure mode is silent. Applying orientation *before* discarding it matters too, or every portrait photo from an iPhone arrives sideways.

**"No metadata" includes the colour profile, and that one is easy to miss** *(found in SRV-27)*. `SKBitmap.Copy` preserves the decoded image's `SKColorSpace`, and the JPEG encoder then writes it out as an ICC profile in an `APP2` segment — which can name the device that produced it. Every bitmap the ingest hands to the encoder is therefore built from an `SKImageInfo` carrying **no colour space**. The sharp part is which images took the leaky path: the ones needing neither rotation nor downscaling, because the rotate and resize routes already construct their target that way. That is the small, upright photograph — the ordinary case, and the one a spot check is least likely to open in a hex editor. The guard is `Photo_AllMetadata_IsAbsentAfterReEncode`, which asserts on the file's *segment structure* rather than on three named tags, so it keeps holding as formats gain new places to hide things.

Downscale to `Photos:MaxDimension` (default 2048 px on the long edge) and generate a thumbnail (`Photos:ThumbDimension`, 320 px) for the map callout, both re-encoded at `Photos:Quality` (85). Two objects per photo, both on the blob volume behind `IBlobStore` (§9.1 — *not* object storage; v0.18 moved every blob to a Docker volume), both counting against the account's storage quota (§13 Q13).

#### The library choice is a licence decision (§14.6.3)

**SkiaSharp (MIT)** — not ImageSharp. ImageSharp v3+ ships under the Six Labors Split Licence, which grants free use to open-source projects under conditions rather than being plainly permissive; under §14.6.3's allow-list it needs a deliberate decision rather than an assumption, and the CI licence gate exists precisely to force that moment. SkiaSharp is MIT, is a first-class .NET binding, and **is already arriving in the dependency graph with Mapsui** (§4.5) — so the marker feature adds a capability, not a dependency. This is the same reasoning that put Shouldly in §10.4, applied before the fact instead of after.

#### Upload always goes through the server

A presigned direct-to-object-storage PUT is the obvious cloud pattern. **Rejected**: it hands the client the job of stripping metadata, and a stripping step the client can skip is a stripping step that does not exist. Every byte goes through the server. Since there is no object store and no CDN in the deployment anyway (§9.1), this costs nothing but VPS bandwidth, of which there is a great deal more than this needs.

#### Offline is the normal case, not the edge case

A photo is taken at the top of a hill, which is exactly where there is no signal. So the photo is a **separate resource from the marker**:

```
POST /api/v1/photos          multipart → { photoId }        (re-encoded, stripped)
PATCH /api/v1/markers/{id}   { photoId }
```

The marker is created immediately with a local file reference, appears on the map at once, and the upload queues in the outbox (§4.4) exactly like a track. Other members see the marker before they see its photo, with a placeholder in the callout. Binding the photo into marker creation would mean no marker until the rider reaches coverage, which in this app means no marker until the ride is over.

### 16.5 Who may place one, and the caps that keep it civil

| Parent | Create | Edit / delete |
|---|---|---|
| **Track** | The owner | The owner |
| **Group ride** | **Any admitted member — if `AllowMemberMarkers` is on** (§5.8) | The author, or the organiser |

Any member, not just the organiser, because the useful marker is *"gravel across the whole corner at the 40 km mark"* and the person who found it is whoever hit it first. The organiser keeps control the same way they do everywhere else in §5.2 — they chose who is in the ride, they can delete any marker, they can clear all of them, and since v0.15 they can **switch member marker-adding off entirely** for that ride (§5.8). Markers may be added **before and after the ride as well as during it**, and nothing forbids it — an adventure has no state to be read-only in (§5.1).

Caps, all configuration per §14.5, all enforced server-side:

| Limit | Default |
|---|---|
| Markers per track | 200 |
| Markers per group ride | 500 |
| Markers per member per group ride | 50 |
| `POST /markers` | 60/hour per user |
| `POST /photos` | 30/hour, 200/day per user |

**Photos shared between riders make this an app with user-generated content**, and that has a store consequence that no amount of good architecture removes: Apple requires a way to **report objectionable content and block the user who posted it**, plus a stated response commitment (§10.2). A ride's audience is small and organiser-admitted, which makes abuse unlikely — it does not make the requirement optional at review. The mechanism is small: report a marker, which notifies the organiser and flags it for the operator; block a user, which hides their markers from you and prevents future co-membership. It is the *existence* of these that review checks.

*(v0.14: this became one mechanism covering markers **and** comments — `ContentReport` in §17.7. Blocking hides both.)*

**Blocking is one-directional, silent, and covers four surfaces** *(built in SRV-31)*. Blocking somebody hides *their* posts, markers, reactions and poll votes from *you*; nothing is sent to them and their own view is unchanged, because a block that announced itself would turn a quiet "I would rather not read this person" into the confrontation it exists to avoid. All four reads go through one `BlockList.HiddenFromAsync`, since four copies of "and not from somebody I blocked" is how one of them ends up without it. Note this is a different mechanism from `GroupRideJoinRequest.Blocked`, which is an *organiser* refusing a requester entry to one ride — same word, different actor.

**"Prevents future co-membership" is not built, and that is recorded rather than assumed** *(SRV-31)*. It is in no task's build list; it needs a decision about direction (does my block stop me joining their ride, or them joining mine, or both); and a symmetric check would let a single block keep a rider out of a fifty-person ride. Report-and-block — the pair store review actually checks for — is complete without it.

### 16.6 Realtime, lifecycle and the GPX round-trip

**Hub messages** join `IRideClient` (§5.3), sent to the ride group only:

```csharp
Task MarkerAdded(MarkerDto marker);
Task MarkerUpdated(MarkerDto marker);
Task MarkerRemoved(Guid markerId);
```

Markers are **not** part of the 5 s position batch. A batch is a continuous telemetry stream where dropping a tick is harmless; a marker is a discrete authored event where dropping it is data loss. They travel as their own messages, and the reconnect path fetches them from the ride snapshot alongside positions (`GET /group-rides/{id}` and `GET /group-rides/{id}/positions`), per §5.3's rule of re-fetching state rather than replaying history.

**Lifecycle** — and the contrast with positions is the point:

- A rider turning sharing off **deletes their position row** (§5.5) and **keeps every marker they placed**. Positions are measured exhaust; markers are the record of what happened. They become part of the adventure summary.
- Deleting a track or a ride cascades to its markers, and their photos.
- Deleting an account deletes its photos from object storage. **`ON DELETE CASCADE` does not reach object storage** — the blob must be deleted explicitly, so the nightly job (§7.11) sweeps for orphaned objects as a backstop. An orphaned blob is a privacy failure that looks like a storage bill.
- `GET /api/v1/me/export` includes markers and their photos.

**GPX waypoints round-trip, and v0.12's "ignored" rule is retired.** §15.3 dropped `<wpt>` elements because nothing in the model could hold them; markers are exactly what they are:

| GPX | Marker |
|---|---|
| `<wpt lat lon>` | Point |
| `<name>` | Title (truncated to 40, with the remainder appended to the note rather than lost) |
| `<desc>` / `<cmt>` | Note |
| `<sym>` | Icon, via a mapping table with `note` as the fallback |
| `<link>` | Ignored — an external URL is not a photo attachment, and fetching one server-side would be an SSRF hole |
| *(extension)* | Direction, written under a `dlr:` namespace; other readers ignore it, and we read ours back |

Import maps waypoints onto the imported track's markers; export writes them back. A file exported from this app and re-imported produces the same markers, which is the test that says the mapping is honest (`Gpx_MarkerRoundTrip_IsLossless`). Photos are not in GPX and are not attempted — the export is a `.gpx`, not an archive.

**Two details settled in SRV-26.** Waypoints are file-level rather than per-track, so they attach to the **first usable track** in the file — the ordinary case being one ride and the places along it. A file with waypoints and *no* track imports no markers at all, and that is correct rather than a gap: a marker needs exactly one parent, and there is nothing for these to hang off. The `dlr:` extension namespace is **`dlr://gpx/v1`**, reusing the app's own scheme rather than an `https://` domain, because a namespace is an identifier and not an address — pointing it at a real host invites something to try fetching it.

### 16.7 Schema and configuration

```
Marker(Id, TrackId?, GroupRideId?, CreatedByUserId, Lat, Lon, DirectionDeg?,
       Icon, Title, Note?, PhotoId?, CreatedUtc, UpdatedUtc)
       -- CHECK ((TrackId IS NULL) <> (GroupRideId IS NULL))            §16.1
       -- DirectionDeg NULL means "no direction", never north           §16.2
Photo(Id, OwnerId, BlobRef, ThumbBlobRef, WidthPx, HeightPx, ByteSize,
      ContentHash, CreatedUtc)
       -- Content is re-encoded and metadata-free by construction       §16.4
       -- Marker.PhotoId is ON DELETE SET NULL, never a cascade         §16.4
       -- ContentHash is of the STORED bytes, not of the upload         §16.4
```

**`ContentHash` hashes what was stored, not what arrived.** Hashing the upload would make the hash a function of the sender's encoder, so one photograph sent from two devices would look like two different images; hashing the re-encoded bytes describes what is actually on the disk being backed up.

*(v0.14: `MarkerReport` was generalised into `ContentReport`, which covers markers and comments and snapshots the reported content — §17.7.)*

Indexes: `Marker(GroupRideId)`, `Marker(TrackId)`, `Photo(OwnerId)`, `ContentReport(ResolvedUtc)` partial on unresolved.

| Key | Default |
|---|---|
| `Markers:MaxPerTrack` / `MaxPerGroupRide` / `MaxPerMemberPerRide` | 200 / 500 / 50 |
| `Markers:TitleMaxChars` / `NoteMaxChars` | 40 / 500 |
| `Photos:MaxUploadBytes` | 12 MB |
| `Photos:MaxDecodedPixels` | 40 MP |
| `Photos:MaxDimension` / `ThumbDimension` | 2048 / 320 px |
| `Photos:Quality` | 85 |
| `Photos:UploadsPerHourPerUser` / `UploadsPerDayPerUser` | 30 / 200 |

### 16.8 Tests to write first

```
Marker_WithBothParents_IsRejectedByCheckConstraint
Marker_WithNeitherParent_IsRejectedByCheckConstraint
Marker_NullDirection_IsStoredAsNullNotZero
Marker_DirectionOutOfRange_Returns400
Marker_UnknownIconKey_IsStoredAndRendersAsFallback
Marker_TitleOverLimit_Returns400
Marker_NoteIsNeverRenderedAsHtml
Marker_OnGroupRide_ByNonMember_Returns403
Marker_OnGroupRide_AnyMemberMayCreate
Marker_EditByOtherMember_Returns403
Marker_DeleteByOrganiser_Succeeds
Marker_ExceedingPerRideCap_Returns409
Marker_Added_IsBroadcastAsItsOwnMessageNotInPositionBatch
Marker_ReconnectingClient_ReceivesMarkersFromSnapshot
SharingOff_DeletesThePositionButKeepsTheMarkers
TrackDeleted_CascadesMarkersAndPhotos

Photo_ExifGpsTag_IsAbsentFromStoredImage
Photo_ExifOrientation_IsAppliedBeforeStripping
Photo_AllMetadata_IsAbsentAfterReEncode
Photo_DecompressionBomb_IsRejectedBeforeAllocating
Photo_ExceedsByteCap_Returns413
Photo_NotAnImage_ReturnsProblemDetails
Photo_ContentTypeLies_IsDetectedBySniffing
Photo_LargeImage_IsDownscaledAndThumbnailed
Photo_UploadedOffline_QueuesAndMarkerShowsPlaceholder
Photo_AccountDeleted_RemovesBlobsFromObjectStorage
NightlySweep_DeletesOrphanedPhotoBlobs

Gpx_WaypointsImportAsMarkers
Gpx_MarkerRoundTrip_IsLossless
Gpx_WaypointLinkElement_IsIgnoredAndMakesNoRequest
Gpx_UnknownSymValue_FallsBackToNoteIcon

Renderer_WithoutPersistentLabels_ShowsTitleInCallout
Renderer_WithoutRotatedMarkers_StatesDirectionAsText
Renderer_RotatingMap_LeavesLabelsUpright
Car_MarkerRendering_ShowsIconsWithoutTitlesOrNotes
```

---

## 17. Comments

### 17.1 The safety decision comes before the feature

A group ride gets one **thread**: text, photos, pinned posts, reactions and polls, visible to the ride's admitted members and nobody else. **Since v0.30 a shared route gets the same thread**, on a wider audience — see the closing note of this sub-section, and §19.2.

Before any of the mechanics, the constraint that shapes them:

> **The people this notifies are operating vehicles.** A thread that buzzes a phone in someone's tank bag at 100 km/h is not a chat feature, it is a design that asks riders to look down. §4.6 already accepted this reasoning for the car screen; a notification is worse than a car screen, because the car screen at least sits at eye level and the platform enforces the rules.

That yielded three rules, of which **two remain** (detailed in §17.6):

1. ~~**While the ride is `Live`, ordinary comments do not push.**~~ **Reversed.** Comments now notify in every ride state. The reasoning above still describes a real hazard; what changed is who answers it. Silence is now a rider's choice, made **entirely in the operating system** — Do Not Disturb, riding and driving focus modes, and the per-app and per-channel notification settings every phone already has. There is deliberately **no mute control in this app** (see §17.6). The cost is stated plainly so it is not discovered later as a bug: a rider who silences nothing will be notified mid-ride, which is exactly what §17.1 was written to prevent. **v0.27 finished the reversal**: the app also stopped suppressing a notification for the thread the rider happened to have open, and the Android channel moved from `Default` to `High`. What stands between a post and the phone is now one question — *is this the rider's own post* — and after that, the operating system.
2. **A pinned post from the organiser still carries the most weight** — *"fuel at the servo in 8 km"* is what a group needs mid-ride — but pinning is now an ordering and prominence device, not the sole way through a silence.
3. **Comments never appear on a car screen.** Not truncated, not as a count badge, not at all. Unchanged, and still structural: the car screen is the one surface where the platform, not the app, sets the rules.

**The thread spans the whole ride, not just the live window**, and that is where most of the value is: *before* (what time, which route, who's actually coming — the poll case), and *after* (photos and argument about who was slowest). During the ride, traffic should be near zero, and the design should make that the path of least resistance rather than something riders have to resist.

~~**Group rides only in v1.**~~ **Reversed in v0.30 — a shared route has a thread too (§19.2).**

v0.14 wrote the rule down like this, and it is left here rather than deleted because the reasoning is worth reading against what replaced it:

> *A comment thread on a publicly shared track (§15) would let people the organiser never admitted post into someone's space, which discards the entire abuse model of §5.2 — organiser consent — and replaces it with a moderation problem.*

**What that argument got wrong is which space it was talking about.** Organiser consent protects an adventure, whose whole premise is a curated group; it was never what protected a shared route, because a route on the browse list has been **deliberately** put in front of every signed-in rider on the service. There was no consent model there to discard. And "replaces it with a moderation problem" describes something the project had already solved for the harder case: reporting, blocking, an owner who can delete and pin, the §7.8 ladder holding new accounts back, and the caps in §17.7. A route thread inherits every one of those without a line being written for it.

The rest of the paragraph was simply accurate. *"Adding a `TrackId` parent later is one migration"* — it was one migration; and building the arc in v0.14, before anything published a route to anybody, would have been the speculative generality it was called.

**What the two threads do not share is who gets in**, and that is the whole of the difference:

| | Adventure thread | Shared route thread |
|---|---|---|
| Who reads it | Admitted members only (§5.2) | Any signed-in rider, while the route is `Public` |
| Who posts | Members, subject to §5.8's switches | Any signed-in rider, subject to §7.8's ladder |
| Who moderates | Organiser and leaders | The route's owner |
| Read-only when | Never — an adventure has no lifecycle to end either, since v0.32 (§5.1) | Never — a route has no lifecycle to end |
| Disappears when | The ride is deleted | The route is un-shared (posts survive) or deleted (they do not) |

### 17.2 Comments, and what they carry

| Field | Rules |
|---|---|
| **Body** | Up to `Comments:MaxChars` (default 2000). **Plain text** — never rendered as HTML or Markdown, exactly as for marker notes (§16.2) |
| **Photo** | One, optional, and it is the **same `Photo` resource as §16.4** — same ingest, same re-encode, same EXIF destruction, same quotas. Nothing new to secure |
| **Author** | An admitted member, or — on a route's thread — any signed-in rider (§19.2). Their immutable username (§7.2) labels the post, and because it is immutable it can be denormalised into a cached thread with no invalidation |
| **Kind** | `Text` or `Poll` (§17.5) |

**A comment with a photo and no text is legitimate** — most post-ride posts are exactly that — so the validation is "body or photo, at least one", not "body required".

**URLs are not linkified, and no link preview is ever fetched.** Rendering a tapable link inside a trusted ride thread is a phishing surface, and fetching a preview server-side is the same SSRF hole §16.6 refused for GPX `<link>` elements. Plain text is the whole feature.

**Editing** is allowed by the author within `Comments:EditWindowMinutes` (default 15) and sets `EditedUtc`, which the UI shows. After that, delete and repost. A permanently editable thread lets someone rewrite what a poll was actually asking after people have voted on it.

### 17.3 Ordering, offline posting and the clock

Comments compose offline and drain through the outbox (§4.4) like everything else, with a client-generated GUID making the upload idempotent. That raises a question a live thread cannot dodge:

> A rider writes a comment at 10:04 in a valley with no signal. It uploads at 14:32. Where does it go in the thread?

**Ordered by server receipt (`PostedUtc`), not by authored time.** Dropping four-hour-old text into the middle of a conversation that has moved on is confusing, and ordering by a client-supplied timestamp means the ordering is only as trustworthy as the least accurate clock in the group — or the most malicious. Where `CreatedUtc` and `PostedUtc` differ by more than `Comments:StaleAuthorMinutes` (default 10), the UI shows both: *"14:32 — written 10:04"*. The rider's intent is preserved without letting it rewrite history.

`CreatedUtc` is clamped server-side to never exceed receipt time. A client clock set to next year must not pin a comment to the top of every thread forever.

### 17.4 Reactions

A fixed, keyed set — `like`, `love`, `laugh`, `wow`, `sad`, `thanks` — for the same three reasons the marker icon set is fixed (§16.2): it renders identically everywhere, it needs no emoji-font negotiation across platforms, and it has no moderation surface. String keys, so a newer client can send one an older client renders as a generic reaction rather than crashing.

**One reaction per user per comment**, so the table is `PRIMARY KEY (comment_id, user_id)` and reacting again replaces rather than accumulates. "Who reacted" is then a trivial query, and the storage cost of the whole feature is one narrow row per person per comment they cared about.

**Reactions are aggregated on the wire, never enumerated.** A comment DTO carries counts plus the caller's own reaction:

```json
"reactions": { "like": 7, "thanks": 2, "mine": "like" }
```

**And they are not broadcast one message per tap.** Reactions are the highest-frequency, lowest-value event in the product — a batch of *"n × 12 members"* hub messages for people tapping a thumbs-up on the same photo is precisely the O(n²) mistake §5.3 avoided for positions. So reaction changes are **coalesced per comment on a short timer** (`Comments:ReactionCoalesceSeconds`, default 3) and delivered as one `ReactionsUpdated` message carrying the new counts. A count arriving 3 seconds late has cost nobody anything.

### 17.5 Polls are comments

A poll is `Comment.Kind = Poll` with a `Poll` record hanging off the same row — not a parallel entity. That is worth being deliberate about, because it means polls inherit **threading, pinning, reactions, permissions, reporting, deletion, export and the whole realtime path** without a line of new code for any of them. A separate `RidePoll` table would have needed its own copy of all of it.

| Aspect | Decision |
|---|---|
| Question | The comment body |
| Options | 2 to `Polls:MaxOptions` (default 6), each ≤ 80 chars, ordinal-ordered |
| Multi-select | `AllowMultiple` flag, chosen at creation |
| Closing | Optional `ClosesUtc`, plus the author or organiser closing it early. A closed poll shows results and rejects votes with a distinguishable `409` |
| Results | **Always visible, before and after voting** |
| Votes | **Attributed — you can see who voted for what** |
| Changing a vote | Replaces it (single-select) or toggles the option (multi-select) |

**Votes are attributed and there is no anonymous mode**, because the actual question people ask is *"who's coming on Saturday?"* and an anonymous tally answers a different, less useful one. It also means votes need no separate privacy story: a vote is visible to exactly the audience the ride already has. If a genuinely sensitive poll is ever needed, that is a new feature with its own design, not a checkbox on this one.

Poll results freeze when the poll closes, and ride into the adventure summary alongside the markers (§16.6).

### 17.6 Notifications, pinning and lifecycle

**Pinned posts** are the ride's noticeboard. The organiser or a leader (`GroupRideMember.Role`) may pin up to `Comments:MaxPinned` (default 3); pinned posts render at the top of the thread regardless of age and are the only comments that survive the thread's pagination on first load.

**What pushes, and when** — the table that encodes §17.1:

| Post kind | Notification |
|---|---|
| Ordinary comment | Push |
| Poll created | Push |
| Pinned post | Push |

**One row, because there are no adventure states left to have rows for** (v0.32, §5.1). The table used to have five, and the `Live` one **read `Silent` until v0.26**: the rule moved from something the app enforced for everyone to something each rider sets for themselves, and v0.32 removed the axis it varied on.

**There is no mute setting in this app, and that is the decision rather than an omission.** Earlier drafts of this section specified a per-ride mute toggle. It is not built and will not be. Every phone already has Do Not Disturb, a riding or driving focus mode, per-app notification switches, and — on Android — per-channel control that can silence adventure posts while leaving the ride's ongoing location notification alone. An in-app copy of that would be a second, worse control that covers one app, has to be found in a settings screen the rider does not habitually visit, and cannot know that they are currently driving. The platform's version is better on every axis that matters, so the app ships none.

What that leaves the app responsible for is **one** question in `CommentNotifier.ShouldNotify`: is the post the rider's own. Nothing else.

**v0.27 removed the second question** — *"are they already reading the thread it landed in"* — and with it `AppForegroundState`, which existed only to keep that question honest. The rule was defensible on its own terms and it failed in a way worth recording: it needed the app to know both that the thread page was mounted *and* that the rider was actually looking at it, because a rider who opens the thread at a set of lights and then locks the phone leaves the page mounted for the rest of the ride. The foreground flag patched that, and the result was two pieces of state, a platform event pair on the MAUI window and a rule that no rider had asked for. Presenting always costs one redundant banner while somebody is reading the thread — a case in which they are, by definition, looking at the phone — and buys the guarantee that a post never fails to arrive because the app second-guessed where the rider's attention was. Opening a thread still **withdraws the card already in the shade** for that adventure (`CommentNotifier.ThreadOpened`), which is housekeeping, not silence.

**Delivery is by local notification, not push (v0.26).** The app registers with no push service at all:

| | What is *not* needed | Why it works without it |
|---|---|---|
| **iOS** | No APNs key, no `aps-environment` entitlement, no `RegisterForRemoteNotifications`, no device tokens on the server | `UNUserNotificationCenter` schedules on the device. The `location` background mode the receiver already declares (§4.3) keeps the process — and the hub connection — alive through a ride |
| **Android** | No FCM sender key, no `google-services.json`, no Firebase dependency | `NotificationManagerCompat`, on a process the receiver's foreground service is already keeping alive (§4.3) |

The insight is that **the message has already arrived**. Every ride screen holds a SignalR connection (§5.3), so the post a push service would have carried is in memory before any notification is composed; a push path would have been a second, slower, credentialed route for something the app already has. The whole feature is one `notify` call.

**The cost, stated so it is not found as a bug:** a notification can only be raised by a process that is running. During a ride the app is running, which is the case §17.1 is about and the case this has to get right. Outside a ride the OS suspends it and nothing is raised — the rider sees the thread when they next open it. That is the trade for owning no push infrastructure, and it falls in the right place.

Android importance is **`High` as of v0.27** — a heads-up banner as well as a sound and a card in the shade. It read `Default` on the argument that a banner slides over the live map a rider is navigating by, which was the last of §17.1 surviving as code; the argument stands, and the answer to it is now the rider's, in the channel's own settings or a focus mode. Lock-screen visibility moved from `Private` to `Public` at the same time and for the same reason: hiding the text until the phone is unlocked was the app overriding, in one direction and for one app, a choice Android already offers system-wide.

**The channel id is `dlr.thread.v2`, and the version is not decoration.** Android fixes a channel's importance at creation and ignores every later create for the same id — deliberately, so an app cannot turn its own volume back up on a rider who turned it down. Raising the constant alone would therefore have changed nothing on any phone that already had the app: `dlr.thread` would still sit there at `Default`. A new id is a new channel, so the new importance actually lands; the retired one is deleted on the first post after the upgrade rather than left in the app's settings as a second, dead *"Adventure posts"* row. The cost is that a rider who had tuned the old channel has to tune this one once.

**Lifecycle**, following the authored-versus-measured line already drawn in §16.1:

- A rider stops sharing → **their position is deleted (§5.5); the thread is untouched.** Measured location and authored content have different lifetimes, and that is the §16.1 line.
- **Nothing makes a thread read-only.** v0.14 through v0.31 said a thread went read-only thirty days after the ride was `Completed`; nothing in the product ever assigned that state, so the rule never once fired. It went with the enum in v0.32. If read-only-after-N-days is wanted it comes back as its own `ArchivedUtc` column and its own sweep, with a test that it actually runs.
- A member who leaves, or is removed, **keeps their posts in the thread** — deleting half a conversation makes the other half nonsense — but loses all access to it. An organiser who removed someone for abuse can delete their posts explicitly.
- **Account deletion removes that account's comments, reactions and votes** (§10.1's hard delete is not negotiable). This leaves gaps in old threads. Accepted, and stated here so it is not discovered as a bug.
- Deleting the ride cascades everything, including photos out of object storage (§16.6).

**Neither thread has a lifecycle**, because neither subject has one — a line on a map never did, and since v0.32 nor does an adventure (§5.1). Neither is ever read-only. The two events that do affect it are in §19.2: un-sharing hides it and keeps the posts, deleting the route cascades them away.

### 17.7 Moderation, permissions and caps

The thread is now the largest user-generated-content surface in the product, so **`MarkerReport` (§16.5) is generalised** rather than joined by a second table:

```
ContentReport(Id, TargetKind{Marker,Comment}, TargetId, GroupRideId?, ReportedByUserId,
              Reason, ContentSnapshot, CreatedUtc, ResolvedUtc?)
```

`ContentSnapshot` is the point of the change: an organiser deleting an abusive comment must not also destroy the evidence for the report they just filed. The snapshot is purged with the resolved report by the nightly job (§7.11) after `Moderation:ReportRetentionDays` (default **90**). **Resolved ones only** — ageing out an open report would turn a backlog into a silent amnesty, and the operator's queue is exactly the thing that gets behind.

`GroupRideId` is **null for a report on a route's thread**, and the column was already nullable. A report is attached to a ride when there is an organiser to route it to; on a route the only other person with standing is the owner, who may well be who the report is about, so it goes straight to the operator. *Who may report* is likewise **reachability, not membership** — a membership check would have left the most public thread on the service as the one nobody could report, which is precisely the store-review requirement §10.2 exists to satisfy.

The rows below are the adventure's. **A route's thread answers the same questions differently, and only these** (§19.2):

| Action | Adventure | Shared route |
|---|---|---|
| Post | Any admitted member **while `AllowMemberComments` is on**; photos additionally need `AllowMemberPhotos` (§5.8) | Any signed-in rider who can see the route. No content switches exist — there is no organiser to own them — so §7.8's ladder is the whole gate |
| React, vote | Any admitted member — **never gated by the content switches**, since neither carries free text or storage worth moderating | Any signed-in rider who can see the route |
| Edit own post (within the window) | Author | Author |
| Delete a post | Author, **or** the organiser/leader | Author, **or** the route's owner |
| Pin / unpin | Organiser, leader | The route's owner |
| Create a poll | Any member; close it — author or organiser | Accepted by the server, but **the composer does not offer it** (§19.2) |
| Report | Any member | Anyone who can reach the thread |
| Block a user | Any member — hides that user's comments, reactions and markers from them, and prevents future co-membership (§16.5) | Same, and it additionally takes the blocked rider's **routes, their threads and their ratings pages** off the blocker's screen entirely (§19.1) |

Caps and limits, all configuration (§14.5):

| Limit | Default |
|---|---|
| Comments per thread | 2 000 |
| `POST /comments` | 30/hour per user **per thread** |
| Polls per ride | 20; 5/day per user |
| Reactions | 120/hour per user |
| Pinned per ride | 3 |
| Body / poll option length | 2 000 / 80 chars |

### 17.8 Realtime and API

Hub additions to `IRideClient` (§5.3), scoped to the group the client joined:

```csharp
Task CommentPosted(CommentDto comment);
Task CommentEdited(CommentDto comment);
Task CommentRemoved(Guid commentId);
Task CommentPinChanged(Guid commentId, bool isPinned);
Task ReactionsUpdated(Guid commentId, ReactionCounts counts);   // coalesced, §17.4
Task PollUpdated(Guid commentId, PollResults results);          // coalesced
```

**There are two groups, in two namespaces**: `ride:{id}`, joined by `JoinRide` after the membership check §5.3 already describes, and `track:{id}`, joined by `JoinTrack` after a narrower one — *is the route public, or is the caller its owner*. The prefixes are load-bearing rather than tidy: both identifiers are guids, and one namespace would put every reader of a route into the group that carries a ride's live positions if the two ever collided. A message body carries `GroupRideId` and `TrackId` so a client watching both can tell them apart; nothing does today, but the events are process-wide.

On reconnect the client **fetches the thread**, it does not replay — the same rule as positions, markers and everything else on this hub (§5.3). Both group kinds are re-joined.

```
GET    /api/v1/group-rides/{id}/comments        cursor-paginated, pinned first
POST   /api/v1/group-rides/{id}/comments        { body?, photoId?, poll? }
GET    /api/v1/tracks/{id}/comments             a route's thread — identical shape (§19.2)
POST   /api/v1/tracks/{id}/comments             { body?, photoId? }
PATCH  /api/v1/comments/{id}                    author, within the edit window
DELETE /api/v1/comments/{id}                    author, or whoever runs the thread
POST   /api/v1/comments/{id}/pin                whoever runs the thread; { pinned }
PUT    /api/v1/comments/{id}/reaction           { reaction } — null clears
POST   /api/v1/comments/{id}/votes              { optionIds }
POST   /api/v1/comments/{id}/close-poll         author, or whoever runs the thread
POST   /api/v1/comments/{id}/report             → ContentReport
```

**Only the first two lines above are per-subject. Everything from `PATCH` down keys on the comment's own id and did not change at all** when routes gained threads — the difference is resolved once, before any of it runs, by a `ThreadAccess` record answering *may they read / post / attach a photo / moderate*, plus the refusal to return if not. That is the whole of the arc: one table, one controller, one resolver, and every permission check written in one place rather than two that will be changed once.

The web app needs no separate work for any of this: since v0.16 it runs the same `DLR.UI` thread component and the same SignalR client as the phone (§18.4), so a self-updating thread is not a web feature at all — it is the feature, rendered in a second host. **v0.30 applied the same argument one level down**: the thread is a component (`CommentThreadView`) rather than a page, so the adventure's screen and the route's screen render the same reactions, the same pinning and the same optimistic insert because they are the same component, not because two files agree.

### 17.9 Schema

```
RideComment(Id, GroupRideId?, TrackId?, AuthorId, Kind{Text,Poll}, Body?, PhotoId?,
            IsPinned, PinnedByUserId?, PinnedUtc?,
            CreatedUtc, PostedUtc, EditedUtc?)
            -- CHECK (Body IS NOT NULL OR PhotoId IS NOT NULL)      §17.2
            -- CHECK ((GroupRideId IS NULL) <> (TrackId IS NULL))   §17.1, v0.30
            -- Ordering is on PostedUtc; CreatedUtc is clamped      §17.3
CommentReaction(CommentId, UserId, Reaction)   -- PK (CommentId, UserId)  §17.4
Poll(CommentId, AllowMultiple, ClosesUtc?, ClosedUtc?, ClosedByUserId?)
                                               -- PK CommentId, 1:1 with the comment
PollOption(Id, CommentId, Ordinal, Text)
PollVote(PollOptionId, UserId)                 -- PK (PollOptionId, UserId)  §17.5
ContentReport(Id, TargetKind, TargetId, GroupRideId?, ReportedByUserId, Reason,
              ContentSnapshot, CreatedUtc, ResolvedUtc?)            -- §17.7
```

**One table for both threads, with a check constraint holding the shape.** `RideComment` hangs off an adventure **or** a route, never both and never neither — and that is enforced in the database rather than promised by the endpoints, because it is exactly the state a future write path reaches by accident. Both foreign keys cascade.

Indexes:

| Index | Why |
|---|---|
| `RideComment(GroupRideId, PostedUtc desc)` | The adventure thread, newest first |
| `RideComment(TrackId, PostedUtc desc)` | The route thread. **One per subject, not one composite** — a composite leading on both columns has a null in every row's leading column for half the table, which is an index the planner reaches for in neither case |
| partial `RideComment(GroupRideId) WHERE IsPinned` | At most three rows per thread, fetched on every first load |
| partial `RideComment(TrackId) WHERE IsPinned` | The same, for a route |
| **unique** `RideComment(GroupRideId, AuthorId, ClientGuid)` | Idempotency for an adventure post (§17.3) |
| **unique** `RideComment(TrackId, AuthorId, ClientGuid)` | Idempotency for a route post — and **this is why the pair could not simply be widened.** PostgreSQL treats nulls as distinct in a unique index, so the index above cannot decide anything about a row whose `GroupRideId` is null: every drain of an outbox would slip past it. Each kind gets an index whose leading column is never null for the rows it judges |
| `PollVote(PollOptionId)` | Tallying a poll |
| partial `ContentReport(ResolvedUtc)` on unresolved | The operator's queue |

**Migrating is a widening, not a rewrite.** `GroupRideId` becoming nullable leaves every existing row holding its value, and the new check constraint is satisfied by all of them because `TrackId` defaults to null. Nothing is backfilled. Going back down narrows the column again and **fails outright if any route comment exists** — as it should: there is nowhere for those rows to go, and a `Down` that quietly discarded a conversation to make a column fit would be worse than one that refuses.

### 17.10 Tests to write first

```
Comment_ByNonMember_Returns403
Comment_WithNeitherBodyNorPhoto_Returns400
Comment_BodyIsNeverRenderedAsHtml
Comment_UrlInBody_IsNotLinkifiedAndFetchesNothing
Comment_EditAfterWindow_Returns409
Comment_EditByOtherMember_Returns403
Comment_DeleteByOrganiser_Succeeds
Comment_PostedOffline_OrdersByServerReceiptNotAuthoredTime
Comment_ClientClockInFuture_IsClampedToReceiptTime
Comment_StaleAuthoredTime_IsSurfacedAlongsidePostedTime
Comment_ExceedingRideCap_Returns409
Comment_PhotoUsesTheSameStrippedIngestPath                  — §16.4

Pin_ByOrganiser_MovesCommentToTopOfThread
Pin_ByOrdinaryMember_Returns403
Pin_ExceedingMaxPinned_Returns409

Reaction_SecondReactionBySameUser_ReplacesTheFirst
Reaction_Cleared_RemovesTheRow
Reaction_Response_CarriesAggregateCountsNotIndividualRows
Reaction_ManyInQuickSuccession_CoalescesIntoOneHubMessage

Poll_WithFewerThanTwoOptions_Returns400
Poll_VoteByNonMember_Returns403
Poll_SingleSelect_ChangingVoteReplacesIt
Poll_MultiSelect_TogglesOptionsIndependently
Poll_VoteAfterClose_Returns409
Poll_ClosesUtcElapsed_RejectsVotesWithoutABackgroundJob
Poll_Results_AreAttributedToVoters
Poll_IsPinnableAndReactableLikeAnyComment                   — the point of §17.5

APostFromSomebodyElse_RaisesANotification                   — §17.6, v0.26 reversal
ARidersOwnPost_NeverNotifiesThem
APostInTheThreadTheRiderIsReading_IsNotAlsoBuzzedAtThem
AThreadLeftOpenOnABackgroundedPhone_DoesNotSuppressAnything
ARiderWhoRefusedThePermission_IsNotNotified                 — a choice, not a fault
Car_ThreadIsNotRenderedAtAll                                — §4.6

SharingOff_DeletesThePositionButKeepsTheThread
MemberRemoved_KeepsPostsButRevokesAccess
BlockedUser_CommentsAreHiddenFromTheBlocker
AccountDeleted_RemovesCommentsReactionsAndVotes
Report_SnapshotSurvivesDeletionOfTheComment                 — §17.7

                                                            — the route thread, v0.30 (§19.2)
AnySignedInRider_CanReadAndPostToASharedRoutesThread
APrivateRoutesThread_IsA404ToEverybodyButItsOwner
UnsharingHidesTheThread_AndResharingBringsItBack
ARouteWhoseOwnerTheReaderBlocked_HasNoThreadForThem
TheRoutesOwner_CanDeleteAndPinSomebodyElsesPost
AReader_CannotPinOrDeleteSomebodyElsesPost
RepostingTheSameClientGuid_IsTheSamePostRatherThanASecondOne — the second unique index, §17.9
ARoutesThreadAndAnAdventuresThread_DoNotLeakIntoEachOther    — one table, two filters
APostOnARoutesThread_CanBeReported                           — reachability, not membership
DeletingTheRoute_TakesItsThreadWithIt
ReactionsWorkOnARoutesThreadWithoutAnythingBeingAddedForThem  — the claim §17.8 makes, tested
```

---

## 18. UI Architecture — One Razor Library, Two Hosts

### 18.1 The decision, and what it replaces

**Every screen is a Razor component in `DLR.UI`, compiled into two hosts:**

| Host | What it is | Serves |
|---|---|---|
| **`DLR.App`** | **MAUI Blazor Hybrid** — a single MAUI project for Android and iOS, whose UI is one `BlazorWebView` hosting `DLR.UI` | Both phone platforms |
| **`DLR.Web.Client`** | **Blazor WebAssembly**, served by `DLR.Server` | The website |

This replaces v0.15's split of **XAML + MVVM on mobile** and **Blazor Server + Razor on the web**, which meant every screen in §4.1 was built twice in two different technologies, with a third JS map implementation on the web only. One component set is the point of the change.

**Two things are explicitly unchanged**, and both matter:

- **Android and iOS remain a single MAUI project.** They have been since v0.1; Blazor Hybrid does not alter it. One project, two target frameworks, one `Platforms/` folder for the genuinely platform-specific code (§4.3).
- **The car heads stay entirely native** (§4.6). Android Auto and CarPlay render templates and a raw drawing `Surface`; there is no browser and no DOM in a head unit. Nothing in this section reaches them.

**What this is not.** `DLR.Core` is untouched — the domain, sync engine, SQLite repository, track codecs and stats stay exactly where they are, referenced by both hosts. This change is about the presentation layer only, and the fact that it can be made without opening `DLR.Core` is the evidence that v0.1's layering was drawn in the right place.

### 18.2 What actually gets shared, and what cannot be

| Concern | Shared in `DLR.UI` | Host-specific |
|---|---|---|
| Screens, forms, lists, navigation | ✅ everything in §4.1 | — |
| Ride thread, polls, reactions (§17) | ✅ | — |
| Marker editor, track editor (§15, §16) | ✅ | — |
| The map (§18.3) | ⚠️ one component, one C# surface | ✅ **two JS modules** — MapKit JS on mobile, MapLibre + OSM on web (§4.5, v0.19) |
| GPS capture (§4.3) | Interface only | ✅ `ILocationProvider` per platform |
| Token storage (§18.5) | Interface only | ✅ Keychain/Keystore vs HttpOnly cookie |
| Local data (§18.6) | Interface only | ✅ SQLite on mobile; API calls on web |
| Camera / file picking | Component surface | ✅ `MediaPicker` vs `<InputFile>` |
| Notifications (§17.6) | Decision in `CommentNotifier` | ✅ local, on a hub message the app already has — no FCM, no APNs; the browser gets none in v1 |
| Background recording | — | ✅ mobile only, and the reason MAUI is here at all |
| Car heads (§4.6) | — | ✅ fully native |

**The rule that keeps this honest: `DLR.UI` references no MAUI assembly and no platform API.** It must compile into a browser. Everything platform-shaped reaches it through the interfaces that already exist in `DLR.Core/Abstractions` — which were written for testability in v0.1 and now carry a second job. An architecture test enforces it (§10.4), because this is precisely the convention that erodes the first time someone needs "just one" `DeviceInfo` call.

**`#if ANDROID` inside a shared component is the failure mode to watch for.** The correct move is always an interface with two implementations registered per host. A shared library full of conditional compilation is two libraries wearing one name, and it will drift.

### 18.3 The map — a JS module per host, Mapsui for the car

This is the largest consequence of the change, and it retires a decision that has stood since v0.2.

**A native map control cannot be hosted inside a Razor page.** `Microsoft.Maui.Controls.Maps` is a MAUI view; the UI is now a WebView. Mixing native pages into a hybrid app is possible, but the map appears inside the live ride screen, the track detail, the marker editor and the route planner — nearly every screen — so keeping it native would fragment most of the app and defeat the reason for the change.

So **`NativeMapRenderer` is deleted from the design** and the renderer set becomes:

| Renderer | Hosts | Purpose |
|---|---|---|
| **MapKit JS** *(v0.19)* | `DLR.UI` in the MAUI WebView | Android and iOS |
| **MapLibre GL JS + OSM** *(v0.19)* | `DLR.UI` in the browser | The web |
| **Mapsui / SkiaSharp** | Android Auto `Surface`, CarPlay `CPWindow` | The car heads, via `IMapRenderer` (§4.6) |

**This was already most of the way to happening.** v0.9 found the native control could not draw into a car Surface; v0.13 found it could not draw custom marker icons, rotation or persistent labels either, so §16 was going to ship "with Mapsui or degraded" regardless.

> **v0.19 correction to this section.** v0.16 claimed here that the phone's map and the web's map would be *the same code*, and counted it as a benefit that removed a class of "right on the website, wrong on the phone" defect. **That is no longer true** — the phone uses Apple Maps and the web uses OSM (§4.5), so that class of defect is back and the tests in §18.8 have to cover both modules. What survives is real but more modest: one `RideMap` component, one C# surface, one `MapCapabilities` contract, two JS modules behind one interop interface. The claim about a shared *UI* stands; the claim about a shared *renderer* lasted three versions.

`IMapRenderer` and `MapHostKind` survive **unchanged in purpose but narrowed in scope**: they now describe the car path only. The startup assertion that every `MapHostKind` has a factory (§4.6) still holds and matters more, since it is the only remaining consumer.

~~**The honest cost: tile hosting moves from Phase 3 to Phase 1.**~~ **Reversed in v0.19** — Apple hosts the phone's tiles and OSM hosts the web's, so nothing needs standing up in Phase 1 after all (§4.5, §9.1). The bill comes due when the web app is publicly announced and OSM's usage policy stops covering it (§13 Q26), not before.

**And a real unknown: map performance and battery inside a WebView.** Twenty rider pins updating every 5 s over a moving map, with GPS running and the screen on, on a mid-range Android, against a target of under 8 %/hour (§10.3). Native rendering was a known quantity; this is not — and v0.19 sharpens it, because the module under test on Android is now **MapKit JS in an Android WebView**, which is further off Apple's tested path than MapLibre was. **It joins background location as a Phase 0 spike**, with the licensing question in §4.5 attached to it. If it fails, the fallback is MapLibre on Android — the web's module, already written.

### 18.4 The web: WASM for the app, static SSR for the public pages

Blazor **Server** is out. The web client is WebAssembly, and that changes three things for the better and one for the worse.

**Render mode per page**, which the Blazor Web App model expresses directly:

| Pages | Mode | Why |
|---|---|---|
| Landing, shared-ride links, shared-track pages | **Static SSR** | §6.1 requires these to be SEO-friendly and to work without the app installed. WASM is bad at both |
| Auth landing pages (confirm, reset) | **Static SSR** | Must work in any browser, instantly, including for app-only users (§7.7) |
| The signed-in application | **WASM** | Interactive, offline-tolerant of a flaky connection, and it is where the shared components live |
| AGPL §13 footer (§14.6.2) | Rendered by the SSR shell | Present on every page regardless of mode |

**What improves:**

- **The sticky-session requirement disappears.** Blazor Server held a live circuit per browser tab, which pinned the web tier to one instance and ruled out scale-to-zero. WASM is static files: served and cached by Caddy, zero server memory per tab. The scale-out ladder in §9.2 gets shorter, and the €4 VPS carries more.
- **The web becomes just another API client.** It calls the same REST endpoints and the same SignalR hub as the mobile app, so there is no server-rendered special case, no second authorisation path, and the `DLR.Core.Contracts` compile-time guarantee (§3) now covers the website too.
- **One transport story.** §5.3's reconnect-and-snapshot rules, the position batch, the coalesced reactions — all identical on web and mobile, because it is literally the same client code.

**What gets worse, stated plainly:** the first load downloads a WASM runtime and the app assemblies — a few megabytes, versus Blazor Server's near-instant first paint. Mitigations are ordinary and sufficient here: trimming, brotli, immutable cache headers on fingerprinted assets, and static SSR on exactly the pages a first-time visitor lands on (§9.1). A returning signed-in user is hitting cache.

### 18.5 Authentication diverges by host, and should

`ITokenStore` (§3) already abstracted this, which is why the change is small. But the two hosts genuinely need different answers:

| | Mobile (`DLR.App`) | Web (`DLR.Web.Client`) |
|---|---|---|
| Refresh token | Keychain / Keystore via `SecureStorage` | **HttpOnly, Secure, SameSite cookie** — never reachable from JS |
| Access token | Memory | Memory |
| Session lifetime | **Never expires** (§7.4) | **Expires** — sliding, `Auth:WebSessionDays` default 30 |

**A refresh token must never sit in `localStorage`.** §7.4 makes refresh tokens effectively permanent, so an XSS bug in a browser build would mean permanent account takeover rather than a session's worth of damage. The token stays in a cookie the JS cannot read, and the token endpoint accepts it there for web callers, with CSRF protection on that endpoint specifically.

**Web sessions expire and mobile sessions do not.** v0.5's "sign in once, never again" was reasoned about a *phone* — a personal device, in a pocket, protected by a device passcode. A browser is a library computer, a partner's laptop, a shared desktop. Applying "permanent" there would be applying a conclusion outside the argument that produced it. Thirty sliding days is generous for a browser and materially safer, and the revocation machinery (§7.10) treats a browser as a device like any other.

### 18.6 Offline-first stays a mobile property

`DLR.Core`'s SQLite repository, the outbox and the sync state machine (§4.4) are mobile-only. **The WASM client is online-only in v1.**

This has to be stated, because "shared components" invites the assumption that behaviour is shared too. It is not, and the seam is `IRideRepository`: SQLite-backed in `DLR.App`, API-backed in `DLR.Web.Client`. A component renders a ride list; it does not know or care where the list came from.

**The design consequence for the components themselves:** every screen must tolerate a repository that can fail with a network error, because on the web it can. That is good discipline the mobile app already needed for its sync path, and it is cheaper to honour from the first component than to retrofit.

Browser-side persistence (IndexedDB, or EF Core over sqlite-wasm) is not attempted. The web app is the big-screen surface for planning, editing and reading (§6.1) — the offline case belongs to the thing in the rider's pocket.

### 18.7 Testing gets simpler, which is the second-order win

A component written once is tested once. `DLR.UI.Tests` uses **bUnit** to render components against fake `DLR.Core` abstractions — no simulator, no emulator, no browser, running in the same `dotnet test` pass as everything else.

That covers the screens on **all three surfaces at once**, and it is a genuine change in what is practical: under v0.15, testing the mobile UI meant a MAUI UI-testing harness on real or emulated devices, which is slow enough that in practice it does not happen. The parts that remain device-bound are the parts that were always device-bound — background location (§4.3), the car heads (§4.6), and battery (§10.3).

bUnit joins the dependency list under §14.6.3's rule: it is MIT, so it passes the licence gate without a decision.

### 18.8 Tests to write first

```
Ui_NoProjectReferenceToMauiAssemblies                  — the load-bearing one (§18.2)
Ui_NoConditionalCompilationSymbolsInSharedComponents
Ui_ComponentsResolveOnlyDlrCoreAbstractions

RideList_RendersFromRepository_WithoutAPlatformHost    — bUnit, §18.7
Thread_PostingDisabled_WhenPermissionRevoked          — §5.8 through the UI
MarkerEditor_RendersDirectionAndIcon_WithoutAMap
TrackEditor_RangeSelection_MapsToRawIndices           — §15.5 in the component

Map_SameComponentInitialisesWhateverIsRegistered      — §4.5, v0.24
Map_EveryHost_ResolvesTheMapLibreModule
Map_BaseMapUnavailable_ShowsStatedErrorNotBlankMap
Map_AttributionIsPresent_AndDeclaredOnTheTileSource
Map_CarSurface_UsesMapsuiNotAJsModule                 — §18.3
MapHostKind_EveryHostHasAFactory                      — unchanged from §4.6

WebAuth_RefreshTokenIsNotReadableFromJavaScript       — §18.5
WebAuth_SessionExpiresAfterConfiguredDays
MobileAuth_SessionStillNeverExpires                   — §7.4 unchanged
Repository_WebImplementation_SurfacesNetworkErrors    — §18.6
```

---

## 19. Shared Routes — Ratings and Conversation

Sharing a route (`TrackVisibility.Public`) puts it on a catalogue every signed-in rider browses. Two things v0.30 adds to it, in one section because they answer the same question from opposite ends: **a star rating**, which is the fastest possible verdict, and **a thread**, which is the one with the detail in it.

Appended as §19 rather than folded into §15 for the reason v0.12 gave when it appended §15: renumbering §§16–18 and every cross-reference in this document is a bad trade against a heading number.

> **A note on where the browse list itself is written down: nowhere.** The two publication rules — *this road is already shared by somebody else* (a fingerprint over the coordinates) and *that name is taken* — along with the description, the cover photograph and the paged, filtered browse endpoint, were all built and shipped without a section here, and the `§6.2` citations throughout that code point at *6.2 Stack*, which is about Blazor and Npgsql. **That is a documentation debt this section does not clear.** It documents what v0.30 added; the gap is recorded so the next reader does not conclude those citations mean something.

### 19.1 The rating

**One to five whole stars. No half stars, and no zero.**

The scale lives in `DLR.Core/Tracks/TrackRatings.cs` because three things have to agree about it — the endpoint that refuses a six, the check constraint on the column, and the widget that draws five glyphs — and a scale only two of them knew would be a widget drawing five boxes for a column that accepts ten values.

**There is deliberately no zero.** Clearing a rating **deletes the row**; it does not store a nought. A nought would average in as the worst possible verdict for every rider who tapped a star and thought better of it, which is the opposite of what they meant — the same rule §8 already applies to a null ascent, applied to a score.

**One rating per rider per route, and the primary key is the rule.** `TrackRating` is keyed on `(TrackId, UserId)`, exactly as `CommentReaction` is keyed on `(CommentId, UserId)` and for the same reason: rating again replaces rather than accumulates, as a shape rather than as something every write path has to remember. An average over the table is then the average of what people currently think, not of every time anybody changed their mind.

| Concern | Decision |
|---|---|
| Who may rate | Any signed-in rider who can see the route — **the same audience that can post to its thread**, and for the same reason: a route on the browse list was put in front of everybody on purpose, and a score only the owner's friends could give is a score nobody should read |
| The §7.8 ladder | Applies. A brand-new account cannot rate, exactly as it cannot share a route or post a comment. Carried by the endpoint's policy attribute, not restated in the body |
| Rating your own route | **Allowed.** It is a strange thing to do and it is not this endpoint's business to forbid it — the alternative is a rule nobody asked for. The count is always shown beside the average precisely so that a score standing on one vote reads as one vote |
| A route nobody has rated | `Average` is **null**, never `0`, and the UI says *"Not rated yet"* rather than drawing five empty stars and a `0.0` (§8) |
| Verbs | `GET` reads, `PUT` sets — idempotent, because rating again replaces, so there is nothing for a `POST` to mean — `DELETE` withdraws. Withdrawing a rating that was never given is **success**, not a 404: an outbox sends it twice, and tapping your own star to clear it should not produce a scolding |

**A rating is anonymous, and that is why the block list does not filter it.** This is the one place §17.7's rule deliberately does not reach, so it is worth saying why rather than leaving it to look like an oversight. Blocking hides what somebody *wrote* — their comments, their reactions, their votes are all authored content with a name attached. Nothing anywhere records who gave a route three stars. Filtering a blocked rider out of the tally would make one reader's average differ from another's for a number they are both being asked to trust, and the difference itself would leak that a blocked rider had rated this route. The block still works where there is something to hide: it takes the whole route, its thread and its rating off the blocker's screen.

**The average and the count are on every browse row**, hydrated with one grouped query per page rather than a correlated sub-select per row — the same shape, and the same reasoning, as a thread page hydrating its reaction tallies (§17.4). Choosing between twenty roads without opening any of them is the entire job of that list; twenty extra round trips to draw one page would make the feature cost more than it is worth.

### 19.2 The thread

**It is §17's thread**, not a second one that resembles it. Same table, same controller, same plain-text body, same photograph, same six reactions, same fifteen-minute edit window, same pinning cap, same reporting and blocking. §17.1 carries the table of what differs; §17.7 carries the permissions; §17.8 carries the API and the hub groups. Only what is specific to a route is here.

**Who gets in.** Any signed-in rider, while the route is `Public`. The owner reaches their own route's thread whatever its visibility, so that taking a route off the list does not lock them out of what was said about it. Three things are refused, and all three answer **404** rather than 403 — a track id travels in links, and a distinguishable refusal would turn the endpoint into an oracle for which identifiers are real:

1. The route does not exist.
2. It is not `Public`, and the caller is not its owner.
3. The caller has blocked its owner (§17.7).

**Un-sharing hides the thread and keeps the posts.** Un-sharing is reversible and is the owner's own call about their own row; destroying other people's writing over it would be the app punishing a rider for changing their mind. Re-sharing brings the conversation back rather than starting a new one. **Deleting the route cascades the thread away**, because there is nothing left for it to be about.

**The route's owner moderates it** — deletes any post, pins up to `Comments:MaxPinned` — on the organiser's reasoning exactly: the person who published the thing is the person who has to be able to take an abusive post off it and pin the one worth reading first.

**Polls are not offered, and that is a UI default rather than a server rule.** A poll is a group deciding something together; a route's thread is riders telling each other what the road is like. The server accepts one either way and the composer's `AllowPolls` parameter is a single `false` — so this is a position that can be reversed by changing one word, which is the right weight for an editorial judgement.

**Rate limiting is per thread, not per ride.** Thirty posts an hour is a limit on flooding one conversation; spending it on somebody's route should not silence a rider in the adventure they are actually on. The bucket key is prefixed by kind, so a route and an adventure that happen to share an identifier do not share an allowance.

### 19.3 What this cost, and what it did not

The interesting property of v0.30 is how little of it is new code.

| Reused unchanged | Written for this |
|---|---|
| The comment table, plus one nullable column and two constraints | `ThreadAccess` — a record of decisions, and two resolvers that fill it in |
| Edit, delete, pin, react, vote, close a poll, report — **every one of them**, keying on the comment's own id | Two thread endpoints (`GET`/`POST /tracks/{id}/comments`) whose bodies are a caller check and a delegation |
| `CommentReaction`, `Poll`, `PollOption`, `PollVote`, `ContentReport` — no schema change | `TrackRating` + its three verbs |
| The whole thread UI, lifted from `RideThread.razor` into `CommentThreadView` | `StarRating.razor`, and the star row on the browse list |
| Blocking, reporting, the §7.8 ladder, the caps, the cursor, the coalescing broadcast | A second unique index, because nulls are distinct (§17.9) |

**Two seams moved to make that true**, and both are worth recording because each replaced a thing that would otherwise have been duplicated:

- **`ReactionBroadcastService` holds a hub group name, not a ride id.** The group is decided once, at the point where the change happened, by asking `ThreadAccess`; storing an id and re-deciding at flush time would have been the same decision in a second file.
- **`RideThread.razor` became a page of chrome.** The route page had to render *exactly* the thread the adventure page renders, and the only way to guarantee "exactly" is for there to be one of them. What is left on each page is the part that is genuinely about its own subject: a back link, and reading the permissions that decide whether the composer appears.

### 19.4 Tests to write first

```
Rating_AveragesEverybodyAndReportsTheCallersOwn
RatingTwice_ReplacesRatherThanCountingTwice                  — the PK is the rule, §19.1
Withdrawing_RemovesTheRatingRatherThanScoringItZero
WithdrawingARatingNeverGiven_IsSuccessRatherThanNotFound
StarsOutsideTheScale_AreRefusedWithASentence                 — 0, 6, -1; a 400, not a 500
APrivateRoute_CannotBeRatedByAnybodyElse_AndIsA404
ARouteWhoseOwnerTheReaderBlocked_IsNotRateable
ABlockedRidersRating_StaysInTheAverageEverybodyElseSees      — anonymous, so unfiltered §19.1
Browse_CarriesTheAverageAndTheCountOnEveryRow
DeletingTheRoute_TakesItsRatingsWithIt
```

The route thread's own list is in §17.10, beside the adventure's, because they are the same feature asked two questions.
