# Store listing copy

The text fields for Google Play and App Store Connect. `store-release.md` is the procedure; this is
the wording. Character limits are the stores' own and are counted below each field — check them
again after any edit, because both consoles reject rather than truncate.

Every claim here is written against what the build actually does. Two things the design mentions
that are **not** in the shipping app, and must not appear in the listing until they are:
**Android Auto / CarPlay** (no `CarAppService`, no CarPlay scene) and push notifications
(`NoopNotificationService`). Offline map packs, GPX import, the track editor, markers, the thread
with photos and polls, and the private area are all built and are safe to describe.

## The vocabulary, and where it does not yet reach

This copy says **adventure** and **adventurer**, never *ride* or *rider*, so the app reads as being
for motorcyclists, drivers, cyclists and walkers alike.

**The app's own screens have not been changed to match.** They still say *Group rides*, *Ride
members live*, *Ride thread*, *Blocked riders*, and the location disclosure asks *"Share your
location while you ride?"*. Where this file has to name a screen a user will look for — the
*What's New* entry below — it uses the app's real label rather than the listing's vocabulary,
because a release note pointing at a screen that does not exist under that name is worse than an
inconsistent one. The mismatch is listed at the end of this file as the work that would close it.

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
motorcycle,motorbike,cycling,walking,hiking,4wd,gpx,track,route,offline,tour,convoy,gps,touring
```

*(95)*

---

## Full description

4000 characters on both stores. Play renders a little formatting; the App Store renders none, so
this is written to read correctly as plain text. Same copy in both places.

```
Dumb Luck Routes is a group-adventure companion for motorcyclists, drivers, cyclists and walkers. Set out together, see where everyone is on one live map, and keep the track afterwards.

TRAVEL TOGETHER, NOT IN A LINE
Join an adventure with a code from the organiser, or ask and be admitted. Inside it, every member appears on a shared map with the planned route, so nobody is guessing at the next turn. A neighbour panel tells you who is ahead of you and who is behind, and by how far — the two questions you actually have while you are moving. The live member list shows each adventurer's name, colour, distance along the route and how fresh their last fix is, sorted by position, by distance or by who moved most recently.

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

*(3386)*

---

## What's New — 8.0.0

Play calls it *Release notes* (500 characters); App Store calls it *What's New* (4000). Written to
fit the shorter one. The screen names here are the app's own, not this file's vocabulary — see the
note at the top.

```
• New "Ride members live" screen — everyone on the trip with their colour, sharing state, distance along the route and how recent their last fix is, sorted however you like.
• Ahead / behind panel on the live map: who is in front, who is back, and by how far.
• Offline map packs — search by country and region, download before you leave, travel with no signal.
• The map now turns with you, like the follow-me button.
```

*(418)*

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

## If the vocabulary should reach the app too

Not required to ship — the listing is consistent on its own, and a store reviewer is given the
app's real labels. This is what would have to change for a user to stop meeting the word *rider*
one tap after reading the description, in rough order of visibility:

| What | Where |
|---|---|
| `"Share your location while you ride?"` | `BlazorDLR.Shared/State/LocationBroadcastState.cs:326` — Play's required background-location wording is elsewhere in the same dialog and **must not** be touched; re-record the declaration video if this line changes |
| `Ride members live` (nav label) | `BlazorDLR.Shared/Layout/NavMenu.razor:80` |
| `Blocked riders` (page title, nav) | `BlazorDLR.Shared/Pages/Settings/Blocks.razor:6,9`, `Settings.razor:38` |
| `Group rides`, `Ride thread`, `My rides` | nav menu and page titles across `BlazorDLR.Shared/Pages/` |
| Route and URL segments (`/group-rides/…`) | changing these breaks saved links and every navigation path in the review notes — leave them |

The API contracts (`GroupRide`, `RideDetail`, `RideComment`) are wire types, not user-visible text,
and renaming them would be a breaking change for no listing benefit.
