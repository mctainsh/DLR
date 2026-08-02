using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Identity;

/// <summary>Wires ASP.NET Core Identity up the way §7.2 describes it.</summary>
public static class IdentityRegistration
{
	/// <summary>Registers the user store, the §7.2 username rules and the token providers.</summary>
	/// <param name="services">The container.</param>
	public static IServiceCollection AddDlrIdentity(this IServiceCollection services)
	{
		// AddIdentityCore, not AddIdentity: the latter also installs cookie authentication
		// schemes, and this project's callers are mobile clients holding bearer tokens
		// (§7.4). The browser session cookie is a separate, later decision (§7.5, SRV-34),
		// and having a half-configured scheme sitting in the pipeline until then is how a
		// login ends up working by accident.
		services
			.AddIdentityCore<AppUser>(options =>
			{
				// Uniqueness of a present email is a database constraint here, not an
				// in-memory check (§7.13). Turning this on would reject the null address
				// that the majority of accounts are expected to have (§7.2).
				options.User.RequireUniqueEmail = false;
				options.User.AllowedUserNameCharacters = UserNameRules.AllowedCharacters;

				// Confirmation is never a gate on signing in. An account with no email
				// address is a first-class account, and §7.8's ladder is the only thing
				// that ever asks for one.
				options.SignIn.RequireConfirmedAccount = false;
				options.SignIn.RequireConfirmedEmail = false;

				ApplyPasswordPolicy(options.Password);
				ApplyLockoutPolicy(options.Lockout);

				// Two purposes, two providers, two lifespans (§7.7). Selected by name so that
				// changing one cannot silently move the other — which is exactly what a single
				// global TokenLifespan does, and why neither of these is the framework default.
				options.Tokens.EmailConfirmationTokenProvider =
					EmailConfirmationTokenProvider.ProviderName;

				options.Tokens.PasswordResetTokenProvider =
					PasswordResetTokenProvider.ProviderName;
			})
			.AddUserValidator<UserNameValidator>()
			.AddPasswordValidator<BreachedPasswordValidator>()
			.AddEntityFrameworkStores<DlrDbContext>()
			.AddTokenProvider<EmailConfirmationTokenProvider>(EmailConfirmationTokenProvider.ProviderName)
			.AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName);

		// The token providers seal their payload with it (§7.7).
		services.AddDataProtection();

		PwnedPasswordsClient.AddPwnedPasswords(services);

		services.AddSingleton<AccessTokenIssuer>();

		// Singleton on purpose. Its constructor hashes a throwaway password to get a
		// realistic-cost target to fail against, and paying that on every unknown-username
		// login would be a denial-of-service vector wearing a timing defence's clothes.
		services.AddSingleton<DummyPasswordVerifier>();

		// The grace cache is process memory and outlives a request by design (§7.4); the
		// other two hold a DbContext and must not.
		services.AddSingleton<RefreshTokenGraceCache>();
		services.AddScoped<RefreshTokenService>();
		services.AddScoped<SessionFactory>();
		services.AddScoped<ActivityTracker>();
		services.AddScoped<NewDeviceNotifier>();
		services.AddScoped<AccountEmails>();
		services.AddSingleton<IEmailSender, MailKitEmailSender>();

		// No token providers yet. They arrive in SRV-11, where the point is that email
		// confirmation and password reset need *separate* providers — TokenLifespan is a
		// global setting, and one number silently governing both a 24-hour link and a
		// 1-hour one is the bug that task's first test exists to catch (§7.7, §7.12).
		return services;
	}

	/// <summary>
	/// Length instead of composition (§7.2).
	/// <para>
	/// Every rule turned off below is turned off on purpose. Composition requirements do not
	/// measure strength; they measure compliance, and what people comply with is
	/// <c>Passw0rd!</c> — a string that satisfies all four and is in every breach corpus in
	/// existence. Ten characters and a corpus lookup measure the thing the rules were
	/// standing in for. Identity's default six-character minimum is raised explicitly rather
	/// than inherited.
	/// </para>
	/// </summary>
	private static void ApplyPasswordPolicy(PasswordOptions password)
	{
		password.RequiredLength = 10;
		password.RequireDigit = false;
		password.RequireLowercase = false;
		password.RequireUppercase = false;
		password.RequireNonAlphanumeric = false;
		password.RequiredUniqueChars = 1;
	}

	/// <summary>
	/// Five failures, then fifteen minutes (§7.8).
	/// <para>
	/// <c>AllowedForNewUsers</c> is stated rather than inherited because the whole point is
	/// the account that was created this morning: an attacker working a published handle list
	/// is not going to wait for the accounts to age.
	/// </para>
	/// </summary>
	private static void ApplyLockoutPolicy(LockoutOptions lockout)
	{
		lockout.MaxFailedAccessAttempts = 5;
		lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
		lockout.AllowedForNewUsers = true;
	}
}
