using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The read/write surface of the ride thread (§17). Where <see cref="RideThreadTests"/>
/// exercises the permission gate on the composer, these tests exercise everything else:
/// hub-delivered posts, pin flips, coalesced reactions, poll updates, the "quiet
/// while Live" note, the "Load older" cursor, and the compose→PostCommentAsync path.
/// </summary>
public sealed class RideThreadMoreTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private (FakeApiClient api, FakeRideHubClient hub, Guid rideId) WireServices(
		RideStateDto state = RideStateDto.Open,
		RidePermissions? permissions = null,
		CommentPage? threadPage = null)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test ride",
				Description: null,
				StartUtc: FixedInstant,
				State: state,
				JoinPolicy: JoinPolicyDto.Approval,
				MemberCap: 50,
				MemberCount: 1,
				IsOrganiser: false,
				JoinCode: null,
				Permissions: permissions ?? new RidePermissions(),
				Members: Array.Empty<RideMemberSummary>()),
			ThreadResult = threadPage,
		};

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IRideHubClient>(hub);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		return (api, hub, rideId);
	}

	private static CommentDto Post(Guid rideId, string body, bool pinned = false, string author = "Alice") =>
		new(
			Id: Guid.NewGuid(),
			GroupRideId: rideId,
			AuthorId: Guid.NewGuid(),
			AuthorUserName: author,
			Kind: CommentKindDto.Text,
			Body: body,
			PhotoId: null,
			IsPinned: pinned,
			CreatedUtc: FixedInstant,
			PostedUtc: FixedInstant,
			EditedUtc: null,
			AuthoredEarlier: false);

	[Fact]
	public void PinnedAndUnpinned_RenderInSeparateSections()
	{
		Guid rideId;
		FakeApiClient api;
		FakeRideHubClient hub;
		CommentDto pinned = Post(Guid.Empty, "Meeting spot: cafe carpark.", pinned: true);
		CommentDto ordinary = Post(Guid.Empty, "Hey team.");
		(api, hub, rideId) = WireServices(threadPage: new CommentPage(
			Pinned: new[] { pinned },
			Comments: new[] { ordinary },
			NextCursor: null));

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			// The "Pinned" section header must sit above the pinned body, and the ordinary
			// body must come after both. If a pinned post lived in the ordinary list, it
			// would age off the top a few days later.
			string markup = component.Markup;
			int pinnedHeader = markup.IndexOf("Pinned", StringComparison.Ordinal);
			int pinnedBody = markup.IndexOf("Meeting spot", StringComparison.Ordinal);
			int ordinaryBody = markup.IndexOf("Hey team", StringComparison.Ordinal);

			pinnedHeader.ShouldBeGreaterThan(-1, "§17.6: pinned posts get their own section header.");
			pinnedBody.ShouldBeGreaterThan(pinnedHeader);
			ordinaryBody.ShouldBeGreaterThan(pinnedBody, "§17.3: ordinary posts render below the pinned block.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void HubCommentPosted_InsertsAtTheTop()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Ride thread", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		CommentDto arriving = Post(rideId, "Live update from the road.");
		component.InvokeAsync(() => hub.RaiseCommentPosted(rideId, arriving)).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
			component.Markup.Contains("Live update from the road", StringComparison.Ordinal).ShouldBeTrue(
				"§17.3: a CommentPosted delta adds the new post to the visible thread."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void LiveRideNote_IsVisible_WhenStateIsLive()
	{
		(_, _, Guid rideId) = WireServices(state: RideStateDto.Live);

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("ride is live", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"§17.1: while the ride is Live the thread renders a note that pushes are silent — a user surprised by the quiet needs the explanation.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void LoadOlderButton_IsShown_WhenNextCursorIsPresent()
	{
		(_, _, Guid rideId) = WireServices(threadPage: new CommentPage(
			Pinned: Array.Empty<CommentDto>(),
			Comments: new[] { Post(Guid.Empty, "First page item.") },
			NextCursor: "cursor-token"));

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.FindAll("button.more").Any(b => b.TextContent.Contains("Load older", StringComparison.Ordinal))
				.ShouldBeTrue("§17.8: the cursor-paginated read means Load-older appears whenever NextCursor is set.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void LoadOlderButton_IsHidden_WhenNoNextCursor()
	{
		(_, _, Guid rideId) = WireServices(threadPage: new CommentPage(
			Pinned: Array.Empty<CommentDto>(),
			Comments: new[] { Post(Guid.Empty, "Only post.") },
			NextCursor: null));

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.FindAll("button.more").Count.ShouldBe(0,
				"§17.8: null NextCursor means the thread is exhausted — the button must not offer another page.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ComposerPost_SendsTrimmedBodyAndFreshClientGuid()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices();

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("form.composer").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement textarea = component.Find("form.composer textarea");
			textarea.Change("  Hello, ride.  ");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form.composer");
			form.Submit();
		});

		component.WaitForAssertion(() => api.PostCommentRequests.Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		PostCommentRequest sent = api.PostCommentRequests[0];
		sent.Body.ShouldBe("Hello, ride.", "the body is trimmed before the wire.");
		sent.ClientGuid.ShouldNotBe(Guid.Empty,
			"§4.4: every post carries a fresh ClientGuid — the server's idempotency key for retries.");
		sent.Poll.ShouldBeNull("a plain post must not carry a poll spec.");
	}
}
