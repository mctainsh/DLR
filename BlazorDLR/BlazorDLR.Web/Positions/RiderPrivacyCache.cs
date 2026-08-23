using System.Collections.Concurrent;

namespace DLR.Server.Positions;

/// <summary>
/// Which riders are currently inside their own private area (§10.1, §5.6).
/// <para>
/// <strong>The one bit a private area ever puts on the wire.</strong> The circle itself — where it
/// is and how big it is — stays on the rider's profile and reaches no other rider; what a ride
/// learns is that somebody is somewhere they chose not to be observed, which is the difference
/// between a group waiting at a junction for a rider who is in their kitchen and one that does not.
/// Nothing here can be turned back into a coordinate, because nothing here holds one.
/// </para>
/// <para>
/// <strong>Memory, deliberately, and never a column.</strong> This is live presence in exactly the
/// sense <see cref="RiderPositionCache"/>'s entries are, and it has a shorter life than they do: a
/// rider is private for the ten minutes between their driveway and the main road. Persisting it
/// would give the database a durable record of when each account was at home, which is a weaker
/// version of the very thing the feature exists to withhold — so a process restart forgets, and the
/// next fix or the next entry into the area says it again.
/// </para>
/// <para>
/// <strong>Per rider, not per ride.</strong> The private area is a property of the person, not of
/// one adventure, and a device publishes once for all of them (§5.7). Which rides are <em>told</em>
/// is still the server's decision, applied on the way out in <see cref="PositionStore"/> against
/// each ride's own consent flag.
/// </para>
/// </summary>
public sealed class RiderPrivacyCache
{
	private readonly ConcurrentDictionary<Guid, byte> hidden = new();

	/// <summary>Whether this rider is inside their private area right now.</summary>
	/// <param name="userId">Which rider.</param>
	/// <returns><c>true</c> while they are private.</returns>
	public bool IsPrivate(Guid userId) => hidden.ContainsKey(userId);

	/// <summary>
	/// Records where a rider now is relative to their circle.
	/// </summary>
	/// <param name="userId">Which rider.</param>
	/// <param name="isPrivate">True on the way in, false on the way out.</param>
	/// <returns>
	/// <c>true</c> when this actually changed something — which is what the caller announces on. A
	/// device re-stating what the server already believes (after a hub reconnect, say) must not put a
	/// message on every member's connection for a fact none of them would redraw.
	/// </returns>
	public bool Set(Guid userId, bool isPrivate) =>
		isPrivate ? hidden.TryAdd(userId, 0) : hidden.TryRemove(userId, out _);

	/// <summary>
	/// Everyone currently private, for building a member list (§5.2).
	/// <para>
	/// A copy rather than a live view: a snapshot is read once per member and must not change
	/// underneath the loop that reads it, or one list could describe two different moments.
	/// </para>
	/// </summary>
	/// <returns>The riders who are private.</returns>
	public IReadOnlySet<Guid> Everyone() => hidden.Keys.ToHashSet();
}
