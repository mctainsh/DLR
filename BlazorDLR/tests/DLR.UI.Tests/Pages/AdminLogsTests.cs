using BlazorDLR.Shared.Pages.Admin;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Admin;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The server log screen behind <c>/admin/logs</c> (§14.6).
/// <para>
/// Nothing on this page filters anything. EF Core logs every SQL statement it runs at Information,
/// so no level floor can separate them from the lines about riders and rides — but the screen does
/// not drop them on the way to the list either, because the read is capped and a cap spent on
/// lines nobody is going to see buys minutes of a busy day instead of the day. It asks the server
/// not to read them at all. What is pinned here is that the unticked default reaches the server,
/// that ticking the box is a fresh read rather than a fresh view, and that the page repeats what
/// the server said it stepped over.
/// </para>
/// </summary>
public sealed class AdminLogsTests : PageTestContext
{
	private static readonly DateOnly Day = new(2026, 1, 1);

	private static AdminLogEntry Entry(string category, string message) =>
		new(new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero), "INFO", category, message);

	/// <summary>One of EF Core's statement lines, under the category the framework itself uses.</summary>
	private static AdminLogEntry Sql(string message = "SELECT 1") =>
		Entry("Microsoft.EntityFrameworkCore.Database.Command", message);

	private static AdminLogEntry Ride(string message = "A ride started.") =>
		Entry("DLR.Server.Rides.RideController", message);

	/// <summary>Wires a client that answers every read with the one page.</summary>
	/// <param name="hidden">What the server says it stepped over to build that page.</param>
	/// <param name="entries">The lines it answers with.</param>
	/// <returns>The client, so a test can read back what the screen asked it for.</returns>
	private FakeApiClient Wire(int hidden, params AdminLogEntry[] entries)
	{
		FakeApiClient api = new()
		{
			AdminLogs = new(
				entries,
				Day,
				[Day],
				Truncated: false,
				DatabaseCommandsHidden: hidden,
				Enabled: true,
				Directory: @"C:\dlr\logs",
				Problem: null),
		};

		Services.AddSingleton<IApiClient>(api);

		return api;
	}

	/// <summary>
	/// Off by default, and off means the server is told not to read them rather than the page
	/// throwing them away afterwards. An administrator opening this screen is looking for the one
	/// line that is not SQL, and the cap has to be spent on finding it.
	/// </summary>
	[Fact]
	public void DatabaseCommands_AreNotAskedForUntilTheyAreWanted()
	{
		FakeApiClient api = Wire(hidden: 1, Ride());

		IRenderedComponent<AdminLogs> component = Render<AdminLogs>();

		api.LastAdminLogsDatabaseCommands.ShouldBe(false);

		component.FindAll(".entries li").Count.ShouldBe(1);
		component.Markup.ShouldContain("A ride started.");
	}

	/// <summary>
	/// The count under the list says what was stepped over. Without it, a day that is mostly SQL
	/// reads as a quiet day rather than a filtered one.
	/// </summary>
	[Fact]
	public void TheCountBeneathTheList_SaysHowManyWereSteppedOver()
	{
		Wire(hidden: 2, Ride());

		IRenderedComponent<AdminLogs> component = Render<AdminLogs>();

		string hint = component.Find(".hint").TextContent;

		hint.ShouldContain("1 line");
		hint.ShouldContain("2 database commands");
	}

	/// <summary>
	/// Ticking the box is a new reading of the file, not a new view of the page in hand — that is
	/// the point of the round trip, because the server is being asked for a different set of lines.
	/// </summary>
	[Fact]
	public async Task TickingTheBox_AsksTheServerForThemAgain()
	{
		FakeApiClient api = Wire(hidden: 1, Ride());

		IRenderedComponent<AdminLogs> component = Render<AdminLogs>();

		api.AdminLogs = api.AdminLogs with
		{
			Entries = [Ride(), Sql()],
			DatabaseCommandsHidden = 0,
		};

		await component.InvokeAsync(() =>
			component.Find(".controls input[type=checkbox]").Change(true));

		api.LastAdminLogsDatabaseCommands.ShouldBe(true);

		api.Calls.Count(call => call == nameof(IApiClient.AdminLogsAsync)).ShouldBe(2,
			"the filter is the server's, so a different filter is a different read.");

		component.FindAll(".entries li").Count.ShouldBe(2);
		component.Markup.ShouldContain("SELECT 1");
	}

	/// <summary>
	/// A day whose every line is a statement is not a day with nothing on it, and saying "nothing
	/// at this level" would send somebody looking for a fault that is a tick box.
	/// </summary>
	[Fact]
	public void ADayThatIsAllDatabaseCommands_SaysSoRatherThanLookingEmpty()
	{
		Wire(hidden: 2);

		IRenderedComponent<AdminLogs> component = Render<AdminLogs>();

		string empty = component.Find(".empty").TextContent;

		empty.ShouldContain("database commands");
		empty.ShouldContain("2");
	}
}
