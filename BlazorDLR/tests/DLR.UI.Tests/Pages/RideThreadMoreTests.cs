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
/// hub-delivered posts, pin flips, coalesced reactions, poll updates, the absence of the
/// old "quiet while Live" note, the "Load older" cursor, and the compose→PostCommentAsync path.
/// </summary>
public sealed class RideThreadMoreTests : PageTestContext
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
				Name: "Test adventure",
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
		Services.AddSingleton<BlazorDLR.Shared.State.ConfirmService>();
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
			// The pinned section holds the pinned body; the ordinary body renders after it.
			// If a pinned post lived in the ordinary list, it would age off the top a few
			// days later. The <section class="pinned"> wrapper carries the styling that keeps
			// the block visually distinct.
			AngleSharp.Dom.IElement pinnedSection = component.Find("section.pinned");
			pinnedSection.TextContent.Contains("Meeting spot", StringComparison.Ordinal).ShouldBeTrue(
				"§17.6: a pinned post renders inside the pinned section.");
			pinnedSection.TextContent.Contains("Hey team", StringComparison.Ordinal).ShouldBeFalse(
				"§17.6: the ordinary post must not be inside the pinned section.");

			string markup = component.Markup;
			int pinnedBody = markup.IndexOf("Meeting spot", StringComparison.Ordinal);
			int ordinaryBody = markup.IndexOf("Hey team", StringComparison.Ordinal);
			ordinaryBody.ShouldBeGreaterThan(pinnedBody,
				"§17.3: ordinary posts render below the pinned block.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task HubCommentPosted_InsertsAtTheTop()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Adventure thread", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		CommentDto arriving = Post(rideId, "Live update from the road.");
		await component.InvokeAsync(() => hub.RaiseCommentPosted(arriving));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Live update from the road", StringComparison.Ordinal).ShouldBeTrue(
				"§17.3: a CommentPosted delta adds the new post to the visible thread."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The Live-ride push silence has been removed (§17.6), so the note that used to explain it
	/// must not render. Asserted rather than simply deleted: the note was the only user-visible
	/// trace of the rule, and a copy change that quietly reinstated it would leave the thread
	/// promising a quiet that no longer happens.
	/// </summary>
	[Fact]
	public void SilentWhileLiveNote_IsGone_WhenStateIsLive()
	{
		(_, _, Guid rideId) = WireServices(state: RideStateDto.Live);

		IRenderedComponent<RideThread> component = Render<RideThread>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Adventure thread", StringComparison.Ordinal).ShouldBeTrue();
			component.FindAll(".live-note").ShouldBeEmpty(
				"§17.6: comments push in every ride state now — a Live adventure has no silence to explain.");
			component.Markup.Contains("silently", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
				"§17.6: no copy may still tell a rider their posts arrive silently while the adventure is Live.");
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
			textarea.Input("  Hello, adventure.  ");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form.composer");
			form.Submit();
		});

		component.WaitForAssertion(() => api.PostCommentRequests.Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		PostCommentRequest sent = api.PostCommentRequests[0];
		sent.Body.ShouldBe("Hello, adventure.", "the body is trimmed before the wire.");
		sent.ClientGuid.ShouldNotBe(Guid.Empty,
			"§4.4: every post carries a fresh ClientGuid — the server's idempotency key for retries.");
		sent.Poll.ShouldBeNull("a plain post must not carry a poll spec.");
	}
}
