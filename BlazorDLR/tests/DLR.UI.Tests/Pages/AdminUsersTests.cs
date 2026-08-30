using BlazorDLR.Shared.Pages.Admin;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Admin;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The traveller list behind <c>/admin/users</c> (§14.6).
/// <para>
/// A renderer test can say nothing about pixels, so it does not try. What it can hold is the
/// structural invariant the column alignment rests on: the table declares its own tracks in a
/// <c>colgroup</c> and uses fixed layout, so a heading and the figures under it cannot end up in
/// different columns. That bug shipped once — the numbers bunched left while the headings marched
/// off to the right — and the shape of the mistake that caused it is "a column was added to the
/// head and not to the colgroup", which is exactly what this catches.
/// </para>
/// </summary>
public sealed class AdminUsersTests : PageTestContext
{
	private static AdminUserRow Row(
		string userName,
		bool isAdmin = false,
		long positions = 0,
		int held = 0) =>
		new(
			UserId: Guid.NewGuid(),
			UserName: userName,
			Email: $"{userName}@example.com",
			EmailConfirmed: true,
			CreatedUtc: new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero),
			LastActiveUtc: new DateTimeOffset(2025, 12, 20, 0, 0, 0, TimeSpan.Zero),
			PositionsRecorded: positions,
			PositionsHeld: held,
			Adventures: 1,
			Routes: 2,
			Posts: 3,
			Photos: 4,
			Markers: 5,
			TrackedHours: 9.5,
			Devices: 6,
			IsAdmin: isAdmin);

	private FakeApiClient Wire(params AdminUserRow[] rows)
	{
		FakeApiClient api = new() { AdminUsers = rows };

		Services.AddSingleton<IApiClient>(api);

		return api;
	}

	/// <summary>
	/// One <c>col</c> per heading, or the tracks the headings and the figures share stop being the
	/// same tracks. Under <c>table-layout: fixed</c> a missing <c>col</c> is not a compile error and
	/// not a blank column — it silently shifts every column after it.
	/// </summary>
	[Fact]
	public void TheTableDeclaresOneColumnTrackPerHeading()
	{
		Wire(Row("JRM"));

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		int columns = component.FindAll(".admin-users colgroup col").Count;
		int headings = component.FindAll(".admin-users thead th").Count;

		columns.ShouldBe(headings);

		// And every body row fills all of them, which is the other half of the same invariant.
		foreach (var row in component.FindAll(".admin-users tbody tr"))
		{
			row.Children.Length.ShouldBe(headings);
		}
	}

	[Fact]
	public void EveryNumericColumn_IsMarkedOnBothTheHeadingAndTheCell()
	{
		Wire(Row("JRM"));

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		// The heading carries the class too, not just the cell: a left-aligned heading over
		// right-aligned figures is the misalignment this screen is read for, at one column's width.
		int headings = component.FindAll(".admin-users thead th.num").Count;
		int cells = component.FindAll(".admin-users tbody tr:first-child td.num").Count;

		headings.ShouldBe(9);
		cells.ShouldBe(headings);
	}

	/// <summary>
	/// Bootstrap is linked on every host, and its own <c>.figure</c> component is
	/// <c>display: inline-block</c>. On a <c>&lt;td&gt;</c> that stops the cell being a cell: the
	/// numeric columns fall out of the table's tracks and stack up the side of it, which is what
	/// this screen did until the class was renamed.
	/// <para>
	/// A renderer test cannot see the cascade, so it guards the thing it can see — that the markup
	/// does not reach for that name again. The same trap is waiting for any class on shared markup
	/// that Bootstrap also defines.
	/// </para>
	/// </summary>
	[Fact]
	public void TheNumericColumns_DoNotUseAClassNameBootstrapAlreadyOwns()
	{
		Wire(Row("JRM"));

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		component.FindAll(".admin-users .figure").ShouldBeEmpty(
			"Bootstrap's .figure is display:inline-block, which un-cells a table cell.");
	}

	[Fact]
	public void EachAccountGetsARow_WithItsFiguresAndItsAdminBadge()
	{
		Wire(Row("JRM", isAdmin: true, positions: 184_220, held: 1_412), Row("DaveSmith"));

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		component.FindAll(".admin-users tbody tr").Count.ShouldBe(2);

		component.FindAll(".admin-users .tag.admin").Count.ShouldBe(1,
			"the roster is the server's, and the list is where an administrator sees who else is on it.");

		// Thousands separators, because these are the two columns that get large.
		component.Find(".admin-users tbody tr:first-child td.num").TextContent.Trim()
			.ShouldBe("184,220");
	}

	[Fact]
	public void WhenTheServerRefuses_TheReasonIsShown_RatherThanAnEmptyTable()
	{
		FakeApiClient api = new()
		{
			AdminUsersFailure = new ApiException(new ApiError(
				System.Net.HttpStatusCode.Forbidden,
				"Forbidden",
				["This account is not an administrator."])),
		};

		Services.AddSingleton<IApiClient>(api);

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		component.FindAll(".admin-users table").ShouldBeEmpty();
		component.Find(".admin-users .error").TextContent.ShouldContain("Forbidden");
	}

	/// <summary>
	/// A roster account gets no delete button. The server refuses those outright — deleting one
	/// frees the username its roster entry names — so a button there could only ever be told no.
	/// </summary>
	[Fact]
	public void OnlyNonRosterAccountsOfferDelete()
	{
		Wire(Row("JRM", isAdmin: true), Row("DaveSmith"));

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		component.FindAll(".admin-users tbody .danger-icon").Count.ShouldBe(1);
	}

	[Fact]
	public async Task DeletingAnAccount_AsksFirst_ThenDropsTheRow()
	{
		FakeApiClient api = Wire(Row("JRM"), Row("DaveSmith"));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		_ = component.InvokeAsync(() =>
			component.FindAll(".admin-users tbody .danger-icon")[1].Click());

		// The dialog is up and nothing has been sent: the ask is what gates the request.
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull());
		api.LastAdminDelete.ShouldBeNull();

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() => api.LastAdminDelete.ShouldNotBeNull());
		api.LastAdminDelete!.Value.UserName.ShouldBe("DaveSmith");

		component.WaitForAssertion(() =>
			component.FindAll(".admin-users tbody tr").Count.ShouldBe(1));
	}

	[Fact]
	public async Task DecliningTheConfirmation_SendsNothing()
	{
		FakeApiClient api = Wire(Row("DaveSmith"));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		_ = component.InvokeAsync(() =>
			component.Find(".admin-users tbody .danger-icon").Click());

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull());

		await component.InvokeAsync(() => confirm.Respond(false));

		api.LastAdminDelete.ShouldBeNull();
		component.FindAll(".admin-users tbody tr").Count.ShouldBe(1);
	}

	/// <summary>
	/// A refused delete says why and leaves the table standing. Replacing the rows with the error
	/// would make a 409 about one account read as the whole list having failed.
	/// </summary>
	[Fact]
	public async Task WhenADeleteIsRefused_TheTableStays_AndTheReasonIsShown()
	{
		FakeApiClient api = Wire(Row("DaveSmith"));

		api.AdminDeleteFailure = new ApiException(new ApiError(
			System.Net.HttpStatusCode.Conflict,
			"Administrator account",
			["DaveSmith is named in this server's Admins roster."]));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		IRenderedComponent<AdminUsers> component = Render<AdminUsers>();

		_ = component.InvokeAsync(() =>
			component.Find(".admin-users tbody .danger-icon").Click());

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull());

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
			component.Find(".admin-users .error").TextContent.ShouldContain("Admins roster"));

		component.FindAll(".admin-users tbody tr").Count.ShouldBe(1);
	}
}
