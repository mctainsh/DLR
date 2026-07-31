using Microsoft.Extensions.Options;

namespace DLR.Server.Api;

/// <summary>
/// The AGPL §13 source offer, served by the running instance about itself (§14.6.2).
/// <para>
/// This is the part of choosing the AGPL that shows up in the code. Section 13 obliges
/// anyone who lets users interact with a modified version remotely to offer those users
/// the Corresponding Source of that running version — so it is a live obligation from the
/// moment the server is reachable, not a file at the repository root.
/// </para>
/// </summary>
public static class AboutEndpoints
{
	/// <summary>Maps <c>GET /api/v1/about</c>.</summary>
	public static IEndpointRouteBuilder MapAbout(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapGet("/api/v1/about", (BuildInformation build, IOptions<AboutOptions> options) =>
				Results.Ok(new AboutResponse(
					Licence: AboutOptions.Licence,
					SourceUrl: options.Value.SourceUrl,
					Commit: build.Commit,
					Version: build.Version,
					BuiltUtc: build.BuiltUtc)))
			.AllowAnonymous()
			.WithName("About")
			.WithSummary("The licence, the source repository and the exact commit of this build.");

		return endpoints;
	}
}

/// <summary>The §13 offer, as served.</summary>
/// <param name="Licence">SPDX identifier of the licence this software is under.</param>
/// <param name="SourceUrl">Where the Corresponding Source lives.</param>
/// <param name="Commit">The commit this build came from, <c>+dirty</c> if the tree was not clean.</param>
/// <param name="Version">Full informational version.</param>
/// <param name="BuiltUtc">When this build was compiled, if it recorded that.</param>
public sealed record AboutResponse(
	string Licence,
	string SourceUrl,
	string Commit,
	string Version,
	DateTimeOffset? BuiltUtc);

/// <summary>Configuration for the source offer.</summary>
public sealed class AboutOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "About";

	/// <summary>
	/// The licence is not configurable. A deployment that changed this string would be
	/// making a legal claim about software it did not license.
	/// </summary>
	public const string Licence = "AGPL-3.0-only";

	/// <summary>
	/// Where this instance's Corresponding Source can be obtained. Configurable because a
	/// fork running a modified server owes its users <em>its own</em> source, not ours —
	/// a hard-coded upstream URL would make every downstream deployment non-compliant by
	/// construction.
	/// </summary>
	public string SourceUrl { get; set; } = "https://github.com/dumbluckrides/dlr";
}
