using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// What the red bubble on the rail's thread item is counting (§17.6, §18.6).
/// <para>
/// Every rule here fails silently in one direction or the other, which is why they are asserted
/// rather than eyeballed: a badge that counts too much tells a rider about their own typing, and
/// one that counts too little is indistinguishable from a quiet group until they open the thread
/// and find six posts.
/// </para>
/// </summary>
public sealed class UnreadThreadStateTests
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static readonly Guid Me = Guid.NewGuid();
	private static readonly Guid SomebodyElse = Guid.NewGuid();
	private static readonly Guid TheRide = Guid.NewGuid();
	private static readonly Guid AnotherRide = Guid.NewGuid();

	private sealed record Harness(
		UnreadThreadState Unread,
		FakeRideHubClient Hub,
		InMemoryDeviceSettings Settings);

	/// <summary>
	/// Counting over a signed-in rider. Signed in matters: a rider's own post is recognised by user
	/// id, and an anonymous <c>AuthState</c> would make every post somebody else's.
	/// </summary>
	private static async Task<Harness> BuildAsync(InMemoryDeviceSettings? settings = null)
	{
		FakeApiClient api = new();
		AuthState auth = new(api, new FakeTokenStore(), new FakeTimeProvider(FixedInstant));

		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(Me, "DaveSmith", HasEmail: true, EmailConfirmed: true)));

		FakeRideHubClient hub = new();
		settings ??= new InMemoryDeviceSettings();

		return new Harness(new UnreadThreadState(hub, auth, settings), hub, settings);
	}

	private static CommentDto Post(Guid? rideId, Guid authorId, Guid? trackId = null) =>
		new(
			Id: Guid.NewGuid(),
			GroupRideId: rideId,
			TrackId: trackId,
			AuthorId: authorId,
			AuthorUserName: authorId == Me ? "DaveSmith" : "SarahJones",
			Kind: CommentKindDto.Text,
			Body: "Fuel at the servo in 8 km.",
			PhotoId: null,
			IsPinned: false,
			CreatedUtc: FixedInstant,
			PostedUtc: FixedInstant,
			EditedUtc: null,
			AuthoredEarlier: false);

	[Fact]
	public async Task EachPostFromSomebodyElse_AddsOneToThatAdventuresCount()
	{
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));
		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		harness.Unread.CountFor(TheRide).ShouldBe(2);
	}

	[Fact]
	public async Task ARidersOwnPost_IsNotUnread()
	{
		// It comes straight back down the hub it was published on, so without this the badge
		// counts the rider's own half of the conversation.
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(TheRide, Me));

		harness.Unread.CountFor(TheRide).ShouldBe(0);
	}

	[Fact]
	public async Task APostToASharedRoutesThread_IsNotCounted()
	{
		// A route's thread (§6.2) is not on the rail, so there is nothing for its count to appear
		// on — and adding it to the adventure's would be a badge for a conversation the tap does
		// not open.
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(rideId: null, SomebodyElse, trackId: Guid.NewGuid()));

		harness.Unread.CountFor(TheRide).ShouldBe(0);
	}

	[Fact]
	public async Task CountsAreKeptPerAdventure()
	{
		// A rider can be live in more than one ride (§5.7), and the rail points at one of them.
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));
		harness.Hub.RaiseCommentPosted(Post(AnotherRide, SomebodyElse));
		harness.Hub.RaiseCommentPosted(Post(AnotherRide, SomebodyElse));

		harness.Unread.CountFor(TheRide).ShouldBe(1);
		harness.Unread.CountFor(AnotherRide).ShouldBe(2);
	}

	[Fact]
	public async Task OpeningTheThread_ClearsIt_AndPostsLandingWhileItIsOpenDoNotStartItAgain()
	{
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));
		await harness.Unread.OpenedAsync(TheRide);

		harness.Unread.CountFor(TheRide).ShouldBe(0);

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		harness.Unread.CountFor(TheRide).ShouldBe(0,
			"the rider is looking at the post as it arrives — a badge for a message on screen is a "
			+ "badge that has to be dismissed by reading something already read.");
	}

	[Fact]
	public async Task OpeningOneThread_DoesNotSilenceAnother()
	{
		Harness harness = await BuildAsync();

		await harness.Unread.OpenedAsync(TheRide);
		harness.Hub.RaiseCommentPosted(Post(AnotherRide, SomebodyElse));

		harness.Unread.CountFor(AnotherRide).ShouldBe(1);
	}

	[Fact]
	public async Task LeavingTheThread_StartsCountingAgain()
	{
		Harness harness = await BuildAsync();

		await harness.Unread.OpenedAsync(TheRide);
		harness.Unread.Closed(TheRide);

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		harness.Unread.CountFor(TheRide).ShouldBe(1);
	}

	[Fact]
	public async Task CountsSurviveARestart()
	{
		// The case they are persisted for: an app the OS reclaimed mid-ride comes back with the
		// posts the rider had not read still unread.
		InMemoryDeviceSettings settings = new();
		Harness first = await BuildAsync(settings);

		// As the rail does on first render, and before any post: a count is only written once the
		// device has been read.
		await first.Unread.LoadAsync();

		first.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));
		first.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		// The hub callback has nobody to await it, so the write is fire-and-forget — give it the
		// turn of the loop it needs before reading the store through a second instance.
		await Task.Yield();

		Harness relaunched = await BuildAsync(settings);
		await relaunched.Unread.LoadAsync();

		relaunched.Unread.CountFor(TheRide).ShouldBe(2);
	}

	[Fact]
	public async Task ReadingTheThread_IsRememberedAcrossARestart()
	{
		InMemoryDeviceSettings settings = new();
		Harness first = await BuildAsync(settings);

		first.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));
		await first.Unread.OpenedAsync(TheRide);

		Harness relaunched = await BuildAsync(settings);
		await relaunched.Unread.LoadAsync();

		relaunched.Unread.CountFor(TheRide).ShouldBe(0);
	}

	[Fact]
	public async Task PostsArrivingBeforeTheStoreIsRead_AreAddedToWhatItHeld()
	{
		// The rail loads after first render, and the hub is already delivering by then. Replacing
		// rather than adding would lose whichever of the two arrived first.
		InMemoryDeviceSettings settings = new();
		await settings.SetAsync(UnreadThreadState.StorageKey, $"1|{TheRide:N}:2");

		Harness harness = await BuildAsync(settings);
		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await harness.Unread.LoadAsync();

		harness.Unread.CountFor(TheRide).ShouldBe(3);
	}

	[Fact]
	public async Task AStoredValueInAFormatThisVersionDoesNotKnow_ReadsAsNothingUnread()
	{
		InMemoryDeviceSettings settings = new();
		await settings.SetAsync(UnreadThreadState.StorageKey, "not a count");

		Harness harness = await BuildAsync(settings);
		await harness.Unread.LoadAsync();

		harness.Unread.CountFor(TheRide).ShouldBe(0);
	}

	[Fact]
	public async Task OnlyTheMostRecentlyTalkedInAdventuresAreKept()
	{
		// The cap is what stops a year of rides accumulating in a device store nothing sweeps. The
		// one nobody has posted to for longest is the one that goes.
		Harness harness = await BuildAsync();

		List<Guid> rides = [];

		for (int i = 0; i <= UnreadThreadState.MaxTracked; i++)
		{
			Guid rideId = Guid.NewGuid();
			rides.Add(rideId);
			harness.Hub.RaiseCommentPosted(Post(rideId, SomebodyElse));
		}

		harness.Unread.CountFor(rides[0]).ShouldBe(0, "the oldest fell off the end.");
		harness.Unread.CountFor(rides[^1]).ShouldBe(1, "the newest is still counted.");
	}

	[Fact]
	public async Task ADisposedStateStopsCounting()
	{
		// It holds a hub subscription for as long as the app runs; a second one left behind by a
		// torn-down scope would double every count.
		Harness harness = await BuildAsync();

		harness.Unread.Dispose();
		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		harness.Unread.CountFor(TheRide).ShouldBe(0);
	}
}
