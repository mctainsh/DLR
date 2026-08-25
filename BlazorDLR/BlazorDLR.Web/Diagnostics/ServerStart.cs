namespace DLR.Server.Diagnostics;

/// <summary>
/// When this run began, and how long ago that was (§14.6).
/// <para>
/// One owner for the fact, because two things need it and they need to agree: the startup block
/// stamps it and ages the process each time a new day's file is opened, and
/// <see cref="ServerLifetimeLog"/> reports the same age in the line that closes the file. Two
/// independent anchors are two answers to "how long has this been up", and the reader has no way
/// to tell which is the real one.
/// </para>
/// <para>
/// A singleton rather than a static, so a suite that builds a host per test class gets a fresh
/// anchor with its own <c>FakeTimeProvider</c> rather than whichever one happened to run first.
/// </para>
/// </summary>
/// <param name="clock">Read once at construction, and again for each age (§10.4).</param>
public sealed class ServerStart(TimeProvider clock)
{
	/// <summary>
	/// The instant the container first needed this.
	/// <para>
	/// An offset rather than a <c>DateTime</c>: an unspecified-kind value converts to one by
	/// assuming the <em>machine's</em> zone, so on any server that is not on UTC both the stamp and
	/// the uptime beside it come out shifted by that zone's offset.
	/// </para>
	/// </summary>
	public DateTimeOffset Utc { get; } = clock.GetUtcNow();

	/// <summary>How long this run has served for.</summary>
	public TimeSpan Uptime => clock.GetUtcNow() - Utc;
}
