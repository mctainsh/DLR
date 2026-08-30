# Shipping to Google Play and the App Store

Companion to §4.3 (background location) and §14.2 (never commit these). The build-side pieces are
in `BlazorDLR/BlazorDLR.csproj` under **Store release configuration**; this file is everything that
is a form, an account setting or a decision rather than a build.

Read the **blocker** first. Everything else is procedure; that one will fail a submission.

---

## Blocker to clear before the first upload

### Publish a privacy policy at a stable URL

Both stores require one, and Play requires it **before** the background-location review can even be
submitted. It has to cover, in these terms:

- Precise location, collected only while a member has turned sharing on for a group adventure,
  shared only with the members of that adventure.
- That location is collected **in the background**, and what stops it.
- The private area (§10.1) — a circle around home inside which nothing is sent. **Since v0.28 the
  circle is stored on the member's account**, not only on the handset, so it survives a reinstall and
  follows them to a new phone. That makes it precise location data held at rest by us, and the policy
  has to say so — along with the part that did not change: no other member can see it, and there is no
  route by which anyone could ask for somebody else's.
- Account data: username, optional email, optional phone (§7.3).
- Photos and comments attached to adventures.
- Deletion: the account-deletion path in Settings → Data & export, and what it removes.

The URL goes in three places: the Play data safety form, App Store Connect, and the app itself.

### Settled, and not to be undone

`NSAllowsArbitraryLoads` was removed from `Platforms/iOS/Info.plist` — it was the second blocker
here until then. What remains is `NSAllowsLocalNetworking` (loopback, for the on-device map-pack
server and the simulator's debug API base) and one `NSExceptionDomains` entry for
`pmtiles.securehub.net`. Both are scoped; the blanket key does not come back. A debug build on a
*device* pointed at a LAN server needs that host added to `NSExceptionDomains` locally and never
committed.

`NSBluetoothAlwaysUsageDescription` was removed at the same time. Nothing in the app touches
CoreBluetooth, and a purpose string for a permission the binary never requests invites a 5.1.1
question from a reviewer already looking hard at background location.

---

## Google Play

### Signing

Enrol in **Play App Signing**. Google holds the app signing key; you hold an *upload* key. A lost
upload key can be reset by Google — a lost app signing key, on an app not enrolled, ends the app's
update path permanently.

Build config reads the upload key from the environment (never the repo — §14.2):

```
DLR_ANDROID_KEYSTORE       path to the .jks
DLR_ANDROID_KEYSTORE_PASS  store password
DLR_ANDROID_KEY_ALIAS      key alias
DLR_ANDROID_KEY_PASS       key password
```

### Where the signing material lives

Created once by **`Create-AndroidUploadKey.bat`** (solution root), which refuses to overwrite an
existing keystore. It writes two files, both **outside the repository**:

| File | What it is |
|---|---|
| `%USERPROFILE%\.dlr-signing\dlr-upload.jks` | The upload key. PKCS12, RSA 4096, alias `dlr-upload`, 10000 days — Play requires validity past 22 Oct 2033. |
| `%USERPROFILE%\.dlr-signing\dlr-signing-env.bat` | Sets the four `DLR_ANDROID_*` variables. **Contains the password in cleartext** — treat it as the key itself. |

Both are ACL'd to the current user by the script. `%USERPROFILE%` on this machine is *not*
OneDrive-redirected, which is deliberate: cloud-syncing an unrotatable key spreads it to every
device and to a vendor's servers. Back it up to encrypted offline media instead, and put the
password in a password manager.

### Building the bundle

**`Publish-Android.bat`** (solution root) is the whole procedure. It sources the signing
environment, then runs the release checklist's gates in order before it will build — versions
agreeing across the csproj and `AndroidManifest.xml`, a clean tree, `dotnet format`, the test suite.

```
Publish-Android.bat              full run
Publish-Android.bat /skiptests   skip the test suite (no Docker needed)
Publish-Android.bat /force       build from a dirty or unpushed tree
```

Output (the app ID is `au.com.securehub.dlr.v2`, so the bundle is named for it):

```
BlazorDLR/bin/Release/net10.0-android/publish/
    au.com.securehub.dlr.v2-Signed.aab   <- upload this
    mapping.txt                          <- upload this too, for readable crash reports
```

Or by hand, with the four variables already in the environment:

```bash
dotnet publish BlazorDLR/BlazorDLR.csproj -f net10.0-android -c Release
```

> Do not export a bare `OUTDIR`, `PROJ` or similar in a shell that then runs `dotnet publish`.
> MSBuild imports the process environment as properties, so `OUTDIR` becomes `$(OutDir)` and
> silently relocates every project's output. This is why every variable in `Publish-Android.bat`
> is `DLR_`-prefixed.

### Background location — the part that gets apps rejected

This is the single hardest review gate the app faces, and it is failed on process rather than on
merit. Play requires **all** of the following, and checks them against a video:

1. **Declaration form** (Play Console → App content → Sensitive app permissions → Location). It
   asks what the feature is, why background access is required, and for a video showing it.
2. **The video.** A screen recording, on a public URL, showing: the in-app disclosure appearing,
   the person granting permission, the feature working, and the ongoing notification while the app
   is backgrounded. Record it from a clean install.
3. **Prominent in-app disclosure.** Before the system permission dialog, the app must state — in
   its own UI — that it collects location "to enable *feature*, even when the app is closed or not
   in use", with an explicit accept/deny.

   > **Implemented** in `LocationBroadcastState.DiscloseAsync` — shown once per device, before the
   > platform permission request, on every route that can start the receiver (the join-time consent
   > prompt and the info page's sharing switch both pass through it). It carries Play's required
   > "even when the app is closed or not in use" wording with an explicit *I agree* / *Cancel*.
   > **Record the video against this dialog**, and keep its wording intact if the copy is revised.
   > **The copy was revised** when the app moved from "ride" to "adventure" — the title now reads
   > *"Share your location while you travel?"*. Play's required clause survives verbatim in the
   > body, but any existing declaration video shows a dialog that no longer exists: **re-record it
   > before the next submission**.

4. **Foreground service type declaration** (App content → Foreground service types → Location).
   Describe the user-visible feature and link the same video. Required since Android 14.
5. The app must remain usable when background location is refused — it is: the receiver runs while
   the app is on screen, and the settings screen says so.

### Data safety form

Answer it to match `PrivacyInfo.xcprivacy`, which is the same set of claims:

| Data type | Collected | Shared | Purpose | Optional |
|---|---|---|---|---|
| Precise location | Yes | Yes — with the other members of an adventure the user chose | App functionality | Yes |
| User IDs (username) | Yes | Yes — shown to the other members | App functionality, account management | No |
| Email address | Yes | No | Account management | Yes |
| Photos | Yes | Yes — to the other members | App functionality | Yes |
| Other user content | Yes | Yes — to the other members | App functionality | Yes |

Also declare: data encrypted in transit — **yes**; a way to request deletion — **yes** (Settings →
Data & export).

### Target API level

Play enforces a target API floor (API 35 as of August 2025, rising annually). `net10.0-android`
targets the SDK's current platform, which is above the floor — but check it at each release rather
than assuming, because the failure is an upload Play refuses.

### Store listing

App name, short and full description, an app icon (512×512), a feature graphic (1024×500), phone
screenshots (at least two), a content rating questionnaire, a contact email, and the privacy policy
URL.

---

## App Store

### App ID and capabilities

Register an **explicit** App ID for `au.com.securehub.dlr` — it has to match `ApplicationId` in
`BlazorDLR.csproj` exactly — and select **no capabilities at all**. Nothing in the app requires an
entitlement, and there is no `Platforms/iOS/Entitlements.plist` (the only one in the repo is
MacCatalyst's). Anything ticked here has to be justified later and regenerates provisioning
profiles when changed.

| Capability | Needed | Why not |
|---|---|---|
| Push Notifications | No | `MauiProgram` registers `NoopNotificationService`; no APNs, no `UNUserNotification` |
| Sign in with Apple | No | Only sign-in is the app's own JWT; Google is `UnavailableExternalSignInProvider`. Enabling any third-party sign-in makes this **mandatory** under 4.8 |
| Associated Domains | No | No universal links, no custom URL scheme |
| App Groups | No | No extensions or widgets |
| Keychain Sharing | No | `SecureStorageTokenStore` uses the default access group (the bundle ID) |
| Maps | No | MapLibre in the WebView, not MapKit — this capability is for offering directions *to* Apple Maps |
| HealthKit / iCloud / Access WiFi Information | No | No references anywhere in the tree |

Two things that are *not* App ID capabilities and are already configured in `Info.plist`:

- **Background Modes → Location updates** (`UIBackgroundModes`), which is what allows
  `AppleLocationProvider` to set `AllowsBackgroundLocationUpdates`. No entitlement, no portal toggle.
- **Document types** for `.gpx`, which is what puts the app in the share sheet.

iPad support is `UIDeviceFamily` in `Info.plist`, not an App ID setting — see the listing
requirements below.

> Local-dev caveat only: MAUI `SecureStorage` on the iOS **simulator** can need a Keychain Sharing
> entitlement with an access group matching the bundle ID. Device and App Store builds work off the
> automatic `application-identifier` entitlement from the provisioning profile — do not add the
> capability to the shipping App ID for it.

### Signing and upload

```
DLR_IOS_CODESIGN_KEY   e.g. "Apple Distribution: Your Company (TEAMID)"
DLR_IOS_PROVISION      the App Store provisioning profile name
```

```bash
# macOS only — the iOS build invokes Xcode
dotnet publish BlazorDLR/BlazorDLR.csproj -f net10.0-ios -c Release
```

**Upload from Xcode's Organizer on the Mac, not from Visual Studio's Archive Manager.** The
"Distribute…" button in VS on Windows hands the archive straight to `altool` as it was signed, and
the signing block at `BlazorDLR.csproj:78-81` applies to *every* configuration — an `Apple
Development` identity and a Development profile. The distribution override further down only fires
when `DLR_IOS_CODESIGN_KEY` is set in the environment, which a VS process launched without it does
not have, so the archive is dev-signed and validation fails (ITMS-90034, "not signed using an Apple
submission certificate"). Xcode's Organizer re-signs on export and does not have this problem.

If VS's Distribute is ever wanted, either set `DLR_IOS_CODESIGN_KEY` / `DLR_IOS_PROVISION` as
user-level environment variables and restart VS, or scope the dev pair to `Debug` so a Release
archive cannot pick it up silently.

`ITSAppUsesNonExemptEncryption=false` is already in `Info.plist`, so uploads will not stop on the
export-compliance question. It is true as long as the app ships no cryptography of its own.

### Privacy manifest

`Platforms/iOS/PrivacyInfo.xcprivacy` exists and is bundled at the root of the `.app` by the
`BundleResource` item in the csproj. Required since 1 May 2024 — a build without one is rejected at
processing, before review.

Its `NSPrivacyCollectedDataTypes` must match the App Store Connect privacy answers exactly; they are
written from the same table as the Play data safety form above.

### Background location review

Apple's guideline 2.5.4: an app may only declare the `location` background mode if the feature
genuinely requires it, and the review notes must say what it is. `fetch` has been removed from
`UIBackgroundModes` for exactly this reason — nothing implements it.

The notes below say what it is.

### App Review Information → Notes

Paste this into **App Store Connect → App Review Information → Notes**, with the four `«…»`
placeholders filled in. Every navigation path in it is real; check them against the build before
submitting, because a reviewer following a path that does not exist is worse than no notes at all.

> **What this app is**
>
> Dumb Luck Routes is a group-adventure app for anyone who travels together — motorbikes, 4WDs,
> road trips, cycling, kayaking, walking. Someone creates or joins a group adventure, and its
> members can see each other on a live map along the route.
>
> **Demo account**
>
> Username: «DEMO_USERNAME»
> Password: «DEMO_PASSWORD»
>
> This account is already a member of an adventure with other people on it, so the live map has
> something to show. To join a second one: **Group adventures → Join**, join code «DEMO_JOIN_CODE».
>
> **Seeing the main feature, in about two minutes**
>
> 1. Sign in with the account above.
> 2. **Group adventures →** tap the one the account is already in. The live map opens; other members
>    appear as coloured markers with their names and their distance along the route.
> 3. The nav rail's **Live members** shows the same people as a sortable list.
> 4. Hamburger menu (top of the map) **→ Info → My sharing**. Turning that switch on shows our own
>    disclosure dialog ("Share your location while you travel?") *before* the iOS permission prompt.
> 5. Hamburger menu **→ Adventure thread** for comments, and **→ Add marker** to attach a photo or note
>    to a point on the route.
>
> **Why the app requests background location (guideline 2.5.4)**
>
> A member opts in to sharing their position with one group adventure at a time. While sharing is
> on, the app publishes their location so the other members of that adventure can see them on the
> live map. This has to continue with the screen off and the phone in a mount or a pocket, because
> that is the normal case — someone travelling is not holding their phone. Without background
> location the map goes stale for everyone else the moment they stop looking at it, which is
> precisely when they are under way.
>
> Sharing is off until it is turned on, it is per adventure rather than global, and it stops when
> the member turns the switch off, leaves, or is removed. The blue background-location
> indicator is left enabled throughout. Someone who grants only "While Using the App" still has a
> working app: position updates stop when it is backgrounded and resume on return.
>
> A private area can also be set — **Settings → Location → Home private area** — a circle around
> home inside which no position is sent to anyone. It is saved to the member's account so that it
> survives reinstalling the app and follows them to a new device; it is visible to no other member,
> and there is no request by which one member could obtain another's.
>
> **User-generated content (guideline 1.2)**
>
> Members can post comments in an adventure's thread and attach photos and notes to map markers,
> visible only to the other members of that adventure.
>
> - **Report**: the flag control on any comment in an **Adventure thread**. Reports go to the organiser
>   and to us.
> - **Block**: an organiser can decline and block a join request at **Group adventures →
>   [adventure] → Requests**. Blocked accounts are listed and can be unblocked at
>   **Settings → Blocked travellers**.
> - **Delete your account and everything in it**: **Settings → Data & export**. The same screen
>   exports the account's data.
> - Moderation contact: «SUPPORT_EMAIL».
>
> **Other things you may notice**
>
> - **Offline maps** — **Settings → Maps** downloads regional map packs so the map works without a
>   signal. The app serves those files to its own map view over `127.0.0.1`, which is why
>   `Info.plist` declares `NSAllowsLocalNetworking`.
> - **GPX** — the app registers as a handler for `.gpx` files, so it appears in the share sheet when
>   a GPS track is shared from another app.
> - The app supports iPhone and iPad.
> - Encryption: HTTPS and the platform keychain only; no proprietary cryptography.

### ⚠ Two 1.2 gaps the notes are worded around

Both of these are endpoints that exist with no UI on top of them, so both are small to close — and
both are cheaper to close now than to answer a rejection about.

**Markers cannot be reported.** `ReportMarkerAsync` is on `IApiClient` and
`POST /api/v1/markers/{id}/report` is live, but `MarkerDetails.razor` offers only a delete. A photo
attached to a marker is user-generated content a member can see and cannot report — only comments
carry the flag control. The notes above therefore claim reporting for comments only.

**Blocking is organiser-only.** Guideline 1.2 wants "the ability to block abusive users", and what
the app has is narrower:

- `POST /api/v1/blocks` exists, and `IApiClient.BlockUserAsync` is wired to it — but **no screen
  calls it**. The only path that blocks is `RideRequests.razor`'s *Decline & block*, which goes
  through `DecideJoinRequestAsync`, and only an organiser deciding a join request can reach it.
- `Settings → Blocked travellers` lists blocks and unblocks them. It cannot create one.

So an ordinary member who is harassed in a thread has *report*, but no way to block the person
themselves. A "Block this person" action on a thread comment or in the members list, calling the
endpoint that already exists, closes it.

### A demo account

Review needs one, because everything behind the sign-in wall is invisible otherwise. Supply a
username and password **and** an adventure the account is already a member of — an account with
none shows a reviewer an empty app, which is a rejection for "incomplete functionality".

### Other listing requirements

**iPad is supported** — `UIDeviceFamily` in `Info.plist` is `[1, 2]` and stays that way, so the app
has to work there and the listing has to show it.

Screenshots: one 6.9" iPhone set and one 13" iPad set. App Store Connect scales those down for the
smaller sizes, so the older 6.7"/6.5" sets are no longer required. Also needed: a support URL, the
privacy policy URL, an age rating, and a category (Navigation or Sports).

---

## Before every release

On Android, `BlazorDLR/Publish-Android.bat` aborts on the first four items below: the version
check it makes is the Android half of item 1 (csproj against `AndroidManifest.xml` — it does not
read `Info.plist`, which is the iOS build's problem), and then the clean tree, the format gate and
the test suite. Everything after that is still yours.

- [ ] Bump `ApplicationDisplayVersion` and `ApplicationVersion` in `BlazorDLR.csproj`, and the
      matching `CFBundleShortVersionString` / `CFBundleVersion` in `Info.plist` and
      `versionCode` / `versionName` in `AndroidManifest.xml`. They are currently maintained in all
      three places and must agree.
- [ ] Build from a clean, committed, pushed tree — `Directory.Build.targets` appends `.dirty` to
      `SourceRevisionId` otherwise, and it is visible to end users at `GET /api/v1/about` (§14.6.2).
- [ ] `dotnet format BlazorDLR.slnx --verify-no-changes`
- [ ] `dotnet test BlazorDLR.slnx` (Docker running, for the server integration tests)
- [ ] Verify on hardware, not an emulator: start an adventure, lock the phone, travel for a few minutes,
      confirm the position moves on a second device — see the hardware checklist below.
- [ ] Upload the Android symbol file with the bundle so Play's crash reports are readable.

## Hardware checklist for the location feature

None of this can be verified by the test suite; `LocationBroadcastStateTests` covers everything
above the platform seam and nothing below it.

- [ ] **Android**: permission ladder appears in order — precise location, then background, then
      notifications.
- [ ] **Android**: the ongoing notification appears when sharing starts and disappears when it
      stops. It must never outlive the sharing switch.
- [ ] **Android**: fixes continue with the screen off for at least ten minutes.
- [ ] **Android**: kill the app from the recents list; confirm the service restarts (`START_STICKY`)
      or that sharing stops cleanly, and that no orphaned notification remains.
- [ ] **Android**: a device without Play Services falls back to `LocationManager` and still
      publishes.
- [ ] **Android**: OEM battery managers (Xiaomi, Huawei, Samsung, OnePlus) — confirm behaviour and
      consider offering `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` with an explanation (§4.3).
- [ ] **iOS**: the blue background indicator appears while sharing and clears when it stops.
- [ ] **iOS**: choosing "While using the app" degrades rather than breaks — fixes stop when
      backgrounded and resume on return.
- [ ] **iOS**: fixes continue with the screen off and the app backgrounded.
- [ ] **iPad**: every screen is usable at iPad width and in all four orientations — the listing
      claims iPad support (`UIDeviceFamily` includes 2), and a broken layout there is a rejection
      even though the phone build is fine.
- [ ] **Both**: standing inside a configured private area publishes nothing, and the adventure's info
      page says so (§10.1).
- [ ] **Both**: and riding *across* one, the member's own mark still moves, follow-me still follows
      and heading-up still turns — the area hides the position from other members, not from its
      owner (§10.1).
- [ ] **Both**: a private area set on one device is in force on a second device signed in to the same
      account, and survives reinstalling the app — the whole reason it moved off the handset (§10.1).
- [ ] **Both**: with the phone in flight mode, the Location screen still shows the circle, still says
      it is this phone's copy, and saving a change says the account has not got it yet (§10.1).
- [ ] **Both**: battery cost over a two-hour trip on each of the three accuracy profiles.
