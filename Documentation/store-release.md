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
   its own UI — what it collects, **every** use it puts it to, and that it does so "even when the
   app is closed or not in use", with an explicit accept/deny.

   > **Implemented** in `BlazorDLR.Shared/State/LocationDisclosure.cs` — the copy, the key it is
   > remembered under and the ask, in one type, in front of every route to a fix: the broadcaster
   > (which the join-time consent prompt, the info page's sharing switch and the launch restore all
   > enter through) and the *Use my location* button on **My routes**.
   >
   > **8.0.0.28 was rejected here**, with *"the in-app Prominent Disclosure does not disclose the
   > usage of accessed or collected Location data"*. Two holes, both closed:
   >
   > - The copy named live sharing and never mentioned that the same fixes are written into a
   >   **recorded track** (§15.1). One of the two uses of the collected data was undisclosed.
   > - **My routes → Use my location** called `EnsurePermissionsAsync` directly. That climbs the
   >   whole Android ladder, background rung included, so a traveller who had never shared anything
   >   could reach the system location dialog with nothing said first.
   >
   > The disclosure now opens *"Dumb Luck Routes collects location data — your precise position — to
   > show you to the other members of the group adventures you turn sharing on for, and to record
   > the track of where you went, even when the app is closed or not in use."* **That sentence is
   > the requirement. Leave it alone; revise the paragraphs after it freely.** The buttons are
   > *I agree* / *No thanks*.
   >
   > The storage key is suffixed (`dlr.location-disclosure.2`) and **the suffix moves whenever the
   > disclosure gains a use** — a device that accepted copy which never mentioned recording has not
   > agreed to what this one says, so it is asked again.
   >
   > **Re-record the declaration video against this dialog before the next submission.** Any
   > existing one shows a dialog that no longer exists.

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

Its `NSPrivacyCollectedDataTypes` must match the App Store Connect privacy answers exactly. Take
them from the table in the next section, **not** from the Play data safety form above - Play has
no tracking question, and answering Apple's from that table is what caused the 5.1.2(i)
rejection.

### App privacy answers - and the 5.1.2(i) rejection

**8.0.0 (28) was rejected here** (submission `101b1e53-4956-41b9-8007-654a4f0bb0bd`, 4 September
2026): *"The app privacy information provided in App Store Connect indicates the app collects data
in order to track the user, including User ID, Precise Location, and Coarse Location. However, the
app does not use App Tracking Transparency."*

**Nothing in the binary is wrong, and adding an ATT prompt is the wrong fix.** ATT asks permission
for tracking this app does not do; a prompt the binary cannot justify is its own rejection, and
answering yes to Apple's tracking question obliges a privacy policy that says the app tracks. The
fix is the App Store Connect answers.

Apple's *tracking* means linking this app's data to data gathered by other companies' apps or sites
for advertising or measurement, or sharing it with a data broker. This app does neither: no
advertising identifier (nothing in the tree references `AdSupport` or `AppTrackingTransparency`), no
ad network, no analytics SDK, no data broker. Position and username go to this app's own server and
to the members of an adventure the traveller chose to share with, and nowhere else. That is what
`NSPrivacyTracking` being `false` in `PrivacyInfo.xcprivacy` asserts, and the label has to agree
with it.

**Showing a member's position and username to the other members is not tracking**, however much
the plain English word fits. Other travellers are end users of this app, not third-party
companies and not data brokers, and the data is never linked to anything gathered elsewhere. That
sharing is disclosed by declaring Precise Location and User ID as *linked to the user* - which is
the bucket below, and is the whole disclosure Apple asks for.

**The answers.** App Store Connect -> App Privacy -> Data Types. Every type below is *linked to the
user* and *not* used for tracking, so **Data Used to Track You must come out empty**.

| Data type | Collected | Linked to user | Used to track | Purpose |
|---|---|---|---|---|
| Precise Location | Yes | Yes | **No** | App Functionality |
| User ID | Yes | Yes | **No** | App Functionality |
| Email Address | Yes | Yes | **No** | App Functionality |
| Photos or Videos | Yes | Yes | **No** | App Functionality |
| Other User Content | Yes | Yes | **No** | App Functionality |

**"Data is not collected from this app" is the same mistake pointing the other way.** It is the
tempting answer once a tracking claim has just been rejected, and it is false - positions, accounts
and photos are all stored on the server, which is Apple's definition of collected. It also
contradicts the five `NSPrivacyCollectedDataTypes` entries in the manifest, and the label is checked
against those. *Collect* and *track* are separate questions: yes to the first for all five types
above, no to the second for all five.

**Coarse Location is not collected and must not be declared.** `AppleLocationProvider.DesiredAccuracy`
asks for `AccuracyBest` or `AccuracyNearestTenMeters`, and every fix published is full resolution -
Apple's Coarse Location means a position deliberately reduced below three decimal places of
latitude and longitude, which this app never produces. It was ticked in App Store Connect and has
never been in `PrivacyInfo.xcprivacy`; that mismatch is a review finding on its own.

**No new build is required.** The rejected 28 was rejected on its label, not its code. Save the
corrected answers, reply in Resolution Center, and resubmit - the tree is already at 8.0.0 (30) if a
fresh upload is preferred.

**Reply to paste into Resolution Center**, after the answers are saved:

> Dumb Luck Routes does not track users, and does not track on any other platform it ships to.
>
> The app contains no advertising identifier, no ad network, no analytics SDK and no third-party
> tracking SDK, and it shares no data with data brokers. Its privacy manifest declares
> NSPrivacyTracking = false.
>
> The App Store Connect privacy answers were entered incorrectly: User ID, Precise Location and
> Coarse Location were marked as used to track. We have corrected them. User ID and Precise
> Location are now declared as linked to the user and not used for tracking, and Coarse Location
> has been removed entirely - the app only ever requests full-accuracy fixes and never a reduced
> one. Data Used to Track You is now empty, which matches the binary.
>
> Location is used for two things only, both named in our own disclosure dialog shown before the
> iOS permission prompt: showing a traveller to the other members of a group adventure they turned
> sharing on for, and recording that traveller's own track. Both stay on our own server.

### The 5.1.2(i) rejection - location shown to other users

**8.0.0 (31) was rejected here** (13 September 2026): *"The app enables the display of nearby
users' locations on a map, but does not have the required privacy precautions in place."* Apple
asked for four things. Three are done in the build; the fourth is a deliberate product decision and
is described honestly below rather than claimed.

**1. Age rating 18+.** App Store Connect only - no build change. **App Store Connect -> your app ->
Age Rating -> Edit.** If no content descriptor applies, use **Override to a Higher Age Rating** and
set **18+**. Do this before resubmitting; it is the item most likely to be checked first because it
costs the reviewer one click to verify.

Keep it in step with Google Play, whose content rating questionnaire asks about location sharing
separately. The two stores do not read each other's answers.

**2. A way to block other users.** Closed in this release. `LiveMemberList` now carries a block
control on every row but the reader's own, reachable in two taps from the live map (**nav rail ->
Live members -> the block icon beside a name**). It confirms first, because the block is symmetric
and the undo lives on another screen.

The block is **symmetric on the map and one-directional for content**, which is not an
inconsistency:

- Content (`BlockList`): blocking hides *their* posts from *you*. Hiding yours from them would
  announce the block, which section 16.5 is written to avoid.
- Position (`BlockCache`): blocking takes *both* pins off *both* maps. A one-way block here would
  leave the person you blocked still watching where you are, which is the whole case a block on a
  live map exists for.

Filtered on the server, on both channels - `PositionStore.SnapshotAsync` for the load and
`RideBroadcastService.SendAsync` for the five-second batch. A blocked traveller's coordinates never
reach the other party's device at all; nothing relies on the client agreeing to hide a pin.

**3. Permission to be displayed, with the option to decline.** `ConsentPrompt` asks per adventure
and its second button is *Not now*; dismissing counts as no (`ConsentAskedState`).

This prompt existed before the rejection and **was unreachable for anybody who joined by code**:
`JoinRide.ShareByDefaultAsync` set the sharing flag at join, and `GroupRideLive` only raises the
prompt while sharing is off. That call is gone. Joining now lands a traveller on the map screen not
sharing, and the ride asks.

**4. Manual check-in each time, with no automatic check-in. Not implemented, by decision.**

Sharing remains a durable per-adventure flag that resumes from saved state: `LaunchRestore` brings
the receiver back up at app start for an adventure whose flag still stands, and
`RideSession.LoadAsync` does the same whenever the ride screen loads. A traveller taps once, ever,
per adventure.

This is the item Apple named most concretely, so expect it to be raised again. What the build can
honestly say for itself, and what the Resolution Center reply below says:

- Nothing is shown to *nearby* users. A position reaches the members of one group adventure the
  traveller joined by code and turned sharing on for, and nobody else. There is no proximity
  discovery, no public map, and no way to see a stranger.
- Sharing is off until turned on, per adventure, and off again in one tap from the map's menu.
- The map states in red, continuously, whenever sharing is off.
- A private area (**Settings -> Location**) suppresses transmission entirely inside a circle around
  home.

If Review holds the line, the smallest change that satisfies it without losing the saved state is to
keep `LaunchRestore`'s memory of the adventure and put a one-tap confirmation in front of the
receiver start - *"You were sharing with Sunday Run. Resume?"* - instead of starting it silently.
That is one prompt in one file; it is not done here because it was declined, not because it is hard.

**Reply to paste into Resolution Center**, once the age rating is set:

> Thank you - we have made the following changes.
>
> **Age rating.** We have used Override to a Higher Age Rating to set the app to 18+.
>
> **Blocking.** Any member can now block any other member from the Live members list of an
> adventure (nav rail -> Live members -> the block icon beside a name). Blocking is mutual on the
> map: neither person can see the other's position afterwards. It also hides the blocked member's
> comments, reactions, markers and poll votes. Blocks are managed at Settings -> Blocked travellers.
> Filtering is applied on our server, so a blocked member's coordinates are never sent to the other
> person's device.
>
> **Permission to be displayed.** Each adventure asks before the member's location is shown to
> anyone on it, with Share and Not now, and dismissing the prompt counts as declining. Our own
> disclosure dialog naming what is collected and why is shown before the iOS location prompt.
> Joining an adventure no longer turns sharing on by itself - a member who joins arrives not
> sharing and is asked.
>
> **On check-ins.** We would like to clarify what the app does, as we think "nearby users" may
> describe a different app from ours. Dumb Luck Routes has no proximity discovery and no public
> map. A member's position is visible only to the other members of a specific private group
> adventure that they joined using a code given to them by its organiser, and that they then
> explicitly turned sharing on for. There is no way for a member to see, or be seen by, a stranger
> or a nearby user.
>
> Sharing is off by default, is per adventure rather than account-wide, is turned on by an explicit
> tap after the consent prompt above, and is turned off in one tap from the map. While it is off the
> map displays a persistent red indicator. Members can also set a private area around their home
> inside which no position is transmitted at all.
>
> If, given that this is closed-group sharing rather than nearby-user discovery, you still require a
> per-session check-in, please let us know and we will add a confirmation step on each app launch.

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
> 3. The nav rail's **Live members** shows the same people as a sortable list, and carries the
>    **block** control beside each name.
> 3a. Opening an adventure the account is not yet sharing with puts up the sharing consent prompt
>    — *Share* / *Not now*. Declining is remembered; the map then states in red that sharing is off.
> 4. Hamburger menu (top of the map) **→ Info → My sharing**. Turning that switch on shows our own
>    disclosure dialog ("Dumb Luck Routes collects location data") *before* the iOS permission
>    prompt. It names both things a fix is used for — showing you to the other members, and
>    recording your own track — and is shown once per device.
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
> - **Block**: any member can block any other member of an adventure from the nav rail's
>   **Live members** list — the block icon beside a name. Blocking is mutual on the map: neither
>   person can see the other's position afterwards, and the blocked member's comments, reactions,
>   markers and poll votes are hidden as well. An organiser can also decline and block a join
>   request at **Group adventures → [adventure] → Requests**. Blocked accounts are listed and can
>   be unblocked at **Settings → Blocked travellers**.
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

### ⚠ One 1.2 gap the notes are worded around

**Markers cannot be reported.** `ReportMarkerAsync` is on `IApiClient` and
`POST /api/v1/markers/{id}/report` is live, but `MarkerDetails.razor` offers only a delete. A photo
attached to a marker is user-generated content a member can see and cannot report — only comments
carry the flag control. The notes above therefore claim reporting for comments only.

**Blocking was organiser-only and is not any more.** It is recorded here because the previous state
of it was half of what 8.0.0 (31) was rejected for — see the 5.1.2(i) section below. `Live members
→ the ⊘ beside a name` now calls the endpoint that always existed, and a block takes both parties
off each other's live map as well as hiding the blocked party's posts, reactions, markers and poll
votes. `Settings → Blocked travellers` still lists and unblocks.

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
- [ ] Confirm the App Store Connect privacy answers still match `PrivacyInfo.xcprivacy`, and that
      **Data Used to Track You** is empty - a label that claims tracking is a 5.1.2(i) rejection
      however clean the binary is, and one that claims nothing is collected contradicts the
      manifest just as badly. Check the edit is published, not left as a draft.

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
