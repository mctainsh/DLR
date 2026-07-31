using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// Raw SQL lives in three folders and nowhere else (§10.4).
/// <para>
/// The position writer's <c>UNNEST</c> upsert earns its exemption — one command for every
/// dirty rider, with a <c>WHERE</c> guard that will not overwrite a newer row (§5.5). The
/// identity and maintenance folders earn theirs for the registration ladder's row count
/// and the nightly sweep's set-based deletes. Everywhere else, a hand-written query is a
/// missed opportunity to let EF Core keep the model and the schema in step.
/// </para>
/// </summary>
public sealed class SqlRules
{
	private static readonly string[] PermittedFolders =
	[
		"src/DLR.Server/Positions/",
		"src/DLR.Server/Identity/",
		"src/DLR.Server/Maintenance/",
	];

	/// <summary>
	/// Ways of executing SQL that was not built by the query translator. Schema
	/// declarations — a filtered index, a check constraint, a migration's own DDL — are
	/// deliberately not on this list: they describe the database rather than query it,
	/// and EF Core has no non-string way to express them.
	/// </summary>
	private static readonly string[] RawSqlEntryPoints =
	[
		"FromSqlRaw",
		"FromSqlInterpolated",
		"SqlQueryRaw",
		"ExecuteSqlRaw",
		"ExecuteSqlInterpolated",
		"NpgsqlCommand",
		"CreateCommand(",
	];

	/// <summary>
	/// The rule governs production code. A test harness that has to <c>CREATE DATABASE</c>
	/// before EF Core has anything to connect to is doing something EF Core cannot express,
	/// and constraining it buys nothing — §10.4 names three folders, all of them under
	/// <c>src/</c>.
	/// </summary>
	[Fact]
	public void RawSqlAppearsOnlyInThePermittedFolders()
	{
		List<string> offenders = SourceTree.Under("src/")
			.Where(file => !PermittedFolders.Any(folder => file.Path.StartsWith(folder, StringComparison.Ordinal)))
			.SelectMany(file => file.Cite(file.LinesContaining(RawSqlEntryPoints)))
			.ToList();

		offenders.ShouldBeEmpty(
			$"Raw SQL belongs in {string.Join(", ", PermittedFolders)} and nowhere else. " +
			"If a query genuinely needs it, move the query — do not widen the rule.");
	}
}
