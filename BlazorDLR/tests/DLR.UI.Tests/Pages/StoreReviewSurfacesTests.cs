using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Legal;
using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Pages.Admin;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using Bunit.TestDoubles;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Components;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The three things App Store guideline 1.2 asks a user-generated-content app to have, each
/// asserted at the surface a reviewer is told to look at (§10.2).
/// <list type="bullet">
///   <item><strong>Terms before registering or signing in</strong> - the gate on Welcome, the
///     "no tolerance" sentence it carries, and the full text at <c>/terms</c> without an
///     account.</item>
///   <item><strong>A way to flag objectionable content</strong> - on markers as well as on
///     posts. 8.0.0 (31) shipped with the marker half missing and was rejected.</item>
///   <item><strong>A way to block an abusive user</strong>, reachable from where the reader
///     meets them rather than only from a list inside an adventure.</item>
/// </list>
/// <para>
/// These are written as one suite on purpose. They are not three unrelated features - they are
/// one submission requirement, and a change that quietly drops any of them should fail here with
/// a test name that says why it matters.
/// </para>
/// </summary>
[Collection(SourceOfferFooterCollection.Name)]
public sealed class StoreReviewSurfacesTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	// ---------- Terms ----------

	/// <summary>
	/// The sentence review reads the terms for. Held here as well as in the file so that softening
	/// it is a failing test rather than a silent edit.
	/// </summary>
	[Fact]
	public void Terms_StateNoToleranceForObjectionableContentOrAbusiveUsers()
	{
		string everything = string.Join(
			" ",
			TermsOfUse.Clauses.SelectMany(clause => clause.Paragraphs.Prepend(clause.Title)));

		everything.ShouldContain("no tolerance", Case.Insensitive);
		everything.ShouldContain("objectionable", Case.Insensitive);
		everything.ShouldContain("abusive", Case.Insensitive);

		TermsOfUse.AcceptanceSummary.ShouldContain("no tolerance", Case.Insensitive);
	}

	/// <summary>
	/// "Before registering or logging in" means the gate is on both forms, not only Register. An
	/// existing account arriving on a new phone has agreed to nothing this device can see.
	/// </summary>
	[Fact]
	public async Task Welcome_GatesBothFormsUntilTheTermsAreAccepted()
	{
		WireWelcome();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(
			() => component.FindAll(".terms-gate").Count.ShouldBe(1, "the gate is on the sign-in form"),
			timeout: TimeSpan.FromSeconds(3));

		component.Find("button[type=submit]").HasAttribute("disabled").ShouldBeTrue(
			"sign-in is refused until the box is ticked");

		await component.InvokeAsync(() =>
			component.FindAll("button.tab")
				.First(tab => tab.TextContent.Contains("Register", StringComparison.Ordinal))
				.Click());

		component.FindAll(".terms-gate").Count.ShouldBe(1, "and on the register form");
		component.Find("button[type=submit]").HasAttribute("disabled").ShouldBeTrue(
			"registering is refused until the box is ticked");

		component.Markup.ShouldContain("no tolerance", Case.Insensitive);
		component.Markup.ShouldContain("/terms", Case.Insensitive);

		await component.InvokeAsync(() =>
			component.Find(".terms-gate input[type=checkbox]").Change(true));

		component.Find("button[type=submit]").HasAttribute("disabled").ShouldBeFalse(
			"ticking it releases the form");
	}

	/// <summary>A device that has already agreed is not asked again - the value is a version, not a flag.</summary>
	[Fact]
	public async Task Welcome_DoesNotAskADeviceThatHasAlreadyAgreed()
	{
		InMemoryDeviceSettings device = new();

		await new TermsAcceptanceState(device).AcceptAsync();

		WireWelcome(device);

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(
			() =>
			{
				component.FindAll(".terms-gate").ShouldBeEmpty();
				component.Find("button[type=submit]").HasAttribute("disabled").ShouldBeFalse();
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The gate a version bump fires, which Welcome on its own cannot reach.
	/// <para>
	/// Welcome sends a signed-in rider straight home, so bumping <see cref="TermsOfUse.Version"/>
	/// would re-ask nobody in the installed base - while clause 1 of the shipped terms promises we
	/// will ask again when what they require changes. MainLayout sends them here with
	/// <c>?accept=1</c>; this is the screen that has to hold them.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Terms_AsGate_HoldsUntilAgreedAndThenReturns()
	{
		InMemoryDeviceSettings device = new();

		WireWelcome(device);

		BunitNavigationManager nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

		// Through the URL, not as parameters: they are [SupplyParameterFromQuery], and bUnit binds
		// those the way the router does.
		nav.NavigateTo("/terms?accept=1&return=%2Fgroup-rides");

		IRenderedComponent<Terms> component = Render<Terms>();

		component.Markup.ShouldContain("changed since you agreed", Case.Insensitive);
		component.Find("button.primary").HasAttribute("disabled").ShouldBeTrue(
			"there is no way past the gate until the box is ticked");

		await component.InvokeAsync(() =>
			component.Find(".terms-gate input[type=checkbox]").Change(true));

		await component.InvokeAsync(() => component.Find("button.primary").Click());

		// Agreed on this device, so the next launch does not ask again...
		(await new TermsAcceptanceState(device).MustAskAsync()).ShouldBeFalse();

		// ...and the rider lands where they were going, not on the home screen.
		// Nav.Uri, not History - bUnit records history newest-first, which reads as the opposite.
		component.WaitForAssertion(
			() => nav.Uri.EndsWith("/group-rides", StringComparison.Ordinal).ShouldBeTrue(nav.Uri),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The gate's return path comes off the query string, so it is filtered (§18.6). A
	/// protocol-relative value is the spelling that looks like a path and is not - a guard that
	/// only checks the first character would send the rider to somebody else's site.
	/// </summary>
	[Fact]
	public async Task Terms_AsGate_RefusesAnOffsiteReturn()
	{
		WireWelcome();

		BunitNavigationManager nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

		nav.NavigateTo("/terms?accept=1&return=%2F%2Fevil.example%2Fx");

		IRenderedComponent<Terms> component = Render<Terms>();

		await component.InvokeAsync(() =>
			component.Find(".terms-gate input[type=checkbox]").Change(true));

		await component.InvokeAsync(() => component.Find("button.primary").Click());

		component.WaitForAssertion(
			() => nav.Uri.ShouldNotContain("evil.example", Case.Insensitive, nav.Uri),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>Opened from Settings rather than as a gate, it is a reference copy with no controls.</summary>
	[Fact]
	public void Terms_WithoutAcceptFlag_IsJustTheText()
	{
		WireWelcome();

		IRenderedComponent<Terms> component = Render<Terms>();

		component.FindAll(".terms-gate").ShouldBeEmpty();
		component.FindAll("button.primary").ShouldBeEmpty();
		component.Markup.ShouldNotContain("changed since you agreed", Case.Insensitive);
	}

	/// <summary>
	/// The terms have to be readable by somebody who has not registered, which is the whole point
	/// of presenting them before registration. Rendering the page with no authentication at all is
	/// the test for it.
	/// </summary>
	[Fact]
	public void Terms_AreReadableWithoutAnAccount()
	{
		WireWelcome();

		IRenderedComponent<Terms> component = Render<Terms>();

		component.Markup.ShouldContain("no tolerance", Case.Insensitive);
		component.Markup.ShouldContain(TermsOfUse.ModerationContact);

		foreach (TermsClause clause in TermsOfUse.Clauses)
		{
			component.Markup.ShouldContain(clause.Title);
		}
	}

	// ---------- Reporting ----------

	/// <summary>
	/// The gap 8.0.0 (31) was rejected over. A marker carries a title, a note and a photograph -
	/// content every member of the adventure can see - and until this control existed the only
	/// thing offered on one was a delete, which is not a report.
	/// </summary>
	[Fact]
	public async Task MarkerDetails_OffersAReportOnSomebodyElsesMarker()
	{
		MarkerDto marker = Marker();
		List<Guid> reported = [];

		IRenderedComponent<MarkerDetails> component = Render<MarkerDetails>(parameters => parameters
			.Add(details => details.Marker, marker)
			.Add(details => details.CanReport, true)
			.Add(details => details.OnReport, EventCallback.Factory.Create<MarkerDto>(this, reportedMarker => reported.Add(reportedMarker.Id))));

		component.Markup.ShouldContain("Report marker");

		await component.InvokeAsync(() => component.Find("button.report").Click());

		reported.ShouldBe([marker.Id]);
	}

	/// <summary>
	/// A note is full-length rider prose, so its URLs are tapable (§17.2). The common case is a
	/// marker on a meeting point whose note says "details at &lt;url&gt;", which read as dead text
	/// until v0.35.
	/// </summary>
	[Fact]
	public void MarkerDetails_MakesUrlsInTheNoteTapable()
	{
		IRenderedComponent<MarkerDetails> component = Render<MarkerDetails>(parameters => parameters
			.Add(details => details.Marker, Marker() with
			{
				Note = "Meet here, details at https://example.org/ride",
			}));

		AngleSharp.Dom.IElement link = component.Find("a.linked");

		link.GetAttribute("href").ShouldBe("https://example.org/ride");
		link.TextContent.ShouldBe("https://example.org/ride", "the visible text is what the rider typed");
		link.GetAttribute("target").ShouldBe("_blank");
		link.GetAttribute("rel").ShouldContain("noopener");

		component.Markup.ShouldContain("Meet here, details at", Case.Insensitive);
	}

	/// <summary>
	/// And LinkScanner's rules come with it. A scheme the app will not follow stays text wherever
	/// LinkedText is used - linkifying a new surface must not widen what counts as a link.
	/// </summary>
	[Fact]
	public void MarkerDetails_LeavesANonHttpSchemeInTheNoteAsText()
	{
		IRenderedComponent<MarkerDetails> component = Render<MarkerDetails>(parameters => parameters
			.Add(details => details.Marker, Marker() with
			{
				Note = "javascript:alert(1) and https://dlr.example.org@evil.example/x",
			}));

		component.FindAll("a.linked").ShouldBeEmpty(
			"§17.2: only http and https, and never a URL carrying userinfo");
	}

	/// <summary>Never on the reader's own marker - they have the delete, and reporting yourself is not a thing.</summary>
	[Fact]
	public void MarkerDetails_OffersNoReportWhenTheMarkerIsTheReadersOwn()
	{
		IRenderedComponent<MarkerDetails> component = Render<MarkerDetails>(parameters => parameters
			.Add(details => details.Marker, Marker())
			.Add(details => details.CanReport, false)
			.Add(details => details.CanDelete, true));

		component.FindAll("button.report").ShouldBeEmpty();
		component.Markup.ShouldContain("Delete marker");
	}

	// ---------- The operator's side of the hold ----------

	/// <summary>
	/// A report hides content from everybody at once, so this screen is what keeps that from being
	/// a way for one account to silence another. It has to offer both directions.
	/// </summary>
	[Fact]
	public async Task AdminReports_ListsWhatIsHeldAndOffersRestoreAndRemove()
	{
		FakeApiClient api = WireAdmin();

		Guid reportId = Guid.NewGuid();

		api.OpenReportsResult =
		[
			new OpenReport(
				reportId,
				"Comment",
				Guid.NewGuid(),
				"SamJones",
				"DaveSmith",
				"Abusive towards another traveller",
				"""{"body":"the offending text"}""",
				FixedInstant,
				ReportsOnTarget: 2),
		];

		IRenderedComponent<AdminReports> component = Render<AdminReports>();

		component.WaitForAssertion(
			() =>
			{
				component.Markup.ShouldContain("SamJones");
				component.Markup.ShouldContain("DaveSmith");
				component.Markup.ShouldContain("Abusive towards another traveller");
				component.Markup.ShouldContain("the offending text", Case.Insensitive);
				component.Markup.ShouldContain("1 other report", Case.Insensitive);
			},
			timeout: TimeSpan.FromSeconds(3));

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		await component.InvokeAsync(() =>
			component.FindAll("button")
				.First(button => button.TextContent.Contains("Restore", StringComparison.Ordinal))
				.Click());

		// Restoring asks first, like every other decision that changes what other people see.
		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(
			() => api.RestoredReports.ShouldBe([reportId]),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>An empty queue is the normal state, and it has to read as one rather than as a failure.</summary>
	[Fact]
	public void AdminReports_SaysSoWhenNothingIsWaiting()
	{
		WireAdmin();

		IRenderedComponent<AdminReports> component = Render<AdminReports>();

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("Nothing is waiting", Case.Insensitive),
			timeout: TimeSpan.FromSeconds(3));
	}

	private static MarkerDto Marker() => new(
		Id: Guid.NewGuid(),
		TrackId: null,
		GroupRideId: Guid.NewGuid(),
		Lat: PositionScale.FromDegrees(-33.86),
		Lon: PositionScale.FromDegrees(151.20),
		Icon: "hazard",
		Title: "Gravel",
		Note: "Loose on the apex",
		DirectionDeg: null,
		PhotoId: null,
		CreatedByUserId: Guid.NewGuid(),
		CreatedByUserName: "SamJones",
		CreatedUtc: FixedInstant,
		UpdatedUtc: FixedInstant);

	private void WireWelcome(IDeviceSettings? device = null)
	{
		FakeApiClient api = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<AuthenticationStateProvider>(auth);
		Services.AddSingleton<IEnumerable<IExternalSignInProvider>>(Array.Empty<IExternalSignInProvider>());
		IDeviceSettings store = device ?? new InMemoryDeviceSettings();

		Services.AddSingleton(store);
		Services.AddSingleton(new TermsAcceptanceState(store));
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);
	}

	private FakeApiClient WireAdmin()
	{
		FakeApiClient api = new();

		Services.AddSingleton<IApiClient>(api);

		return api;
	}
}
