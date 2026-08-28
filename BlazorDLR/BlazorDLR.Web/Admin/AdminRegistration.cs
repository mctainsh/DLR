using DLR.Server.Diagnostics;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Configuration;

namespace DLR.Server.Admin;

/// <summary>The administration policy name, spelled once.</summary>
public static class AdminPolicies
{
	/// <summary>
	/// An account named in the server's <c>Admins</c> roster (§14.6).
	/// <para>
	/// Guards every endpoint under <c>/api/v1/admin</c>. There is no partial administrator and no
	/// second tier: the screens behind it list every account's email address and the server's own
	/// log, so anything that can reach one of them can reach all of them anyway.
	/// </para>
	/// </summary>
	public const string Admin = "Admin";
}

/// <summary>
/// Wires the roster, the policy, the log file and the log reader (§14.6).
/// </summary>
public static class AdminRegistration
{
	/// <summary>
	/// Registers everything the administration screens need.
	/// </summary>
	/// <param name="services">The container.</param>
	/// <param name="configuration">Where the roster and the log settings come from.</param>
	/// <returns>The container, for chaining.</returns>
	public static IServiceCollection AddDlrAdmin(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		// A bare array section, so the value is bound *into* a property rather than onto the
		// options object. Configure<AdminOptions>(section) would look for "Admins:Users".
		services.Configure<AdminOptions>(options =>
			options.Users = configuration.GetSection(AdminOptions.Section).Get<string[]>() ?? []);

		services.Configure<FileLogOptions>(configuration.GetSection(FileLogOptions.Section));

		services.AddSingleton<AdminRoster>();
		services.AddSingleton<ServerLogReader>();

		// A handler rather than RequireAssertion, because the assertion would have to close over a
		// roster built before the container exists, rather than resolving the one the rest of the
		// server answers from.
		services.AddSingleton<IAuthorizationHandler, AdminHandler>();

		services.AddAuthorizationBuilder()
			.AddPolicy(AdminPolicies.Admin, policy => policy
				.RequireAuthenticatedUser()
				.AddRequirements(new AdminRequirement()));

		return services;
	}

	/// <summary>
	/// Adds the file log provider to the host's logging (§14.6).
	/// <para>
	/// Separate from <see cref="AddDlrAdmin"/> because it registers into the logging builder rather
	/// than the container, and because a deployment can sensibly want one without the other — a
	/// file log with nobody on the roster is a perfectly good thing to run.
	/// </para>
	/// </summary>
	/// <param name="logging">The host's logging builder.</param>
	/// <returns>The builder, for chaining.</returns>
	public static ILoggingBuilder AddDlrFileLog(this ILoggingBuilder logging)
	{
		logging.AddConfiguration();
		logging.Services.AddSingleton<FileLoggerProvider>();
		logging.Services.AddSingleton<ILoggerProvider>(services =>
			services.GetRequiredService<FileLoggerProvider>());

		return logging;
	}
}

/// <summary>The requirement <see cref="AdminPolicies.Admin"/> is built from.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Answers <see cref="AdminRequirement"/> from the roster and the caller's username claim.
/// </summary>
/// <param name="roster">Who the server treats as an administrator, as configured right now.</param>
public sealed class AdminHandler(AdminRoster roster) : AuthorizationHandler<AdminRequirement>
{
	/// <inheritdoc />
	protected override Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		AdminRequirement requirement)
	{
		// The username claim, not Identity.Name: MapInboundClaims is off and the token spells the
		// name "unm" (§7.4), so the framework's own name lookup would find nothing here.
		string? userName = context.User.FindFirst(DlrClaims.UserName)?.Value;

		if (roster.IsAdmin(userName))
		{
			context.Succeed(requirement);
		}

		return Task.CompletedTask;
	}
}
