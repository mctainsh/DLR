using System.Reflection;
using DLR.Core.Contracts.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The username is chosen once and can never be changed — no endpoint, no settings screen,
/// no support path (§7.2, §7.14).
/// <para>
/// That is not a restriction for its own sake. Immutability is what lets every client
/// denormalise a username onto a cached member row, a stored ride summary or an exported GPX
/// and never invalidate any of it; it is what stops a stale <c>unm</c> claim sitting in an
/// already-issued access token; and it is what makes a handle a stable identity for the
/// people who ride with that person. A single profile-update endpoint that accepted the field
/// would undo all three, quietly.
/// </para>
/// </summary>
public sealed class UsernameImmutabilityTests(PostgresFixture postgres)
{
	/// <summary>
	/// Endpoints where a username is an <em>input</em> rather than a change: you say who you
	/// are, and nothing about you is rewritten.
	/// <para>
	/// A named list rather than a cleverer rule, because no reliable signal distinguishes the
	/// two from a method signature — and the point of the list is that adding to it is a
	/// deliberate act somebody has to justify in a review, which is exactly what §7.14 asks
	/// for. The same shape as the licence allow-list, for the same reason.
	/// </para>
	/// </summary>
	private static readonly string[] MayAcceptAUserName =
	[
		// Choosing the name. The one moment it is writable, and permanently after that.
		RegistrationEndpoints.RegisterRouteName,

		// Proving you are its owner. §7.4's password grant identifies by handle because
		// that is the only identifier a rider knows about themselves.
		TokenEndpoints.TokenRouteName,
	];

	/// <summary>
	/// Written as a sweep over the routing table rather than as a list of endpoints, because
	/// the endpoint that breaks this rule is by definition one that does not exist yet. §7.14
	/// says any profile-update surface added later must reject or ignore the field; this is
	/// what turns that sentence into a build failure.
	/// </summary>
	[Fact]
	public async Task Username_CannotBeChangedByAnyEndpoint()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		// Touching the client is what forces the pipeline — and therefore the routing table —
		// to be built. Reading EndpointDataSource off a server that has served nothing
		// reports an empty list and passes for the wrong reason.
		using HttpClient client = app.CreateClient();
		using HttpResponseMessage warmUp = await client.GetAsync("/api/v1/about");

		EndpointDataSource routes = app.Services.GetRequiredService<EndpointDataSource>();

		routes.Endpoints.ShouldNotBeEmpty();

		List<string> offenders = [];

		foreach (Endpoint endpoint in routes.Endpoints)
		{
			if (MayAcceptAUserName.Contains(NameOf(endpoint)))
			{
				continue;
			}

			MethodInfo? handler = endpoint.Metadata.GetMetadata<MethodInfo>();

			if (handler is null)
			{
				continue;
			}

			foreach (ParameterInfo parameter in handler.GetParameters())
			{
				offenders.AddRange(
					UserNameCarrierNames(parameter.ParameterType)
						.Select(carrier => $"{endpoint.DisplayName} accepts {carrier}"));
			}
		}

		offenders.ShouldBeEmpty(
			"A username is permanent (§7.2). An endpoint that takes one is either changing " +
			"it or silently ignoring it, and the second is how the first ships. If a new " +
			"surface needs to identify a user, take the id.");
	}

	private static string? NameOf(Endpoint endpoint) =>
		endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;

	/// <summary>
	/// Properties named for a username, on the contract types an endpoint binds a body to.
	/// <para>
	/// Scoped to the contracts assembly on purpose: an endpoint's other parameters are
	/// services and framework types, and walking those would report on
	/// <c>UserManager.Options</c> rather than on anything the project wrote.
	/// </para>
	/// </summary>
	private static IEnumerable<string> UserNameCarrierNames(Type type)
	{
		if (type.Assembly != typeof(RegisterRequest).Assembly)
		{
			yield break;
		}

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.Name.Equals("UserName", StringComparison.OrdinalIgnoreCase))
			{
				yield return $"{type.Name}.{property.Name}";
			}
		}
	}
}
