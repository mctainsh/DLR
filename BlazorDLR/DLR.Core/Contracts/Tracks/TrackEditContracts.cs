namespace DLR.Core.Contracts.Tracks;

/// <summary>
/// <c>POST /api/v1/tracks/{id}/edit</c> (§15.5).
/// </summary>
/// <param name="Version">
/// Optimistic concurrency. Two browser tabs editing one track is the realistic case, and
/// silently applying stale indices would cut the wrong span.
/// </param>
/// <param name="Removals">
/// Half-open ranges of <strong>raw</strong> point indices, ascending and disjoint. Raw is the
/// load-bearing word: the map draws a simplified line, and "delete the 412th point I am
/// displaying" would delete a different point on the server — plausibly hundreds of metres
/// away, invisibly, and only on tracks dense enough for simplification to have done anything.
/// </param>
public sealed record EditTrackRequest(int Version, IReadOnlyList<IndexRange> Removals);

/// <summary>A half-open span of raw point indices, <c>[From, To)</c>.</summary>
/// <param name="From">First index removed.</param>
/// <param name="To">First index kept after the span.</param>
public readonly record struct IndexRange(int From, int To);

/// <summary>
/// What an edit or an undo produced (§15.5).
/// <para>
/// Everything derived is recomputed rather than adjusted, so this is the whole set of numbers
/// again rather than a delta. Incrementally patching stats — subtracting the removed span's
/// distance — is how a track ends up with figures that no longer describe it after the third
/// edit.
/// </para>
/// </summary>
/// <param name="Track">The track as it now stands.</param>
/// <param name="UndoAvailableUntilUtc">
/// When the retained original stops being restorable (§15.6), or null if there is nothing to
/// undo. The removed points are gone from the app, from exports and from every share link at
/// once — but nightly backups keep a copy until they roll out of retention, and the UI says so
/// at the moment of the trim rather than later.
/// </param>
public sealed record TrackEditResponse(TrackSummary Track, DateTimeOffset? UndoAvailableUntilUtc);
