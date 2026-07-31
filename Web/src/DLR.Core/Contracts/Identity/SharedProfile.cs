using System.Text.Json.Serialization;

namespace DLR.Core.Contracts.Identity;

/// <summary>
/// What one rider is allowed to see of another (§7.3).
/// <para>
/// Three booleans defaulting to false are trivial to get right at creation and easy to get
/// wrong on a read path. One forgetful DTO mapper leaks a phone number, and there is no way to
/// un-leak it — so the defence is structural rather than conventional: the properties are
/// <c>private init</c>, which makes <see cref="For"/> the only thing in any assembly that can
/// build one, and <see cref="For"/> cannot be called without stating the viewer's relationship
/// to the owner.
/// </para>
/// </summary>
public sealed record SharedProfile
{
	/// <summary>
	/// Appears in a ride's member list <em>beside</em> the username, never instead of it.
	/// <para>
	/// Map pins and the position batch always carry the immutable username (§7.2), which is
	/// what lets a client denormalise it forever with no invalidation — and what stops a rider
	/// labelling themselves <c>RideLeader</c> on somebody else's map.
	/// </para>
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? DisplayName { get; private init; }

	/// <summary>
	/// Never verified, and never a gate. Tapping to call a rider mid-ride is the whole use.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? PhoneNumber { get; private init; }

	/// <summary>
	/// Sharing this exposes the account's recovery address — the rider is telling a ride full
	/// of people which mailbox to attack in order to reset the password. Not a reason to
	/// forbid it; a reason for the UI to say so plainly next to the switch.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Email { get; private init; }

	/// <summary>Nothing shared, for a viewer with no claim on any of it.</summary>
	public static readonly SharedProfile Empty = new();

	/// <summary>
	/// The only way to build one.
	/// </summary>
	/// <param name="owner">Whose profile.</param>
	/// <param name="viewerSharesActiveRide">
	/// Whether the viewer is <em>currently</em> a co-member of a group ride with the owner
	/// (§7.3). Leaving the ride, being removed, or the ride completing all end access — and
	/// unlike position sharing, profile sharing does not follow the wind-down (§5.6). The
	/// wind-down exists so people can watch each other get home; there is no equivalent reason
	/// to keep a phone number visible for two more hours.
	/// </param>
	public static SharedProfile For(IProfileOwner owner, bool viewerSharesActiveRide) =>
		!viewerSharesActiveRide
			? Empty
			: new SharedProfile
			{
				DisplayName = owner.ShareDisplayName ? Blank(owner.DisplayName) : null,
				PhoneNumber = owner.SharePhoneNumber ? Blank(owner.PhoneNumber) : null,
				Email = owner.ShareEmail ? Blank(owner.Email) : null,
			};

	/// <summary>
	/// Empty is not a value. A recorded-but-blank field would serialise as <c>""</c> while an
	/// absent one is omitted, which is exactly the distinction §7.3 says must not be visible.
	/// </summary>
	private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// The half of an account <see cref="SharedProfile"/> reads.
/// <para>
/// An interface rather than the entity, so the rule lives in <c>DLR.Core</c> with the type it
/// protects and every host compiles against the same one — and so nothing has to reference the
/// persistence assembly in order to describe what a viewer may see.
/// </para>
/// </summary>
public interface IProfileOwner
{
	/// <summary>Optional, and not the map label.</summary>
	string? DisplayName { get; }

	/// <summary>Optional, never verified.</summary>
	string? PhoneNumber { get; }

	/// <summary>Optional; also the recovery address (§7.7).</summary>
	string? Email { get; }

	/// <summary>Whether co-members may see the display name. Off by default.</summary>
	bool ShareDisplayName { get; }

	/// <summary>Whether co-members may see the phone number. Off by default.</summary>
	bool SharePhoneNumber { get; }

	/// <summary>Whether co-members may see the email address. Off by default.</summary>
	bool ShareEmail { get; }
}
