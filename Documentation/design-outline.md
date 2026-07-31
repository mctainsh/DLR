# Dumb Luck Rides — Design Outline

> **Status:** Draft **v0.20** — architecture outline; Milestone A of `tasks-server.md` is built.
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
- Live sharing is **asked for when you join, off unless you say yes, and revocable at any moment**. It ends when the ride ends — or, if the organiser grants it and you leave it on, within at most two hours afterwards so nobody's map goes dark while they are still riding home (§5.6). Never open-ended. No always-on tracking of friends.
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
│  │  │  + MapKit JS (Apple)  │  │  │   │ │  + MapLibre  │ │   (§18)
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
│  │                         PositionCacheRehydrator, PositionWriter (§5.5),
│  │                         SharingWindDownService (§5.6)
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
          └─ accuracy gate, speed sanity, min-distance/min-time gate
```

- **Accuracy profiles:** `Eco` (10 s / 25 m), `Balanced` (5 s / 10 m), `Precise` (1 s / 5 m). Precise for twisty roads / track days; Eco for touring.
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

### 4.5 Maps — Apple Maps on the phone, OSM on the web, Mapsui for the car

The renderer set, as of **v0.19**:

| Surface | Renderer | Tiles | Cost |
|---|---|---|---|
| **Android + iOS** | **MapKit JS** in the `BlazorWebView` | Apple's | Free within a generous daily quota; needs an Apple Developer account |
| **Web** | **MapLibre GL JS** | **OpenStreetMap**, *"to begin with"* | Free, but see the usage-policy note below |
| **Android Auto / CarPlay** | **Mapsui / SkiaSharp** into a raw `Surface` | Whatever it is given | Unchanged since v0.9 (§4.6) |

**Apple Maps on Android is not a mistake in that table.** MapKit JS is a *web* SDK — it runs in any modern browser, and since v0.16 the mobile UI **is** a browser (§18). So the same map renders on both phones, which is a better outcome for consistency than the platform-native split this project started with. Two things about it have to be verified rather than assumed, and both are Phase 0 (§11):

1. **Does it render and perform acceptably in an Android `WebView`?** Apple test against Safari and the major desktop browsers; an Android WebView is not on anyone's headline support matrix.
2. **Do Apple's MapKit JS terms permit it there?** Nothing in the SDK is OS-locked, but "runs" and "is licensed to run" are different questions, and this one is worth an answer in writing before the Android build depends on it.

If either answer is no, the fallback is the web's stack — MapLibre — on Android only, which is a swap of one JS module (below), not a redesign.

#### What this costs: v0.16's "one map everywhere" is withdrawn

v0.16 claimed the phone and the web would run *identical* map code, and listed that as a benefit. **Two providers means that is no longer true**, and the honest accounting is:

- `RideMap.razor` remains **one component** in `DLR.UI` with one C# surface. ViewModels, markers, route overlays and camera control are unchanged and still written once.
- Behind it sit **two JS modules** — `map.mapkit.js` and `map.maplibre.js` — implementing one interop contract, chosen at host registration.
- So the seam is one file boundary, not a fork of the UI. Every other screen in §4.1 is unaffected.

```csharp
// DLR.UI/Components/RideMap.razor.cs — the JS side implements this shape
public interface IMapInterop
{
	ValueTask InitAsync(MapOptions options);
	ValueTask SetCameraAsync(MapCamera camera);
	ValueTask SetRouteAsync(RouteOverlay? route);
	ValueTask UpsertMarkerAsync(MarkerDto marker);   // riders and §16 markers
	ValueTask RemoveMarkerAsync(Guid id);
	MapCapabilities Capabilities { get; }            // §4.5 rule 3, unchanged
}
```

The `MapCapabilities` flags earn their keep for a third time: MapKit JS and MapLibre do not support exactly the same things, and the UI degrades against the flags rather than against a provider name.

#### MapKit JS makes the map a server dependency

This is new, and it is the part most likely to be discovered late. **MapKit JS authenticates with a short-lived JWT signed ES256 with a private key issued by Apple**, and that `.p8` key must never reach a client. So:

```
GET /api/v1/maps/token        authed → { token, expiresUtc }
```

- The key lives with the other secrets (§14.3) and is added to the never-commit list (§14.2). It is in the same class as the APNs key: leaking it means someone else bills their map usage to you.
- Tokens are short-lived and minted on demand, cached in memory on the client until near expiry.
- The endpoint is authenticated and rate-limited like the rest of §7.8. A public token endpoint is a free map quota for the internet.
- **A map that cannot get a token shows a stated error, not an empty grey rectangle.** The same rule §4.6 applies to a car screen: a blank map is a worse failure than an honest one.

#### Offline maps are gone, not deferred

**MapKit JS has no offline mode**, and there is no tile cache to point at a local file. For an app whose premise is a trailhead with no signal, this deserves stating bluntly rather than in a capability table:

> Recording, markers, the thread and the whole app keep working with no signal (§4.4, §7.9). **The map behind them does not.** A rider in a dead zone sees their track and their position over a blank background.

That is a real regression against the Phase 3 ambition of MBTiles/PMTiles offline packs, and it is the one thing Apple Maps costs that the alternatives did not. It is survivable because the recorded data is never at risk — but it should be a deliberate acceptance, not a surprise. The `MapProvider` setting (below) is what keeps the door open: a self-hosted PMTiles option can be added later for riders who want offline maps, without touching anything but the module registration.

#### OpenStreetMap on the web — and the words "to begin with"

MapLibre with OSM raster tiles is free, needs nothing hosted, and is the right way to get the web app onto a map this week. Two obligations come with it:

- **OSM's tile usage policy is a real constraint, not a formality.** `tile.openstreetmap.org` is a donated service: it forbids bulk downloading and heavy or commercial use, and it requires an identifying `User-Agent`. It is appropriate for development and a handful of friends; it is **not** appropriate for a public launch, and continuing to lean on it at scale would be taking something that was given for a different purpose.
- **Attribution is mandatory and permanent** — "© OpenStreetMap contributors" under ODbL on the web, and Apple's required attribution on the phone. Both live in the map component so they cannot be forgotten per screen.

So *"to begin with"* is load-bearing, and it is written into the plan rather than left as an intention: **before the web app is publicly announced, the tile source moves** to self-hosted PMTiles (§9.1) or a paid tier. Recorded as §13 Q26.

#### One thing this gives back

v0.16 pulled tile hosting into Phase 1 and put a multi-gigabyte extract on a 40 GB VPS disk (§9.1). **Both of those go away again**: Apple hosts the phone's tiles, OSM hosts the web's, and the disk pressure from §9.1 eases considerably. The v0.16 "bill" is deferred rather than paid — with the note above about when it comes due.

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

**[Mapsui](https://mapsui.com/) remains, scoped to the car.** It is the only renderer that can draw into an Android Auto `Surface` or a CarPlay window (§4.6), which was true in v0.9 and is unaffected by anything since — head units have no browser, and MapKit JS is a browser SDK. It keeps `IMapRenderer` alive as a contract with exactly one implementation and two hosts.

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

```
Draft ──publish──► Open ──start──► Live ──end──► Completed ──30d──► Archived
                     │                             │
                     └────────── Cancelled ────────┘
```

- **Open:** joinable, route visible, no live positions. The thread is already active — this is where the planning polls happen (§17.1).
- **Live:** members publish positions; server fans out to the ride group only. **Thread notifications go quiet** except for pinned posts (§17.6).
- **Completed:** the organiser chooses between **stopping sharing for everyone immediately** (the default — channel closes, every position row deleted) and a **capped wind-down** in which riders stop themselves (§5.6). Each member's recorded track is offered for attachment to the ride summary. Markers and the thread are kept — they were authored, not measured (§16.1).
- **Archived** (30 days later): the thread becomes **read-only** (§17.6). Until v0.14 this state existed without meaning anything.

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
- Pending requests notify the organiser by push; decisions notify the rider.
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
	Task RideStateChanged(RideState state);
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
	Task SharingWindDownStarted(DateTime endsUtc);        // §5.6
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
| `SharingWindDownService` | 60 s | Force-stops expired post-ride sharing (§5.6) | `Ride:WindDownSweepSeconds` — the *window length* is the separate `Ride:MaxWindDownMinutes` |

**Collaborators** (all in `DLR.Server/Positions/`):

| Type | Responsibility |
|---|---|
| `RiderPositionCache` | `ConcurrentDictionary<Guid rideId, ConcurrentDictionary<Guid userId, PositionEntry>>`. `Upsert` rejects an older `RecordedUtc` and sets `IsDirty`. Exposes `ReadyAsync()`. |
| `PositionFlushService` | `BackgroundService` on `PeriodicTimer(FlushSeconds, TimeProvider)`. Drains dirty entries → one upsert. Also flushes on `StopAsync`. |
| `PositionCacheRehydrator` | Loads positions for Live rides — **and rides inside an unexpired wind-down (§5.6)** — into the cache exactly once at startup, gated by `Lazy<Task>`. |
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

**Rehydration rules** — all four matter; each one omitted is a defect:

1. **Live rides only** — plus rides inside an **unexpired wind-down** (§5.6), which are `Completed` but still legitimately sharing. A restart during a wind-down must not blank the map for the riders it exists to protect, and must not resurrect one that has expired.
2. **Freshness gate:** only rows with `recorded_utc > now - Ride:StalenessMinutes` (default 15). A stale point must not reappear on the map as if it were current.
3. **Loaded entries are marked clean.** Otherwise startup immediately schedules a pointless write of everything it just read.
4. **Reads await `ReadyAsync()`.** Hub reads and `GET /positions` block until rehydration completes, so no client can observe a half-warm cache. The gate lives *inside the cache* rather than relying on hosted-service ordering, because Kestrel's `GenericWebHostService` can start serving before custom hosted services have run. The rehydrator also kicks the task off eagerly so the cache is warm before the first request arrives.

**Lifecycle and cleanup**
- Ride → `Completed` with the default ending: delete that ride's rows and evict the ride from the cache. Ride → `Cancelled`: the same, with no choice offered.
- Ride → `Completed` **with a wind-down** (§5.6): rows survive until each member stops, or until `SharingEndsUtc`, whichever comes first. A `SharingWindDownService` on the same `PeriodicTimer`/`TimeProvider` pattern as the flush sweeps expired windows and deletes unconditionally — **the expiry must not depend on a client being awake to honour it.**
- Member sets `ShareLocation = false`, or leaves, or is removed by the organiser: delete that member's row immediately — stopping the broadcast is not sufficient (§10.1).
- Ride deletion is covered by `ON DELETE CASCADE`.
- Nightly sweep for rows belonging to rides that are neither `Live` nor inside an unexpired wind-down, as a backstop against a missed transition (§7.11).

**Cost of the trade-off, stated plainly:** a hard process kill loses up to 10 s of movement. On restart the cache rehydrates slightly stale and self-corrects on each rider's next 5 s push, so the worst observable symptom is a pin that lags for a few seconds. A graceful shutdown loses nothing. At 500 concurrent riders the flush is ~50 rows/s in a single statement — negligible on the €4 VPS (§9).

### 5.6 Consent to share, and what happens when the ride ends

**Joining a ride and agreeing to broadcast your position are two separate decisions**, and the app treats them that way.

#### At join

Both join paths (§5.2) end at the same prompt, before the rider is in:

> **Share your location with this ride?**
> Members of *Saturday Coast Run* will see where you are while the ride is live. You can turn this off at any time. It stops when the ride ends — or, if the organiser lets riders finish getting home, within two hours of that.
>
> **[ Share ]  [ Not now ]**

*(The second sentence is not padding. Through v0.14 this copy said flatly "it stops when the ride ends", which the wind-down below made untrue — and consent copy that overstates the protection is worse than none.)*

- **Dismissing is "not now", and the flag defaults to `false`.** A prompt that treats a swipe-away as consent is not a consent prompt. This matches §7.3's structural default-off for profile fields, for the same reason: an accidental "on" cannot be un-shared.
- The choice is **per ride**, stored on `GroupRideMember.ShareLocation`. A rider who shares with their regular Sunday group and not with a charity ride full of strangers is expressing something sensible, and one global switch could not express it.
- Turning it on later is one tap from the ride screen, and the ride screen makes the current state obvious rather than burying it in settings.

#### A rider may be in a ride without sharing

This is allowed deliberately, and it is worth defending because the alternative is tempting: making sharing the price of seeing the map would be simpler, and it would be coercive. Someone joining a big organised ride to follow the route, a pillion, an organiser driving a support van — all have reason to watch without broadcasting.

The control is **visibility, not enforcement**: the member list shows each rider's state — *sharing*, *not sharing*, *no signal* — so a group that cares can see the asymmetry and say something. That is a social problem with a social fix, and the app's job is to make the fact legible rather than to compel.

**"No signal" and "not sharing" must be distinguishable in the UI.** They mean completely different things to somebody waiting at a junction, and collapsing them into one grey pin is the kind of small ambiguity that gets someone left behind.

#### Turning it off, at any time

Unchanged from §5.5 and worth restating because it is the load-bearing part: setting `ShareLocation = false` **deletes the persisted row immediately** and evicts the cache entry. Stopping the broadcast alone would leave a last-known position at rest in the database — precisely what a rider turning sharing off is asking you not to do. Leaving the ride and being removed by the organiser do the same thing.

#### The end of the ride is a choice, not an event

The naive rule — *ride ends, all sharing stops, all positions deleted* — is what §5.5 through v0.14 described, and it has a real failure mode: an organiser who ends the ride at the pub blanks the map while three riders are still an hour from home in the dark.

So **ending a ride asks the organiser one question**:

| Choice | Effect | Default |
|---|---|---|
| **Stop sharing for everyone** | Live channel closes, every position row deleted, immediately | ✅ |
| **Let riders stop themselves** | A bounded **wind-down**: members who were sharing keep sharing until they turn it off, or until the window expires | |

During a wind-down the ride is `Completed` — the thread, markers and summary all behave as §5.1 says — but the live map stays readable **by members**, which is the actual use: the organiser at home wants to see that everyone else got home too.

**Four rules make the wind-down safe rather than a loophole:**

1. **It is capped.** `Ride:MaxWindDownMinutes`, default **120**. At the deadline the server force-stops sharing for everyone still on, deletes every position row, and closes the channel. This is server-side and unconditional — it does not depend on any client being awake to honour it.
2. **It cannot be extended.** No renewal, no "add another hour". A window that can be extended is an indefinite window with extra steps, and indefinite is precisely what §1 promises this app never does.
3. **Every rider still sharing is told, persistently.** The recording notification (§4.3) — or a standalone one if they are not recording — reads *"Still sharing your location with Saturday Coast Run — stops at 16:40"*, with a one-tap stop. Nobody should discover this by opening the app.
4. **A rider can stop at any point**, which deletes their row exactly as in the live case, and the organiser can end the wind-down early for everyone.

**The organiser cannot switch a rider's sharing back on.** They can end sharing for everybody, and they can grant the wind-down; they can never grant consent on someone's behalf. That asymmetry is the whole point — the organiser controls the *ride*, the rider controls their *location*.

#### Which means §1's headline claim needed correcting

Through v0.14 the product summary said live sharing *"is scoped to the group ride and ends with it"*. With a wind-down that is no longer exactly true, and this document has a rule about that (§10.1, and the v0.2 correction that established it). The accurate claim, now used in both places:

> Live sharing is scoped to the group ride. It ends when the ride ends, or — if the organiser chooses and you keep it on — within at most two hours afterwards. It is never open-ended, and you can stop it at any moment.

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

**Bounded**, because unbounded means a rider can be broadcast into fifty groups at once: `Ride:MaxConcurrentLiveRidesPerUser`, default **5**, enforced when a ride goes Live rather than at join. Being a *member* of many rides is fine; being live in many at once is what costs.

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

RideEnd_DefaultChoice_DeletesAllPositionsImmediately
RideEnd_WindDown_KeepsSharingMembersPublishing
RideEnd_WindDown_ExpiresServerSideWithoutAnyClient
RideEnd_WindDown_CannotBeExtended
RideEnd_WindDown_OrganiserCanEndItEarlyForEveryone
RideEnd_WindDown_RiderStoppingDeletesOnlyTheirRow
Rehydrate_RideInUnexpiredWindDown_IsLoaded
Rehydrate_RideInExpiredWindDown_IsNotLoaded
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
| Map | **MapLibre GL JS + OpenStreetMap tiles** via a small JS interop wrapper (§4.5) | Free and hosted by someone else, which is the right trade for getting onto a map early. *"To begin with"* is doing real work in that sentence — OSM's usage policy does not cover a public launch (§13 Q26). The phone runs the same **component** but a different module: Apple Maps (v0.19) |
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
POST   /api/v1/group-rides/{id}/state          start / end / cancel  (owner only)
                                               end takes { endSharing: Immediate|WindDown } (§5.6)
POST   /api/v1/group-rides/{id}/end-sharing    owner: stop everyone now, incl. mid-wind-down
PUT    /api/v1/group-rides/{id}/permissions    owner/leader: { markers, comments, photos } (§5.8)
GET    /api/v1/group-rides/{id}/positions      snapshot (awaits cache ready)
PUT    /api/v1/group-rides/{id}/route          attach/replace planned route
PUT    /api/v1/group-rides/{id}/sharing        { shareLocation } — false deletes the row
GET    /api/v1/me/export                       full data export
DELETE /api/v1/me                              account + data deletion
GET    /api/v1/maps/token                      authed → short-lived MapKit JS token (§4.5)
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
	→ policy: >= 10 chars, no composition rules, breached-password check
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

**Password policy.** Minimum 10 characters, no composition rules (they push people toward `Passw0rd!`), and a **breached-password check** against the Pwned Passwords range API — k-anonymity, free, no API key, only the first five SHA-1 hex characters ever leave the server. If that service is unreachable, **registration proceeds**: a third-party outage must not stop signups. Identity's default 6-character minimum is raised explicitly. Password strength matters more here than in most apps, because for an email-less account it is the *only* credential and there is no reset path.

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

**Sharing is ride-scoped and revokes itself.** A shared field is visible to riders who are **currently co-members of a group ride** with the owner, surfacing in that ride's member list. Leaving the ride, being removed by the organiser, or the ride completing all end access — the same lifecycle as live position sharing (§5.5), for the same reason. **One deliberate difference since v0.15: profile sharing ends the moment the ride is `Completed`, and does not follow the position wind-down (§5.6).** The wind-down exists so people can watch each other get home safely; there is no equivalent reason to keep a phone number visible for two more hours. A rider who has never joined a ride has no audience at all, whatever their switches say.

**The phone number is not verified and is not a recovery path.** SMS verification needs a paid provider the €4 budget (§9) does not want, and an SMS reset path would add an account-takeover surface for no benefit. Identity's `PhoneNumber` column is reused, but **`PhoneNumberConfirmed` stays permanently `false` and must never be used as a gate** — a future contributor who sees that column will otherwise assume verification happened somewhere. The field is a convenience for mates on a ride, nothing more; tapping to call a rider mid-ride is the obvious use (§5.4).

**Sharing an email exposes the account's recovery address.** Worth one line of UI copy, because a rider who shares it is telling a ride full of people which mailbox to attack in order to reset the password. Not a reason to forbid it — a reason to say so plainly next to the switch.

#### Default-off has to be structural, not conventional

Three booleans defaulting to false are trivial to get right at creation and easy to get wrong on a read path. One forgetful DTO mapper leaks a phone number, and there is no way to un-leak it. So the defence is structural:

- **No endpoint ever projects the user entity.** Shared fields reach the wire through exactly one factory, which cannot be called without stating the viewer's relationship to the owner:

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

### 7.7 Password reset and recovery

Reset requires a **confirmed email**. An account without one cannot be recovered — that is the trade-off §7.2 surfaces at registration.

- `POST /auth/forgot-password` takes an email address and **always** returns `202`, whether or not it exists (§7.8).
- Identity's `GeneratePasswordResetTokenAsync`, lifetime **1 hour**.
- Links are `https://` universal links with a web fallback page, so reset works whether or not the app is installed — the reason §6.1 needs auth landing pages.
- **On successful reset: update the security stamp and revoke every refresh-token family.** Every device signs in again. This is the one place permanent sessions are deliberately broken, because if the reset was triggered by a compromise, leaving other sessions alive defeats the point.
- `change-password` (authed, requires the current password) revokes *other* families but keeps the current device signed in.
- Adding an email later (`POST /auth/email`) sends a 24 h confirmation link and, once confirmed, enables recovery from that point on.

#### Two lifespans need two token providers

Email confirmation is valid **24 hours**; password reset **1 hour**. These cannot both come from configuration, because **`DataProtectionTokenProviderOptions.TokenLifespan` is global** — it governs *every* `DataProtectorTokenProvider` at once (confirm email, reset password, change email). Setting it to one hour for reset silently drops confirmation to one hour too, and nothing warns you. Identity's default happens to be 1 day, so 24-hour confirmation is the default; the shorter reset window is what needs its own provider:

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

**Conventional rate limits** still apply on top, via the in-memory limiter:

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

- **Warned at 150 days** by email, if a confirmed address is known. Without one there is no way to warn — the same gap as password recovery, and a further reason the §7.2 notice matters.
- Hard delete. `ON DELETE CASCADE` clears devices, refresh tokens and any residual rows; there is nothing worth soft-deleting given the criteria.
- **Batched** — at most `Maintenance:MaxDeletesPerRun` (default 500) per night, so one run can never take a long lock.
- Username is released back to the pool on deletion. This does not undermine the permanence rule in §7.2, and the deletion criteria are what make it safe: an eligible account has **never joined a ride**, so it never appeared on anyone's map and no rider can have formed an association with that name. There is no reputation to inherit and nothing to impersonate. Names that were ever visible to another rider belong to accounts that are never auto-deleted.

**The same nightly job carries three other sweeps**, all small and all destructive in their own way: nulling `created_by_ip` after 30 days (§7.8), deleting position rows for rides that are no longer Live (§5.5), and **purging expired `TrackRevision` originals past their undo window** (§15.6). They share the job because they share the requirement below — a destructive timer nobody watches.

**A destructive automated job needs brakes.** Two non-negotiables:

- **`Maintenance:DryRun` defaults to `true`.** It logs exactly which accounts *would* go. Run it that way for at least a week and read the output before enabling deletion for real.
- **A kill switch** (`Maintenance:DeleteInactiveAccounts`) that disables the sweep without a redeploy.

**Client handling.** When an account has been deleted, the device's next refresh fails. The response carries a distinguishable reason so the app can say *"This account was removed after 180 days without use"* and offer to create a new one — not a generic sign-in error, which would look like a bug and be indistinguishable from a bad password.

**One nightly service**, not four. `NightlyMaintenanceService` consolidates:

| Sweep | Reference |
|---|---|
| Orphaned `rider_position` rows for rides that are neither Live nor in an unexpired wind-down | §5.5, §5.6 |
| `refresh_token` rows expired or revoked > 30 days | §7.13 |
| Null `created_by_ip` on users older than 30 days | §7.8 |
| `TrackRevision` originals past their undo window | §15.6 |
| Orphaned photo blobs in object storage — `ON DELETE CASCADE` does not reach it | §16.6 |
| Resolved `ContentReport` rows and their content snapshots past retention | §17.7 |
| Warn at 150 days, delete empty accounts at 180 | this section |

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
# Email:Host       smtp.zoho.com.au    →  smtp.zeptomail.com
# Email:Port       587
# Email:User       no-reply@example    →  emailapikey
# Email:Password   <app password>      →  <ZeptoMail send-mail token>
# Email:From       no-reply@example       (unchanged)
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
--   last_active_utc    timestamptz  NOT NULL   -- §7.10
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
```

The raw refresh token is never stored — only its SHA-256. `successor_id` is what makes the idempotent replay window in §7.4 possible.

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
                                                    shareEmail }
```

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
Username_CannotBeChangedByAnyEndpoint
Register_CanReuseUsernameOfDeletedAccount
Register_DuplicateEmail_ReturnsGenericSuccessAndNotifiesOwner
Register_NullEmails_DoNotCollideOnUniqueIndex
Register_WeakOrBreachedPassword_IsRejected
Register_BreachServiceUnavailable_StillAllowsRegistration
Register_FourthAccountFromSameIpInOneDay_RequiresEmail
Register_FourthAccountFromDifferentIp_DoesNotRequireEmail
Register_LadderCountSurvivesProcessRestart
Register_LadderUsesForwardedClientIp
Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide
Restricted_AfterConfirming_CanJoinRide
Login_UnknownUsername_ResponseTimingMatchesKnownUsername
Login_FiveFailures_LocksAccountForFifteenMinutes
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
Device(Id, UserId, Platform, PushToken, AppVersion, LastSeenUtc)

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

GroupRide(Id, OwnerId, Name, Description, StartUtc, EndUtc?, State,
          JoinCode, JoinPolicy{Open,Approval}, MemberCap,
          PlannedRouteTrackId?, MeetPointLat/Lon, CreatedUtc,
          SharingEndsUtc?,                                               -- §5.6
          AllowMemberMarkers, AllowMemberComments, AllowMemberPhotos)    -- §5.8
          -- SharingEndsUtc non-null == an unexpired wind-down. The server
          --   force-stops at that instant; it is never extended (§5.6).
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
- Push notifications: FCM (free) for Android and, via FCM's APNs bridge, iOS — avoids running a second push service.
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

When it does change — before the web app is publicly announced, because OSM's usage policy does not cover that (§13 Q26) — the answer is already designed: self-hosted **PMTiles, served straight off the VPS by Caddy.** PMTiles is a single file read over HTTP range requests, which Caddy's `file_server` handles natively, so the usual "PMTiles needs object storage plus a Worker" setup is not required here. A regional extract is the practical unit: a few GB for Australia, versus ~100 GB for the planet. That is also the route to an offline map pack, which MapKit JS cannot provide (§4.5).

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

Live location is shared **only** within an active group ride, **only** with its admitted members, **only if the rider said yes when they joined** (§5.6), and **stops when the ride ends — or within at most two hours afterwards if the organiser grants a wind-down and the rider leaves it on**.

*The wind-down clause is new in v0.15 and the sentence above was corrected to include it. It is the same discipline as the v0.2 correction below: a privacy statement that describes an earlier version of the code is worse than no statement, because people rely on it.*

**What is stored, precisely** *(corrected in v0.2 — v0.1 claimed positions were never persisted, which the 10 s flush makes false)*:

> Exactly **one row per rider per active ride**, overwritten in place. **No location history is ever stored** — there is no positions table to accumulate, no trail, no replay. Rows are **deleted when the ride completes or is cancelled** — or, where the organiser granted a wind-down, when each rider stops or the capped window expires, whichever comes first (§5.6). Recorded tracks are a separate, opt-in artefact.

**Measured location is deleted; authored content is kept.** Markers (§16) are locations too, and the ride thread (§17) is a record of who said what to whom — both survive the ride precisely because a person chose to write them. The distinction is worth naming so the promise above stays exact: the app deletes what it *observed* about where you were, and keeps what you *wrote down* about where something is. A marker is visible to whoever its parent is visible to — a track's audience, or a ride's admitted members — and no wider.

**Who can see a rider's live position** *(rewritten in v0.5 — the confirmed-email gate is gone)*:

> Only members of a ride the **organiser** admitted them to. There are two ways in and the organiser controls both: they handed out the join code, or they pressed *Admit* on a request (§5.2).

This is a stronger statement than v0.4's email gate. Confirming an email only ever proved that somebody could read a mailbox; it said nothing about whether the organiser wanted them on the ride. The membership check in the hub (§7.6) is what enforces it, which is why that check is tested directly rather than assumed.

- **Consent is asked at join and defaults to off** (§5.6). A rider can be in a ride without sharing, and the member list shows who is and is not — visible rather than enforced.
- Per-ride `ShareLocation` toggle. Setting it false, or leaving the ride, **deletes the persisted row** — merely stopping the broadcast would leave a last-known point at rest in the database, which is exactly what a user turning sharing off is asking you not to do.
- **The wind-down is capped, unextendable, force-stopped server-side, and announced by a persistent notification** on every phone still sharing (§5.6). An organiser may end sharing for everyone and may offer the window; they can never switch a rider's sharing back on.
- An organiser can remove a member mid-ride; their position row is deleted immediately.
- **Several concurrent rides mean several independent consents** (§5.7), and a rider sharing with one ride and not another has no stored position in the second at all — the filter is applied on the write, not on the read.
- Revoking a device (§7.10) cuts that device's ability to read positions — its next refresh fails.
- **Minimal collection by default:** a working account is a username and a password hash. Email, phone number and display name are all optional, and all three are **shared with nobody unless the user switches them on** (§7.3).
- **Shared profile fields are ride-scoped**, visible only to current co-members of a group ride, and access ends the moment the ride completes — **without** the position wind-down's grace period, deliberately (§7.3). There is no profile lookup endpoint and no way to resolve a username to a person's details.
- Registration IP is kept 30 days for abuse throttling, then nulled (§7.8).
- Dormant empty accounts are deleted after 180 days (§7.11) — data minimisation by construction.
- Public share links are unguessable tokens, revocable, optionally expiring.
- Optional "hide start/end" radius on shared tracks — don't publish home addresses. This is a **display** rule: the points are still stored, and they are still in the owner's own export and GPX.
- **Trimming a track in the web editor is the destructive counterpart** (§15.5): the removed points are gone from the stored track, from exports, and from every share link. Two caveats are stated in the UI rather than buried here, because a user trimming their house off a ride is making a privacy decision and deserves the truth about it:
  - the pre-edit original is retained for a **7-day undo window** unless the user chooses *remove the original now* (§15.6);
  - **nightly backups (§9) still contain it** until they roll out of retention. Deleting from the live database is not deleting from a backup, and saying otherwise would be the same class of error as v0.1's "positions are never persisted".
- **Photo metadata is destroyed on upload, not on display** (§16.4). Every image is re-encoded server-side with no metadata written, because an EXIF GPS tag in a photo taken at home would reinstate the exact address the trim above just removed — in a file handed to every member of the ride. The two features are one decision, and getting one right without the other is worth nothing.
- Full export and hard delete endpoints. Deleting an account cascades refresh tokens, devices, join requests and positions.
- Applicable law: **Australian Privacy Act / APPs**, and **GDPR** if EU users.

### 10.2 Store compliance (plan for it — it bites late)
- **Google Play:** background-location declaration plus prominent in-app disclosure *before* the permission prompt; a demo video is typically required. The **Data Safety form must declare that location is stored, not merely transmitted** (§5.5). It must also now declare **name, email address and phone number as collected-but-optional**, and — because §7.3 lets riders show them to each other — that some personal information is **visible to other users**, which is a distinct disclosure from sharing with third parties (which the app does not do). Note that Play and Apple both ask whether optional data is *required* to use the app; here the honest answer is no, which is worth stating precisely rather than approximately.
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
RideCompleted_WithImmediateEnding_DeletesPositionsAndEvictsCache
MemberStopsSharing_DeletesPersistedRow
```

**Sharing consent, the ride-end wind-down, multi-ride publishing and the organiser's content switches** have their own list in §5.9. The wind-down expiry test is the one that matters most — `RideEnd_WindDown_ExpiresServerSideWithoutAnyClient` is what stops a bounded window becoming an unbounded one the first time a phone goes flat.

**Identity, joining and account lifecycle** have their own list in §7.15 — it is the largest single block of tests in the project, which is appropriate given that the membership check is now the only thing protecting a rider's location.

**Ride comments, reactions and polls** have their own list in §17.10. `Notify_OrdinaryCommentDuringLiveRide_SendsNoPush` is the one to write first: it is the safety decision of §17.1 expressed as an assertion, and it is the kind of rule that erodes silently the first time someone "improves engagement".

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
| **0 — Spikes** (1–2 wk) | `Replay_KnownGpx_ProducesExpectedDistanceAndAscent` | GPX replay harness; background GPS on both platforms; **`DLR.UI` skeleton rendering in both a `BlazorWebView` and WASM (§18)**; **MapKit JS in the WebView on both phones, 20 pins updating every 5 s — measure battery, and settle whether Apple's terms permit the Android case (§4.5)**; SignalR through Caddy; **verify an `androidx.car.app` .NET binding exists** (§4.6) | A 2-hour ride recorded with the screen off on a real iPhone **and** a real Android, no gaps — plus written answers on the Android Auto binding **and MapKit JS on Android**, and a battery number for the WebView map against §10.3's 8 %/hour |
| **1 — Solo** | `Register_UsernameAndPasswordOnly_Succeeds` | Username/password registration, permanent refresh tokens, IP ladder, optional email + confirm/reset, `last_active_utc`. Record, store, list, view, GPX export. Track upload. **GPX import on app and web, with the full hostile-input corpus (§15.3)**. **`DLR.UI` shared components in both hosts; the map in both modules — MapKit JS on the phones with its token endpoint, MapLibre + OSM on the web (§4.5)**. Web track view. **`LICENSE` + `/api/v1/about` + footer source link, and the CI licence gate** (§14.6) | Install on your own phone and stop using anything else — including a reinstall that signs straight back in without typing a password |
| **2 — Group rides** | `JoinByCode_ApprovalRide_CreatesPendingRequestOnly` | Both join paths + admit/decline, **join-time sharing consent, per-ride toggle and the ride-end wind-down (§5.6)**, **multi-ride membership and publishing (§5.7)**, **organiser content switches (§5.8)**, planned route, live map, member list, batched fan-out, position cache + 10 s flush (§5.5), hub membership authz. **Web track editor + undo window (§15.5–15.6)**. **Markers with photos (§16)**, rendering fully — MapLibre draws icons, rotation and labels from Phase 1, so v0.13's degraded-pin fallback never has to ship (§18.3). **Ride thread: text, photos, pinning, reactions, and the Live-ride notification rules (§17.1, §17.6)** | 4 people, 1 real ride, all pins moving; one joined by code, one admitted from a request; kill and restart the server mid-ride and watch the map come back warm. **One rider joins without sharing and stays invisible on the map while still seeing everyone; end the ride with a wind-down and watch it expire on its own with every phone switched off.** **Trim your own house off a real recorded ride, watch the distance change, undo it, then purge the original.** **Drop a hazard marker with a photo mid-ride and have it appear on three other phones; confirm the stored image carries no EXIF GPS** |
| **3 — Polish + car** | `Snapshot_GapList_OrdersRidersAlongRoute` | `IRideSessionState` + gap list, **Mapsui renderer** (on the critical path for both the car *and* markers, §16.3), **full marker rendering — icons, rotation, labels**, **Android Auto + CarPlay heads (§4.6)**, inactivity cleanup behind dry-run, push notifications, **polls (§17.5)**, **report/block moderation (§17.7)**, off-route alerts, ride summaries, load test, **social sign-in + guest riders (§7.16)** | Store submission; a real ride navigated from a head unit; a week of dry-run deletion logs read |
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
| **A wind-down becomes indefinite sharing** | **High** | The whole risk of §5.6, and the reason the window is capped, unextendable, and expired by a server-side sweep that needs no client to cooperate. A persistent notification names the stop time on every phone still sharing, and `RideEnd_WindDown_ExpiresServerSideWithoutAnyClient` is the test that keeps it honest. A flat battery must not leave someone broadcasting |
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
| Shared phone number becomes a harassment vector | Medium | Off by default, ride-scoped, and revoked when the ride ends. The organiser controls who is in the ride at all (§5.2), so the audience is never strangers-at-large. Unverified by design, so it is also never an identity claim |
| **Apple refuses the CarPlay entitlement** | **High** | Request filed in Phase 1 so the wait overlaps other work (§4.6). No engineering mitigation exists — if refused, iOS ships phone-only and the `carplay-driving-task` entitlement is the fallback to investigate. Do not promise CarPlay in store copy until it is granted |
| **No usable .NET binding for `androidx.car.app`** | **High** | Phase 0 spike answers this before any planning depends on it (§4.6). Fallback is a binding project over the AAR — real work, and the reason it is a spike rather than an assumption |
| Mapsui is now on the critical path, not optional | Medium | Car support cannot use the native map control at all (§4.6). `IMapRenderer` was designed for exactly this swap, and `MapHostKind` makes an unsupported pairing a startup failure rather than a blank screen |
| Auto/CarPlay review rejection on distraction grounds | Medium | Templates enforce most rules mechanically; two-screen depth, capped list rows and single-tap actions are design constraints from the start (§4.6), not late fixes. DHU and CarPlay Simulator before hardware, one real head unit before submission |
| ~~Built-in map's pin limits force an early Mapsui swap~~ | — | **Retired in v0.16.** There is no built-in map in the design any more (§18.3) |
| **A JS map in a WebView misses the battery or frame-rate target** | **High** | The genuine unknown since v0.16, sharpened by v0.19: the module on Android is now **MapKit JS in an Android WebView**, further off Apple's tested path than MapLibre was. **Phase 0 spike** with a number attached (§10.3's 8 %/hour). Fallback is MapLibre on Android — the web's module, already written (§4.5) |
| **MapKit JS is not licensed, or not usable, in an Android WebView** | **High** | Nothing in the SDK is OS-locked, but "runs" and "is licensed to run" are different questions and the Android build would depend on the answer. **Phase 0, in writing**, exactly as §4.6 treats the `androidx.car.app` binding. Fallback is MapLibre on Android, which costs consistency between the two phones and nothing else (§4.5) |
| **Shipping Android depends on an Apple Developer account** | Medium | An unusual coupling worth keeping visible: if Apple changes MapKit JS terms or pricing, the map disappears from *Android* as well as iOS. The account is already required for iOS and CarPlay (§4.6), so this adds no new cost — only a new failure mode, mitigated by the MapLibre module already existing |
| **Offline maps are lost, not deferred** | Medium | MapKit JS has no offline mode (§4.5). Recording, markers and the thread all work in a dead zone; the map behind them does not. Accepted deliberately for v1, with `MapProvider`'s `Offline` option and the PMTiles work (§9.1) as the route back if riders complain |
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
| **A ride thread encourages phone use while riding** | **High** | The product risk in this feature, and it is not solvable by a warning dialog. Structural instead: ordinary comments raise no notification while the ride is `Live`, only a pinned organiser post breaks through, the thread never renders on a car head unit, and `Notify_OrdinaryCommentDuringLiveRide_SendsNoPush` is a test rather than a convention (§17.1, §17.6). The pressure to relax this will come from engagement, and the answer is no |
| Notification storms from an active thread | Medium | Coalesced reactions (§17.4), no push per reaction or vote at all, per-ride mute, and the `Live`-state silence above. Twelve riders on a wet Sunday generate a lot of chat |
| Moderation load once the app is public | Medium | Report-and-block with a content snapshot (§17.7), organiser deletion inside their own ride, and audiences bounded by organiser consent (§5.2) so no comment ever reaches strangers-at-large. Proactive scanning is deliberately out of scope and recorded as §13 Q17 |
| Thread storage grows without bound | Low–Med | Caps per ride, `Archived` making threads read-only (§17.6), and photos already quota'd (§16.4). Text is cheap; the photos attached to it are not |
| **UGC rules bite at store review** | Medium | Photos and notes visible to other riders make this a UGC app: Apple and Play require reporting, blocking, and a response commitment (§10.2, §16.5). Cheap to build with the feature, a whole review cycle to add afterwards |
| Photo storage outgrows the €4 budget | Medium | Photos are an order of magnitude larger than tracks, and they land on the same 40 GB disk as everything else (§9.1). Downscale to 2048 px, thumbnails for callouts, per-account quotas (§13 Q13), and Caddy caching the reads. Uploads go through the VPS deliberately (§16.4) — that cost is accepted to keep metadata stripping non-optional |
| ~~Marker icons cannot render on the Phase 1 native map~~ | — | **Retired in v0.16.** Both JS modules draw icons, rotation and labels from Phase 1 (§16.3), so v0.13's degradation path now applies only to the car renderer — and to the one MapKit JS caveat about persistent labels |
| Orphaned photo blobs after a delete | Low–Med | `ON DELETE CASCADE` does not reach object storage, so blob deletion is explicit and the nightly job sweeps orphans as a backstop (§16.6, §7.11). An orphan here is a privacy failure wearing a storage bill's clothes |
| **Trimmed points survive in backups** | Medium | Unavoidable and therefore disclosed rather than mitigated: the UI and privacy policy say the removed points leave the live database immediately (or after the undo window) but persist in nightly backups until retention rolls (§10.1, §15.6). Bounded backup retention is the only real control |
| A dependency arrives under a licence that cannot ship in an AGPL project | Low–Med | Allow-list plus a CI licence scan that also fails on *unknown* (§14.6.3). Cheap to fix at PR time, expensive to unpick after release |

---

## 13. Open Questions

1. **Primary audience** — motorcycles, bicycles, or 4WD? It changes accuracy profiles, ascent handling, and whether "speed" or "cadence" is the hero stat. *Worth settling before Phase 1.*
2. **Voice/audio in-ride?** Intercom-adjacent features are a much bigger project; explicitly in or out.
3. **Spectator links** — should a non-member watch a live ride via a link? Positions are persisted, so a spectator link is a standing read grant over stored location data. Sharper now that organiser consent is the whole access model: a spectator link is the one way into a ride's data that is *not* a per-person admission, so it needs its own expiry and revocation.
4. ~~**Anonymous use**~~ — **largely resolved (v0.5):** with email optional and username-only registration, an account is already nearly frictionless. `is_guest` remains in the schema for a device-bound password-less participant in Phase 3 (§7.16).
5. **Retention** — how long are completed rides, their recorded tracks and their threads kept? (Live positions: deleted at ride end or when the wind-down expires, §5.6. Empty dormant accounts: 180 days, §7.11. Threads go read-only at `Archived` but are never deleted, §17.6 — which makes this the one entity in the product with no retention answer at all.)
6. **New-device email alerts** — every new device, or only a new device in an unusual location? Every-time is simpler and safer; it also trains people to ignore them. Moot for accounts with no address.
7. ~~**Email provider**~~ — **resolved (v0.4):** Zoho Mail SMTP for Phase 0–1, ZeptoMail before real users (§7.12). Three setup facts to confirm, none of them design decisions: the Zoho **datacentre region** (it sets the SMTP host), whether the plan **includes SMTP access**, and which **`no-reply@` alias** to send as.
8. **Ride discovery for join requests** *(new in v0.5)* — path 2 in §5.2 needs the rider to reach the ride somehow. v0.5 assumes an organiser-shared link. Should there also be browsable nearby/public rides? That is a much larger surface: discovery plus request spam plus the privacy question of listing rides at all.
9. ~~**Username changes**~~ — **resolved (v0.7): never.** Usernames are immutable, with a confirmation step at registration as the only safeguard (§7.2). Deleted accounts release their name, which is safe because such an account was never in a ride (§7.11).
10. **Should the app auto-create an account on first launch** *(reshaped by v0.7)* — immutability largely answers this. A silently generated handle would be **permanent**, so the user would be stuck as `rider_8f21` forever, and prompting for a name instead is just registration by another route. The remaining variant worth considering is deferring account creation entirely: record locally with no account (§7.9 already supports this) and ask for a username only when the rider first needs the network — uploading, or joining a ride. That keeps first launch free of any signup without ever assigning a name the user did not choose.
11. ~~**Licence**~~ — **resolved (v0.11): AGPL-3.0-only**, plus an additional permission under GPL-3 §7 for app-store distribution and proprietary platform SDK linking (§14.6). Network copyleft is the only licence that reaches someone running a modified server, which is the only way this software is ever "used". Settled now precisely because inbound = outbound with no CLA (§14.6.4) means a relicence would later need every contributor's agreement.
12. **Rate limit on join-code submission** *(new in v0.10)* — §7.8 limits auth endpoints and join *requests* but never `POST /group-rides/join`. Publishing the code makes the omission legible, so it needs a limit and a decision on how strict (§14.5).
13. **Per-account storage quota** *(new in v0.12)* — §15.3 says imports must be bounded and makes the caps configuration, but the actual numbers are unset. What is a fair ceiling on tracks per account and megabytes per account, given a 40 GB VPS disk shared with Postgres and the tile extract (§9.1)? Cheap to set now, awkward to lower once people are over it.
14. **Editing beyond removal** *(new in v0.12)* — v1 removes points and nothing else: no splitting one track into two, no merging, no moving or inserting a point, no redrawing a span. Splitting is the most likely next ask (one file containing a whole weekend), and it is the only one that needs no new geometry — a split is two range removals over a copy. Moving or inserting points is a different feature: it makes the track a drawing rather than a record, which is a product decision, not a technical one.
15. **Should the app be able to edit too?** *(new in v0.12)* — deliberately web-only in v1 (§6.1). The domain code is in `DLR.Core` and therefore already available to MAUI, so this is a UI question — whether trimming is workable on a phone-sized map — not an architectural one.
16. **Marker visibility on a shared track** *(new in v0.13)* — a track's markers are visible to whoever the track is (§16.1). Should a marker be individually private, so a rider can annotate *"awful surface, don't come back"* on a ride they also share publicly? It is one `IsPrivate` flag, but it needs UI that makes the state obvious at a glance, and a wrong default here is a leak rather than an inconvenience.
17. **Does UGC need moderation beyond report-and-remove?** *(new in v0.13, widened in v0.14)* — §17.7 builds reporting and blocking because the stores require them. Proactive scanning (hash matching against known illegal material) is a different order of cost and commitment, and small organiser-admitted audiences make it hard to justify today. The honest answer changes if public ride discovery (Q8) ever ships.
18. **Comments on shared tracks** *(new in v0.14)* — §17.1 confines the thread to group rides, because commenting on a public share link would let people the organiser never admitted post into someone's space. Is a track-scoped thread wanted at all, and if so is it members-only, or open to anyone with the link plus a moderation story that does not exist today?
19. **@mentions** *(new in v0.14)* — a natural fit that v1 skips. Immutable usernames (§7.2) make it unusually cheap here: a mention can be stored as plain text and still resolve forever, with no rename propagation and no stale reference. The open part is notification behaviour, which collides head-on with §17.6's `Live` silence — a mention is exactly the "but this one is important" case that erodes a safety rule.
20. **Threaded replies** *(new in v0.14)* — v1 is a flat thread. Replies change deletion semantics (an orphaned reply chain), ordering, and the pagination contract, so it is a real feature rather than a field.
21. **Should the sharing toggle reach the car screen?** *(new in v0.15)* — §4.6 caps the head unit at one-tap actions and v1 leaves sharing off it, so stopping mid-ride means stopping the bike. Defensible for a privacy control that deserves a moment's thought, and arguably wrong if someone wants to drop out of a ride while moving. One template action either way.
22. **Wind-down default length** *(new in v0.15)* — 120 minutes is a guess that sounds right for a day ride. Too short strands the people it exists for; too long is tracking. Worth revisiting after one real season, and it is configuration (§14.5), so it moves without a release.
23. **Non-focused ride cadence** *(new in v0.15)* — §5.7 sends every live ride's batch at the full 5 s rate. Slowing the ones a rider is not looking at is the obvious saving and is deliberately not built; the question is whether anyone is ever in enough simultaneous live rides for it to matter.
24. **Does the web need offline at all?** *(new in v0.16)* — §18.6 makes the WASM client online-only. A service worker plus IndexedDB would give the planning surface some resilience, but it duplicates the sync engine that already exists for mobile (§4.4) in a second, weaker form. Probably not worth it — worth asking once rather than drifting into it.
25. **Native fallback for the live map** *(new in v0.16)* — if the Phase 0 WebView battery spike (§18.3) fails, does the live ride screen become a native MAUI page while everything else stays shared? That is the designed retreat, and it is worth knowing in advance which screens would follow it down.
26. **When does the web leave OSM tiles?** *(new in v0.19)* — "to begin with" needs a trigger, not a hope. `tile.openstreetmap.org` is donated infrastructure whose policy does not cover a public launch (§4.5), so the honest answer is *before the web app is announced to anyone outside the test group*, with self-hosted PMTiles (§9.1) as the destination. The open part is only whether that is worth doing earlier, since it also unlocks the offline map option.
27. **Do riders actually miss offline maps?** *(new in v0.19)* — MapKit JS cannot do them (§4.5), and the alternative costs a third module plus a few GB of tiles on the phone. Worth answering with real riders in a real dead zone rather than in advance; the answer decides whether `MapProvider.Offline` ever becomes selectable.
28. **Should CarPlay use native MapKit instead of Mapsui?** *(new in v0.19)* — now that Apple Maps is the phone's provider, a native `MKMapView` inside the `CPWindow` would be the natural pairing and would look better than a Skia-drawn map. It would also mean a *fourth* renderer, since Android Auto still needs Mapsui. Not worth it today; worth revisiting if CarPlay quality ever becomes the thing being judged.

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
| **FCM service-account JSON / APNs key** | Sends push notifications to your entire userbase |
| **Backblaze B2 credentials and the `restic` repository password** (§9.1) | The credentials read every backup; the password decrypts them. Store them apart — an encrypted backup whose key sits beside it is an unencrypted backup |
| **`google-services.json`, `GoogleService-Info.plist`** | Not strictly confidential, but they carry API keys that get scraped from public repos within hours. Commit templates instead |
| **MapKit JS private key** (`.p8`) plus its key ID and team ID (§4.5) | Signs the tokens that authorise every map view on both phone platforms. Same class as the APNs key: a leak means someone else's map usage billed to your Apple account, and the key is **not** shipped to clients — the server mints tokens (v0.19) |
| **Map tile API key**, if a paid tier ever replaces OSM (§4.5) | See the note below. *(Through v0.15 this row named the Google Maps Android key; v0.16 removed the native map; v0.19 removed the need for a tile key at all for now — OSM and Apple both authenticate differently.)* |
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

A 12-hour tour at 1 Hz is ~43 000 points, roughly 200 KB gzipped in that encoding — fine for a desktop browser, which is the only place the editor runs. MapLibre simplifies for *rendering* client-side; the indices the editor manipulates stay the server's indices throughout. One index space, no mapping layer, no class of bug.

**Validation** — all `400`, all with the offending range named:

- ranges ascending, disjoint, non-empty, within `[0, PointCount)`;
- at least **2 points** must survive, or it is not a line (deleting the whole thing is `DELETE /tracks/{id}`, which already exists);
- `version` must match, or `409` — two browser tabs editing the same track is the realistic case, and silently applying stale indices would cut the wrong span.

**An interior removal inserts a segment break.** The alternative — splicing the neighbours together — would draw a straight line across the gap and add its length to the distance, inventing a path the rider never took. So the removed span leaves a genuine discontinuity, distance and duration are summed within segments only, and `SegmentCount` increments. This is the same mechanism as a multi-`<trkseg>` import (§15.3), which is a good sign the concept is right rather than bolted on. For the common case — cutting a three-point GPS spike out of a suburban street — the resulting gap is a few metres and invisible at any sane zoom.

Trimming the start or the end creates no break: there is nothing on the outside to disconnect from.

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

Authored is the word that matters. Everything else this app puts on a map is *measured* — a recorded track, a live position — and measured data is governed by the rules in §10.1 that delete it when a ride ends. A marker is something a person deliberately placed and typed, so it lives as long as the thing it is attached to, and it is visible to whoever that thing is visible to. Two different lifecycles, and conflating them is how a "privacy-first" app quietly starts retaining locations.

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

**Two providers means the marker rendering is written twice**, once per JS module (§4.5). `MapCapabilities` is what keeps that from becoming two behaviours: the flags are declared per module and the component degrades against the flags, so a difference in what MapKit and MapLibre can draw shows up as a *declared* difference rather than a surprise on one platform.

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

- **Cap bytes *and* decoded pixels.** `Photos:MaxUploadBytes` (default 12 MB) bounds the transfer; `Photos:MaxDecodedPixels` (default 40 MP) bounds the decompression bomb — a 40 KB PNG can expand to hundreds of megabytes of bitmap, and a byte cap alone does not see it coming. Read the header, check the dimensions, and refuse *before* allocating.
- **Accept JPEG, PNG, HEIC, WebP by content sniffing**, never by extension or client-supplied content type.
- **Everything is re-encoded**, always, even when the original is already a conformant JPEG — see below.
- Failures return Problem Details, not an unhandled decoder exception.

#### Re-encoding is how metadata is stripped, and stripping is mandatory

**Photos carry GPS coordinates, timestamps, device serials and sometimes a thumbnail of the *unedited* original.** In this app specifically that is not a generic privacy nicety — it is a direct contradiction of a feature shipped one section ago:

> §15.6 lets a rider trim the first 400 m off a track so the ride does not start at their house. If they then attach a photo taken in their driveway, the EXIF GPS tag puts the house back — in a file handed to every member of the ride.

So: **decode, apply the EXIF orientation, re-encode to JPEG, write no metadata.** Re-encoding rather than running a metadata-stripping pass is deliberate — strippers work on the tags they know about, and the failure mode is silent. Applying orientation *before* discarding it matters too, or every portrait photo from an iPhone arrives sideways.

Downscale to `Photos:MaxDimension` (default 2048 px on the long edge) and generate a thumbnail (`Photos:ThumbDimension`, 320 px) for the map callout. Two objects per photo, both in object storage (§6.2), both counting against the account's storage quota (§13 Q13).

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

Any member, not just the organiser, because the useful marker is *"gravel across the whole corner at the 40 km mark"* and the person who found it is whoever hit it first. The organiser keeps control the same way they do everywhere else in §5.2 — they chose who is in the ride, they can delete any marker, they can clear all of them, and since v0.15 they can **switch member marker-adding off entirely** for that ride (§5.8). Markers may be added **before and after the ride as well as during it**; the only state that forbids it is `Archived`, which is read-only for the same reason the thread is (§5.1).

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

### 16.6 Realtime, lifecycle and the GPX round-trip

**Hub messages** join `IRideClient` (§5.3), sent to the ride group only:

```csharp
Task MarkerAdded(MarkerDto marker);
Task MarkerUpdated(MarkerDto marker);
Task MarkerRemoved(Guid markerId);
```

Markers are **not** part of the 5 s position batch. A batch is a continuous telemetry stream where dropping a tick is harmless; a marker is a discrete authored event where dropping it is data loss. They travel as their own messages, and the reconnect path fetches them from the ride snapshot alongside positions (`GET /group-rides/{id}` and `GET /group-rides/{id}/positions`), per §5.3's rule of re-fetching state rather than replaying history.

**Lifecycle** — and the contrast with positions is the point:

- A ride reaching `Completed` **deletes every position row** (§5.5) and **keeps every marker**. Positions are measured exhaust; markers are the record of what happened. They become part of the ride summary.
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

### 16.7 Schema and configuration

```
Marker(Id, TrackId?, GroupRideId?, CreatedByUserId, Lat, Lon, DirectionDeg?,
       Icon, Title, Note?, PhotoId?, CreatedUtc, UpdatedUtc)
       -- CHECK ((TrackId IS NULL) <> (GroupRideId IS NULL))            §16.1
       -- DirectionDeg NULL means "no direction", never north           §16.2
Photo(Id, OwnerId, BlobRef, ThumbBlobRef, WidthPx, HeightPx, ByteSize,
      ContentHash, CreatedUtc)
       -- Content is re-encoded and metadata-free by construction       §16.4
```

*(v0.14: `MarkerReport` was generalised into `ContentReport`, which covers markers and comments and snapshots the reported content — §17.7.)*

Indexes: `Marker(GroupRideId)`, `Marker(TrackId)`, `Photo(OwnerId)`, `ContentReport(ResolvedUtc)` partial on unresolved.

| Key | Default |
|---|---|
| `Markers:MaxPerTrack` / `MaxPerGroupRide` / `MaxPerMemberPerRide` | 200 / 500 / 50 |
| `Markers:TitleMaxChars` / `NoteMaxChars` | 40 / 500 |
| `Photos:MaxUploadBytes` | 12 MB |
| `Photos:MaxDecodedPixels` | 40 MP |
| `Photos:MaxDimension` / `ThumbDimension` | 2048 / 320 px |

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
RideCompleted_DeletesPositionsButKeepsMarkers
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

## 17. Ride Comments

### 17.1 The safety decision comes before the feature

A group ride gets one **thread**: text, photos, pinned posts, reactions and polls, visible to the ride's admitted members and nobody else.

Before any of the mechanics, the constraint that shapes them:

> **The people this notifies are operating vehicles.** A thread that buzzes a phone in someone's tank bag at 100 km/h is not a chat feature, it is a design that asks riders to look down. §4.6 already accepted this reasoning for the car screen; a notification is worse than a car screen, because the car screen at least sits at eye level and the platform enforces the rules.

That yields three rules that are not negotiable later (detailed in §17.6):

1. **While the ride is `Live`, ordinary comments do not push.** They arrive silently and are there when someone stops.
2. **A pinned post from the organiser is the one exception** — *"fuel at the servo in 8 km"* is exactly what a group needs mid-ride, and pinning is the deliberate act that says so.
3. **Comments never appear on a car screen.** Not truncated, not as a count badge, not at all.

**The thread spans the whole ride, not just the live window**, and that is where most of the value is: *before* (what time, which route, who's actually coming — the poll case), and *after* (photos and argument about who was slowest). During the ride, traffic should be near zero, and the design should make that the path of least resistance rather than something riders have to resist.

**Group rides only in v1.** A comment thread on a publicly shared *track* (§15) would let people the organiser never admitted post into someone's space, which discards the entire abuse model of §5.2 — organiser consent — and replaces it with a moderation problem. Adding a `TrackId` parent later is one migration and this project applies migrations on startup (§6.2); building the arc now on the chance it is wanted is speculative generality, and the last two sections earned their arcs by having a use for both sides on day one.

### 17.2 Comments, and what they carry

| Field | Rules |
|---|---|
| **Body** | Up to `Comments:MaxChars` (default 2000). **Plain text** — never rendered as HTML or Markdown, exactly as for marker notes (§16.2) |
| **Photo** | One, optional, and it is the **same `Photo` resource as §16.4** — same ingest, same re-encode, same EXIF destruction, same quotas. Nothing new to secure |
| **Author** | An admitted member. Their immutable username (§7.2) labels the post, and because it is immutable it can be denormalised into a cached thread with no invalidation |
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

Poll results freeze into the ride summary when the ride completes, alongside the markers (§16.6).

### 17.6 Notifications, pinning and lifecycle

**Pinned posts** are the ride's noticeboard. The organiser or a leader (`GroupRideMember.Role`) may pin up to `Comments:MaxPinned` (default 3); pinned posts render at the top of the thread regardless of age and are the only comments that survive the thread's pagination on first load.

**What pushes, and when** — the table that encodes §17.1:

| Ride state | Ordinary comment | Poll created | Pinned post |
|---|---|---|---|
| `Draft` / `Open` | Push | Push | Push |
| **`Live`** | **Silent** — badge only | **Silent** | **Push** |
| `Completed` | Push | — | Push |
| `Archived` | Thread is read-only | — | — |

Plus a per-ride **mute** toggle that overrides all of it, and the standard platform quiet hours. The `Live` row is the one that matters and it is deliberately the most restrictive: a group of twelve riders generates a lot of small talk, and the app should not be the reason somebody reads it at speed.

**Lifecycle**, following the authored-versus-measured line already drawn in §16.1:

- Ride `Completed` → **positions are deleted (§5.5); the thread is kept and stays open.** The best photos land after everyone gets home.
- Ride `Archived` (30 days after completion, §5.1) → thread becomes **read-only**. The existing lifecycle already had this state and nothing to say about it; this is what it means.
- A member who leaves, or is removed, **keeps their posts in the thread** — deleting half a conversation makes the other half nonsense — but loses all access to it. An organiser who removed someone for abuse can delete their posts explicitly.
- **Account deletion removes that account's comments, reactions and votes** (§10.1's hard delete is not negotiable). This leaves gaps in old threads. Accepted, and stated here so it is not discovered as a bug.
- Deleting the ride cascades everything, including photos out of object storage (§16.6).

### 17.7 Moderation, permissions and caps

The thread is now the largest user-generated-content surface in the product, so **`MarkerReport` (§16.5) is generalised** rather than joined by a second table:

```
ContentReport(Id, TargetKind{Marker,Comment}, TargetId, ReportedByUserId,
              Reason, ContentSnapshot, CreatedUtc, ResolvedUtc?)
```

`ContentSnapshot` is the point of the change: an organiser deleting an abusive comment must not also destroy the evidence for the report they just filed. The snapshot is purged with the resolved report by the nightly job (§7.11) after `Moderation:ReportRetentionDays`.

| Action | Who |
|---|---|
| Post | Any admitted member **while `AllowMemberComments` is on**; photos additionally need `AllowMemberPhotos` (§5.8) |
| React, vote | Any admitted member — **never gated by the content switches**, since neither carries free text or storage worth moderating |
| Edit own post (within the window) | Author |
| Delete a post | Author, **or** the organiser/leader |
| Pin / unpin | Organiser, leader |
| Create a poll | Any member; close it — author or organiser |
| Report | Any member |
| Block a user | Any member — hides that user's comments, reactions and markers from them, and prevents future co-membership (§16.5) |

Caps and limits, all configuration (§14.5):

| Limit | Default |
|---|---|
| Comments per ride | 2 000 |
| `POST /comments` | 30/hour per user per ride |
| Polls per ride | 20; 5/day per user |
| Reactions | 120/hour per user |
| Pinned per ride | 3 |
| Body / poll option length | 2 000 / 80 chars |

### 17.8 Realtime and API

Hub additions to `IRideClient` (§5.3), all scoped to the ride group:

```csharp
Task CommentPosted(CommentDto comment);
Task CommentEdited(CommentDto comment);
Task CommentRemoved(Guid commentId);
Task CommentPinChanged(Guid commentId, bool isPinned);
Task ReactionsUpdated(Guid commentId, ReactionCounts counts);   // coalesced, §17.4
Task PollUpdated(Guid commentId, PollResults results);          // coalesced
```

On reconnect the client **fetches the thread**, it does not replay — the same rule as positions, markers and everything else on this hub (§5.3).

```
GET    /api/v1/group-rides/{id}/comments        cursor-paginated, pinned first
POST   /api/v1/group-rides/{id}/comments        { body?, photoId?, poll? }
PATCH  /api/v1/comments/{id}                    author, within the edit window
DELETE /api/v1/comments/{id}                    author or organiser
POST   /api/v1/comments/{id}/pin                organiser or leader; { pinned }
PUT    /api/v1/comments/{id}/reaction           { reaction } — null clears
POST   /api/v1/comments/{id}/votes              { optionIds }
POST   /api/v1/comments/{id}/close-poll         author or organiser
POST   /api/v1/comments/{id}/report             → ContentReport
```

The web app needs no separate work for any of this: since v0.16 it runs the same `DLR.UI` thread component and the same SignalR client as the phone (§18.4), so a self-updating thread is not a web feature at all — it is the feature, rendered in a second host.

### 17.9 Schema

```
RideComment(Id, GroupRideId, AuthorId, Kind{Text,Poll}, Body?, PhotoId?,
            IsPinned, PinnedByUserId?, PinnedUtc?,
            CreatedUtc, PostedUtc, EditedUtc?)
            -- CHECK (Body IS NOT NULL OR PhotoId IS NOT NULL)      §17.2
            -- Ordering is on PostedUtc; CreatedUtc is clamped      §17.3
CommentReaction(CommentId, UserId, Reaction)   -- PK (CommentId, UserId)  §17.4
Poll(CommentId, AllowMultiple, ClosesUtc?, ClosedUtc?, ClosedByUserId?)
                                               -- PK CommentId, 1:1 with the comment
PollOption(Id, CommentId, Ordinal, Text)
PollVote(PollOptionId, UserId)                 -- PK (PollOptionId, UserId)  §17.5
ContentReport(Id, TargetKind, TargetId, ReportedByUserId, Reason,
              ContentSnapshot, CreatedUtc, ResolvedUtc?)            -- §17.7
```

Indexes: `RideComment(GroupRideId, PostedUtc desc)`, partial `RideComment(GroupRideId) WHERE IsPinned`, `PollVote(PollOptionId)`, `ContentReport(ResolvedUtc)` partial on unresolved.

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

Notify_OrdinaryCommentDuringLiveRide_SendsNoPush            — §17.1
Notify_PinnedCommentDuringLiveRide_SendsPush
Notify_MutedRide_SendsNothing
Car_ThreadIsNotRenderedAtAll                                — §4.6

ArchivedRide_ThreadIsReadOnly
RideCompleted_DeletesPositionsButKeepsThread
MemberRemoved_KeepsPostsButRevokesAccess
BlockedUser_CommentsAreHiddenFromTheBlocker
AccountDeleted_RemovesCommentsReactionsAndVotes
Report_SnapshotSurvivesDeletionOfTheComment                 — §17.7
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
| Push notifications (§17.6) | — | ✅ FCM/APNs; the browser gets none in v1 |
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

Map_SameComponentRenders_AgainstBothJsModules         — §4.5, v0.19
Map_MobileHost_ResolvesMapKitModule
Map_WebHost_ResolvesMapLibreModule
Map_MapKitTokenUnavailable_ShowsStatedErrorNotBlankMap
Map_AttributionIsPresent_OnEveryProvider
Map_CarSurface_UsesMapsuiNotAJsModule                 — §18.3
MapHostKind_EveryHostHasAFactory                      — unchanged from §4.6

WebAuth_RefreshTokenIsNotReadableFromJavaScript       — §18.5
WebAuth_SessionExpiresAfterConfiguredDays
MobileAuth_SessionStillNeverExpires                   — §7.4 unchanged
Repository_WebImplementation_SurfacesNetworkErrors    — §18.6
```
