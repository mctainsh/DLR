namespace DLR.Server;

/// <summary>
/// Settings without which the server cannot do its job, checked before it accepts a request.
/// <para>
/// The signing key has its own check with its own rules about <em>where</em> a value may come
/// from (§7.4). This is the plainer companion: a value that is simply absent, told at startup
/// rather than discovered as a stack trace on somebody's first request.
/// </para>
/// </summary>
public static class RequiredSettings
{
	/// <summary>Configuration key for the database.</summary>
	public const string ConnectionStringPath = "ConnectionStrings:Dlr";

	/// <summary>The same setting as an environment variable, with its double underscore.</summary>
	public const string ConnectionStringVariable = "ConnectionStrings__Dlr";

	/// <summary>Refuses to start without a database to connect to.</summary>
	/// <param name="configuration">The application's configuration, as built.</param>
	/// <exception cref="InvalidOperationException">There is no connection string.</exception>
	public static void ValidateConnectionString(IConfiguration configuration)
	{
		if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Dlr")))
		{
			return;
		}

		// appsettings.json carries an empty placeholder, so the value is present and useless —
		// which without this check surfaces as an Npgsql failure on the first request that
		// touches a table, several layers below anything that names the cause.
		throw new InvalidOperationException(
			$"""
			No database: {ConnectionStringPath} is not set.

			Locally, use user secrets:

			    dotnet user-secrets set "{ConnectionStringPath}" "Host=localhost;Database=dlr;Username=dlr;Password=…" --project Web/src/DLR.Server

			In production, an environment variable ({ConnectionStringVariable}) or a Docker
			secret.

			The placeholder in appsettings.json is deliberately empty (§14.3) — a connection
			string is a credential, and that file ships with the code.

			Running the tests needs none of this: they start their own PostgreSQL container.
			""".ReplaceLineEndings("\n"));
	}
}
