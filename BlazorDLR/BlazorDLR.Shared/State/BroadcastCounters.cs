using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The running totals for one watch, and the single log line that states them (§4.2, §4.3).
/// <para>
/// <strong>Why the log needed counting rather than more events.</strong> A working broadcast is
/// silent: <c>LocationBroadcastState.Set</c> is a no-op when the status does not move, so a run of
/// fixes publishing normally and a receiver that has stopped delivering anything at all leave
/// exactly the same log - nothing. That ambiguity cost a morning, and it is what these answer.
/// </para>
/// <para>
/// Written through <see cref="DiagnosticLog.WriteTransient"/>, so the whole of a ride's traffic
/// occupies one line that the next update overwrites, and what settles permanently into the log is
/// the totals as they stood at each thing that did get its own line.
/// </para>
/// </summary>
internal sealed class BroadcastCounters
{
	private int _fixes;
	private int _hidden;
	private int _refused;
	private int _keepalives;
	private int _sends;
	private int _landed;
	private int _superseded;
	private int _failed;

	private volatile string _reason = nameof(PositionGateReason.Accepted);

	/// <summary>A fix the platform produced, whatever became of it.</summary>
	public void Fix() => Interlocked.Increment(ref _fixes);

	/// <summary>A fix dropped by the rider's private area (§10.1).</summary>
	public void Hidden() => Interlocked.Increment(ref _hidden);

	/// <summary>A fix the §4.2 gate refused, and the rule that refused it.</summary>
	/// <param name="reason">Which rule decided.</param>
	public void Refused(PositionGateReason reason)
	{
		Interlocked.Increment(ref _refused);
		_reason = reason.ToString();
	}

	/// <summary>The last good fix restated because the receiver has gone quiet.</summary>
	public void Keepalive() => Interlocked.Increment(ref _keepalives);

	/// <summary>A send starting, on either transport.</summary>
	public void Send() => Interlocked.Increment(ref _sends);

	/// <summary>A send the server acknowledged.</summary>
	public void Landed() => Interlocked.Increment(ref _landed);

	/// <summary>A send abandoned because a newer fix was already waiting.</summary>
	public void Superseded() => Interlocked.Increment(ref _superseded);

	/// <summary>A send both transports refused.</summary>
	public void Failed() => Interlocked.Increment(ref _failed);

	/// <summary>
	/// States the totals in the log, replacing whatever the last such line said.
	/// </summary>
	/// <param name="sinceLanded">How long ago a fix last reached the ride, or null if none has.</param>
	public void Report(TimeSpan? sinceLanded) => DiagnosticLog.WriteTransient(Describe(sinceLanded));

	/// <summary>The line itself, separated from the writing of it so a test can read it.</summary>
	/// <param name="sinceLanded">How long ago a fix last reached the ride, or null if none has.</param>
	/// <returns>One line.</returns>
	public string Describe(TimeSpan? sinceLanded) =>
		$"GPS totals: {Volatile.Read(ref _fixes)} fix, {Volatile.Read(ref _hidden)} hidden, " +
		$"{Volatile.Read(ref _refused)} refused ({_reason}), {Volatile.Read(ref _keepalives)} keepalive, " +
		$"{Volatile.Read(ref _sends)} sent - {Volatile.Read(ref _landed)} landed, " +
		$"{Volatile.Read(ref _superseded)} superseded, {Volatile.Read(ref _failed)} failed; " +
		(sinceLanded is { } age
			? $"last reached the adventure {Since(age)} ago."
			: "nothing has reached the adventure yet.");

	/// <summary>
	/// An age a person reads at a glance: seconds while that is the useful unit, minutes after.
	/// </summary>
	/// <param name="age">How long.</param>
	/// <returns>The age in words.</returns>
	public static string Since(TimeSpan age)
	{
		if (age < TimeSpan.Zero)
			age = TimeSpan.Zero;

		return age < TimeSpan.FromSeconds(90)
			? $"{age.TotalSeconds:0} s"
			: $"{age.TotalMinutes:0} min";
	}
}
