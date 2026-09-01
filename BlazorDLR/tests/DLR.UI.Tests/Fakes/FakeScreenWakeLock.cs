using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An <see cref="IScreenWakeLock"/> that counts holders instead of touching a window. Mirrors the
/// mobile binding's reference counting, because that is the behaviour a page has to get right -
/// leaving the live map and coming back must not strand the screen either way.
/// </summary>
public sealed class FakeScreenWakeLock : IScreenWakeLock
{
	/// <summary>Whether this host can hold its screen on. Settable, so a test can play the browser (§18.6).</summary>
	public bool IsSupported { get; set; } = true;

	/// <summary>How many holders there currently are - zero means the screen is the rider's again.</summary>
	public int Holders { get; private set; }

	/// <summary>Whether the screen is being held right now.</summary>
	public bool IsHeld => Holders > 0;

	/// <summary>Every request ever made, so a test can tell "still held" from "taken twice".</summary>
	public int RequestCount { get; private set; }

	/// <summary>Every release ever made.</summary>
	public int ReleaseCount { get; private set; }

	public ValueTask RequestAsync(CancellationToken cancellationToken = default)
	{
		RequestCount++;
		Holders++;
		return ValueTask.CompletedTask;
	}

	public ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
	{
		ReleaseCount++;

		// Matches the real store: a release with nothing held is ignored rather than counted
		// negative, so an unbalanced caller cannot stop the next holder taking it.
		if (Holders > 0)
		{
			Holders--;
		}

		return ValueTask.CompletedTask;
	}
}
