namespace BlazorDLR.Shared.State;

/// <summary>
/// One dialog per session, resolved as a task. A page calls
/// <see cref="AskAsync(ConfirmRequest)"/> or the shorthand overload, awaits the answer, and
/// the modal component mounted in <c>MainLayout</c> renders whatever this service is holding.
/// <para>
/// Scoped rather than singleton: each user's session has one active dialog at a time, and
/// concurrent asks queue by cancelling the outstanding one - a second ask means the first
/// answer is no longer awaited.
/// </para>
/// </summary>
public sealed class ConfirmService
{
	private TaskCompletionSource<bool>? _pending;

	/// <summary>Fires whenever <see cref="Current"/> changes, so the modal re-renders.</summary>
	public event Action? Changed;

	/// <summary>The active request, or null when no dialog is open.</summary>
	public ConfirmRequest? Current { get; private set; }

	/// <summary>Long-form ask.</summary>
	/// <param name="request">Title, body, button labels, danger flag.</param>
	/// <returns>True if the user confirmed, false if they cancelled or dismissed.</returns>
	public Task<bool> AskAsync(ConfirmRequest request)
	{
		// A second ask supersedes the first. The old TCS resolves false so the caller unblocks
		// and can render whatever came next.
		_pending?.TrySetResult(false);

		_pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Current = request;
		Changed?.Invoke();
		return _pending.Task;
	}

	/// <summary>Shorthand for the common shape.</summary>
	/// <param name="title">The bold line at the top.</param>
	/// <param name="message">The paragraph below.</param>
	/// <param name="confirmText">The confirm button label; defaults to <c>OK</c>.</param>
	/// <param name="danger">
	/// True for destructive actions - the confirm button turns red so accidental clicks look
	/// like accidents rather than routine.
	/// </param>
	public Task<bool> AskAsync(string title, string message, string confirmText = "OK", bool danger = false) =>
		AskAsync(new ConfirmRequest(title, message, confirmText, "Cancel", danger));

	/// <summary>Called by <c>ConfirmDialog</c> when the user clicks a button.</summary>
	public void Respond(bool confirmed)
	{
		TaskCompletionSource<bool>? tcs = _pending;
		_pending = null;
		Current = null;
		Changed?.Invoke();
		tcs?.TrySetResult(confirmed);
	}
}

/// <summary>Everything the modal needs to render one dialog.</summary>
/// <param name="Title">Bold line at the top; a short question.</param>
/// <param name="Message">Paragraph below; the consequence of the confirm.</param>
/// <param name="ConfirmText">Label on the confirm button.</param>
/// <param name="CancelText">Label on the cancel button.</param>
/// <param name="Danger">Whether the confirm button gets the destructive style.</param>
public sealed record ConfirmRequest(
	string Title,
	string Message,
	string ConfirmText = "OK",
	string CancelText = "Cancel",
	bool Danger = false);
