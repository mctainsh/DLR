namespace BlazorDLR.Shared.Services;

/// <summary>
/// The prerender's <see cref="IFileSaver"/> (§18.6). Saving a file needs a rider and a
/// click; the SSR pass has neither, and the interactive host that takes over from it binds
/// a real one. Registered so the shared pipeline resolves here too rather than failing DI
/// validation at startup.
/// </summary>
public sealed class UnavailableFileSaver : IFileSaver
{
	/// <inheritdoc />
	public Task<FileSaveResult> SaveAsync(
		string fileName,
		string contentType,
		byte[] content,
		CancellationToken cancellationToken = default) =>
		Task.FromResult(FileSaveResult.Unavailable("this page is still loading - try again in a moment."));
}
