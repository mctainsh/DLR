namespace DLR.Core.Contracts.Admin;

/// <summary>
/// One account, as the administration screen lists it.
/// <para>
/// <strong>This type may only ever be built from an explicit projection.</strong> It carries an
/// email address and it is handed to somebody who is not its owner, which is exactly the shape
/// <c>ApiSurfaceRules</c> exists to stop happening by accident — an <c>AppUser</c> returned from
/// a response factory would carry the password hash and the security stamp with it.
/// </para>
/// <para>
/// Every count here is over rows the account <em>owns</em>. That is the honest reading of
/// "how much has this person put into the service", and it is the only reading that can be
/// answered in one query per column without walking membership tables.
/// </para>
/// </summary>
/// <param name="UserId">The account.</param>
/// <param name="UserName">Their handle, which is also the identity (§7.2).</param>
/// <param name="Email">The recorded address, or null. Shown because this is the screen that
/// answers "who is this and how do I reach them" when something has gone wrong.</param>
/// <param name="EmailConfirmed">Whether that address has been proved (§7.7).</param>
/// <param name="CreatedUtc">When the account was made.</param>
/// <param name="LastActiveUtc">
/// When the server last heard from it (§7.10). Throttled to one write an hour, so it is accurate
/// to the hour and no better — the list must not be read as a session log.
/// </param>
/// <param name="PositionsRecorded">
/// Lifetime count of GPS fixes this account has published (§5.5).
/// <para>
/// A counter rather than a row count, because the rows do not survive: positions are deleted when
/// the ride carrying them stops being live, so counting <c>rider_position</c> would answer "how
/// many fixes are on a map right now", which is <see cref="PositionsHeld"/> and a different
/// question. Accounts that predate the counter start at zero rather than at their true total —
/// there is nothing left to count them from.
/// </para>
/// </param>
/// <param name="PositionsHeld">Fixes currently stored for this account — non-zero only while they
/// are on a live ride.</param>
/// <param name="Adventures">Group rides this account created (§5.1).</param>
/// <param name="Routes">Tracks this account owns, recorded or imported (§6).</param>
/// <param name="Posts">Comments this account has written on a ride or a route (§17).</param>
/// <param name="Photos">Photographs this account has uploaded (§16.4).</param>
/// <param name="Markers">Markers this account has placed on a map (§16.3).</param>
/// <param name="TrackedHours">
/// Total recorded riding time, summed from the duration of the account's tracks.
/// <para>
/// Riding time, not time in the app, and the distinction is worth keeping: the app has never
/// recorded session length, and a number derived from <see cref="LastActiveUtc"/> would be a guess
/// dressed as a measurement. A track's duration is a thing the device actually measured.
/// </para>
/// </param>
/// <param name="Devices">Devices with a live session (§7.10).</param>
/// <param name="IsAdmin">Whether this account is named in the server's admin roster.</param>
public sealed record AdminUserRow(
	Guid UserId,
	string UserName,
	string? Email,
	bool EmailConfirmed,
	DateTimeOffset CreatedUtc,
	DateTimeOffset LastActiveUtc,
	long PositionsRecorded,
	int PositionsHeld,
	int Adventures,
	int Routes,
	int Posts,
	int Photos,
	int Markers,
	double TrackedHours,
	int Devices,
	bool IsAdmin);

/// <summary>
/// One line off the server's log file, parsed as far as it can be.
/// </summary>
/// <param name="Utc">When it was written, or null for a line the reader could not date — a stack
/// trace continuation, or a line from a provider that formatted itself differently.</param>
/// <param name="Level">Trace, Debug, Information, Warning, Error or Critical, or blank when the
/// line carried no level.</param>
/// <param name="Category">The logger's category, usually a type name, or blank.</param>
/// <param name="Message">The rest of the line, including any exception text that followed it.</param>
public sealed record AdminLogEntry(
	DateTimeOffset? Utc,
	string Level,
	string Category,
	string Message);

/// <summary>
/// A page of the server's log, newest first (§14.6).
/// </summary>
/// <param name="Entries">The lines, newest first.</param>
/// <param name="Day">Which day's file this came from.</param>
/// <param name="AvailableDays">Every day the log directory currently holds a file for, newest
/// first — the picker's options, so a caller never has to guess a filename.</param>
/// <param name="Truncated">Whether older lines exist in this file beyond what was returned.</param>
/// <param name="DatabaseCommandsHidden">
/// How many of EF Core's statement lines the reader stepped over on the way to
/// <paramref name="Entries"/>, or zero when they were asked for.
/// <para>
/// Counted over the part of the file that was read rather than over the whole day, because the
/// read stops at <paramref name="Truncated"/>. It is here so the screen can say that a filter is
/// on and how much it is holding back — a short list otherwise reads as a quiet day.
/// </para>
/// </param>
/// <param name="Enabled">Whether the server was asked to write a file at all — <c>FileLog:Enabled</c>
/// as the running process bound it, not as the file on disk reads.</param>
/// <param name="Directory">The absolute directory the server resolved and is writing to. Relative
/// configuration is resolved against the application's base directory, which is not the working
/// directory under IIS — so the answer to "where are they then" is worth stating rather than
/// leaving an administrator to derive.</param>
/// <param name="Problem">Why nothing is being written, when the writer has failed — a directory it
/// may not create, a disk that has filled. Null when the writer is healthy.</param>
/// <remarks>
/// The last three carry no entries and exist for the empty case. "No files" has several causes
/// that look identical on screen and have completely different fixes, and the one an administrator
/// guesses at first — the setting — is the one that is usually already right.
/// </remarks>
public sealed record AdminLogPage(
	IReadOnlyList<AdminLogEntry> Entries,
	DateOnly Day,
	IReadOnlyList<DateOnly> AvailableDays,
	bool Truncated,
	int DatabaseCommandsHidden,
	bool Enabled,
	string Directory,
	string? Problem);

/// <summary>
/// What the service is doing right now (§5.5, §7.10).
/// </summary>
/// <param name="UsersTotal">Every account on the server.</param>
/// <param name="ActiveLastDay">Accounts the server has heard from in the last 24 hours.</param>
/// <param name="ActiveLastWeek">…in the last 7 days.</param>
/// <param name="ActiveLastMonth">…in the last 30 days.</param>
/// <param name="RidersSharingNow">
/// Accounts with a live position in the ride cache — the only one of these numbers that is
/// "active" in the sense of somebody being out on a road at this moment.
/// </param>
/// <param name="LiveRides">Group rides currently in the live state.</param>
/// <param name="PositionsPerMinute">
/// Fixes accepted in each of the last 24 hours' minutes, oldest first, one entry per minute.
/// <para>
/// Counted in memory rather than from the stored rows, which do not survive the ride (see
/// <see cref="AdminUserRow.PositionsRecorded"/>). It is therefore this process's own count and
/// starts empty after a restart — <paramref name="MeterStartedUtc"/> says from when, so a graph
/// that is climbing out of a restart is not read as a service that lost its riders.
/// </para>
/// </param>
/// <param name="WindowStartUtc">The minute the first entry of <paramref name="PositionsPerMinute"/>
/// covers.</param>
/// <param name="MeterStartedUtc">When this process started counting.</param>
public sealed record AdminStats(
	int UsersTotal,
	int ActiveLastDay,
	int ActiveLastWeek,
	int ActiveLastMonth,
	int RidersSharingNow,
	int LiveRides,
	IReadOnlyList<int> PositionsPerMinute,
	DateTimeOffset WindowStartUtc,
	DateTimeOffset MeterStartedUtc);
