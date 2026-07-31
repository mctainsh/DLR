using DLR.TestSupport.Database;

namespace DLR.Server.Tests;

/// <summary>
/// The collection every test that needs a database joins: one PostgreSQL container for
/// the whole assembly, and one throwaway database per test.
/// <para>
/// This lives here rather than in <c>DLR.TestSupport</c> because xUnit only discovers
/// collection definitions in the test assembly being run. The fixture is shared; the
/// three-line declaration is not.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
	/// <summary>The collection name, for <c>[Collection(...)]</c>.</summary>
	public const string Name = "postgres";
}
