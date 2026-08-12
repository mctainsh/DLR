# Shipping to Google Play and the App Store

Companion to §4.3 (background location) and §14.2 (never commit these). The build-side pieces are
in `BlazorDLR/BlazorDLR.csproj` under **Store release configuration**; this file is everything that
is a form, an account setting or a decision rather than a build.

Read the two **blockers** first. Everything else is procedure; those two will fail a submission.

---

## Blockers to clear before the first upload

### 1. Scope the iOS ATS exception

`Platforms/iOS/Info.plist` sets `NSAllowsArbitraryLoads = true`, which disables App Transport
Security for every host. It is there so a debug build can reach `http://` on a developer's laptop.
App Review asks for a justification for a blanket exception and routinely refuses one from an app
whose production API is HTTPS.

Replace it with a scoped exception for the development host only, or drop the key from Release:

```xml
<key>NSAppTransportSecurity</key>
<dict>
	<key>NSExceptionDomains</key>
	<dict>
		<key>localhost</key>
		<dict>
			<key>NSExceptionAllowsInsecureHTTPLoads</key><true/>
		</dict>
	</dict>
</dict>
```

### 2. Publish a privacy policy at a stable URL

Both stores require one, and Play requires it **before** the background-location review can even be
submitted. It has to cover, in these terms:

- Precise location, collected only while the rider has turned sharing on for a group ride, shared
  only with the members of that ride.
- That location is collected **in the background**, and what stops it.
- The private area (§10.1) — a device-local circle inside which nothing is recorded or sent.
- Account data: username, optional email, optional phone (§7.3).
- Photos and comments attached to rides.
- Deletion: the account-deletion path in Settings → Data & export, and what it removes.

The URL goes in three places: the Play data safety form, App Store Connect, and the app itself.

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

```bash
dotnet publish BlazorDLR/BlazorDLR.csproj -f net10.0-android -c Release
# → bin/Release/net10.0-android/publish/au.com.securehub.dlr-Signed.aab
```

### Background location — the part that gets apps rejected

This is the single hardest review gate the app faces, and it is failed on process rather than on
merit. Play requires **all** of the following, and checks them against a video:

1. **Declaration form** (Play Console → App content → Sensitive app permissions → Location). It
   asks what the feature is, why background access is required, and for a video showing it.
2. **The video.** A screen recording, on a public URL, showing: the in-app disclosure appearing,
   the rider granting permission, the feature working, and the ongoing notification while the app
   is backgrounded. Record it from a clean install.
3. **Prominent in-app disclosure.** Before the system permission dialog, the app must state — in
   its own UI — that it collects location "to enable *feature*, even when the app is closed or not
   in use", with an explicit accept/deny.

   > **Implemented** in `LocationBroadcastState.DiscloseAsync` — shown once per device, before the
   > platform permission request, on every route that can start the receiver (the join-time consent
   > prompt and the info page's sharing switch both pass through it). It carries Play's required
   > "even when the app is closed or not in use" wording with an explicit *I agree* / *Cancel*.
   > **Record the video against this dialog**, and keep its wording intact if the copy is revised.

4. **Foreground service type declaration** (App content → Foreground service types → Location).
   Describe the user-visible feature and link the same video. Required since Android 14.
5. The app must remain usable when background location is refused — it is: the receiver runs while
   the app is on screen, and the settings screen says so.

### Data safety form

Answer it to match `PrivacyInfo.xcprivacy`, which is the same set of claims:

| Data type | Collected | Shared | Purpose | Optional |
|---|---|---|---|---|
| Precise location | Yes | Yes — with other members of a ride the rider chose | App functionality | Yes |
| User IDs (username) | Yes | Yes — shown to ride members | App functionality, account management | No |
| Email address | Yes | No | Account management | Yes |
| Photos | Yes | Yes — to ride members | App functionality | Yes |
| Other user content | Yes | Yes — to ride members | App functionality | Yes |

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

### Signing and upload

```
DLR_IOS_CODESIGN_KEY   e.g. "Apple Distribution: Your Company (TEAMID)"
DLR_IOS_PROVISION      the App Store provisioning profile name
```

```bash
# macOS only — the iOS build invokes Xcode
dotnet publish BlazorDLR/BlazorDLR.csproj -f net10.0-ios -c Release
```

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

In **App Review Information → Notes**, state plainly:

> Riders join a group ride and opt in to sharing their position with that ride. While sharing is on,
> the app publishes the rider's location so other members can see them on a live map — this
> continues with the screen off and the phone in a mount, which is the normal riding case. Sharing
> is off by default, is per ride, and stops when the rider turns it off, leaves the ride, or the
> ride ends. The blue background-location indicator is left enabled.

### A demo account

Review needs one, because everything behind the sign-in wall is invisible otherwise. Supply a
username and password **and** a ride the account is already a member of — an account with no rides
shows a reviewer an empty app, which is a rejection for "incomplete functionality".

### Other listing requirements

Screenshots for 6.7" and 6.5" iPhone (and iPad, since `UIDeviceFamily` includes it — either supply
them or drop iPad from the family), a support URL, the privacy policy URL, an age rating, and a
category (Navigation or Sports).

---

## Before every release

- [ ] Bump `ApplicationDisplayVersion` and `ApplicationVersion` in `BlazorDLR.csproj`, and the
      matching `CFBundleShortVersionString` / `CFBundleVersion` in `Info.plist` and
      `versionCode` / `versionName` in `AndroidManifest.xml`. They are currently maintained in all
      three places and must agree.
- [ ] Build from a clean, committed, pushed tree — `Directory.Build.targets` appends `.dirty` to
      `SourceRevisionId` otherwise, and it is visible to end users at `GET /api/v1/about` (§14.6.2).
- [ ] `dotnet format BlazorDLR.slnx --verify-no-changes`
- [ ] `dotnet test BlazorDLR.slnx` (Docker running, for the server integration tests)
- [ ] Verify on hardware, not an emulator: start a ride, lock the phone, ride for a few minutes,
      confirm the position moves on a second device — see the hardware checklist below.
- [ ] Upload the Android symbol file with the bundle so Play's crash reports are readable.

## Hardware checklist for the location feature

None of this can be verified by the test suite; `LocationBroadcastStateTests` covers everything
above the platform seam and nothing below it.

- [ ] **Android**: permission ladder appears in order — precise location, then background, then
      notifications.
- [ ] **Android**: the ongoing notification appears when sharing starts and disappears when it
      stops. It must never outlive the ride.
- [ ] **Android**: fixes continue with the screen off for at least ten minutes.
- [ ] **Android**: kill the app from the recents list; confirm the service restarts (`START_STICKY`)
      or that the ride ends cleanly, and that no orphaned notification remains.
- [ ] **Android**: a device without Play Services falls back to `LocationManager` and still
      publishes.
- [ ] **Android**: OEM battery managers (Xiaomi, Huawei, Samsung, OnePlus) — confirm behaviour and
      consider offering `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` with an explanation (§4.3).
- [ ] **iOS**: the blue background indicator appears while sharing and clears when it stops.
- [ ] **iOS**: choosing "While using the app" degrades rather than breaks — fixes stop when
      backgrounded and resume on return.
- [ ] **iOS**: fixes continue with the screen off and the app backgrounded.
- [ ] **Both**: standing inside a configured private area publishes nothing, and the ride's info
      page says so (§10.1).
- [ ] **Both**: battery cost over a two-hour ride on each of the three accuracy profiles.
