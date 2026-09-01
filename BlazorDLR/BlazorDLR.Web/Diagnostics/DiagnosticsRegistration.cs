namespace DLR.Server.Diagnostics;

/// <summary>
/// Wires the event recorder, the startup log and the two ways an exception can reach them (§14.6).
/// </summary>
public static class DiagnosticsRegistration
{
	/// <summary>
	/// Registers <see cref="ServerEvents"/>, the lifetime log and the unhandled-exception handler.
	/// </summary>
	/// <param name="services">The container.</param>
	/// <returns>The container, for chaining.</returns>
	/// <remarks>
	/// Separate from <c>AddDlrAdmin</c>, which owns the log <em>file</em> and the screen that reads
	/// it. This owns what gets written to it, and a deployment could sensibly have one without the
	/// other - the events still go to the console when no file is configured.
	/// </remarks>
	public static IServiceCollection AddDlrDiagnostics(this IServiceCollection services)
	{
		services.AddSingleton<ServerEvents>();

		// Resolved by whichever of the startup block and the lifetime log gets there first, both of
		// which are boot - so the anchor is the boot, and both then age the run from the same one.
		services.AddSingleton<ServerStart>();

		// Hosted so it can hook the lifetime, and singleton-hosted so nothing ends up with a second
		// copy holding a second set of process-wide event handlers.
		services.AddSingletonHostedService<ServerLifetimeLog>();

		// Runs inside UseExceptionHandler ahead of the /Error re-execute, records the exception and
		// declines to handle it - so the response every caller gets is exactly what it was before.
		services.AddExceptionHandler<UnhandledExceptionLogger>();

		return services;
	}
}
