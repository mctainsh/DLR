using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// What a post arriving on the hub does to the phone in a rider's pocket (§17.6).
/// <para>
/// This is the whole of the notification decision, and it is worth asserting rather than
/// eyeballing because every one of these rules fails <em>silently</em> in one direction or the
/// other. A notifier that is too quiet looks exactly like a working app on a quiet ride; a notifier
/// that is too loud tells a rider about their own posts at 100 km/h. Neither shows up in a build.
/// </para>
/// <para>
/// The platform halves are deliberately not here — <c>UiLayeringRules</c> bars every MAUI assembly
/// from this project, and <c>UNUserNotificationCenter</c> has to be proven on a device anyway. What
/// a compiler cannot check and a device should not have to is <em>which</em> posts get that far.
/// </para>
/// </summary>
public sealed class CommentNotifierTests
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static readonly Guid Me = Guid.NewGuid();
	private static readonly Guid SomebodyElse = Guid.NewGuid();
	private static readonly Guid TheRide = Guid.NewGuid();
	private static readonly Guid AnotherRide = Guid.NewGuid();

	private sealed record Harness(
		CommentNotifier Notifier,
		FakeRideHubClient Hub,
		FakeNotificationService Notifications,
		AppForegroundState App);

	/// <summary>
	/// A notifier over a signed-in rider. Signed in matters: <see cref="CommentNotifier.ShouldNotify"/>
	/// recognises a rider's own post by user id, and an anonymous <c>AuthState</c> would make every
	/// post somebody else's.
	/// </summary>
	private static async Task<Harness> BuildAsync()
	{
		FakeApiClient api = new();
		AuthState auth = new(api, new FakeTokenStore(), new FakeTimeProvider(FixedInstant));

		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(Me, "DaveSmith", HasEmail: true, EmailConfirmed: true)));

		FakeRideHubClient hub = new();
		FakeNotificationService notifications = new();
		AppForegroundState app = new();

		return new Harness(new CommentNotifier(hub, notifications, auth, app), hub, notifications, app);
	}

	private static CommentDto Post(
		Guid rideId,
		Guid authorId,
		string? body = "Fuel at the servo in 8 km.",
		CommentKindDto kind = CommentKindDto.Text,
		Guid? photoId = null) =>
		new(
			Id: Guid.NewGuid(),
			GroupRideId: rideId,
			AuthorId: authorId,
			AuthorUserName: authorId == Me ? "DaveSmith" : "SarahJones",
			Kind: kind,
			Body: body,
			PhotoId: photoId,
			IsPinned: false,
			CreatedUtc: FixedInstant,
			PostedUtc: FixedInstant,
			EditedUtc: null,
			AuthoredEarlier: false);

	// ---------- What interrupts a rider ----------

	[Fact]
	public async Task APostFromSomebodyElse_RaisesANotification()
	{
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await harness.Notifications.Shown.ShouldEventuallyContainOne(
			"§17.6: since v0.26 removed the Live silence, an ordinary post notifies in every ride state — " +
			"there is no ride state left that changes this.");

		harness.Notifications.Shown[0].Title.ShouldBe("SarahJones",
			"the author is the title: a hub post carries the handle and only the ride's *id*, and naming the " +
			"adventure would cost a round trip per notification for a line the rider can already infer.");
		harness.Notifications.Shown[0].Body.ShouldBe("Fuel at the servo in 8 km.");
	}

	[Fact]
	public async Task ARidersOwnPost_NeverNotifiesThem()
	{
		Harness harness = await BuildAsync();

		harness.Hub.RaiseCommentPosted(Post(TheRide, Me));

		harness.Notifier.ShouldNotify(Post(TheRide, Me)).ShouldBeFalse(
			"every post a rider makes comes straight back down the hub they published it on, so without " +
			"this the app would notify you about everything you said.");
		harness.Notifications.Shown.ShouldBeEmpty();
	}

	[Fact]
	public async Task NoRideStateIsConsulted_BecauseThereIsNoLongerOneToConsult()
	{
		Harness harness = await BuildAsync();

		// The notifier is handed no ride state at all — it has no constructor parameter for one and
		// no way to ask. That is the v0.26 reversal expressed as a shape rather than as a branch
		// somebody could re-add: there is nothing here for a "but during a ride…" condition to hang
		// off. The assertion is that a post notifies with the notifier knowing nothing about the ride.
		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await harness.Notifications.Shown.ShouldEventuallyContainOne(
			"§17.6: the Live row of the push table now reads Push, and the notifier cannot tell one " +
			"ride state from another.");
	}

	// ---------- What does not ----------

	[Fact]
	public async Task APostInTheThreadTheRiderIsReading_IsNotAlsoBuzzedAtThem()
	{
		Harness harness = await BuildAsync();
		harness.Notifier.ThreadOpened(TheRide);

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		// Delayed rather than asserted immediately: showing is fire-and-forget, so an assertion that
		// ran straight away would pass even if suppression were broken. The window has to be wide
		// enough that a notification which *was* going to appear has appeared.
		await Task.Delay(50);

		harness.Notifications.Shown.ShouldBeEmpty(
			"the post is about to render in front of them — notifying somebody about a message on the " +
			"screen they are looking at is the fastest way to teach them to ignore notifications.");
	}

	[Fact]
	public async Task APostInADifferentAdventure_StillInterrupts()
	{
		Harness harness = await BuildAsync();
		harness.Notifier.ThreadOpened(TheRide);

		harness.Hub.RaiseCommentPosted(Post(AnotherRide, SomebodyElse));

		await harness.Notifications.Shown.ShouldEventuallyContainOne(
			"§5.7: a rider can be on several adventures at once, and reading one thread is not a reason " +
			"to go quiet about another.");
	}

	[Fact]
	public async Task OpeningAThread_WithdrawsTheCardAlreadyShowingForIt()
	{
		Harness harness = await BuildAsync();

		harness.Notifier.ThreadOpened(TheRide);

		harness.Notifications.Cancelled.ShouldContain(CommentNotifier.TagFor(TheRide),
			"a rider reading the conversation does not also need a card about it sitting in the shade.");
	}

	[Fact]
	public async Task LeavingAThread_MakesItsPostsInterruptAgain()
	{
		Harness harness = await BuildAsync();
		harness.Notifier.ThreadOpened(TheRide);
		harness.Notifier.ThreadClosed(TheRide);

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await harness.Notifications.Shown.ShouldEventuallyContainOne();
	}

	/// <summary>
	/// Blazor initialises the incoming page before disposing the outgoing one. A
	/// <c>ThreadClosed</c> that ignored which thread it was closing would let the first page's
	/// teardown clear the second page's claim — and the rider would then be notified about the
	/// thread they are looking at.
	/// </summary>
	[Fact]
	public async Task SteppingFromOneThreadStraightToAnother_LeavesTheNewOneClaimed()
	{
		Harness harness = await BuildAsync();

		harness.Notifier.ThreadOpened(TheRide);
		harness.Notifier.ThreadOpened(AnotherRide);   // the incoming page initialises…
		harness.Notifier.ThreadClosed(TheRide);       // …then the outgoing one tears down

		harness.Notifier.ShouldNotify(Post(AnotherRide, SomebodyElse)).ShouldBeFalse(
			"the thread on screen is the second one, and the first page's disposal must not have " +
			"un-claimed it.");
	}

	/// <summary>
	/// The one that turns the whole feature back into the silence v0.26 removed if it is missing:
	/// a rider opens the thread at a set of lights, locks the phone and rides on. The page stays
	/// mounted for the rest of the ride.
	/// </summary>
	[Fact]
	public async Task AThreadLeftOpenOnABackgroundedPhone_DoesNotSuppressAnything()
	{
		Harness harness = await BuildAsync();
		harness.Notifier.ThreadOpened(TheRide);

		harness.App.Set(false);

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await harness.Notifications.Shown.ShouldEventuallyContainOne(
			"a mounted page on a phone in a tank bag is not a rider reading a thread — suppressing on " +
			"the strength of it would be the Live silence coming back through a side door.");
	}

	// ---------- Permission, and hosts with no notifier ----------

	[Fact]
	public async Task ARiderWhoRefusedThePermission_IsNotNotified()
	{
		Harness harness = await BuildAsync();
		harness.Notifications.PermissionGranted = false;

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await Task.Delay(50);

		harness.Notifications.Shown.ShouldBeEmpty(
			"a refusal is a rider saying they do not want to be interrupted, which is exactly the choice " +
			"§17.6 now leaves to them — not an error to work around.");
		harness.Notifications.PermissionRequests.ShouldBeGreaterThan(0);
	}

	[Fact]
	public async Task AHostThatCannotNotify_IsNeverEvenAsked()
	{
		Harness harness = await BuildAsync();
		harness.Notifications.IsSupported = false;

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await Task.Delay(50);

		harness.Notifications.PermissionRequests.ShouldBe(0,
			"§18.2: the browsers raise nothing, so prompting there would be a dialog for a capability " +
			"that does not exist.");
		harness.Notifications.Shown.ShouldBeEmpty();
	}

	[Fact]
	public async Task ADisposedNotifier_StopsListening()
	{
		Harness harness = await BuildAsync();
		harness.Notifier.Dispose();

		harness.Hub.RaiseCommentPosted(Post(TheRide, SomebodyElse));

		await Task.Delay(50);

		harness.Notifications.Shown.ShouldBeEmpty();
	}

	// ---------- What the lock screen actually says ----------

	[Fact]
	public void APostWithNoTextButAPhoto_StillSaysSomething()
	{
		LocalNotification notification = CommentNotifier.Compose(
			Post(TheRide, SomebodyElse, body: null, photoId: Guid.NewGuid()));

		notification.Body.ShouldBe("Sent a photo.",
			"§17.2 makes a photo-only post legitimate — and an empty notification body is a bug that " +
			"looks like one.");
	}

	[Fact]
	public void APoll_SaysThatItIsOne()
	{
		CommentNotifier.Compose(Post(TheRide, SomebodyElse, body: null, kind: CommentKindDto.Poll))
			.Body.ShouldBe("Started a poll.");

		CommentNotifier.Compose(Post(TheRide, SomebodyElse, body: "Which route?", kind: CommentKindDto.Poll))
			.Body.ShouldBe("Poll: Which route?",
				"§17.5: the question is the body, and a lock screen showing it bare would read as an " +
				"ordinary comment.");
	}

	[Fact]
	public void ALongPost_IsCutToSomethingALockScreenCanHold()
	{
		string essay = new('x', 4000);

		string body = CommentNotifier.Compose(Post(TheRide, SomebodyElse, body: essay)).Body;

		body.Length.ShouldBeLessThanOrEqualTo(CommentNotifier.MaxBodyChars,
			"the ellipsis replaces the last character rather than being appended — a payload that grows " +
			"past the cap it promised is how a cap stops being one.");
		body.ShouldEndWith("…");
	}

	[Fact]
	public void AMultiLinePost_ArrivesAsOneLine()
	{
		string body = CommentNotifier.Compose(
			Post(TheRide, SomebodyElse, body: "Running late.\n\nStart without me.\r\nSee you at the pub.")).Body;

		body.ShouldBe("Running late. Start without me. See you at the pub.",
			"both platforms render newlines, and a three-line post would push everything else off the card.");
	}

	[Fact]
	public void EveryPostInOneAdventure_SharesATagSoTheNewestReplacesTheLast()
	{
		LocalNotification first = CommentNotifier.Compose(Post(TheRide, SomebodyElse, "One."));
		LocalNotification second = CommentNotifier.Compose(Post(TheRide, SomebodyElse, "Two."));
		LocalNotification elsewhere = CommentNotifier.Compose(Post(AnotherRide, SomebodyElse, "Three."));

		second.Tag.ShouldBe(first.Tag,
			"twenty entries in a shade is not twenty times as useful as one — it is a wall the rider has " +
			"to dismiss at the next set of lights.");
		elsewhere.Tag.ShouldNotBe(first.Tag,
			"but two adventures are two conversations, and collapsing them would hide one behind the other.");
	}

	[Fact]
	public void ANotification_CarriesTheRouteToTheThreadItIsAbout()
	{
		LocalNotification notification = CommentNotifier.Compose(Post(TheRide, SomebodyElse));

		notification.Route.ShouldBe($"group-rides/{TheRide:D}/thread",
			"a notification that only opens the home screen is a dead end: the rider was told there is " +
			"something to read, and making them find it defeats the point of telling them.");
	}
}

/// <summary>
/// The notifier raises notifications from a fire-and-forget continuation — its caller is a hub
/// callback with nowhere to await — so a test that asserts immediately races the post it is
/// checking for. This polls briefly instead of sleeping a fixed amount, which keeps a passing run
/// fast and a failing one honest.
/// </summary>
internal static class NotificationAssertions
{
	public static async Task ShouldEventuallyContainOne(
		this List<LocalNotification> shown,
		string? because = null)
	{
		for (int attempt = 0; attempt < 50 && shown.Count == 0; attempt++)
		{
			await Task.Delay(10);
		}

		shown.Count.ShouldBe(1, because);
	}
}
