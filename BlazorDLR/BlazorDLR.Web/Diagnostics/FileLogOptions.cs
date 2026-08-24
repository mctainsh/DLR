using Microsoft.Extensions.Logging;

namespace DLR.Server.Diagnostics;

/// <summary>
/// Where the server writes its log, and how much of it it keeps (§14.6).
/// <para>
/// A file rather than a table. The log has to survive the process that could not finish writing to
/// a database — a connection failure, a migration half-applied, the startup validation refusing to
/// boot — and those are precisely the times an administrator opens it. A table would be missing
/// exactly the entries worth reading.
/// </para>
/// </summary>
public sealed class FileLogOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "FileLog";

	/// <summary>
	/// Whether to write a log file at all. Off by default, because a library of files appearing
	/// under a deployment that never asked for them is a surprise, and a container that mounts no
	/// volume for them would fill its own layer.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// The directory the daily files live in, absolute or relative to the content root.
	/// <para>
	/// The only path the reader will ever open. It is resolved once at startup and every read is
	/// checked to fall inside it — see <c>ServerLogReader</c>, and the note there about why a
	/// filename never comes off the wire.
	/// </para>
	/// </summary>
	public string Directory { get; set; } = "logs";

	/// <summary>The lowest level written. Independent of the console's, which stays as configured.</summary>
	public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

	/// <summary>
	/// How many days of files to keep. The nightly maintenance job deletes older ones (§9), so an
	/// unattended server does not eventually stop on a full disk.
	/// </summary>
	public int RetainDays { get; set; } = 14;

	/// <summary>
	/// The most lines one read may return, whatever a caller asks for.
	/// <para>
	/// A cap rather than a default, because the endpoint reads from the end of a file that has no
	/// upper size: without it, one request could pull a day of debug logging from a busy server
	/// into memory and onto the wire.
	/// </para>
	/// </summary>
	public int MaxLinesPerRead { get; set; } = 2_000;
}
