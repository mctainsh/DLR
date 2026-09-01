using DLR.Server.Positions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DLR.Server.Tests.Positions;

/// <summary>
/// The lifetime counter's drain (§14.6), against a counter that does not need a database.
/// <para>
/// Since v0.33 this timer is <em>accounting</em> and not durability: a position never leaves
/// <see cref="RiderPositionCache"/> (§5.5), so what is on the tick is each rider's fix count. What
/// is tested here is the bookkeeping - when a drain happens, and what becomes of the counts when
/// the write fails.
/// </para>
/// </summary>
public sealed class PositionCounterFlushTests
{
	private static readonly DateTimeOffset Noon = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// A quiet server must be quiet. Otherwise the idle cost of live tracking is a database round
	/// trip every ten seconds, on a €4 VPS, forever.
	/// </summary>
	[Fact]
	public async Task Flush_NothingCounted_IssuesNoDatabaseCall()
	{
		(PositionCounterFlushService flush, _, IPositionCounter counter) = Build();

		await flush.FlushAsync(CancellationToken.None);

		await counter.DidNotReceiveWithAnyArgs()
			.CountAsync(Arg.Any<IReadOnlyDictionary<Guid, long>>(), Arg.Any<CancellationToken>());
	}

	/// <summary>
	/// One command regardless of rider count - the whole reason the drain is affordable. Five
	/// hundred riders is the §5.5 sizing figure.
	/// </summary>
	[Fact]
	public async Task Flush_ManyRiders_IssuesExactlyOneCommand()
	{
		(PositionCounterFlushService flush, PositionActivityMeter meter, IPositionCounter counter) = Build();

		for (int rider = 0; rider < 500; rider++)
		{
			meter.Record(Guid.NewGuid());
		}

		await flush.FlushAsync(CancellationToken.None);

		await counter.Received(1).CountAsync(
			Arg.Is<IReadOnlyDictionary<Guid, long>>(counts => counts != null && counts.Count == 500),
			Arg.Any<CancellationToken>());
	}

	/// <summary>
	/// The counts have no second copy anywhere - unlike a position, which the cache still holds -
	/// so a deploy without this drops every fix banked since the last tick.
	/// </summary>
	[Fact]
	public async Task Shutdown_BanksPendingCounts()
	{
		(PositionCounterFlushService flush, PositionActivityMeter meter, IPositionCounter counter) = Build();

		meter.Record(Guid.NewGuid());

		await flush.StartAsync(CancellationToken.None);
		await flush.StopAsync(CancellationToken.None);

		await counter.Received(1).CountAsync(
			Arg.Is<IReadOnlyDictionary<Guid, long>>(counts => counts != null && counts.Count == 1),
			Arg.Any<CancellationToken>());
	}

	/// <summary>
	/// A failed write must put the counts back. They left the meter when the drain began, so
	/// dropping them here is the one way a rider's lifetime total silently loses fixes.
	/// </summary>
	[Fact]
	public async Task Flush_WhenTheWriteFails_ReturnsTheCountsToTheMeter()
	{
		(PositionCounterFlushService flush, PositionActivityMeter meter, IPositionCounter counter) = Build();

		counter
			.CountAsync(Arg.Any<IReadOnlyDictionary<Guid, long>>(), Arg.Any<CancellationToken>())
			.ThrowsAsync(new InvalidOperationException("the database is away"));

		Guid rider = Guid.NewGuid();

		meter.Record(rider);

		await Should.NotThrowAsync(() => flush.FlushAsync(CancellationToken.None));

		meter.DrainRiderCounts().ShouldContainKey(rider,
			"a drain that could not be written has to leave the counts where the next one finds them");
	}

	/// <summary>
	/// The timer is the project clock's, so this advances rather than sleeps (§10.4).
	/// <para>
	/// Waits on a signal the counter raises, and <em>keeps advancing</em> until it arrives.
	/// <c>StartAsync</c> returns as soon as <c>ExecuteAsync</c> reaches its first await, which is
	/// not necessarily after the <see cref="PeriodicTimer"/> has been constructed - so a single
	/// advance can land before anything is listening and then wait ten seconds of fake time that
	/// never elapse. Two earlier shapes of this test both failed roughly one run in four.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Flush_OnItsTimer_RunsWithoutAnybodyCallingIt()
	{
		FakeTimeProvider clock = new(Noon);

		TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);

		(PositionCounterFlushService flush, PositionActivityMeter meter, IPositionCounter counter) =
			Build(clock);

		counter
			.CountAsync(Arg.Any<IReadOnlyDictionary<Guid, long>>(), Arg.Any<CancellationToken>())
			.Returns(_ =>
			{
				written.TrySetResult();

				return Task.CompletedTask;
			});

		await flush.StartAsync(CancellationToken.None);

		meter.Record(Guid.NewGuid());

		for (int tick = 0; tick < 100 && !written.Task.IsCompleted; tick++)
		{
			clock.Advance(TimeSpan.FromSeconds(10));

			await Task.WhenAny(written.Task, Task.Delay(20));
		}

		written.Task.IsCompletedSuccessfully.ShouldBeTrue(
			"the service has to drain on its own timer - every other test in this class calls " +
			"FlushAsync directly, so without this one the timer loop could be deleted outright");

		await flush.StopAsync(CancellationToken.None);
	}

	private static (PositionCounterFlushService Flush, PositionActivityMeter Meter, IPositionCounter Counter) Build(
		FakeTimeProvider? clock = null)
	{
		clock ??= new FakeTimeProvider(Noon);

		IPositionCounter counter = Substitute.For<IPositionCounter>();

		counter
			.CountAsync(Arg.Any<IReadOnlyDictionary<Guid, long>>(), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		// A real container rather than a mocked scope factory: the service resolving its counter
		// through a scope is the arrangement that stops it capturing one, and a mock of the scope
		// factory would not exercise it.
		ServiceCollection services = new();

		services.AddSingleton(counter);

		ServiceProvider provider = services.BuildServiceProvider();

		PositionActivityMeter meter = new(clock);

		PositionCounterFlushService flush = new(
			meter,
			provider.GetRequiredService<IServiceScopeFactory>(),
			clock,
			Options.Create(new RideOptions()),
			NullLogger<PositionCounterFlushService>.Instance);

		return (flush, meter, counter);
	}
}
