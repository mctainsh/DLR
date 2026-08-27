using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The seam between the fix pump and the network (§4.2, §4.3).
/// <para>
/// Worth testing on its own because the two rules it enforces are both about what happens when the
/// network is slow, which is the state a phone on a ride spends real time in and the state that is
/// hardest to reproduce by riding: that a superseded position is never sent, and that a fix caught
/// in it when the rider crosses into their private area never leaves.
/// </para>
/// </summary>
public sealed class PositionOutboxTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static LocationFix Fix(int secondsIn) =>
		new(-33.868, 151.209, 5, null, null, Start.AddSeconds(secondsIn));

	[Fact]
	public async Task ASupersededPosition_IsNeverSent()
	{
		// The whole reason this is a slot and not a queue. A sender that is behind used to work
		// through the backlog in order, spending a round trip each on positions the ride had
		// already been overtaken by — which is what made one stalled send take minutes to unwind
		// rather than seconds.
		using PositionOutbox outbox = new();

		outbox.Post(Fix(1));
		outbox.Post(Fix(2));
		outbox.Post(Fix(3));

		OutboxBatch batch = await outbox.TakeAsync();

		batch.Fix!.RecordedUtc.ShouldBe(Start.AddSeconds(3),
			"the newest fix is the only one anybody wants.");

		outbox.Complete();
		(await outbox.TakeAsync()).IsEmpty.ShouldBeTrue(
			"and the two it replaced are gone rather than queued behind it.");
	}

	[Fact]
	public async Task CrossingIntoThePrivateArea_DropsAPositionStillWaitingToGo()
	{
		// §10.1. Sending inline got this for free — the fix had already gone by the time the
		// crossing was noticed — and a slot has to say it out loud. A position read outside the
		// circle, delivered a moment after the rider is inside it, is the leak the feature exists
		// to stop.
		using PositionOutbox outbox = new();

		outbox.Post(Fix(1));
		outbox.PostPrivacy(isPrivate: true);

		OutboxBatch batch = await outbox.TakeAsync();

		batch.Privacy.ShouldBe(true);
		batch.Fix.ShouldBeNull("nothing may accompany a rider going private.");
	}

	[Fact]
	public async Task ComingOutOfThePrivateArea_KeepsThePositionThatFollowsIt()
	{
		// The other direction, and it is not symmetrical: a fix waiting behind a "no longer
		// private" is exactly what should put the rider's pin back.
		using PositionOutbox outbox = new();

		outbox.PostPrivacy(isPrivate: false);
		outbox.Post(Fix(1));

		OutboxBatch batch = await outbox.TakeAsync();

		batch.Privacy.ShouldBe(false);
		batch.Fix!.RecordedUtc.ShouldBe(Start.AddSeconds(1));
	}

	[Fact]
	public async Task WithNothingToSend_ItWaits()
	{
		// The sender must not spin. An outbox with empty slots is the steady state of a rider
		// sitting at a café, and a loop that returned immediately from it would burn a battery
		// doing nothing at all.
		using PositionOutbox outbox = new();

		ValueTask<OutboxBatch> waiting = outbox.TakeAsync();

		waiting.IsCompleted.ShouldBeFalse();

		outbox.Post(Fix(1));

		(await waiting).Fix.ShouldNotBeNull();
	}

	[Fact]
	public async Task Completing_EndsTheSendersLoop_AfterWhatIsAlreadyInIt()
	{
		// What the pump calls when the receiver stops. The last thing posted before a stop is
		// usually the crossing that takes a rider off the map, so it goes before the loop ends.
		using PositionOutbox outbox = new();

		outbox.PostPrivacy(isPrivate: true);
		outbox.Complete();

		(await outbox.TakeAsync()).Privacy.ShouldBe(true);
		(await outbox.TakeAsync()).IsEmpty.ShouldBeTrue();
	}

	[Fact]
	public async Task Completing_WakesASenderThatIsAlreadyWaiting()
	{
		using PositionOutbox outbox = new();

		ValueTask<OutboxBatch> waiting = outbox.TakeAsync();

		outbox.Complete();

		(await waiting).IsEmpty.ShouldBeTrue();
	}

	[Fact]
	public async Task PostingAfterCompletion_IsIgnored()
	{
		// The receiver has already released the platform watch by the time this could happen. A
		// late fix reaching the ride from a stopped GPS would be a position nothing is maintaining.
		using PositionOutbox outbox = new();

		outbox.Complete();
		outbox.Post(Fix(1));

		(await outbox.TakeAsync()).IsEmpty.ShouldBeTrue();
	}
}
