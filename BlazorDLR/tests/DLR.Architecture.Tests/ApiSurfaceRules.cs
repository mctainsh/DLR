using System.Reflection;
using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// The account entity never reaches the wire (§7.3, §10.4).
/// <para>
/// Three sharing switches defaulting to false are easy to get right at creation and easy to get
/// wrong on a read path — one forgetful mapper returns the entity and a phone number is gone,
/// permanently. <c>SharedProfile</c> is the structural answer, and this is what stops somebody
/// going around it.
/// </para>
/// </summary>
public sealed class ApiSurfaceRules
{
	/// <summary>
	/// The persistence entity, named rather than referenced: this project does not reference
	/// <c>DLR.Server.Migrations</c>, and giving it one so a rule could be phrased in types
	/// would be a layering concession made for the benefit of a test.
	/// </summary>
	private const string EntityName = "AppUser";

	private static readonly string[] ResponseFactories =
	[
		"Results.Ok(",
		"Results.Created(",
		"Results.Accepted(",
		"Results.Json(",
	];

	/// <summary>
	/// Nothing under <c>src/</c> hands the entity to a response factory.
	/// <para>
	/// A source rule rather than a signature check, because minimal-API handlers return
	/// <c>IResult</c>: the type that would give it away has been erased by the time there is
	/// an assembly to inspect.
	/// </para>
	/// </summary>
	[Fact]
	public void NoApiSurfaceReturnsAppUser()
	{
		List<string> offenders = SourceTree.Under("src/")
			.SelectMany(file => file.Cite(file
				.LinesContaining(ResponseFactories)
				.Where(line => file.CodeLines[line - 1].Contains(EntityName, StringComparison.Ordinal))))
			.ToList();

		offenders.ShouldBeEmpty(
			$"An endpoint returning {EntityName} ships every column it has — the phone number " +
			"nobody agreed to share, the registration IP, the password hash's neighbours. " +
			"Project to a contract instead; SharedProfile.For is the one for other riders " +
			"(§7.3), and it cannot be called without saying who is asking.");
	}

	/// <summary>
	/// No contract type carries the entity either. A DTO with an <c>AppUser</c> property is the
	/// same leak wearing a wrapper.
	/// </summary>
	[Fact]
	public void NoContractTypeExposesAppUser()
	{
		Assembly contracts = typeof(Core.Contracts.Identity.SharedProfile).Assembly;

		List<string> offenders =
		[
			.. from type in contracts.GetTypes()
			   where type.IsPublic
			   from property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			   where property.PropertyType.Name.Contains(EntityName, StringComparison.Ordinal)
			   select $"{type.Name}.{property.Name}",
		];

		offenders.ShouldBeEmpty(
			$"A contract carrying {EntityName} puts the whole entity on the wire however " +
			"carefully the endpoint was written.");
	}
}
