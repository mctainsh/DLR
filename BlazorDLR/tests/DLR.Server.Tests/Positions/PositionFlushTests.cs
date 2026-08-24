using DLR.Server.Positions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DLR.Server.Tests.Positions;

/// <summary>
/// The 10 s write-behind (§5.5), against a writer that does not need a database.
/// <para>
/// What is being tested here is the <em>bookkeeping</em>: which entries go, what happens to their
/// dirty flags afterwards, and what happens when the write fails. Whether the SQL itself is
/// correct is <c>PositionPersistenceTests</c>' job.
/// </para>
/// </summary>
public sealed class PositionFlushTests
{
	private static readonly DateTimeOffset Noon = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task Flush_WritesOnlyDirtyEntries()
	{
		(PositionFlushService flush, RiderPositionCache cache, IPositionWriter writer) = Build();

		Guid ride = Guid.NewGuid();
		Guid dirty = Guid.NewGuid();
		Guid clean = Guid.NewGuid();

		cache.Upsert(ride, clean, Entry(Noon, isDirty: false));
		cache.Upsert(ride, dirty, Entry(Noon));

		await flush.FlushAsync(CancellationToken.None);

		await writer.Received(1).WriteAsync(
			Arg.Is<IReadOnlyList<DirtyPosition>>(batch =>
				batch != null && batch.Count == 1 && batch[0].UserId == dirty),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Flush_ClearsDirtyFlagsAfterSuccess()
	{
		(PositionFlushService flush, RiderPositionCache cache, _) = Build();

		cache.Upsert(Guid.NewGuid(), Guid.NewGuid(), Entry(Noon));

		await flush.FlushAsync(CancellationToken.None);

		cache.Dirty().ShouldBeEmpty();
	}

	/// <summary>
	/// Marking them clean on a failed write would silently discard exactly the positions that
	/// failed to persist. The upsert's <c>WHERE</c> guard is what makes retrying them safe.
	/// </summary>
	[Fact]
	public async Task Flush_LeavesEntriesDirtyWhenWriteFails()
	{
		(PositionFlushService flush, RiderPositionCache cache, IPositionWriter writer) = Build();

		writer
			.WriteAsync(Arg.Any<IReadOnlyList<DirtyPosition>>(), Arg.Any<CancellationToken>())
			.ThrowsAsync(new InvalidOperationException("the database went away"));

		cache.Upsert(Guid.NewGuid(), Guid.NewGuid(), Entry(Noon));

		await Should.NotThrowAsync(() => flush.FlushAsync(CancellationToken.None));

		cache.Dirty().ShouldHaveSingleItem();

		// And the next tick retries them rather than needing a restart.
		writer.ClearReceivedCalls();
		writer
			.WriteAsync(Arg.Any<IReadOnlyList<DirtyPosition>>(), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		await flush.FlushAsync(CancellationToken.None);

		cache.Dirty().ShouldBeEmpty();
	}

	/// <summary>
	/// A quiet server must be quiet. Otherwise the idle cost of live tracking is a database round
	/// trip every ten seconds, on a €4 VPS, forever.
	/// </summary>
	[Fact]
	public async Task Flush_NoDirtyEntries_IssuesNoDatabaseCall()
	{
		(PositionFlushService flush, _, IPositionWriter writer) = Build();

		await flush.FlushAsync(CancellationToken.None);

		await writer.DidNotReceiveWithAnyArgs()
			.WriteAsync(Arg.Any<IReadOnlyList<DirtyPosition>>(), Arg.Any<CancellationToken>());
	}

	/// <summary>
	/// One command regardless of rider count — the whole reason the flush is affordable. Five
	/// hundred riders is the §5.5 sizing figure.
	/// </summary>
	[Fact]
	public async Task Flush_ManyRiders_IssuesExactlyOneCommand()
	{
		(PositionFlushService flush, RiderPositionCache cache, IPositionWriter writer) = Build();

		Guid ride = Guid.NewGuid();

		for (int rider = 0; rider < 500; rider++)
		{
			cache.Upsert(ride, Guid.NewGuid(), Entry(Noon));
		}

		await flush.FlushAsync(CancellationToken.None);

		await writer.Received(1).WriteAsync(
			Arg.Is<IReadOnlyList<DirtyPosition>>(batch => batch != null && batch.Count == 500),
			Arg.Any<CancellationToken>());
	}

	/// <summary>
	/// A graceful shutdown loses nothing (§5.5) — which is only true because of this. Without it
	/// every deploy discards up to a flush period of movement for every rider.
	/// </summary>
	[Fact]
	public async Task Shutdown_FlushesPendingEntries()
	{
		(PositionFlushService flush, RiderPositionCache cache, IPositionWriter writer) = Build();

		cache.Upsert(Guid.NewGuid(), Guid.NewGuid(), Entry(Noon));

		await flush.StartAsync(CancellationToken.None);
		await flush.StopAsync(CancellationToken.None);

		await writer.Received(1).WriteAsync(
			Arg.Is<IReadOnlyList<DirtyPosition>>(batch => batch != null && batch.Count == 1),
			Arg.Any<CancellationToken>());

		cache.Dirty().ShouldBeEmpty();
	}

	/// <summary>
	/// The timer is the project clock's, so this advances rather than sleeps (§10.4).
	/// <para>
	/// Waits on a signal the writer raises, and <em>keeps advancing</em> until it arrives.
	/// <c>StartAsync</c> returns as soon as <c>ExecuteAsync</c> reaches its first await, which is
	/// not necessarily after the <see cref="PeriodicTimer"/> has been constructed — so a single
	/// advance can land before anything is listening and then wait ten seconds of fake time that
	/// never elapse. Two earlier shapes of this test (spin-on-yield, then advance-once-and-wait)
	/// both failed roughly one run in four.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Flush_OnItsTimer_RunsWithoutAnybodyCallingIt()
	{
		FakeTimeProvider clock = new(Noon);

		TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);

		(PositionFlushService flush, RiderPositionCache cache, IPositionWriter writer) = Build(clock);

		writer
			.WriteAsync(Arg.Any<IReadOnlyList<DirtyPosition>>(), Arg.Any<CancellationToken>())
			.Returns(_ =>
			{
				written.TrySetResult();

				return Task.CompletedTask;
			});

		await flush.StartAsync(CancellationToken.None);

		cache.Upsert(Guid.NewGuid(), Guid.NewGuid(), Entry(Noon));

		for (int tick = 0; tick < 100 && !written.Task.IsCompleted; tick++)
		{
			clock.Advance(TimeSpan.FromSeconds(10));

			await Task.WhenAny(written.Task, Task.Delay(20));
		}

		written.Task.IsCompletedSuccessfully.ShouldBeTrue(
			"the service has to flush on its own timer — every other test in this class calls " +
			"FlushAsync directly, so without this one the timer loop could be deleted outright");

		await flush.StopAsync(CancellationToken.None);
	}

	private static (PositionFlushService Flush, RiderPositionCache Cache, IPositionWriter Writer) Build(
		FakeTimeProvider? clock = null)
	{
		clock ??= new FakeTimeProvider(Noon);

		IPositionWriter writer = Substitute.For<IPositionWriter>();

		writer
			.WriteAsync(Arg.Any<IReadOnlyList<DirtyPosition>>(), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		// A real container rather than a mocked scope factory: the flush service resolving its
		// writer through a scope is the arrangement that stops it capturing one, and a mock of
		// the scope factory would not exercise it.
		ServiceCollection services = new();

		services.AddSingleton(writer);

		ServiceProvider provider = services.BuildServiceProvider();

		RiderPositionCache cache = new(clock);

		PositionFlushService flush = new(
			cache,
			new PositionActivityMeter(clock),
			provider.GetRequiredService<IServiceScopeFactory>(),
			clock,
			Options.Create(new RideOptions()),
			NullLogger<PositionFlushService>.Instance);

		return (flush, cache, writer);
	}

	private static PositionEntry Entry(DateTimeOffset at, bool isDirty = true) =>
		new(1, 2, null, null, null, at, isDirty);
}
