using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// A handler that answers every request with the same body.
/// <para>
/// For the two clients that talk to somewhere other than the DLR API — the map-pack catalogue and
/// the pack downloader — both of which own a credential-free <see cref="HttpClient"/> that a host
/// hands them (§18.5). A test that left the real one in place would reach out to whatever address
/// it used, which is a unit test with a network dependency and a slow failure when there is no
/// network at all.
/// </para>
/// <para>
/// Deliberately simpler than <c>MapPackDownloaderTests</c>'s stub next door, which models ranges,
/// resumes and truncation because those are what it is testing. This one is for suites where the
/// transfer is a means rather than the subject.
/// </para>
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
	private readonly byte[] _body;
	private readonly string _contentType;

	/// <summary>Answers with a text body — JSON, usually.</summary>
	/// <param name="body">What to send.</param>
	/// <param name="contentType">The media type to declare.</param>
	public StubHttpHandler(string body, string contentType = "application/json")
		: this(Encoding.UTF8.GetBytes(body), contentType)
	{
	}

	/// <summary>Answers with bytes — a PMTiles archive, usually.</summary>
	/// <param name="body">What to send.</param>
	/// <param name="contentType">The media type to declare.</param>
	public StubHttpHandler(byte[] body, string contentType = "application/octet-stream")
	{
		_body = body;
		_contentType = contentType;
	}

	/// <summary>What to answer with. A non-success status skips the body.</summary>
	public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

	/// <summary>Thrown instead of answering — a host that cannot be reached at all.</summary>
	public Exception? Fails { get; set; }

	/// <summary>Where the last request went. What a test asserting URL resolution reads.</summary>
	public Uri? LastRequest { get; private set; }

	/// <summary>How many requests actually left the client.</summary>
	public int Requests { get; private set; }

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// Honoured rather than ignored: a caller that cancels is the case the no-throw readers above
		// this have to get right, and a stub that answers anyway cannot tell them apart.
		cancellationToken.ThrowIfCancellationRequested();

		Requests++;
		LastRequest = request.RequestUri;

		if (Fails is { } failure)
		{
			return Task.FromException<HttpResponseMessage>(failure);
		}

		HttpResponseMessage response = new(Status)
		{
			Content = new ByteArrayContent(Status == HttpStatusCode.OK ? _body : []),
		};

		response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);

		return Task.FromResult(response);
	}
}
