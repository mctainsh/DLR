using System.Net;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Puts the adventure and the GPS back the way the app left them, once per launch (§5.6, §5.7, §18.6).
/// <para>
/// The sharing flag is on the server and survives the process; the receiver and the open screen do
/// not. So the server is asked what this rider is still sharing with, and only a phone is asked at
/// all — <see cref="LocationBroadcastState.IsSupported"/> is the gate, which is null on both
/// browsers and false on the MAUI desktop stubs (§18.6).
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
	/// <param name="broadcast">The device's receiver (§5.7), or <c>null</c> on a host with none.</param>
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

	/// <summary>
	/// Starts the receiver if this rider is still sharing, and answers with the screen to reopen.
	/// <para>
	/// Once per launch by the flag rather than by re-deriving the answer: a second render must not
	/// put the rider back on a ride screen they have since navigated off. The caller navigates,
	/// because whether the screen is still the app's own to claim is <c>MainLayout</c>'s question.
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
