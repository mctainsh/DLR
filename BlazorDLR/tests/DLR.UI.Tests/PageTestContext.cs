using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests;

/// <summary>
/// The base for every test that renders a routable page.
/// <para>
/// Pages open with <c>&lt;PageNav&gt;</c>, which injects <see cref="NavigationHistory"/> to
/// decide whether its back arrow steps into real in-app history or follows the page's
/// declared parent route. That is a page-wide dependency rather than any one screen's, so
/// it is registered once here instead of in each test class — a page test that forgot it
/// would fail inside Blazor's property injector with a stack trace naming the renderer and
/// not the missing service.
/// </para>
/// <para>
/// The real type, not a fake: it is a counter over
/// <see cref="Microsoft.AspNetCore.Components.NavigationManager"/> with no I/O, and bUnit
/// supplies a navigation manager already. A test that wants the "we walked here" branch
/// navigates first; one that does not gets the deep-link branch, which is the honest
/// default for a component rendered cold.
/// </para>
/// <para>
/// Component tests (<c>Components/</c>, <c>Layout/</c>) keep inheriting
/// <see cref="BunitContext"/> directly — they render below the page level, so no
/// <c>PageNav</c> appears in their tree. <c>PageNavTests</c> is the exception and inherits
/// this, since the component under test is the one that needs the service.
/// </para>
/// <para>
/// <see cref="RideSnapshotCache"/> is here for the same reason: the ride pages resolve it to keep
/// a device-local copy of the ride (§4.4), so it is a page-wide dependency rather than any one
/// screen's. It is registered over <see cref="UnavailableOfflineStore"/> — the binding the browser
/// hosts get (§18.6) — so a test gets the "this device keeps nothing" behaviour by default and
/// nothing leaks between tests through a real file. A test about the cache itself registers
/// <c>FakeOfflineStore</c> over the top.
/// </para>
/// </summary>
public abstract class PageTestContext : BunitContext
{
	/// <summary>
	/// A fixed instant for the cache's <c>CachedUtc</c> stamps — <c>ClockRules</c> forbids an
	/// ambient clock read in test source (§10.4). A test that cares about time registers its own
	/// <see cref="TimeProvider"/>, which wins: the last registration is the one resolved.
	/// </summary>
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	protected PageTestContext()
	{
		Services.AddScoped<NavigationHistory>();

		// The device store, the pack store and the tile source a RideMap resolves (§4.5). First,
		// so a suite that brings its own IDeviceSettings to assert against still wins.
		Services.AddRideMapServices();

		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));
		Services.AddScoped<RideSnapshotCache>();

		// The live map holds the screen on while it is the page (§4.3). Registered here rather
		// than per suite for the same reason as the cache — it is a dependency of a page, not of
		// one test's subject. A test about the lock registers FakeScreenWakeLock over the top.
		Services.AddScoped<IScreenWakeLock, UnavailableScreenWakeLock>();

		// The notification seams (§17.6). Page-wide in the strongest sense — MainLayout injects the
		// notifier, so every routable page has one above it — and registered over the no-op binding
		// the browsers get, so a page test raises nothing by default. CommentNotifierTests builds
		// its own over FakeNotificationService rather than resolving these.
		Services.AddScoped<INotificationService, NoopNotificationService>();
		Services.AddSingleton<NotificationRouting>();
		Services.AddSingleton<AppForegroundState>();
		Services.AddScoped<CommentNotifier>();
	}
}
