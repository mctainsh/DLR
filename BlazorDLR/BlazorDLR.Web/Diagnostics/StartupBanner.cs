using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using DLR.Server.Admin;
using DLR.Server.Api;
using DLR.Server.Tracks;
using Microsoft.Extensions.Options;

namespace DLR.Server.Diagnostics;

/// <summary>
/// The block written the moment the pipeline is built and before anything is served: which build
/// this is, which machine and account it is running as, where every folder it will touch actually
/// resolved to, and what it is talking to (§14.6).
/// <para>
/// <strong>Resolved values, not configured ones.</strong> Half the questions asked of a deployment
/// — why is it writing there, why is that folder empty, why is it reading the wrong settings — are
/// answered by the difference between what a setting says and what it became. A relative blob path
/// resolves against the content root, which is not the working folder, which is not the folder the
/// executable is in; naming all three costs four lines and settles the argument.
/// </para>
/// <para>
/// Deliberately overlapping with <see cref="ServerLifetimeLog"/>, which writes the same facts one
/// line at a time so the administration screen has scannable rows. This is the block a human
/// reading the file top-down wants, and it is written earlier — before the first request rather
/// than after the addresses bind — so a log that ends in a crash still opens with what crashed.
/// </para>
/// </summary>
public static class StartupBanner
{
	/// <summary>The rule drawn above and below the block, so a restart is findable by eye.</summary>
	public const string Rule = "*****************************************************************************************************";

	/// <summary>Column the values line up in.</summary>
	private const int LabelWidth = 18;

	/// <summary>Builds the block. Never throws: a banner must not be able to stop a boot.</summary>
	/// <param name="app">The built application, for its services, environment and configuration.</param>
	/// <returns>One multi-line string, ruled top and bottom.</returns>
	public static string Describe(WebApplication app)
	{
		StringBuilder text = new(Rule);

		try
		{
			Compose(app, text);
		}
		catch (Exception exception)
		{
			// The banner describes the server; it is not the server. Whatever could not be read,
			// say so and let the thing start.
			Line(text, "Incomplete", $"could not read every value — {exception.GetType().Name}: {exception.Message}");
		}

		return text.Append('\n').Append(Rule).ToString();
	}

	/// <summary>
	/// A connection string with its password taken out.
	/// <para>
	/// <strong>Never the raw string.</strong> It is a credential, and this line lands in a file
	/// readable by everybody on the administration roster. Host and database name are the parts
	/// that answer "is it pointing at the right one", which is the question worth a line.
	/// </para>
	/// </summary>
	/// <param name="connectionString">The configured value, if there is one.</param>
	/// <returns>The remaining keywords, or a note that nothing is configured.</returns>
	public static string RedactConnectionString(string? connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString))
			return "not configured";

		IEnumerable<string> parts = connectionString
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(part => !part.StartsWith("Password", StringComparison.OrdinalIgnoreCase))
			.Where(part => !part.StartsWith("Pwd", StringComparison.OrdinalIgnoreCase));

		return string.Join("; ", parts);
	}

	/// <summary>Every fact, in the order somebody diagnosing a deployment asks for them.</summary>
	/// <param name="app">The built application.</param>
	/// <param name="text">The block being built.</param>
	private static void Compose(WebApplication app, StringBuilder text)
	{
		IServiceProvider services = app.Services;
		IWebHostEnvironment environment = app.Environment;
		IConfiguration configuration = app.Configuration;

		BuildInformation build = services.GetRequiredService<BuildInformation>();
		FileLoggerProvider fileLog = services.GetRequiredService<FileLoggerProvider>();
		FileLogOptions logSettings = services.GetRequiredService<IOptions<FileLogOptions>>().Value;
		AdminOptions admins = services.GetRequiredService<IOptions<AdminOptions>>().Value;
		TimeProvider clock = services.GetRequiredService<TimeProvider>();
		ServerStart started = services.GetRequiredService<ServerStart>();
		BlobStoreOptions blobs = services.GetRequiredService<IOptions<BlobStoreOptions>>().Value;

		Line(text, "Application", $"Dumb Luck Routes {build.Version}");
		Line(text, "Commit", build.Commit + (build.IsDirty ? " (built from a dirty tree)" : string.Empty));
		Line(text, "Built", build.BuiltUtc is { } built ? Moment(built) : "not recorded by the build");

		Line(
			text,
			"Started",
			$"{Moment(started.Utc)} (local zone {clock.LocalTimeZone.Id}), up {Age(started.Uptime)}");

		Line(text, "Environment", environment.EnvironmentName);
		Line(text, "Server", Environment.MachineName + Container());
		Line(text, "Operating system", $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}");
		Line(
			text,
			"Runtime",
			$"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, "
				+ $"{(GCSettings.IsServerGC ? "server" : "workstation")} GC, "
				+ $"{Environment.ProcessorCount} processors, {Environment.WorkingSet / (1024 * 1024)} MB working set");
		Line(
			text,
			"User",
			$"{Environment.UserDomainName}\\{Environment.UserName}"
				+ (Environment.UserInteractive ? " (interactive)" : " (non-interactive — service or container)"));
		Line(text, "Process", $"{Environment.ProcessId} — {Environment.ProcessPath ?? "executable path unknown"}");
		Line(text, "Working folder", Folder(Environment.CurrentDirectory));
		Line(text, "Content root", Folder(environment.ContentRootPath));
		Line(
			text,
			"Web root",
			string.IsNullOrWhiteSpace(environment.WebRootPath) ? "not set" : Folder(environment.WebRootPath));
		Line(text, "Executable folder", Folder(AppContext.BaseDirectory));
		Line(text, "Blob folder", string.IsNullOrWhiteSpace(blobs.RootPath) ? "not configured" : Folder(blobs.RootPath));
		Line(text, "Database", RedactConnectionString(configuration.GetConnectionString("Dlr")));
		Line(text, "Listening on", configuration["urls"] is { Length: > 0 } urls ? urls : "addresses chosen by the host");
		Line(text, "Administrators", $"[{string.Join(", ", admins.Users)}]");
		Line(text, "    Log enabled", logSettings.Enabled ? "yes" : "no — nothing is written to disk");
		Line(text, "    Log folder", Folder(fileLog.Directory));
		Line(text, "    Log level", logSettings.MinimumLevel.ToString());
		Line(text, "    Log retention", $"{logSettings.RetainDays} days, swept by the nightly job");
		Line(text, "    Log read limit", $"{logSettings.MaxLinesPerRead} lines per read");

		if (fileLog.Problem is { Length: > 0 } problem)
			Line(text, "Log problem", problem);
	}

	/// <summary>One label-and-value row.</summary>
	/// <param name="text">The block being built.</param>
	/// <param name="label">What the value is.</param>
	/// <param name="value">The value, already a sentence or a path.</param>
	private static void Line(StringBuilder text, string label, string value) =>
		text.Append('\n').Append(label.PadRight(LabelWidth)).Append(": ").Append(value);

	/// <summary>A time as something that sorts and reads the same everywhere.</summary>
	/// <param name="moment">The instant.</param>
	/// <returns>The UTC form.</returns>
	private static string Moment(DateTimeOffset moment) =>
		moment.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);

	/// <summary>
	/// An elapsed time, in the one format this server writes them in — see
	/// <see cref="ServerLifetimeLog"/>, which closes the file with the same shape so the two can be
	/// read against each other.
	/// </summary>
	/// <param name="uptime">How long the run has served for.</param>
	/// <returns>Days and a clock.</returns>
	internal static string Age(TimeSpan uptime) =>
		uptime < TimeSpan.Zero
			? "an unknown time"
			: uptime.ToString(@"d\d\ hh\:mm\:ss", CultureInfo.InvariantCulture);

	/// <summary>A path, with a note when there is nothing at it.</summary>
	/// <param name="path">An absolute path.</param>
	/// <returns>The path, marked if it does not exist.</returns>
	private static string Folder(string path) =>
		Directory.Exists(path) ? path : $"{path} (does not exist)";

	/// <summary>Whether this is a containerised runtime, which changes every path above.</summary>
	/// <returns>A parenthetical, or nothing.</returns>
	private static string Container() =>
		string.Equals(
			Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
			"true",
			StringComparison.OrdinalIgnoreCase)
			? " (in a container)"
			: string.Empty;
}
