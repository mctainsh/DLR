using DLR.Core.Contracts.Announcements;

namespace DLR.Core.Client;

/// <summary>
/// Which client builds this server will still talk to (§20.1).
/// <para>
/// <strong>Constants, not configuration.</strong> The floor is a property of the build that raised
/// it: the release which breaks a wire contract is the release that moves <see cref="Minimum"/>, in
/// the same commit. A setting would let the two drift, and a database row would let anyone with an
/// administrator account lock every rider out of a running server.
/// </para>
/// <para>
/// <strong>The server decides, not the client.</strong> The client sends the version it is and gets
/// back a verdict, so a later rule it has never heard of - a per-platform floor, a specific build
/// recalled - takes effect without a client release.
/// </para>
/// </summary>
public static class ClientRelease
{
	/// <summary>
	/// Below this, the app is walled off: it can no longer talk to this server correctly and
	/// carrying on would only produce failures the rider cannot act on.
	/// </summary>
	public static readonly Version Minimum = new(8, 0, 0, 30);

	/// <summary>
	/// Below this, the app works but is behind. The rider is offered an update once and may
	/// dismiss it.
	/// </summary>
	public static readonly Version Recommended = new(8, 0, 0, 30);

	/// <summary>The two above as the wire carries them. Cached: every launch reads both.</summary>
	public static readonly string MinimumText = Minimum.ToString();

	/// <inheritdoc cref="MinimumText"/>
	public static readonly string RecommendedText = Recommended.ToString();

	/// <summary>Where an Android rider gets a newer build.</summary>
	public const string PlayStoreUrl = "https://play.google.com/store/apps/details?id=au.com.securehub.dlr.v2";

	/// <summary>
	/// Where an iOS rider gets one - unknown until the listing exists, and null rather than a
	/// guessed URL, because a button that lands nowhere is worse than the sentence beside it.
	/// </summary>
	public const string? AppStoreUrl = null;

	/// <summary>Whether a client of this version is supported, behind, or too old to serve.</summary>
	/// <param name="client">What the client says it is, or null when it said nothing.</param>
	public static ClientSupport Check(Version? client) => Check(client, Minimum, Recommended);

	/// <summary>
	/// The rule itself, against bounds given rather than the shipping ones.
	/// <para>
	/// The two constants are equal in any build that has not had to raise the floor, so the
	/// <see cref="ClientSupport.UpdateAvailable"/> band between them cannot be reached through the
	/// overload above. This is how it is tested - and it is the band that drives the update offer,
	/// so it is worth being able to test.
	/// </para>
	/// </summary>
	/// <param name="client">What the client says it is, or null when it said nothing.</param>
	/// <param name="minimum">The floor.</param>
	/// <param name="recommended">What a current build is.</param>
	public static ClientSupport Check(Version? client, Version minimum, Version recommended)
	{
		// A client that cannot say what it is, is one this server cannot vouch for. Answering
		// "supported" would make the check opt-in for exactly the builds most likely to be broken.
		if (client is null) return ClientSupport.Unsupported;

		if (client < minimum) return ClientSupport.Unsupported;

		return client < recommended ? ClientSupport.UpdateAvailable : ClientSupport.Supported;
	}

	/// <summary>Parses a version a client sent, tolerating anything unparseable as "said nothing".</summary>
	/// <param name="version">The reported version, or null.</param>
	public static Version? Parse(string? version) =>
		Version.TryParse(version, out Version? parsed) ? parsed : null;

	/// <summary>
	/// Where this platform gets a newer build, or null for one with no store to send them to -
	/// the browsers, where the served build is always the current one anyway.
	/// </summary>
	/// <param name="platform">
	/// What <c>IFormFactor.GetPlatform()</c> reports. Matched loosely because the MAUI host appends
	/// the OS version to it ("Android - 14.0").
	/// </param>
	public static string? UpdateUrlFor(string? platform)
	{
		if (string.IsNullOrWhiteSpace(platform)) return null;

		if (platform.Contains("android", StringComparison.OrdinalIgnoreCase)) return PlayStoreUrl;

		return platform.Contains("ios", StringComparison.OrdinalIgnoreCase) ? AppStoreUrl : null;
	}
}
