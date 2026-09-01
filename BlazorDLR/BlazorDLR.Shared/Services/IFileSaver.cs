namespace BlazorDLR.Shared.Services;

/// <summary>
/// Hands bytes the app already has in memory to whatever the host means by "save this file"
/// (§15.2 GPX export, §17.3 account export).
/// <para>
/// <strong>Web:</strong> a Blob URL on a synthetic <c>&lt;a download&gt;</c> click, which is the
/// only thing a browser gives a page. <strong>Mobile:</strong> that same trick does nothing -
/// Android's WebView never implemented the <c>download</c> attribute and WKWebView will not
/// follow a <c>blob:</c>/<c>data:</c> anchor it did not open itself, so the click is swallowed
/// and the rider sees the button do nothing at all. The phone hosts write the bytes to the
/// app's cache and put them through the system share sheet instead, which is where "Save to
/// Files" and "Downloads" live on both platforms.
/// </para>
/// <para>
/// The seam exists for exactly that difference. Callers hold a byte array, a name and a
/// content type; where those end up is the host's business.
/// </para>
/// </summary>
public interface IFileSaver
{
	/// <summary>
	/// Offer <paramref name="content"/> to the rider as a file.
	/// </summary>
	/// <param name="fileName">Suggested name including extension. Hosts may sanitise it further.</param>
	/// <param name="contentType">MIME type, used to pick the receiving app on mobile.</param>
	/// <param name="content">The file itself.</param>
	/// <param name="cancellationToken">Abandons the save if the caller navigates away.</param>
	/// <returns>What actually happened, in terms the calling page can put on screen.</returns>
	Task<FileSaveResult> SaveAsync(
		string fileName,
		string contentType,
		byte[] content,
		CancellationToken cancellationToken = default);
}

/// <summary>How a <see cref="IFileSaver.SaveAsync"/> call ended.</summary>
public enum FileSaveStatus
{
	/// <summary>The file reached the rider - downloaded, or handed to the app they chose.</summary>
	Saved,

	/// <summary>The rider dismissed the share sheet or picker. Not an error; say nothing.</summary>
	Cancelled,

	/// <summary>This host cannot save files at all (the prerender). Nothing was written.</summary>
	Unavailable,

	/// <summary>Something went wrong. <see cref="FileSaveResult.Detail"/> says what.</summary>
	Failed,
}

/// <param name="Status">The outcome the page branches on.</param>
/// <param name="Detail">
/// A sentence fit to show a rider - a path on the desktop hosts, an error message on
/// <see cref="FileSaveStatus.Failed"/>, and usually null when the host has already shown
/// its own UI (a browser download shelf, a share sheet).
/// </param>
public sealed record FileSaveResult(FileSaveStatus Status, string? Detail = null)
{
	/// <summary>The file reached the rider.</summary>
	public static FileSaveResult Saved(string? detail = null) => new(FileSaveStatus.Saved, detail);

	/// <summary>The rider backed out.</summary>
	public static FileSaveResult Cancelled() => new(FileSaveStatus.Cancelled);

	/// <summary>This host has nowhere to put a file.</summary>
	public static FileSaveResult Unavailable(string? detail = null) => new(FileSaveStatus.Unavailable, detail);

	/// <summary>The save was attempted and failed.</summary>
	public static FileSaveResult Failed(string detail) => new(FileSaveStatus.Failed, detail);
}
