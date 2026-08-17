using DLR.Server.Positions;
using Microsoft.Extensions.Time.Testing;

namespace DLR.Server.Tests.Positions;

/// <summary>
/// The write-behind cache's own rules (§5.5). No database — these are about the data structure.
/// </summary>
public sealed class RiderPositionCacheTests
{
	private static readonly Guid Ride = Guid.NewGuid();
	private static readonly Guid Rider = Guid.NewGuid();
	private static readonly DateTimeOffset Noon = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Upsert_NewRider_AddsEntryMarkedDirty()
	{
		RiderPositionCache cache = New();

		cache.Upsert(Ride, Rider, Entry(Noon)).ShouldBeTrue();

		PositionEntry held = cache.ForRide(Ride)[Rider];

		held.RecordedUtc.ShouldBe(Noon);
		held.IsDirty.ShouldBeTrue("nothing has written it yet");

		cache.Dirty().ShouldHaveSingleItem().UserId.ShouldBe(Rider);
	}

	/// <summary>
	/// Batches retry and connections reorder. A rider whose pin jumps backwards is worse than one
	/// whose pin is briefly stale, so the comparison is on the fix's own timestamp rather than on
	/// arrival order.
	/// </summary>
	[Fact]
	public void Upsert_OlderTimestamp_IsIgnored()
	{
		RiderPositionCache cache = New();

		cache.Upsert(Ride, Rider, Entry(Noon, lat: 100));

		cache.Upsert(Ride, Rider, Entry(Noon.AddSeconds(-5), lat: 999)).ShouldBeFalse();

		cache.ForRide(Ride)[Rider].Lat.ShouldBe(100);
	}

	/// <summary>An identical timestamp is not newer, so a duplicate delivery changes nothing.</summary>
	[Fact]
	public void Upsert_SameTimestamp_IsIgnored()
	{
		RiderPositionCache cache = New();

		cache.Upsert(Ride, Rider, Entry(Noon, lat: 100));

		cache.Upsert(Ride, Rider, Entry(Noon, lat: 999)).ShouldBeFalse();

		cache.ForRide(Ride)[Rider].Lat.ShouldBe(100);
	}

	/// <summary>
	/// The compare-and-swap in <c>Upsert</c>. One rider's client retrying while a newer fix lands
	/// genuinely contends, and a plain write would let the loser overwrite the winner.
	/// </summary>
	[Fact]
	public async Task Upsert_UnderParallelLoad_LatestTimestampWins()
	{
		RiderPositionCache cache = New();

		const int Count = 500;

		// Shuffled, so "the last one written" and "the newest one" are different orders.
		List<int> order = [.. Enumerable.Range(0, Count).OrderBy(_ => Guid.NewGuid())];

		await Parallel.ForEachAsync(order, (index, _) =>
		{
			cache.Upsert(Ride, Rider, Entry(Noon.AddSeconds(index), lat: index));

			return ValueTask.CompletedTask;
		});

		PositionEntry held = cache.ForRide(Ride)[Rider];

		held.RecordedUtc.ShouldBe(Noon.AddSeconds(Count - 1));
		held.Lat.ShouldBe(Count - 1);
	}

	[Fact]
	public void MarkClean_AfterANewerFixArrived_LeavesItDirty()
	{
		RiderPositionCache cache = New();

		PositionEntry first = Entry(Noon);

		cache.Upsert(Ride, Rider, first);

		DirtyPosition written = cache.Dirty().ShouldHaveSingleItem();

		// The write is in flight, and the rider moves.
		cache.Upsert(Ride, Rider, Entry(Noon.AddSeconds(5), lat: 700));

		cache.MarkClean(written);

		cache.Dirty().ShouldHaveSingleItem()
			.Entry.Lat.ShouldBe(700, "the newer fix must not be dropped by the older one's receipt");
	}

	[Fact]
	public void Remove_DropsOneRiderAndLeavesTheRest()
	{
		RiderPositionCache cache = New();

		Guid other = Guid.NewGuid();

		cache.Upsert(Ride, Rider, Entry(Noon));
		cache.Upsert(Ride, other, Entry(Noon));

		cache.Remove(Ride, Rider);

		cache.ForRide(Ride).Keys.ShouldBe([other]);

		cache.RemoveRide(Ride);

		cache.ForRide(Ride).ShouldBeEmpty();
	}

	/// <summary>
	/// The §5.5 gate. Kestrel can begin serving before custom hosted services have run, so a read
	/// really can arrive against a half-warm cache — and would answer "nobody is on this adventure"
	/// with total confidence.
	/// </summary>
	[Fact]
	public async Task Reads_BlockUntilRehydrationComplete()
	{
		RiderPositionCache cache = New();

		Task ready = cache.ReadyAsync();

		ready.IsCompleted.ShouldBeFalse("nothing has rehydrated it yet");

		cache.MarkReady();

		await ready.WaitAsync(TimeSpan.FromSeconds(5));

		ready.IsCompletedSuccessfully.ShouldBeTrue();

		// Idempotent: a rehydrator that runs twice, or fails and is retried, must not throw.
		Should.NotThrow(cache.MarkReady);
	}

	private static RiderPositionCache New() => new(new FakeTimeProvider(Noon));

	private static PositionEntry Entry(DateTimeOffset at, int lat = 1) =>
		new(lat, 2, null, null, null, at, IsDirty: true);
}
