using System.Net;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in landing for group rides shows two entry points (Create, Join) and the
/// caller's rides split by role. What matters for the test is that both entry points remain
/// reachable — a landing that only offered "Join" would silently gate ride creation behind
/// rediscovery of the URL — and that the organised and joined sections render even when they
/// are empty.
/// <para>
/// The third list, "Waiting for approval", follows the opposite rule and the tests below say so:
/// it is absent when empty, its rows do not open a ride, and it must never carry a join code.
/// The last of those is the one worth a test — somebody waiting on an approval is not a member,
/// and the code is the credential for getting a third person in (§5.2).
/// </para>
/// </summary>
public sealed class GroupRidesLandingTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireServices()
	{
		FakeApiClient api = new();
		Services.AddSingleton<IApiClient>(api);

		// The delete on an organised row asks before it acts, through the app's own dialog
		// rather than window.confirm — so the page resolves this on every render, not only
		// when somebody presses the trash.
		Services.AddSingleton<ConfirmService>();

		// Leaving a ride from this list has to take the rail's globe with it when that is where it
		// was pointing (§18.6). The snapshot cache is the other half and comes from PageTestContext,
		// as does the IDeviceSettings this reads through.
		Services.AddScoped<CurrentRideState>();

		return api;
	}

	private static MyRides OneOfEach(Guid organised, Guid joined) => new(
		[new RideSummary(organised, "Mine to run", FixedInstant, RideStateDto.Open, IsOrganiser: true, MemberCount: 3, JoinCode: "AB3K9Z")],
		[new RideSummary(joined, "Somebody else's", FixedInstant, RideStateDto.Open, IsOrganiser: false, MemberCount: 5, JoinCode: "QW7T2M")],
		[]);

	/// <summary>A request the caller has made and nobody has answered (§5.2).</summary>
	private static WaitingRide Waiting(string name, TimeSpan agoWaited) => new(
		RideId: Guid.NewGuid(),
		RequestId: Guid.NewGuid(),
		Name: name,
		StartUtc: FixedInstant.AddDays(3),
		State: RideStateDto.Open,
		RequestedUtc: FixedInstant - agoWaited);

	private static IReadOnlyList<AngleSharp.Dom.IElement> TrashButtons(IRenderedComponent<GroupRides> component) =>
		[.. component.FindAll("button.danger")];

	[Fact]
	public void BothEntryPoints_AreRenderedAsCards()
	{
		WireServices();

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.FindAll("a[href='/group-rides/join']").ShouldNotBeEmpty(
			"§5.2: the Join card is one of the two entry points; the landing has no reason to exist without it.");
		component.FindAll("a[href='/group-rides/create']").ShouldNotBeEmpty(
			"§5.2: the Create card is the other entry — a would-be organiser starts here.");
	}

	[Fact]
	public void MyRides_RendersOrganisedAndJoinedSections()
	{
		WireServices();

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		// The two sections are always emitted, even empty, so the caller can see they are
		// empty rather than "not loaded".
		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Organised by you", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: adventures the caller runs render in their own section, where the join code sits.");
			component.Markup.Contains("Joined", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: adventures the caller was admitted to render separately from the ones they run.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// §5.2: the join code badge is on both sections. Once somebody has joined, the landing is
	/// the only place they can read the code back off — without it they cannot tell a friend how
	/// to follow along, which is the thing they want at the start line.
	/// </summary>
	[Fact]
	public void JoinedRide_ShowsItsJoinCode()
	{
		FakeApiClient api = WireServices();

		api.MyRidesResult = new(
			[new RideSummary(
				Guid.NewGuid(),
				"Mine to run",
				FixedInstant,
				RideStateDto.Open,
				IsOrganiser: true,
				MemberCount: 3,
				JoinCode: "AB3K9Z")],
			[new RideSummary(
				Guid.NewGuid(),
				"Somebody else's",
				FixedInstant,
				RideStateDto.Open,
				IsOrganiser: false,
				MemberCount: 5,
				JoinCode: "QW7T2M")],
			[]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("AB3K9Z", StringComparison.Ordinal).ShouldBeTrue(
				"the organiser has always seen the code for an adventure they run.");
			component.Markup.Contains("QW7T2M", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: a joined adventure shows its code too, the same as an organised one.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// -- Deleting an adventure (§5.2) ---------------------------------------------------------

	/// <summary>
	/// The trash is the organiser's, and only theirs. A member of somebody else's adventure has
	/// Leave, which takes their own position with them; deleting would take everybody's day.
	/// </summary>
	[Fact]
	public void Trash_IsOnOrganisedRidesOnly()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), Guid.NewGuid());

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(
			() => TrashButtons(component).Count.ShouldBe(1,
				"one organised adventure and one joined one means exactly one trash."),
			timeout: TimeSpan.FromSeconds(3));

		TrashButtons(component)[0].InnerHtml.ShouldContain("fa-trash");
		TrashButtons(component)[0].GetAttribute("aria-label").ShouldBe("Delete Mine to run",
			"the icon carries no words, so the row it belongs to has to be in the label.");
	}

	/// <summary>Nothing is deleted until the traveller has answered the dialog.</summary>
	[Fact]
	public async Task Trash_AsksBeforeItDeletes()
	{
		FakeApiClient api = WireServices();
		Guid mine = Guid.NewGuid();
		api.MyRidesResult = OneOfEach(mine, Guid.NewGuid());

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => TrashButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => TrashButtons(component)[0].Click());

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		confirm.Current!.Danger.ShouldBeTrue("it is irreversible, so the confirm button is the red one.");
		api.DeletedRides.ShouldBeEmpty("nothing may be deleted before the traveller has answered.");

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() => api.DeletedRides.ShouldBe([mine]), timeout: TimeSpan.FromSeconds(3));

		// The list is refetched rather than spliced locally — the server is what knows what is
		// left — so the row goes only because the second read no longer has it.
		component.WaitForAssertion(
			() => component.Markup.ShouldNotContain("Mine to run"),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>Answering no is answering no: the adventure stays and the row stays with it.</summary>
	[Fact]
	public async Task Trash_Cancelled_DeletesNothing()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), Guid.NewGuid());

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => TrashButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => TrashButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(false));

		api.DeletedRides.ShouldBeEmpty();
		component.Markup.ShouldContain("Mine to run", customMessage: "a cancelled delete leaves the row where it was.");
	}

	/// <summary>
	/// The refusal that actually happens: an adventure in progress. The server says why in words,
	/// and those words are what the organiser reads — the row stays exactly where it was.
	/// </summary>
	[Fact]
	public async Task Trash_WhenTheServerRefuses_SaysWhy_AndKeepsTheRow()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), Guid.NewGuid());
		api.DeleteRideException = new ApiException(new ApiError(
			HttpStatusCode.Conflict, "This adventure is in progress", []));

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => TrashButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => TrashButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(
			() => component.Find("p.error").TextContent.ShouldContain("This adventure is in progress"),
			timeout: TimeSpan.FromSeconds(3));

		component.Markup.ShouldContain("Mine to run", customMessage: "a refused delete deletes nothing, including from the list.");
		TrashButtons(component)[0].HasAttribute("disabled").ShouldBeFalse(
			"the row is pressable again — the organiser may want to end the adventure and try once more.");
	}

	// -- Waiting for approval (§5.2) ----------------------------------------------------------

	[Fact]
	public void NobodyWaiting_MeansNoSection()
	{
		WireServices();

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("Organised by you"),
			timeout: TimeSpan.FromSeconds(3));

		// Unlike the two sections above it, which name their own emptiness. Almost nobody ever has
		// a pending request, and a permanent "you are not waiting on anything" is a line every rider
		// reads forever so that a few can read it once.
		component.FindAll(".ride-section.waiting").ShouldBeEmpty();
		component.Markup.ShouldNotContain("Waiting for approval");
	}

	[Fact]
	public void ARequestNobodyHasAnswered_IsListedWithHowLongItHasBeenWaiting()
	{
		FakeApiClient api = WireServices();

		api.MyRidesResult = new([], [], [Waiting("The long way round", TimeSpan.FromDays(3))]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.ShouldContain("Waiting for approval");
			component.Markup.ShouldContain("The long way round");

			// The thing the list is really for: "3 d ago" is what tells somebody the organiser has
			// forgotten about them, where a bare date leaves them doing the arithmetic.
			component.Find(".ride.pending .asked").TextContent.Trim().ShouldBe("Asked 3 d ago");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The one that matters. A pending requester is not a member, and the join code is the
	/// credential for getting a third person into the adventure — so it must not be on a row for a
	/// ride nobody has admitted them to. <c>WaitingRide</c> has no field to put it in, and this is
	/// the test that says the page never grows one.
	/// </summary>
	[Fact]
	public void AWaitingRow_CarriesNoJoinCodeAndDoesNotOpenTheRide()
	{
		FakeApiClient api = WireServices();

		api.MyRidesResult = new([], [], [Waiting("The long way round", TimeSpan.FromHours(2))]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(
			() => component.FindAll(".ride.pending").ShouldHaveSingleItem(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".ride.pending .join-code").ShouldBeEmpty(
			"the code gets somebody else in — it is a member's, and this caller is not one yet");

		// Nor a way in for themselves: the detail endpoint answers a non-member the same 404 a
		// stranger gets, so a link here would be a control that leads nowhere.
		component.FindAll(".ride.pending a").ShouldBeEmpty();
	}

	[Fact]
	public void WaitingRows_SitAlongsideTheRidesTheCallerIsActuallyOn()
	{
		FakeApiClient api = WireServices();

		api.MyRidesResult = new(
			[new RideSummary(Guid.NewGuid(), "Mine to run", FixedInstant, RideStateDto.Open, IsOrganiser: true, MemberCount: 3, JoinCode: "AB3K9Z")],
			[],
			[Waiting("Still waiting", TimeSpan.FromMinutes(20))]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.ShouldContain("Still waiting");
			component.Markup.ShouldContain("Mine to run");

			// The organised row keeps its code; the pending one has none. Same page, two kinds of
			// row, and the difference between them is the whole point.
			component.FindAll(".join-code").ShouldHaveSingleItem();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// -- Leaving and withdrawing (§5.6, §5.2) -------------------------------------------------

	private static IReadOnlyList<AngleSharp.Dom.IElement> LeaveButtons(IRenderedComponent<GroupRides> component) =>
		[.. component.FindAll("button.leave")];

	private static IReadOnlyList<AngleSharp.Dom.IElement> WithdrawButtons(IRenderedComponent<GroupRides> component) =>
		[.. component.FindAll("button.withdraw")];

	/// <summary>
	/// The organiser cannot leave their own adventure — the server refuses it outright (409, "a ride
	/// nobody organises has nobody to decide who is in it"). So the button is absent there rather
	/// than present and failing, exactly as the trash is absent on a joined row.
	/// </summary>
	[Fact]
	public void Leave_IsOnJoinedRidesOnly()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), Guid.NewGuid());

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(
			() => LeaveButtons(component).Count.ShouldBe(1,
				"one row is joined and one is organised — only the first can be left"),
			timeout: TimeSpan.FromSeconds(3));

		// And the two controls do not both land on one row.
		TrashButtons(component).Count.ShouldBe(1);
	}

	[Fact]
	public async Task Leave_Confirmed_LeavesTheRideAndForgetsIt()
	{
		Guid joined = Guid.NewGuid();
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), joined);

		// The rail's globe is pointing at the ride about to be left (§18.6).
		CurrentRideState current = Services.GetRequiredService<CurrentRideState>();
		await current.SetAsync(joined);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => LeaveButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => LeaveButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			api.LeftRides.ShouldContain(joined);

			// The globe must not go on leading back to an adventure nobody is on.
			current.RideId.ShouldBeNull();

			component.Markup.ShouldNotContain("Somebody else's");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Leave_Cancelled_ChangesNothing()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), Guid.NewGuid());

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => LeaveButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => LeaveButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(false));

		api.LeftRides.ShouldBeEmpty();
		component.Markup.ShouldContain("Somebody else's",
			customMessage: "a cancelled leave leaves the row where it was.");
	}

	/// <summary>
	/// The refusal this list should never be able to produce — the button is only on rides the
	/// caller joined. Shown rather than swallowed all the same: a refusal the app did not expect is
	/// exactly the one worth reading.
	/// </summary>
	[Fact]
	public async Task Leave_WhenTheServerRefuses_SaysWhy_AndKeepsTheRow()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = OneOfEach(Guid.NewGuid(), Guid.NewGuid());
		api.LeaveRideException = new ApiException(new ApiError(
			HttpStatusCode.Conflict, "An organiser cannot leave their own adventure", []));

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => LeaveButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => LeaveButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			component.Find(".error").TextContent.ShouldContain("An organiser cannot leave their own adventure");
			component.Markup.ShouldContain("Somebody else's", customMessage: "a refused leave keeps the row.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Withdraw_Confirmed_TakesTheRequestBack()
	{
		FakeApiClient api = WireServices();

		WaitingRide waiting = Waiting("The long way round", TimeSpan.FromDays(2));
		api.MyRidesResult = new([], [], [waiting]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => WithdrawButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => WithdrawButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			api.WithdrawnRequests.ShouldContain((waiting.RideId, waiting.RequestId));

			// The last row went, so the section goes with it — it is absent when empty.
			component.Markup.ShouldNotContain("Waiting for approval");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Withdraw_Cancelled_LeavesTheRequestStanding()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = new([], [], [Waiting("The long way round", TimeSpan.FromDays(2))]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => WithdrawButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => WithdrawButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(false));

		api.WithdrawnRequests.ShouldBeEmpty();
		component.Markup.ShouldContain("The long way round");
	}

	[Fact]
	public async Task Withdraw_WhenTheServerRefuses_SaysWhy_AndKeepsTheRow()
	{
		FakeApiClient api = WireServices();
		api.MyRidesResult = new([], [], [Waiting("The long way round", TimeSpan.FromDays(2))]);
		api.WithdrawFailure = new ApiException(new ApiError(
			HttpStatusCode.ServiceUnavailable, "The server is not answering", []));

		IRenderedComponent<GroupRides> component = Render<GroupRides>();
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => WithdrawButtons(component).Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => WithdrawButtons(component)[0].Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			component.Find(".error").TextContent.ShouldContain("The server is not answering");
			component.Markup.ShouldContain("The long way round",
				customMessage: "a refused withdrawal leaves them waiting, which is the truth.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
