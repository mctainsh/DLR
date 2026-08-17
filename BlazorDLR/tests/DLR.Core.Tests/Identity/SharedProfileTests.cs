using System.Text.Json;
using DLR.Core.Contracts.Identity;

namespace DLR.Core.Tests.Identity;

/// <summary>
/// The one factory that decides what a rider may see of another rider (§7.3).
/// <para>
/// Tested here rather than through an endpoint because there is nothing to reach for: no
/// database, no HTTP, no ride. That is the whole design — the rule is a pure function of the
/// owner's switches and one boolean about the viewer, so it can be exercised exhaustively and
/// cheaply, and there is no code path that reaches the wire without going through it.
/// </para>
/// </summary>
public sealed class SharedProfileTests
{
	[Fact]
	public void Profile_FreshAccount_AllThreeSharingSwitchesAreOff()
	{
		Owner fresh = new();

		fresh.ShareDisplayName.ShouldBeFalse();
		fresh.SharePhoneNumber.ShouldBeFalse();
		fresh.ShareEmail.ShouldBeFalse();

		SharedProfile shared = SharedProfile.For(fresh, viewerSharesActiveRide: true);

		shared.ShouldBe(SharedProfile.Empty,
			"a co-member of an adventure still sees nothing until the owner says otherwise");
	}

	/// <summary>
	/// A rider with no audience has nothing shared, whatever their switches say (§7.3).
	/// <para>
	/// The full co-membership tests land in SRV-21, when rides exist to be a member of. What
	/// is testable now is the rule those tests will exercise, which is the part that decides
	/// anything.
	/// </para>
	/// </summary>
	[Fact]
	public void Profile_NonCoMember_ReceivesEmptyProfile()
	{
		Owner sharing = new()
		{
			DisplayName = "Dave",
			PhoneNumber = "+61400000000",
			Email = "dave@example.com",
			ShareDisplayName = true,
			SharePhoneNumber = true,
			ShareEmail = true,
		};

		SharedProfile.For(sharing, viewerSharesActiveRide: false).ShouldBe(SharedProfile.Empty);

		SharedProfile visible = SharedProfile.For(sharing, viewerSharesActiveRide: true);

		visible.DisplayName.ShouldBe("Dave");
		visible.PhoneNumber.ShouldBe("+61400000000");
		visible.Email.ShouldBe("dave@example.com");
	}

	/// <summary>
	/// Emitting <c>phone: null</c> for withheld while omitting it for absent would leak the
	/// <em>existence</em> of a phone number — a small leak, and a completely avoidable one.
	/// </summary>
	[Fact]
	public void Profile_WithheldAndUnrecorded_AreIndistinguishableOnTheWire()
	{
		Owner withheld = new()
		{
			DisplayName = "Dave",
			PhoneNumber = "+61400000000",
			Email = "dave@example.com",
			ShareDisplayName = true,
		};

		Owner unrecorded = new() { DisplayName = "Dave", ShareDisplayName = true };

		string withheldJson = JsonSerializer.Serialize(
			SharedProfile.For(withheld, viewerSharesActiveRide: true));

		string unrecordedJson = JsonSerializer.Serialize(
			SharedProfile.For(unrecorded, viewerSharesActiveRide: true));

		withheldJson.ShouldBe(unrecordedJson,
			"one traveller has a phone number and is not sharing it, the other has none at all — " +
			"and the wire must not be able to tell you which");

		withheldJson.ShouldNotContain("PhoneNumber");
		withheldJson.ShouldNotContain("null");
	}

	[Theory]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(false, false, true)]
	public void Profile_EachSwitchGovernsOnlyItsOwnField(bool name, bool phone, bool email)
	{
		Owner owner = new()
		{
			DisplayName = "Dave",
			PhoneNumber = "+61400000000",
			Email = "dave@example.com",
			ShareDisplayName = name,
			SharePhoneNumber = phone,
			ShareEmail = email,
		};

		SharedProfile shared = SharedProfile.For(owner, viewerSharesActiveRide: true);

		(shared.DisplayName is not null).ShouldBe(name);
		(shared.PhoneNumber is not null).ShouldBe(phone);
		(shared.Email is not null).ShouldBe(email);
	}

	/// <summary>
	/// A recorded-but-blank value would serialise as <c>""</c> while an absent one is omitted,
	/// which is the same distinction the test above rules out, arriving by a different route.
	/// </summary>
	[Fact]
	public void Profile_BlankValues_AreTreatedAsAbsent()
	{
		Owner owner = new()
		{
			DisplayName = "   ",
			PhoneNumber = string.Empty,
			ShareDisplayName = true,
			SharePhoneNumber = true,
		};

		SharedProfile shared = SharedProfile.For(owner, viewerSharesActiveRide: true);

		shared.ShouldBe(SharedProfile.Empty);
	}

	private sealed class Owner : IProfileOwner
	{
		public string? DisplayName { get; init; }

		public string? PhoneNumber { get; init; }

		public string? Email { get; init; }

		public bool ShareDisplayName { get; init; }

		public bool SharePhoneNumber { get; init; }

		public bool ShareEmail { get; init; }
	}
}
