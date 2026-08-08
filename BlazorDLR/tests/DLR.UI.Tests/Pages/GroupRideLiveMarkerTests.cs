using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The live ride page's marker list (§16.4, §16.5).
/// <para>
/// The map's pins are drawn on the Skia overlay, which is <c>pointer-events: none</c> so
/// gestures reach the base map — a pin cannot be tapped. The list beside the map is
/// therefore the only place a marker's note, its photograph and its delete exist, and these
/// tests are what say so.
/// </para>
/// <para>
/// The delete rule mirrors <c>MarkerController.CanWriteAsync</c>: the rider who placed it,
/// or the organiser. The server is the one that enforces it — what is asserted here is that
/// the button is <em>absent</em> for everybody else, because a button that always 403s reads
/// as a broken app rather than as a decision somebody made.
/// </para>
/// </summary>
public sealed class GroupRideLiveMarkerTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static readonly Guid MeId = new("11111111-1111-1111-1111-111111111111");
	private static readonly Guid AliceId = new("22222222-2222-2222-2222-222222222222");

	/// <summary>
	/// A square view one degree across, 1000 px on a side, centred on the origin — so 0.001°
	/// is one pixel and "near the pin" is arithmetic rather than guesswork.
	/// </summary>
	private static readonly MapViewport UnitViewport = new(
		TopLeftLatitude: 0.5, TopLeftLongitude: -0.5,
		BottomRightLatitude: -0.5, BottomRightLongitude: 0.5,
		ZoomLevel: 12, HeadingDeg: 0,
		CanvasWidthPx: 1000, CanvasHeightPx: 1000, DevicePixelRatio: 1);

	/// <summary>
	/// The base map behind the page, kept on the fixture because the tests that drive it want
	/// it and the ones that do not should not have to name it.
	/// </summary>
	private readonly FakeMapInterop _map = new()
	{
		// RideMap's stated-error branch (§4.5), which keeps SkiaMapOverlay unmounted. Its
		// SKCanvasView throws PlatformNotSupportedException outside a browser. Viewports and
		// taps are raised on the fake directly, so both seams are still live.
		InitException = new InvalidOperationException("Test host — map interop is stubbed."),
	};

	private async Task<(FakeApiClient api, FakeRideHubClient hub, ConfirmService confirm, Guid rideId)> WireServicesAsync(
		IReadOnlyList<MarkerDto> markers,
		bool isOrganiser = false)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			MarkersResult = markers,
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test ride",
				Description: null,
				StartUtc: FixedInstant,
				State: RideStateDto.Live,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 2,
				IsOrganiser: isOrganiser,
				JoinCode: isOrganiser ? "AB3K9Z" : null,
				Permissions: new RidePermissions(),
				Members: new[]
				{
					new RideMemberSummary(MeId, "Me", isOrganiser ? "Owner" : "Rider", FixedInstant, true, true),
					new RideMemberSummary(AliceId, "Alice", "Rider", FixedInstant, true, true),
				}),
		};

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);
		ConfirmService confirm = new();

		// CanDelete compares against AuthState.UserId, which is only set by a session — without
		// this the page would treat every marker as somebody else's and the test would pass for
		// the wrong reason.
		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(MeId, "Me", HasEmail: true, EmailConfirmed: true)));

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IRideHubClient>(hub);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddSingleton(confirm);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);
		Services.AddSingleton<IMapInterop>(_map);

		// The page's own RideMap logic is what turns an interop viewport into a hit-testable
		// frame; only its rendering is impossible here. See StubRideMap.
		ComponentFactories.Add<RideMap, StubRideMap>();

		return (api, hub, confirm, rideId);
	}

	/// <summary>
	/// A marker, at the origin unless a test says otherwise — <see cref="UnitViewport"/> is
	/// centred there, which is what makes the tap distances in these tests readable.
	/// </summary>
	private static MarkerDto Marker(
		Guid id,
		Guid authorId,
		string authorName,
		string title = "Loose gravel",
		string? note = null,
		Guid? photoId = null,
		double latitudeDeg = 0,
		double longitudeDeg = 0) =>
		new(
			Id: id,
			TrackId: null,
			GroupRideId: null,
			Lat: PositionScale.FromDegrees(latitudeDeg),
			Lon: PositionScale.FromDegrees(longitudeDeg),
			Icon: "gravel",
			Title: title,
			Note: note,
			DirectionDeg: null,
			PhotoId: photoId,
			CreatedByUserId: authorId,
			CreatedByUserName: authorName,
			CreatedUtc: FixedInstant,
			UpdatedUtc: FixedInstant);

	/// <summary>Expands the one marker row and returns the rendered page.</summary>
	private static async Task OpenFirstMarkerAsync(IRenderedComponent<GroupRideLive> component)
	{
		component.WaitForAssertion(
			() => component.FindAll("button.marker-head").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.marker-head").Click());
	}

	[Fact]
	public async Task MarkerList_ShowsTitleAndAuthor_WithoutExpanding()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", title: "Loose gravel"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			string head = component.Find("button.marker-head").TextContent;
			head.ShouldContain("Loose gravel");
			head.ShouldContain("Alice", customMessage:
				"§16.5: who placed a marker is the first thing that decides whether to trust it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ExpandingAMarker_ShowsItsNote()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", note: "Whole corner, right through the apex."),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.ShouldNotContain("Whole corner",
				customMessage: "A note is the detail behind the pin — it stays collapsed until asked for."),
			timeout: TimeSpan.FromSeconds(3));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.Find(".marker-body .note").TextContent
				.ShouldBe("Whole corner, right through the apex."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ExpandingAMarkerWithAPhoto_RendersTheAuthedImage()
	{
		Guid photoId = Guid.NewGuid();
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", photoId: photoId),
		});

		// AuthedImage fetches the bytes itself (§16.4) — an <img> cannot carry a bearer token.
		Services.AddSingleton(new HttpClient { BaseAddress = new Uri("https://test.invalid") });

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		// The photo endpoint is authenticated, so the component renders its loading or error
		// branch rather than an <img> in a test host. What matters is that the page asked for
		// the right photo at all — the fetch itself is AuthedImage's own test.
		component.WaitForAssertion(() =>
			component.Find(".marker-body").InnerHtml.ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".marker-body .hint").Count.ShouldBe(1,
			"A marker with a photo and no note still says the note is missing rather than showing nothing.");
	}

	[Fact]
	public async Task DeleteButton_ShownToTheMarkersAuthor()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), MeId, "Me"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.FindAll(".marker-body button.danger").ShouldNotBeEmpty(
				"§16.5: the rider who placed a marker may take it down again."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task DeleteButton_ShownToTheOrganiser_OnSomebodyElsesMarker()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(
			new[] { Marker(Guid.NewGuid(), AliceId, "Alice") },
			isOrganiser: true);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.FindAll(".marker-body button.danger").ShouldNotBeEmpty(
				"§16.5: the organiser is the other person who may remove a marker."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task DeleteButton_HiddenFromAnOrdinaryMember_OnSomebodyElsesMarker()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(
			new[] { Marker(Guid.NewGuid(), AliceId, "Alice") },
			isOrganiser: false);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.Find(".marker-body").TextContent.ShouldNotContain("Delete marker"),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".marker-body button.danger").ShouldBeEmpty(
			"§16.5: a member who did not place a marker and does not run the ride cannot remove it — " +
			"the server would refuse, so the button must not be there to press.");
	}

	[Fact]
	public async Task ConfirmingTheDelete_CallsTheApi_AndDropsTheRow()
	{
		Guid markerId = Guid.NewGuid();
		(FakeApiClient api, _, ConfirmService confirm, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(markerId, MeId, "Me"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.FindAll(".marker-body button.danger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find(".marker-body button.danger").Click());

		// ConfirmDialog lives in MainLayout, which a page-only render does not mount — answering
		// the service directly is the same thing the dialog's confirm button does.
		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(
			"Deleting a marker is destructive and asks first."), timeout: TimeSpan.FromSeconds(3));

		confirm.Current!.Danger.ShouldBeTrue("A destructive confirm gets the red button.");

		await component.InvokeAsync(() => confirm.Respond(true));

		component.WaitForAssertion(() => api.DeletedMarkers.ShouldContain(markerId),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(() =>
			component.Markup.ShouldContain("Nothing marked on this ride yet",
				customMessage: "The row goes on the caller's own delete, not only when the hub echoes it back — " +
				"§5.3 makes the snapshot authoritative and the hub the delta on top."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task CancellingTheDelete_LeavesTheMarkerAlone()
	{
		Guid markerId = Guid.NewGuid();
		(FakeApiClient api, _, ConfirmService confirm, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(markerId, MeId, "Me"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.FindAll(".marker-body button.danger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find(".marker-body button.danger").Click());

		component.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => confirm.Respond(false));

		api.DeletedMarkers.ShouldBeEmpty("Cancelling the confirm must not reach the server.");
		component.FindAll("button.marker-head").ShouldNotBeEmpty("The marker is still on the ride.");
	}

	[Fact]
	public async Task TappingTheMapNearAMarker_OpensAPopupWithItsDetail()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", title: "Loose gravel", note: "Whole corner"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.marker-head").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseViewport(UnitViewport));

		// Marker() places its pin at the origin; 0.005° on this viewport is 5 px off it.
		await component.InvokeAsync(() => _map.RaiseClick(0.005, 0.005));

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement popup = component.Find(".nearby-dialog");
			popup.TextContent.ShouldContain("Loose gravel");
			popup.TextContent.ShouldContain("Alice", customMessage: "the popup names who placed it.");
			popup.TextContent.ShouldContain("Whole corner",
				customMessage: "§16.4: tapping a pin is how the note gets read without hunting " +
				"for the marker in the sidebar list.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task TappingBareMap_OpensNoPopup()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", title: "Loose gravel"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.marker-head").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseViewport(UnitViewport));

		// 0.2° is 200 px from the pin — a tap on open road, not on anything.
		await component.InvokeAsync(() => _map.RaiseClick(0, 0.2));

		component.FindAll(".nearby-dialog").ShouldBeEmpty(
			"A tap on empty map must stay inert — a popup that opens for every tap on a live " +
			"ride map is a popup permanently in the way.");
	}

	[Fact]
	public async Task TappingTheMap_ListsEveryMarkerUnderThePoint()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", title: "Loose gravel"),
			Marker(Guid.NewGuid(), MeId, "Me", title: "Regroup here"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.marker-head").Count.ShouldBe(2),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => _map.RaiseClick(0, 0));

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement popup = component.Find(".nearby-dialog");
			popup.TextContent.ShouldContain("2 markers here",
				customMessage: "Pins pile up at low zoom — the answer is every marker under the " +
				"tap, not a guess at which one was meant.");
			popup.TextContent.ShouldContain("Loose gravel");
			popup.TextContent.ShouldContain("Regroup here");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task TappingBeforeTheMapReportsAViewport_IsInert()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.marker-head").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		// No RaiseViewport: the base map has not reported a frame, so there are no pixels to
		// measure "near" in. Answering anything here would mean answering "everything".
		await component.InvokeAsync(() => _map.RaiseClick(0, 0));

		component.FindAll(".nearby-dialog").ShouldBeEmpty();
	}

	[Fact]
	public async Task PopupCloses_WhenItsOnlyMarkerIsRemovedOnTheHub()
	{
		Guid markerId = Guid.NewGuid();
		(_, FakeRideHubClient hub, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(markerId, AliceId, "Alice", title: "Loose gravel"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.marker-head").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseViewport(UnitViewport));
		await component.InvokeAsync(() => _map.RaiseClick(0, 0));

		component.WaitForAssertion(() => component.Find(".nearby-dialog").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaiseMarkerRemoved(rideId, markerId));

		component.WaitForAssertion(() =>
			component.FindAll(".nearby-dialog").ShouldBeEmpty(
				"The popup holds ids, not copies — a marker deleted from under it takes its " +
				"card with it rather than leaving detail nobody else can see."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task TappingAMarkerInTheList_CentresAndZoomsTheMapOnIt()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice", latitudeDeg: -33.868, longitudeDeg: 151.209),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() => _map.Cameras.ShouldNotBeEmpty(
			"Choosing a marker in the list must take the map to it — the list and the map are " +
			"two views of the same pins."), timeout: TimeSpan.FromSeconds(3));

		MapCamera moved = _map.Cameras[^1];
		moved.Latitude.ShouldBe(-33.868, tolerance: 1e-5);
		moved.Longitude.ShouldBe(151.209, tolerance: 1e-5);
		moved.ZoomLevel.ShouldBeGreaterThanOrEqualTo(16,
			"§16.4: 'zoom in and centre' means street level — a marker centred at continent " +
			"zoom has not been shown to anybody.");
	}

	[Fact]
	public async Task CollapsingAMarker_LeavesTheCameraAlone()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() => _map.Cameras.Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		// Same row again — this collapses it.
		await component.InvokeAsync(() => component.Find("button.marker-head").Click());

		component.FindAll(".marker-body").ShouldBeEmpty();
		_map.Cameras.Count.ShouldBe(1,
			"Closing a panel means done reading, not 'take me somewhere' — moving the camera " +
			"on the way out would yank the view from under whoever was looking at it.");
	}

	[Fact]
	public async Task ZoomingToAMarker_DoesNotPullTheViewBack()
	{
		(_, _, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(Guid.NewGuid(), AliceId, "Alice"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.marker-head").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		// Someone already zoomed in past the focus level.
		await component.InvokeAsync(() => _map.RaiseViewport(UnitViewport with { ZoomLevel = 18 }));

		await component.InvokeAsync(() => component.Find("button.marker-head").Click());

		component.WaitForAssertion(() => _map.Cameras.ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras[^1].ZoomLevel.ShouldBe(18,
			"Centring on a marker must not zoom *out* — pulling the view back throws away " +
			"detail the rider chose.");
	}

	[Fact]
	public async Task MarkerRemovedOnTheHub_ClosesTheOpenDetail()
	{
		Guid markerId = Guid.NewGuid();
		(_, FakeRideHubClient hub, _, Guid rideId) = await WireServicesAsync(new[]
		{
			Marker(markerId, AliceId, "Alice", note: "Whole corner"),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenFirstMarkerAsync(component);

		component.WaitForAssertion(() =>
			component.Markup.ShouldContain("Whole corner"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaiseMarkerRemoved(rideId, markerId));

		component.WaitForAssertion(() =>
			component.Markup.ShouldContain("Nothing marked on this ride yet",
				customMessage: "Somebody else's delete has to take the expanded detail with it — an open " +
				"panel describing a marker that no longer exists is worse than no panel."),
			timeout: TimeSpan.FromSeconds(3));
	}
}
