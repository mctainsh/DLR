using System.Collections.Concurrent;
using DLR.Server.Identity;

namespace DLR.TestSupport.Email;

/// <summary>
/// The fake <see cref="IEmailSender"/> every server test asserts against (§10.4).
/// Nothing leaves the process, so the whole suite runs with no credentials and no
/// outbound network — which is what lets an outside contributor run it (§14.4).
/// </summary>
public sealed class CollectingEmailSender : IEmailSender
{
	private readonly ConcurrentQueue<EmailMessage> _sent = new();

	/// <summary>Every message sent so far, in order.</summary>
	public IReadOnlyList<EmailMessage> Sent => [.. _sent];

	/// <summary>
	/// Set to make the next send throw. A third-party outage must not stop a signup
	/// (§7.2), and the only way to assert that is to be able to cause one.
	/// </summary>
	public Exception? FailWith { get; set; }

	/// <inheritdoc />
	public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
	{
		if (FailWith is not null)
		{
			return Task.FromException(FailWith);
		}

		_sent.Enqueue(message);

		return Task.CompletedTask;
	}

	/// <summary>Every message sent to one address.</summary>
	public IReadOnlyList<EmailMessage> To(string address) =>
		[.. _sent.Where(message => string.Equals(message.To, address, StringComparison.OrdinalIgnoreCase))];

	/// <summary>Forgets everything sent so far.</summary>
	public void Clear() => _sent.Clear();
}
