# Marker icons

Raster marker icons, one PNG per icon key (§16.2). They live in the shared RCL's `wwwroot`, so
all three hosts - MAUI, the WASM client and the server's SSR pass - get the same bytes from one
place, at:

```
_content/BlazorDLR.Shared/markers/{key}.png
```

The filename **is** the icon key, so a lookup is string concatenation and a key this version has
never seen resolves to a missing file rather than to the wrong picture.

## The set

| File                 | Key              | Label          | Drawn as                                  |
| -------------------- | ---------------- | -------------- | ----------------------------------------- |
| `hazard.png`         | `hazard`         | Hazard         | Amber warning triangle                    |
| `gravel.png`         | `gravel`         | Gravel         | Scattered stones                          |
| `water-crossing.png` | `water-crossing` | Water crossing | Road running under a blue water band      |
| `gate.png`           | `gate`           | Gate           | Braced farm gate                          |
| `turn.png`           | `turn`           | Turn           | Curved arrow                              |
| `fire.png`           | `fire`           | Fire           | Flame                                     |
| `mushroom.png`       | `mushroom`       | Mushroom       | Spotted red cap                           |
| `sheep.png`          | `sheep`          | Sheep          | Fleece, dark face                         |
| `bear.png`           | `bear`           | Bear           | Bear head                                 |
| `snake.png`          | `snake`          | Snake          | Tapered S-body, forked tongue             |
| `kangaroo.png`       | `kangaroo`       | Kangaroo       | Road-sign silhouette                      |
| `crocodile.png`      | `crocodile`      | Crocodile      | Top-down, serrated tail                   |
| `crash.png`          | `crash`          | Crash          | Red impact burst                          |
| `regroup.png`        | `regroup`        | Regroup        | Three figures huddled                     |
| `stopped.png`        | `stopped`        | Stopped        | Red octagon                               |
| `start.png`          | `start`          | Start          | Green flag                                |
| `finish.png`         | `finish`         | Finish         | Chequered flag                            |
| `fuel.png`           | `fuel`           | Fuel           | Fuel pump                                 |
| `food.png`           | `food`           | Food           | Burger                                    |
| `coffee.png`         | `coffee`         | Coffee         | Cup and saucer                            |
| `water.png`          | `water`          | Drinking water | Glass of water                            |
| `toilet.png`         | `toilet`         | Toilet         | Two white figures on a blue badge         |
| `camping.png`        | `camping`        | Camping        | Tent                                      |
| `parking.png`        | `parking`        | Parking        | White P on a blue badge                   |
| `viewpoint.png`      | `viewpoint`      | Viewpoint      | Mountains and sun                         |
| `photo.png`          | `photo`          | Photo          | Camera                                    |
| `repair.png`         | `repair`         | Repair         | Spanner - C jaw one end, ring the other   |
| `medical.png`        | `medical`        | Medical        | White cross on a red badge                |
| `note.png`           | `note`           | Note           | Lined page - the fallback                 |

Keys and labels are owned by `BlazorDLR.Shared/Markers/MarkerIconGlyphs.cs`, and membership by
`DLR.Core/Markers/MarkerIcons.Known`. Nothing here should disagree with either.

Adding an icon means all of it in one change: the PNG here, the key in `MarkerIcons.Known`, the
label in `MarkerIconGlyphs.Curated`, and the `FromSymbol`/`ToSymbol` entries so GPX round trips
name it rather than passing it through as a raw key. Three tests hold that together, so a
half-finished addition fails the build rather than shipping:

| Test | Catches |
| ---- | ------- |
| `MarkerIconAssetRules.EveryKnownKey_HasArtwork` | a key with no PNG - offered in the composer, then drawn as the plain-pin fallback forever |
| `MarkerIconAssetRules.EveryPieceOfArtwork_HasAKnownKey` | a PNG nobody can select, shipped in every app bundle |
| `AddMarkerTests.EveryCuratedIcon_HasItsOwnArtwork_AndUnknownKeysDegrade` | a key in `Known` with no label, or one quietly resolving to the note icon |

## Spec

Every icon in this folder is:

- **48 x 48 px**, transparent background.
- Outlined twice, **1 px each**: black immediately around the artwork, white immediately outside
  the black. The white ring is what keeps the icon readable against dark satellite tiles and dark
  mode; the black ring is what keeps it readable against pale ones. The artwork is inset far
  enough that both rings stay inside the 48 px box.
- Flat colour, no gradients or shadows - these are drawn onto the Skia map overlay at map scale,
  where anything softer than a hard edge turns to mush.

The outlines are generated by dilating the union of all opaque shapes twice, not by stroking each
path, which is why a multi-part icon like `gravel` gets one halo around each stone instead of a
seam where two stones touch.

Cut-outs fall out of the same trick - dilating the silhouette erodes its holes, so the inside of
a hole reads transparent → white → black → fill exactly like the outer edge. The catch is that
**a hole narrower than about 6 px closes up completely**, having 2 px eaten from each side. That
is the single hardest constraint at this size and it is why `gate` is a two-bar gate rather than
the five-bar one it wants to be. Check new icons at 1x, not zoomed.

A badge sidesteps the hole rule entirely, which is worth remembering when a glyph refuses to fit.
Artwork inside a filled badge is an interior colour change, not part of the silhouette, so no halo
eats into it: `toilet` keeps a 1.2px gap between a figure's legs, where the free-standing version
of the same figures needed 6px and ended up as splayed stumps. `parking`, `medical` and `toilet`
all use the same 40px rounded square for that reason.

Two traps worth knowing before editing an icon, both of which have already shipped once:

- **Union every solid before subtracting any hole.** `repair` cut each spanner head's hole first
  and unioned the shaft afterwards, so the shaft filled both holes straight back in and the
  wrench had two solid ends.
- **Build wide strokes as one polygon.** A stroke stitched from per-segment quads leaves pinholes
  at the joints, and because the fill is painted *over* the black ring, every pinhole shows as a
  dark speck strung along the curve.

## How they reach the map

`SkiaMapOverlay.razor` draws these straight onto the Skia canvas, with nothing behind them - the
two outlines are what a backing disc would otherwise do, and they follow the artwork's edge rather
than boxing it into a circle.

Getting the pixels there takes one hop. SkiaSharp may not open a PNG in this assembly: decoding
is confined to the photo-ingest path (§16.4, `ImageRules`), and that rule is what keeps EXIF
stripping structural. So `map/markers.js` decodes each icon once through the host's own 2D canvas
and hands back raw RGBA, which `MarkerIconCache` holds and the overlay wraps with
`SKImage.FromPixelCopy` - a pixel copy, not a decode. One fetch per distinct icon per session.

Until an icon arrives the overlay draws a plain pin, so a marker is never missing from the map
while its artwork loads, and a host that cannot rasterise at all keeps working.
