using DLR.Server.Positions;
using Microsoft.Extensions.Time.Testing;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// The in-memory counter behind the lifetime GPS total and the per-minute graph (§14.6).
/// <para>
/// No database and no host: this is the one piece of the administration feature that is pure
/// arithmetic over a clock, and the two things it has to get right - that a drained count is not
/// counted twice, and that a slot from yesterday reads as zero rather than as today's traffic -
/// are both invisible from an endpoint test.
/// </para>
/// </summary>
public sealed class PositionActivityMeterTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void RiderCounts_AccumulateUntilDrained_AndTheDrainClearsThem()
	{
		FakeTimeProvider clock = new(Start);
		PositionActivityMeter meter = new(clock);

		Guid dave = Guid.NewGuid();
		Guid sarah = Guid.NewGuid();

		meter.Record(dave);
		meter.Record(dave);
		meter.Record(sarah);

		IReadOnlyDictionary<Guid, long> first = meter.DrainRiderCounts();

		first[dave].ShouldBe(2);
		first[sarah].ShouldBe(1);

		// The counts exist nowhere else once drained. Handing them out twice would double every
		// rider's lifetime total on every flush.
		meter.DrainRiderCounts().ShouldBeEmpty();
	}

	/// <summary>
	/// The flush puts the counts back when the write fails, because it has the only copy - and
	/// putting them back must add to whatever arrived in the meantime rather than replace it.
	/// </summary>
	[Fact]
	public void RestoringAfterAFailedWrite_AddsToWhatArrivedSince()
	{
		FakeTimeProvider clock = new(Start);
		PositionActivityMeter meter = new(clock);

		Guid dave = Guid.NewGuid();

		meter.Record(dave);
		meter.Record(dave);

		IReadOnlyDictionary<Guid, long> drained = meter.DrainRiderCounts();

		// A fix arrives while the failed write is still being unwound.
		meter.Record(dave);

		meter.Restore(drained);

		meter.DrainRiderCounts()[dave].ShouldBe(3,
			"the two that failed to persist and the one that arrived since are all still owed.");
	}

	[Fact]
	public void TheGraph_PutsEachFixInTheMinuteItArrived()
	{
		FakeTimeProvider clock = new(Start);
		PositionActivityMeter meter = new(clock);

		meter.Record(Guid.NewGuid());
		meter.Record(Guid.NewGuid());

		clock.Advance(TimeSpan.FromMinutes(1));
		meter.Record(Guid.NewGuid());

		IReadOnlyList<int> window = meter.PerMinute(out _);

		// The window ends at the minute in progress, so the newest minute is the last column and
		// the one before it is the one before that.
		window[^1].ShouldBe(1);
		window[^2].ShouldBe(2);
		window.Sum().ShouldBe(3);
	}

	/// <summary>
	/// The ring is a day wide and keyed by minute-of-epoch modulo its width, so the slot a fix
	/// landed in yesterday is the same slot as today's. The stamp beside it is what stops the
	/// graph reporting yesterday's traffic as this morning's.
	/// </summary>
	[Fact]
	public void AMinuteThatHasFallenOutOfTheWindow_ReadsAsZero()
	{
		FakeTimeProvider clock = new(Start);
		PositionActivityMeter meter = new(clock);

		meter.Record(Guid.NewGuid());

		meter.PerMinute(out _).Sum().ShouldBe(1);

		// Exactly one day on: the same slot in the ring, a different minute.
		clock.Advance(TimeSpan.FromMinutes(PositionActivityMeter.WindowMinutes));

		meter.PerMinute(out _).Sum().ShouldBe(0,
			"a slot whose stamp is a day old is not this minute's count.");
	}

	[Fact]
	public void TheWindow_IsAlwaysADayWide_AndStartsWhereItSaysItDoes()
	{
		FakeTimeProvider clock = new(Start);
		PositionActivityMeter meter = new(clock);

		IReadOnlyList<int> window = meter.PerMinute(out DateTimeOffset windowStart);

		window.Count.ShouldBe(PositionActivityMeter.WindowMinutes);

		// The first column is 1 439 minutes before the minute in progress - the graph's own axis
		// labels are drawn by adding hours to this, so an hour out here is an hour out on screen.
		windowStart.ShouldBe(
			Start.AddMinutes(-(PositionActivityMeter.WindowMinutes - 1)),
			TimeSpan.FromMinutes(1));
	}

	[Fact]
	public void StartedUtc_IsWhenTheProcessBeganCounting()
	{
		FakeTimeProvider clock = new(Start);
		PositionActivityMeter meter = new(clock);

		clock.Advance(TimeSpan.FromHours(3));

		// Fixed at construction, not read on demand: the screen uses it to say "anything before
		// this is missing rather than quiet", which only works if it does not move.
		meter.StartedUtc.ShouldBe(Start);
	}
}
