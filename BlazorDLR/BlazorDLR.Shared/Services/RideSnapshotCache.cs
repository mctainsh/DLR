using System.Text.Json;
using System.Text.Json.Serialization;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Keeps one <see cref="RideSnapshot"/> per ride on this device, so a ride opens without a network
/// (§4.4, §7.9).
/// <para>
/// A thin thing on purpose: JSON in, JSON out, one entry per ride in <see cref="IOfflineStore"/>.
/// The decision about <em>when</em> to fall back to a copy belongs to <c>RideSession</c>, which is
/// the only place that can tell "the server says this ride is gone" apart from "the server could
/// not be reached" - and the two must never be confused, because the first is a reason to forget a
/// ride and the second is a rider in a tunnel (§5.2).
/// </para>
/// <para>
/// <strong>Nothing here throws.</strong> Every failure - a device with no store, a half-written
/// file, a payload from a build that shaped the DTOs differently - reads back as <c>null</c>,
/// meaning "you have no copy", and a write that cannot land is dropped. A cache is an optimisation
/// over the network, and an optimisation that can fail a screen is worse than not having it.
/// </para>
/// </summary>
public sealed class RideSnapshotCache
{
	/// <summary>
	/// The shape of the payload. Bumping this discards every stored snapshot on every device,
	/// which is the correct migration for a cache: the data is one round trip away from being
	/// refetched, and a decoder that tries to be clever about an older layout is a decoder that
	/// can be wrong about which fields it got.
	/// <para>
	/// It has to move whenever the wire types inside a snapshot change shape in a way that would
	/// deserialise into something misleading rather than into nothing - a field that changed
	/// meaning rather than one that was added.
	/// </para>
	/// </summary>
	public const int CurrentVersion = 1;

	/// <summary>
	/// The <see cref="IOfflineStore"/> name prefix, so a ride's entry is recognisable in the
	/// app's data directory and cannot collide with whatever else is stored there later.
	/// </summary>
	private const string NamePrefix = "ride-";

	/// <summary>
	/// The same pipeline <see cref="HttpApiClient"/> parses the server's responses with - web
	/// defaults, nulls omitted. Deliberately identical: what goes in here came off that wire and
	/// comes back out to the same screens, and two JSON configurations for one set of DTOs is how
	/// a cached ride ends up subtly different from a fetched one.
	/// </summary>
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly IOfflineStore _store;
	private readonly TimeProvider _clock;

	/// <summary>Creates the cache over a host's offline store.</summary>
	/// <param name="store">Where entries land. The browser hosts bind one that stores nothing (§18.6).</param>
	/// <param name="clock">Stamps <see cref="RideSnapshot.CachedUtc"/>. Never <c>DateTimeOffset.UtcNow</c> (§10.4).</param>
	public RideSnapshotCache(IOfflineStore store, TimeProvider clock)
	{
		_store = store;
		_clock = clock;
	}

	/// <summary>
	/// Whether this device keeps snapshots at all - false on the browser hosts, where
	/// <see cref="ReadAsync"/> always answers <c>null</c> because nothing was ever written
	/// (§18.6).
	/// </summary>
	public bool IsSupported => _store.IsSupported;

	/// <summary>
	/// The stored copy of <paramref name="rideId"/>, or <c>null</c> when this device has none -
	/// a ride never opened here, one that has been forgotten, or a payload this build cannot read.
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task<RideSnapshot?> ReadAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		string? stored = await _store.ReadAsync(NameFor(rideId), cancellationToken);

		if (string.IsNullOrEmpty(stored))
		{
			return null;
		}

		try
		{
			RideSnapshot? snapshot = JsonSerializer.Deserialize<RideSnapshot>(stored, Json);

			// The version check and the identity check are the same kind of guard: a snapshot that
			// is not this shape, or that describes a different ride, is not one this caller can be
			// handed. Both answer "no copy" rather than throwing - see the type's remarks.
			return snapshot is { Version: CurrentVersion } && snapshot.Ride.Id == rideId
				? snapshot
				: null;
		}
		catch (JsonException)
		{
			// A truncated or foreign payload. Nothing to recover and nothing to say about it: the
			// caller goes to the network exactly as it would on a device that had stored nothing.
			return null;
		}
	}

	/// <summary>
	/// Replaces this device's copy of a ride with what the server just said.
	/// <para>
	/// Called after a load that fully succeeded, so a snapshot is never a mixture of a fresh ride
	/// and stale markers - a partial write would be a copy that looks whole and is not.
	/// </para>
	/// </summary>
	/// <param name="ride">The ride and its member list.</param>
	/// <param name="markers">Every marker on it.</param>
	/// <param name="routes">Its planned routes, encoded.</param>
	/// <param name="positions">Where each sharing member was.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task WriteAsync(
		RideDetail ride,
		IReadOnlyList<MarkerDto> markers,
		IReadOnlyList<RideRoute> routes,
		IReadOnlyList<RiderPositionDto> positions,
		CancellationToken cancellationToken = default)
	{
		if (!_store.IsSupported)
		{
			// Nothing would be kept, so nothing is serialised. The routes are the expensive part
			// to encode and a browser would be paying for it on every load of every ride.
			return;
		}

		RideSnapshot snapshot = new(
			CurrentVersion,
			_clock.GetUtcNow(),
			ride,
			markers,
			routes,
			positions);

		try
		{
			await _store.WriteAsync(NameFor(ride.Id), JsonSerializer.Serialize(snapshot, Json), cancellationToken);
		}
		catch (NotSupportedException)
		{
			// A DTO the serialiser cannot write. It would be a bug rather than a device condition,
			// but it is still not worth failing a ride that has already loaded successfully over.
		}
	}

	/// <summary>
	/// Drops this device's copy of a ride. What a ride that is gone calls - deleted, or one the
	/// rider has left or been removed from (§5.2) - so a forgotten ride does not come back from
	/// the cache the next time somebody types its id.
	/// </summary>
	/// <param name="rideId">The ride to forget.</param>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ForgetAsync(Guid rideId, CancellationToken cancellationToken = default) =>
		await _store.RemoveAsync(NameFor(rideId), cancellationToken);

	/// <summary>
	/// A ride's entry name. <c>"N"</c> format, so it is the plain-slug shape
	/// <see cref="IOfflineStore.ReadAsync"/> requires - a hyphenated GUID would still pass, but
	/// this keeps the name a single token.
	/// </summary>
	private static string NameFor(Guid rideId) => NamePrefix + rideId.ToString("N");
}
