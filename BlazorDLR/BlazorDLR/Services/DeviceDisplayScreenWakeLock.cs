using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The mobile binding for <see cref="IScreenWakeLock"/> - MAUI's
/// <see cref="IDeviceDisplay.KeepScreenOn"/>, which is <c>UIApplication.IdleTimerDisabled</c> on
/// iOS and the <c>FLAG_KEEP_SCREEN_ON</c> window flag on Android (§4.3).
/// <para>
/// <strong>Both of those are the right primitive rather than the strong one.</strong> Neither
/// holds the CPU, neither survives the app being backgrounded, and neither needs a permission -
/// Android's <c>WAKE_LOCK</c> is for <c>PowerManager</c>, which this deliberately does not use, so
/// the manifest is untouched. A rider who puts the phone away gets their battery back, and the
/// fixes keep coming from the receiver's foreground service, which is the component that <em>is</em>
/// entitled to run with the screen off.
/// </para>
/// <para>
/// <strong>Registered as a singleton</strong>, for the same reason <c>ILocationProvider</c> is:
/// there is one window and one flag on it, so there has to be one thing counting who has asked for
/// it. A second instance would be a second count of one shared piece of state.
/// </para>
/// </summary>
public sealed class DeviceDisplayScreenWakeLock : IScreenWakeLock
{
	/// <summary>
	/// Guards <see cref="_holders"/> only. Deliberately never held across the platform call below:
	/// that one hops to the main thread, and a lock held across a thread hop that the main thread
	/// could itself be waiting behind is a deadlock waiting for the right moment.
	/// </summary>
	private readonly Lock _gate = new();

	/// <summary>How many callers currently want the screen on. See <see cref="IScreenWakeLock"/>.</summary>
	private int _holders;

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public async ValueTask RequestAsync(CancellationToken cancellationToken = default)
	{
		bool first;
		lock (_gate)
		{
			first = ++_holders == 1;
		}

		if (first)
		{
			await ApplyAsync(true);
		}
	}

	/// <inheritdoc />
	public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
	{
		bool last = false;
		lock (_gate)
		{
			if (_holders > 0)
			{
				last = --_holders == 0;
			}
		}

		if (last)
		{
			await ApplyAsync(false);
		}
	}

	/// <summary>
	/// Sets the flag, on the main thread and without ever letting a failure out.
	/// <para>
	/// Both platforms reach a window or an application object here, and both want the UI thread to
	/// do it - the same marshalling the location providers do for permissions. A window that has
	/// gone away underneath the call throws, and there is nothing useful to do with that: the map
	/// is already on screen and working, and "the screen may turn itself off" is not a sentence
	/// worth putting in front of a rider, because they cannot act on it either.
	/// </para>
	/// </summary>
	/// <param name="on">True to hold the screen on, false to hand it back to the display timeout.</param>
	private static async Task ApplyAsync(bool on)
	{
		try
		{
			await MainThread.InvokeOnMainThreadAsync(() => DeviceDisplay.Current.KeepScreenOn = on);
		}
		catch (Exception)
		{
			// See the remarks. Swallowed deliberately, and the count above is left as it is: the
			// next balanced pair applies the flag again, which is the recovery.
		}
	}
}
