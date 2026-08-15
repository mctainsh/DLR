using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in track detail page. Two properties:
/// <list type="bullet">
///   <item>The header exposes both the Edit link (route to <c>TrackEditor</c>) and the
///     GPX download button — losing either would silently strand the two operations
///     the design outline names for a stored track (§15.5, §15.7).</item>
///   <item>The stats row honours the null-vs-zero rule: a null ascent renders as "—",
///     a zero-second duration renders as an em dash from the null formatter, and a
///     zero-metre distance renders as "0.0 km" — because zero is a real number for
///     distance (§8).</item>
/// </list>
/// </summary>
public sealed class RideDetailTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly FakeApiClient _api = new();

	private FakeTrackRepository WireServices(TrackDetail? detail)
	{
		FakeTrackRepository repo = new() { DetailResult = detail };
		Services.AddSingleton<ITrackRepository>(repo);
		Services.AddSingleton<IApiClient>(_api);
		Services.AddSingleton<ConfirmService>();
		Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Test host — map interop is stubbed."),
		});

		// The fake answers a rename out of the detail it is already holding, so the summary this
		// page gets back is the one it was showing with the new name on it.
		_api.TrackDetailResult = detail;

		return repo;
	}

	private static TrackSummary Sample(double? ascent = 480, double? duration = 3600) =>
		new(
			Id: Guid.NewGuid(),
			Name: "Sample ride",
			CreatedUtc: FixedInstant,
			StartedUtc: null,
			EndedUtc: null,
			DistanceM: 25_000,
			DurationS: duration,
			AscentM: ascent,
			MaxSpeedMps: null,
			PointCount: 500,
			SegmentCount: 1,
			Source: TrackSourceDto.Recorded,
			Version: 1);

	[Fact]
	public void Header_HasEditLinkAndDownloadButton()
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		component.WaitForAssertion(() =>
		{
			component.FindAll($"a[href='/rides/{id}/edit']").ShouldNotBeEmpty(
				"§15.5: the Edit link routes to the composer. Missing it strands the trimming path.");
			component.FindAll("button").Any(b => b.TextContent.Contains("Download GPX", StringComparison.Ordinal))
				.ShouldBeTrue("§15.7: the raw GPX download button is on this page — the design outline names it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Stats_NullAscentRendersAsEmDash_NotZero()
	{
		WireServices(new TrackDetail(
			Track: Sample(ascent: null),
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("—", StringComparison.Ordinal).ShouldBeTrue(
				"§8: null ascent must render as '—'. Zero would be a real value (a flat coastal ride) and must not collide with 'unknown'.");
			// A dead flat ride would show "0 m", which contains "0" — assert with the unit to be specific.
			component.Markup.Contains("0 m ascent", StringComparison.Ordinal).ShouldBeFalse();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// §15.1: renaming works on this page for a recorded track and an imported one alike — they are
	/// the same entity, and a file a mate sent you is the one most likely to arrive called
	/// "Course 3".
	/// </summary>
	[Theory]
	[InlineData(TrackSourceDto.Recorded)]
	[InlineData(TrackSourceDto.Imported)]
	public async Task Rename_SendsTheNewName_AndShowsItInTheTitle(TrackSourceDto source)
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id, Name = "Course 3", Source = source },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		component.WaitForAssertion(() => Button(component, "Rename").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => Button(component, "Rename")!.Click());

		// The box opens on the name the track already has — a typo is a fix, not a retype.
		component.Find(".rename input").GetAttribute("value").ShouldBe("Course 3");

		await component.InvokeAsync(() =>
			component.Find(".rename input").Input("Saturday coast run"));

		await component.InvokeAsync(() =>
			component.Find(".rename-actions button.primary").Click());

		component.WaitForAssertion(() =>
		{
			_api.RenamedTracks.ShouldHaveSingleItem().ShouldBe((id, "Saturday coast run"));
			component.FindAll(".rename").ShouldBeEmpty("the box closes once the name is stored.");
			component.Markup.ShouldContain("Saturday coast run");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Rename_WithABlankName_CannotBeSent()
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		component.WaitForAssertion(() => Button(component, "Rename").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => Button(component, "Rename")!.Click());

		await component.InvokeAsync(() => component.Find(".rename input").Input("   "));

		component.Find(".rename-actions button.primary").HasAttribute("disabled").ShouldBeTrue(
			"whitespace is not a name, and clearing the name is not what the empty box means.");

		_api.RenamedTracks.ShouldBeEmpty();
	}

	/// <summary>
	/// Deleting asks first, and only then. The dialog is the app's own (§16.5's pattern), and what
	/// goes with the track is stated in the question rather than discovered afterwards.
	/// </summary>
	[Fact]
	public async Task Delete_AsksFirst_ThenDeletesAndReturnsToTheList()
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => Button(component, "Delete").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => Button(component, "Delete")!.Click());

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		_api.DeletedTracks.ShouldBeEmpty("nothing may be deleted before the rider has answered.");

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			_api.DeletedTracks.ShouldHaveSingleItem().ShouldBe(id);

			Services.GetRequiredService<NavigationManager>().Uri
				.ShouldEndWith("/rides",
					customMessage: "a page whose track no longer exists is not a page to stay on.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Delete_Cancelled_DeletesNothing()
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => Button(component, "Delete").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => Button(component, "Delete")!.Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(false));

		_api.DeletedTracks.ShouldBeEmpty();
	}

	/// <summary>
	/// The refusal that actually happens: a ride in progress is running on this line (§15.4). The
	/// server says so in words, and the page shows them rather than "an error occurred".
	/// </summary>
	[Fact]
	public async Task Delete_RefusedByTheServer_SaysWhy_AndStaysPut()
	{
		Guid id = Guid.NewGuid();
		WireServices(new TrackDetail(
			Track: Sample() with { Id = id },
			Bounds: null,
			Polyline: Array.Empty<TrackPoint>()));

		_api.DeleteTrackException =
			new InvalidOperationException("A ride in progress is using this track as its planned route.");

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, id));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		component.WaitForAssertion(() => Button(component, "Delete").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => Button(component, "Delete")!.Click());
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));
		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() =>
		{
			component.Find(".status").TextContent.ShouldContain("ride in progress");

			Services.GetRequiredService<NavigationManager>().Uri
				.ShouldNotEndWith("/rides",
					customMessage: "the track is still there, so the page is too.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>The bar's buttons carry no class of their own, so they are found by their words.</summary>
	private static AngleSharp.Dom.IElement? Button(IRenderedComponent<RideDetail> component, string text) =>
		component.FindAll("button")
			.FirstOrDefault(button => button.TextContent.Contains(text, StringComparison.Ordinal));

	[Fact]
	public void NotFound_RendersFriendlyMessage()
	{
		WireServices(detail: null);

		IRenderedComponent<RideDetail> component = Render<RideDetail>(parameters => parameters
			.Add(p => p.TrackId, Guid.NewGuid()));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Not found", StringComparison.Ordinal).ShouldBeTrue(
				"a page reached with a bad id must say so — silently rendering an empty shell would be a bug that looks like a bug.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
