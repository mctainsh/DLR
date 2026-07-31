using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// <c>TimeProvider</c> everywhere; never the ambient system clock (§10.4).
/// <para>
/// The 10 s flush, the 5 s broadcast, the staleness window, access-token expiry, the
/// refresh grace window, the 24 h and 1 h token lifespans, the 1-hour last-active
/// throttle, the 24-hour registration ladder and the 180-day inactivity sweep are all
/// verified by advancing a fake clock. One ambient read is enough to make a test either
/// flaky or six months long, so the rule is enforced rather than encouraged.
/// </para>
/// </summary>
public sealed class ClockRules
{
	/// <summary>
	/// DLR.TestSupport is the one project allowed to read the real clock — it is where
	/// the fake one is anchored.
	/// </summary>
	private const string ExemptProject = "tests/DLR.TestSupport/";

	private static readonly string[] AmbientClockReads =
	[
		"DateTime.Now",
		"DateTime.UtcNow",
		"DateTime.Today",
		"DateTimeOffset.Now",
		"DateTimeOffset.UtcNow",
	];

	[Fact]
	public void NoAssemblyReadsTheAmbientClockOutsideTestSupport()
	{
		string[] forbidden =
		[
			"System.DateTime.get_Now",
			"System.DateTime.get_UtcNow",
			"System.DateTime.get_Today",
			"System.DateTimeOffset.get_Now",
			"System.DateTimeOffset.get_UtcNow",
		];

		List<string> offenders = CompiledAssembly.All
			.Where(assembly => assembly.Name != "DLR.TestSupport")
			.SelectMany(assembly => assembly.MemberReferences
				.Where(member => forbidden.Contains(member, StringComparer.Ordinal))
				.Select(member => $"{assembly.Name} → {member}"))
			.ToList();

		offenders.ShouldBeEmpty(
			"Resolve TimeProvider and call GetUtcNow() instead. Registering TimeProvider in DI " +
			"from day one is what makes the timing-heavy half of this project testable at all.");
	}

	[Fact]
	public void NoSourceFileReadsTheAmbientClockOutsideTestSupport()
	{
		List<string> offenders = SourceTree.Files
			.Where(file => !file.Path.StartsWith(ExemptProject, StringComparison.Ordinal))
			.SelectMany(file => file.Cite(file.LinesContaining(AmbientClockReads)))
			.ToList();

		offenders.ShouldBeEmpty(
			"The metadata rule catches compiled calls; this one catches the same mistake in " +
			"projects the architecture tests do not reference, which is most of them.");
	}
}
