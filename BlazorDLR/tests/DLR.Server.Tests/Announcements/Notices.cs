using DLR.Core.Contracts.Announcements;
using DLR.Server.Data.Announcements;
using DLR.TestSupport.Hosting;

namespace DLR.Server.Tests.Announcements;

/// <summary>
/// Putting a row in the <c>announcement</c> table (§20.2).
/// <para>
/// One writer rather than one per suite: what a test varies is the window, and three copies that
/// each froze a different field meant a fourth window shape needed a fourth copy.
/// </para>
/// </summary>
internal static class Notices
{
	/// <summary>Writes one announcement straight to the table.</summary>
	/// <param name="app">The server.</param>
	/// <param name="title">Its heading, which is what tests assert on.</param>
	/// <param name="from">When it starts appearing.</param>
	/// <param name="window">How long it runs for. Four hours unless a test cares.</param>
	/// <param name="severity">How loudly it is drawn.</param>
	/// <remarks>
	/// Straight to the table rather than through the endpoint: that endpoint is exercised on its
	/// own, and everything which only needs a row to exist takes the short way.
	/// </remarks>
	internal static Task WriteAsync(
		DlrWebApplicationFactory app,
		string title,
		DateTimeOffset from,
		TimeSpan? window = null,
		NoticeSeverity severity = NoticeSeverity.Information) =>
		app.WithDatabaseAsync(async database =>
		{
			database.Add(new Announcement
			{
				Id = Guid.NewGuid(),
				Severity = severity,
				Title = title,
				Body = "Something is happening.",
				PublishFromUtc = from,
				ExpiresUtc = from.Add(window ?? TimeSpan.FromHours(4)),
				CreatedUtc = from,
			});

			await database.SaveChangesAsync();
		});
}
