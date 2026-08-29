using System.Net;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Puts the adventure and the GPS back the way the app left them, once per launch (§5.6, §5.7, §18.6).
/// <para>
/// <strong>What a relaunch loses, and what it does not.</strong> A phone that ran out of battery, an
/// app Android reclaimed mid-ride, a WebView reload — the sharing flag is on the server and survives
/// every one of them; the receiver and the open screen are in the process and survive none. So the
/// rider comes back to the home screen with their pin standing still on everybody else's map, and
/// the way out is to remember to open the ride again. This is the piece that removes the
/// remembering.
/// </para>
/// <para>
/// <strong>Phones only, and the receiver is the test.</strong> This exists because the phone is
/// where the app is taken away mid-ride and handed back with the flag still standing; a browser tab
/// and a desktop head lose no receiver on a reload because they never had one, and reopening a
/// screen underneath somebody there would be a surprise bought for nothing. So the gate is
/// <see cref="LocationBroadcastState.IsSupported"/> — null on both browsers (§18.6) and false on
/// the MAUI desktop stubs, whose <c>ILocationProvider</c> is the no-op — rather than a form-factor
/// string, which answers what the screen looks like and not whether there is a GPS behind it.
/// </para>
/// <para>
/// <strong>The server is asked rather than the device.</strong> The flag is the one record of
/// whether this rider is sharing, and it is the record the fan-out actually reads — a device-local
/// copy of it could disagree, and would disagree in the direction that leaves a rider being drawn
/// while nothing is being sent. <see cref="CurrentRideState"/> holds the other half, which is only
/// ever which adventure to ask about.
/// </para>
/// <para>
/// <strong>It restores what is underway, not what once was.</strong> Draft and Open carry no
/// positions, and Archived and Cancelled never will again; a ride that finished last week is still
/// on the rail's globe for the rider to look at, but it is not a screen to be reopened on top of
/// them at every launch. What is left is Live, and Completed while this rider is still sharing —
/// which is an open wind-down (§5.6), because the sweep clears the flag when the window shuts.
/// </para>
/// <para>
/// <strong>Nothing is forgotten because the network was.</strong> Only the server saying the ride
/// is not this rider's — 404, 403, 410 — clears the remembered adventure. A tunnel leaves the whole
/// arrangement standing, to be restored by the launch that reaches the server.
/// </para>
/// </summary>
public sealed class LaunchRestore
{
	private readonly IApiClient _api;
	private readonly AuthState _auth;
	private readonly CurrentRideState _currentRide;
	private readonly LocationBroadcastState? _broadcast;

	private bool _ran;

	/// <summary>Builds the restore over the session, the remembered adventure and the receiver.</summary>
	/// <param name="api">Where the sharing flag is read from.</param>
	/// <param name="auth">Who is back — a launch with no session has nothing to restore.</param>
	/// <param name="currentRide">Which adventure this device was on.</param>
	/// <param name="broadcast">
	/// The device's receiver (§5.7), or <c>null</c> on a host with none — which is also what decides
	/// whether anything is restored at all. See the remarks.
	/// </param>
	public LaunchRestore(
		IApiClient api,
		AuthState auth,
		CurrentRideState currentRide,
		LocationBroadcastState? broadcast = null)
	{
		_api = api;
		_auth = auth;
		_currentRide = currentRide;
		_broadcast = broadcast;
	}

	/// <summary>Whether this launch has already been restored.</summary>
	public bool HasRun => _ran;

	/// <summary>
	/// Reads what the app was doing, starts the receiver if this rider is still sharing, and answers
	/// with the screen to reopen — or <c>null</c> when there is nothing to go back to.
	/// <para>
	/// Idempotent by the flag rather than by re-deriving the same answer: this is a launch hook, and
	/// a second render must not put the rider back on a ride screen they have since navigated off.
	/// </para>
	/// <para>
	/// <strong>The caller navigates.</strong> Whether the screen is still the app's own to claim —
	/// a tapped notification and the first-run introduction both got there first — is
	/// <c>MainLayout</c>'s question, and it is the one place that already knows the answer.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Abandons the restore.</param>
	/// <returns>The route to the adventure that is underway, or <c>null</c>.</returns>
	public async Task<string?> RestoreAsync(CancellationToken cancellationToken = default)
	{
		if (_ran)
			return null;

		_ran = true;

		// Not a phone — see the remarks. First, because it settles the whole question.
		if (_broadcast is not { IsSupported: true })
		{
			DiagnosticLog.Write("Startup: no GPS on this device, so nothing is restored.");
			return null;
		}

		if (_auth.UserId is null)
		{
			DiagnosticLog.Write("Startup: no session, so nothing is restored.");
			return null;
		}

		await _currentRide.LoadAsync(cancellationToken);

		if (_currentRide.RideId is not { } rideId)
		{
			DiagnosticLog.Write("Startup: this device has no adventure to go back to.");
			return null;
		}

		DiagnosticLog.Write($"Startup: checking the last adventure ({rideId}).");

		RideDetail ride;

		try
		{
			ride = await _api.GetRideAsync(rideId, cancellationToken);
		}
		catch (ApiException refused) when (refused.Error.StatusCode
			is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
		{
			// Deleted, or this rider is off it. Same answer as a load that 404s on the ride screen.
			DiagnosticLog.Write("Startup: the last adventure is gone; forgetting it.");
			await _currentRide.ForgetAsync(rideId, cancellationToken);
			return null;
		}
		catch (Exception failure)
		{
			DiagnosticLog.Write($"Startup: could not check the last adventure: {failure.Message}.");
			return null;
		}

		bool sharing = ride.Members.FirstOrDefault(member => member.UserId == _auth.UserId)?.Sharing ?? false;

		bool underway = ride.State is RideStateDto.Live
			|| (ride.State is RideStateDto.Completed && sharing);

		if (!underway)
		{
			DiagnosticLog.Write($"Startup: the last adventure is {ride.State}; nothing to restore.");
			return null;
		}

		if (!sharing)
		{
			DiagnosticLog.Write("Startup: the last adventure is underway, but sharing is off.");
		}
		else
		{
			DiagnosticLog.Write("Startup: still sharing with the last adventure; starting the GPS.");

			// Not awaited: bringing the receiver up can put the background-location disclosure on
			// screen and can wait on a platform permission dialog, and the launch is not held
			// behind either. It reports itself through LocationBroadcastState.
			_ = _broadcast.ShareWithAsync(rideId);
		}

		return $"{CurrentRideState.PickRideHref}/live/{rideId}";
	}
}
