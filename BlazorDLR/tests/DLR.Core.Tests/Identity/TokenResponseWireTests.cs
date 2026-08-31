using System.Text.Json;
using DLR.Core.Contracts.Identity;

namespace DLR.Core.Tests.Identity;

/// <summary>
/// <see cref="TokenResponse.DeviceId"/> was added to a wire contract that had already shipped
/// (§7.10), so both halves of the mismatch have to keep working: a phone on the store that has
/// never heard of the field, and a build of the app that meets a server not yet deployed.
/// </summary>
public sealed class TokenResponseWireTests
{
	private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

	/// <summary>
	/// The shape a client compiled before the field still declares. Reading the new payload into
	/// it must not throw — which it would the moment anything set
	/// <c>JsonUnmappedMemberHandling.Disallow</c>, and that is what this pins.
	/// </summary>
	private sealed record OlderContract(
		string AccessToken,
		int ExpiresIn,
		string RefreshToken,
		AuthenticatedUser User);

	[Fact]
	public void AnOlderClient_IgnoresTheFieldItHasNeverHeardOf()
	{
		string payload = JsonSerializer.Serialize(
			new TokenResponse(
				"access",
				900,
				"refresh",
				new AuthenticatedUser(Guid.NewGuid(), "DaveSmith", HasEmail: false, EmailConfirmed: false),
				Guid.NewGuid()),
			Web);

		OlderContract older = JsonSerializer.Deserialize<OlderContract>(payload, Web)!;

		older.AccessToken.ShouldBe("access");
		older.User.UserName.ShouldBe("DaveSmith");
	}

	/// <summary>
	/// And the other way: an answer from a server that predates the field reads back as no device
	/// at all, which is what <c>AuthState</c> treats as "this installation has nothing to claim" —
	/// the behaviour every client had before any of this.
	/// </summary>
	[Fact]
	public void AnOlderServersAnswer_LeavesTheDeviceUnknownRatherThanFailing()
	{
		const string payload = """
			{
				"accessToken": "access",
				"expiresIn": 900,
				"refreshToken": "refresh",
				"user": {
					"id": "11111111-1111-1111-1111-111111111111",
					"userName": "DaveSmith",
					"hasEmail": false,
					"emailConfirmed": false
				}
			}
			""";

		TokenResponse session = JsonSerializer.Deserialize<TokenResponse>(payload, Web)!;

		session.AccessToken.ShouldBe("access");
		session.DeviceId.ShouldBe(Guid.Empty,
			"no device id on the wire is not an error — it is a server that cannot name one yet");
	}
}
