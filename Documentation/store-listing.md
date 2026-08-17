# Store listing copy

The text fields for Google Play and App Store Connect. `store-release.md` is the procedure; this is
the wording. Character limits are the stores' own and are counted below each field — check them
again after any edit, because both consoles reject rather than truncate.

Every claim here is written against what the build actually does. Two things the design mentions
that are **not** in the shipping app, and must not appear in the listing until they are:
**Android Auto / CarPlay** (no `CarAppService`, no CarPlay scene) and push notifications
(`NoopNotificationService`). Offline map packs, GPX import, the track editor, markers, the thread
with photos and polls, and the private area are all built and are safe to describe.

## The vocabulary

An **adventure** is what a group does together; the people on it are **travellers** or, inside one,
**members**. Never *ride* or *rider* — so the app reads as being for motorcyclists, drivers,
cyclists, kayakers and walkers alike.

**The app's own screens now match**: *Group adventures*, *Live members*, *Adventure thread*,
*Blocked travellers*, *My adventures*, and a location disclosure that asks *"Share your location
while you travel?"*. Screen names quoted in this file and in the review notes are the real ones.
What deliberately still says *ride* is listed at the end.

---

## App name

Both stores, 30 characters. This is the name the app already uses on its own pages
(`Blocks.razor:6`) and in the review notes — not "Dumb Luck Rides", which is what the repository
README still calls it.

```
Dumb Luck Routes
```

*(16)*

## Subtitle — App Store only

30 characters.

```
Live map for group adventures
```

*(29)*

## Short description — Google Play only

80 characters. Shown above the fold, before anyone taps *Read more*.

```
A live map for group adventures. Sharing is per trip and off by default.
```

*(72)*

## Promotional text — App Store only

170 characters. Editable without a new build, so this is the field for what is new or seasonal.

```
Sharing is per adventure, off by default, and stops when you say so. Now with offline map packs — download the area before you go and keep the map when the signal dies.
```

*(168)*

## Keywords — App Store only

100 characters, comma-separated, no spaces. Words already in the name and subtitle are indexed
anyway, so they are not repeated here.

```
motorcycle,motorbike,cycling,walking,hiking,kayak,4wd,gpx,track,route,offline,tour,convoy,gps
```

*(93)*

---

## Full description

4000 characters on both stores. Play renders a little formatting; the App Store renders none, so
this is written to read correctly as plain text. Same copy in both places.

```
Dumb Luck Routes is a group-adventure companion for anyone who sets out together — motorbikes, 4WDs, a road trip, cycling, kayaking, a long walk. See where everyone is on one live map, and keep the track afterwards.

TRAVEL TOGETHER, NOT IN A LINE
Join an adventure with a code from the organiser, or ask and be admitted. Inside it, every member appears on a shared map with the planned route, so nobody is guessing at the next turn. A neighbour panel tells you who is ahead of you and who is behind, and by how far — the two questions you actually have while you are moving. The live member list shows each traveller's name, colour, distance along the route and how fresh their last fix is, sorted by position, by distance or by who moved most recently.

SHARING YOU CONTROL, PER ADVENTURE
You are asked when you join, and the answer starts at no. Turning sharing off deletes your stored position rather than merely stopping the broadcast. You can be on an adventure without sharing at all, and the member list makes that visible rather than hiding it. When an adventure ends, sharing ends with it — the organiser can allow a short, capped wind-down so people can watch each other get home, and the server enforces the cap. There is no always-on friend tracking in this app, and there is no way to switch it on.

A PRIVATE AREA THAT NEVER LEAVES YOUR PHONE
Draw a circle around home. Inside it nothing is recorded and nothing is sent. The circle is stored on your device and is never uploaded.

MAPS THAT WORK WITHOUT A SIGNAL
Download a map pack for the region you are heading into before you leave, and the map keeps drawing when the phone drops to no bars. Recording never needed a signal in the first place.

RECORD, IMPORT AND TRIM YOUR TRACKS
Record with the screen off, in a mount or in a pocket. Import GPX files from the app, from the website, or straight from the share sheet. Then trim the track back to the part worth keeping: cut points off the start, off the end, or a span out of the middle — a lunch stop, a wrong turn, the streets around your house. Nothing is sent until you press Apply, and undo steps back through every trim until then.

MARK THE THINGS WORTH MARKING
Drop a pin with an icon, a title, a note, a facing direction and a photo. Gravel across a corner, the fuel stop, the turning everyone misses, where to regroup. Waypoints in an imported GPX arrive as markers, and export back out again.

ONE THREAD PER ADVENTURE — QUIET WHEN IT MATTERS
Text, photos, pinned posts, reactions and polls, all in one place. While an adventure is live, ordinary comments never raise a notification: only a pinned post from the organiser breaks through. The people this app talks to are usually moving, and often at the wheel or the handlebars, and it is written that way. The organiser can turn markers, comments or photos off at any time.

AN ACCOUNT THAT ASKS FOR ALMOST NOTHING
A username and a password. An email address is optional, and so are a display name and a phone number — each with its own sharing switch, off until you turn it on, and shared only with the people you are travelling with. Photos are stripped of all metadata, including GPS, when they are uploaded. Settings has a full data export and account deletion that does what it says.

OPEN SOURCE
Dumb Luck Routes is free software under the AGPL. The app tells you the exact commit it was built from and where to get the source.
```

*(3415)*

---

## What's New — 8.0.0

Play calls it *Release notes* (500 characters); App Store calls it *What's New* (4000). Written to
fit the shorter one.

```
• New "Live members" screen — everyone on the trip with their colour, sharing state, distance along the route and how recent their last fix is, sorted however you like.
• Ahead / behind panel on the live map: who is in front, who is back, and by how far.
• Offline map packs — search by country and region, download before you leave, travel with no signal.
• The map now turns with you, like the follow-me button.
```

*(413)*

---

## The rest of the listing

Not copy, but the same form asks for them — cross-check against `store-release.md`.

- **Category** — Navigation. (Sports is the alternative; Navigation matches the live map and
  the GPX handling better, and does not narrow the audience the copy above just widened.)
- **Age rating / content rating** — the app carries user-generated content: threads, photos and
  markers are visible to other members. Answer both questionnaires as UGC with reporting and
  blocking — and read the *Two 1.2 gaps* section of `store-release.md` first, because blocking is
  organiser-only today.
- **Privacy policy URL** — required in the Play data safety form, in App Store Connect, and inside
  the app. It is still the blocker at the top of `store-release.md`.
- **Support URL** — App Store requires one; Play requires a contact email.
- **Screenshots** — Play: at least two phone shots. App Store: one 6.9" iPhone set and one 13"
  iPad set, because `UIDeviceFamily` claims iPad.
- **Demo account** — App Review needs a username, a password, and a trip the account is already in.

---

## What still says "ride", deliberately

None of it is user-visible, and each would cost something real to change:

| What | Why it stays |
|---|---|
| URL segments — `/group-rides/…`, `/rides/…` | Changing them breaks saved and shared links, and every navigation path in the review notes |
| API routes and contracts — `GroupRide`, `RideDetail`, `RideComment`, `RiderPosition` | Wire types. Renaming is a breaking change for no listing benefit |
| The `Rider` member role | A wire value the client compares against; it is never rendered — the member list shows a role badge only for `Owner` and `Leader` |
| CSS class names, C# identifiers, code comments | Invisible to users |
| OpenAPI summaries (`.WithSummary("Ends the ride.")`) | Developer-facing API documentation, not app text. Worth a sweep one day; nothing rides on it |
| Config keys `Ride:*`, `Rides:*`, the JWT issuer `dumb-luck-rides` | Renaming the issuer invalidates every live token; renaming the keys breaks deployed configuration |

One line that changed and carries a store obligation: the location disclosure
(`BlazorDLR.Shared/State/LocationBroadcastState.cs`) now reads *"Share your location while you
travel?"*. Play's required *"even when the app is closed or not in use"* clause is intact in the
body — but **the background-location declaration video must be re-recorded against the new
dialog** before the next Play submission.
