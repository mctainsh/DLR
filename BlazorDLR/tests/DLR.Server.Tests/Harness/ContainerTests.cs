using DLR.Server.Data;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Harness;

/// <summary>
/// The container is validated in every environment, not just Development (§10.4 by extension).
/// <para>
/// This exists because it was missed. <c>DummyPasswordVerifier</c> shipped as a singleton
/// holding a scoped <c>IPasswordHasher</c>, the whole suite stayed green, and the first thing
/// to notice was <c>dotnet run</c> - because the default arrangement validates the graph only
/// under Development, and the test host runs as "Testing".
/// </para>
/// <para>
/// Every server test now builds a validated container, so that class of mistake cannot reach a
/// commit again. What follows is the guard on the guard: without it, deleting
/// <c>UseDefaultServiceProvider</c> from <c>Program</c> would restore the blind spot and no
/// test would say so.
/// </para>
/// </summary>
public sealed class ContainerTests(PostgresFixture postgres)
{
	/// <summary>
	/// Resolving a scoped service from the root provider throws when scope validation is on,
	/// and quietly hands back a captive instance when it is off. The throw is the assertion.
	/// </summary>
	[Fact]
	public async Task Container_ScopeValidation_IsOnInEveryEnvironment()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		Exception refused = Should.Throw<InvalidOperationException>(
			() => app.Services.GetRequiredService<DlrDbContext>());

		refused.Message.ShouldContain("scoped",
			Case.Insensitive,
			"a captive DbContext is one connection shared by every caller, and it is invisible " +
			"until load arrives");
	}
}
