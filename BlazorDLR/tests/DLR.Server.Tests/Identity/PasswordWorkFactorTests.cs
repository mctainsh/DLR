using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// What a shipped password hash costs an attacker (§7.2).
/// <para>
/// The suite runs at <see cref="DlrWebApplicationFactory.CheapPasswordHasherIterations"/>, which
/// is a tenth of what the application ships, because hundreds of registrations at the real cost
/// is most of a minute of <c>dotnet test</c> spent re-proving PBKDF2. That trade is only safe
/// while something asserts the shipped number is still the shipped number — otherwise the day
/// somebody lowers it in production is the day the whole suite agrees with them.
/// </para>
/// <para>
/// No database and no collection: this asks what the container is configured to do, not what a
/// server does with it.
/// </para>
/// </summary>
public sealed class PasswordWorkFactorTests
{
	/// <summary>
	/// OWASP's floor for PBKDF2-HMAC-SHA512, and the framework default this project relies on
	/// rather than restating.
	/// </summary>
	private const int Floor = 100_000;

	[Fact]
	public void PasswordHasher_ShipsTheFrameworkDefaultWorkFactor()
	{
		ServiceCollection services = new();

		services.AddLogging();
		services.AddDbContext<DlrDbContext>(options => options.UseDlr("Host=nowhere;Database=none"));
		services.AddDlrIdentity();

		using ServiceProvider provider = services.BuildServiceProvider();

		PasswordHasherOptions options =
			provider.GetRequiredService<IOptions<PasswordHasherOptions>>().Value;

		options.IterationCount.ShouldBe(
			DlrWebApplicationFactory.ShippedPasswordHasherIterations,
			"AddDlrIdentity must not lower the work factor — the tests run at a tenth of it, " +
			"and a production reduction would arrive looking exactly like a green suite");

		options.IterationCount.ShouldBeGreaterThanOrEqualTo(
			Floor,
			"a hundred thousand PBKDF2 iterations is the floor a stolen hash has to clear to " +
			"be worth anything less than a fortune to crack");
	}

	/// <summary>
	/// The cheap number is only a saving; it must not become a second opinion about the
	/// algorithm, and it must stay visibly below what ships.
	/// </summary>
	[Fact]
	public void TestWorkFactor_IsLowerThanShipped()
	{
		DlrWebApplicationFactory.CheapPasswordHasherIterations
			.ShouldBeLessThan(DlrWebApplicationFactory.ShippedPasswordHasherIterations);

		DlrWebApplicationFactory.CheapPasswordHasherIterations.ShouldBeGreaterThan(
			0,
			"zero iterations is not a cheaper hash, it is a different code path");
	}
}
