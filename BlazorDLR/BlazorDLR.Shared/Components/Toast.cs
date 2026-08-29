namespace BlazorDLR.Shared.Components;

/// <summary>
/// A sentence that puts itself away: what a screen is saying right now, and the one timer that
/// takes it back off.
/// <para>
/// The timer is restarted rather than queued, and the messages are counted rather than compared by
/// text: two changes in quick succession are one rider changing their mind, and a rider who sees
/// the same words twice inside the window would otherwise have the first timer clear the second
/// one's message early.
/// </para>
/// <para>
/// Not a component. The two screens that show one draw it differently — over the map, and beside
/// the rail — so what is shared is the mechanism and not the markup.
/// </para>
/// </summary>
public sealed class Toast : IDisposable
{
	/// <summary>
	/// How long a message stands, everywhere one is shown. Long enough to be read at a glance by
	/// somebody who is also riding a bike, short enough that it is gone before it becomes furniture.
	/// </summary>
	public static readonly TimeSpan Duration = TimeSpan.FromSeconds(3.5);

	private readonly TimeProvider _clock;
	private readonly Action<Action> _dispatch;

	private ITimer? _timer;
	private int _sequence;

	/// <summary>Creates a slot for one message at a time.</summary>
	/// <param name="clock">Where the timer comes from, so a test advances it rather than sleeping.</param>
	/// <param name="dispatch">
	/// Runs the callback on the owner's renderer and redraws — the timer fires on a thread pool
	/// thread, and Blazor throws if a component's state is touched from off its context.
	/// </param>
	public Toast(TimeProvider clock, Action<Action> dispatch)
	{
		_clock = clock;
		_dispatch = dispatch;
	}

	/// <summary>What is on screen, or <c>null</c> when there is nothing to say.</summary>
	public string? Text { get; private set; }

	/// <summary>Puts a sentence on screen for <see cref="Duration"/>.</summary>
	/// <param name="message">What to say.</param>
	public void Show(string message)
	{
		Text = message;

		int shown = ++_sequence;

		_timer?.Dispose();
		_timer = _clock.CreateTimer(
			_ => _dispatch(() =>
			{
				// A later message owns the slot now, and its own timer will clear it.
				if (_sequence == shown)
				{
					Text = null;
				}
			}),
			state: null,
			dueTime: Duration,
			period: Timeout.InfiniteTimeSpan);
	}

	/// <summary>
	/// Takes the message off now — what a screen calls when what it was describing is gone. The
	/// count moves too, so the timer already running does not clear a message shown after this.
	/// </summary>
	public void Clear()
	{
		_sequence++;
		Text = null;
	}

	/// <summary>
	/// Stops the timer. Callers must do this <em>before</em> anything that can await on their way
	/// out: the callback renders, and a render after the owner has gone is an
	/// <see cref="ObjectDisposedException"/> out of a timer nobody is holding.
	/// </summary>
	public void Dispose()
	{
		_timer?.Dispose();
		_timer = null;
	}
}
