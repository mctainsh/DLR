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
/// The signed-in landing for group rides shows two entry points (Create, Join) and two
/// lists of the caller's rides split by role (organised, joined). What matters for the
/// test is that both entry points remain reachable — a landing that only offered "Join"
/// would silently gate ride creation behind rediscovery of the URL — and that the two
/// sections render even when they are empty.
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
		return api;
	}

	private static MyRides OneOfEach(Guid organised, Guid joined) => new(
		[new RideSummary(organised, "Mine to run", FixedInstant, RideStateDto.Open, IsOrganiser: true, MemberCount: 3, JoinCode: "AB3K9Z")],
		[new RideSummary(joined, "Somebody else's", FixedInstant, RideStateDto.Open, IsOrganiser: false, MemberCount: 5, JoinCode: "QW7T2M")]);

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
				JoinCode: "QW7T2M")]);

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
}
