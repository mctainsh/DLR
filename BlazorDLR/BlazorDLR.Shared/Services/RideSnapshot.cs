using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Everything the live map needs to draw one ride, as this device last saw it (§4.4, §5.3).
/// <para>
/// <strong>What a relaunch in a dead zone gets back.</strong> A rider whose phone was reclaimed
/// mid-ride — a flat battery, an OS that wanted the memory, a WebView reload — comes back with no
/// signal and, without this, a page that can only say the request failed. With it the ride opens
/// on the same members, the same markers and the same planned routes it had when the network went
/// away, over the camera <see cref="LiveMapView"/> already remembered.
/// </para>
/// <para>
/// <strong>The four things are stored together because they are read together.</strong>
/// <c>RideSession.LoadAsync</c> fetches the ride, its positions, its markers and its routes as one
/// act, and a screen holding three of the four has nothing useful to show. One file per ride also
/// means one write, and forgetting a ride is one delete.
/// </para>
/// <para>
/// <strong>It is a cache, not a second source of truth.</strong> Nothing is ever <em>authored</em>
/// here — every field arrived from the server, and the moment the server answers again its copy
/// wins outright. There is no merge, because there is nothing local to merge with: §4.4's outbox
/// is what a write made offline would need, and this is deliberately only the read half.
/// </para>
/// </summary>
/// <param name="Version">
/// The payload's shape. A snapshot written by an older build is discarded rather than migrated —
/// see <see cref="RideSnapshotCache.CurrentVersion"/>.
/// </param>
/// <param name="CachedUtc">
/// When the server last confirmed all of this. What the live map reports to the rider, because
/// "these were the positions" is only meaningful with "at this time" attached to it.
/// </param>
/// <param name="Ride">
/// The ride itself, including its member list — who is on it, their role, their colour and whether
/// they were sharing. The map draws every rider's label and colour off the member row rather than
/// off the fix (§16.3), so without this the pins would come back nameless.
/// </param>
/// <param name="Markers">Every marker on the ride (§16.5).</param>
/// <param name="Routes">
/// The planned routes, encoded (§15.5). The largest thing here by a wide margin, and the reason
/// this goes to <see cref="IOfflineStore"/> rather than the preference store.
/// </param>
/// <param name="Positions">
/// Where each sharing member was when the connection was last good. Kept, though it is the field
/// that goes stale fastest and the one a rider must not mistake for live — which is what
/// <paramref name="CachedUtc"/> on screen is for. Dropping it would open the ride on an empty map,
/// and "where everyone was twenty minutes ago" is the single most useful thing to know when you
/// have lost signal and the group has not.
/// </param>
public sealed record RideSnapshot(
	int Version,
	DateTimeOffset CachedUtc,
	RideDetail Ride,
	IReadOnlyList<MarkerDto> Markers,
	IReadOnlyList<RideRoute> Routes,
	IReadOnlyList<RiderPositionDto> Positions);
