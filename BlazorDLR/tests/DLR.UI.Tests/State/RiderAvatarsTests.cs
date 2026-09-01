using System.Net;
using BlazorDLR.Shared.State;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// The username-to-photograph cache (§7.3).
/// <para>
/// The behaviour worth pinning is the batching. A ride thread and a member list each draw dozens
/// of names, and the whole reason this type exists is that the obvious implementation - one fetch
/// per little circle - is forty round trips to open a screen. Every test below is about that, or
/// about the caching that stops the second render doing it again.
/// </para>
/// </summary>
public sealed class RiderAvatarsTests
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private readonly FakeApiClient _api = new();
	private readonly FakeTimeProvider _clock = new(FixedInstant);
	private readonly StubPhotoHandler _photos = new();

	private RiderAvatars Build() => new(_api, new HttpClient(_photos) { BaseAddress = new Uri("https://test.invalid") }, _clock);

	/// <summary>Opens the batch window, which nothing else will - the clock only moves when a test moves it (§10.4).</summary>
	private void OpenTheWindow() => _clock.Advance(TimeSpan.FromMilliseconds(RiderAvatars.BatchWindowMs + 1));

	[Fact]
	public async Task ManyNamesAskedAtOnce_GoUpAsOneRequest()
	{
		using RiderAvatars avatars = Build();

		Guid photoId = Guid.NewGuid();
		_api.AvatarsByUserName["Alice"] = photoId;

		// Exactly the shape of a list rendering: every row asks before any answer comes back.
		List<Task<string?>> asks =
		[
			avatars.ImageUrlAsync("Alice"),
			avatars.ImageUrlAsync("Bob"),
			avatars.ImageUrlAsync("Cass"),
		];

		OpenTheWindow();

		string?[] results = await Task.WhenAll(asks);

		// One lookup, not three. This is the whole point of the type.
		_api.AvatarLookups.Count.ShouldBe(1);
		_api.AvatarLookups[0].OrderBy(name => name, StringComparer.Ordinal)
			.ShouldBe(["Alice", "Bob", "Cass"]);

		results[0].ShouldNotBeNull("Alice has a photograph");
		results[1].ShouldBeNull("Bob has none, and none is a real answer");
		results[2].ShouldBeNull();
	}

	[Fact]
	public async Task ANameAlreadyAnswered_IsNotAskedAgain()
	{
		using RiderAvatars avatars = Build();

		Task<string?> first = avatars.ImageUrlAsync("Alice");
		OpenTheWindow();
		await first;

		_api.AvatarLookups.Count.ShouldBe(1);

		// A re-render. Nothing goes up, and the window is never even opened.
		(await avatars.ImageUrlAsync("Alice")).ShouldBeNull();

		_api.AvatarLookups.Count.ShouldBe(1);
	}

	/// <summary>
	/// "This rider has no photograph" is the common case and is cached exactly as hard as a photo
	/// id - without it, every re-render asks the server about the same names again.
	/// </summary>
	[Fact]
	public async Task TheAnswerNoPhotograph_IsRemembered()
	{
		using RiderAvatars avatars = Build();

		Task<string?> first = avatars.ImageUrlAsync("Bob");
		OpenTheWindow();
		(await first).ShouldBeNull();

		(await avatars.ImageUrlAsync("Bob")).ShouldBeNull();
		(await avatars.ImageUrlAsync("Bob")).ShouldBeNull();

		_api.AvatarLookups.Count.ShouldBe(1);
	}

	[Fact]
	public async Task OnePhotograph_IsDownloadedOnce_HoweverManyRowsDrawIt()
	{
		using RiderAvatars avatars = Build();

		Guid photoId = Guid.NewGuid();
		_api.AvatarsByUserName["Alice"] = photoId;
		_api.AvatarsByUserName["Alicia"] = photoId;

		List<Task<string?>> asks = [avatars.ImageUrlAsync("Alice"), avatars.ImageUrlAsync("Alicia")];

		OpenTheWindow();

		string?[] results = await Task.WhenAll(asks);

		results[0].ShouldNotBeNull();
		results[0].ShouldBe(results[1], "the same photograph is the same data URL");

		// Two riders pointing at one image, and one GET. The same collapse happens for twenty rows
		// of one rider, which is the case that actually occurs.
		_photos.Requests.Count.ShouldBe(1);
		_photos.Requests[0].ShouldContain(photoId.ToString());
		_photos.Requests[0].ShouldContain("thumbnail", Case.Insensitive);
	}

	[Fact]
	public async Task NamesMatch_WithoutRegardToCase()
	{
		using RiderAvatars avatars = Build();

		_api.AvatarsByUserName["Alice"] = Guid.NewGuid();

		Task<string?> first = avatars.ImageUrlAsync("Alice");
		OpenTheWindow();
		(await first).ShouldNotBeNull();

		// A screen holding "alice" off a cached row must hit the entry stored for "Alice" (§7.2).
		(await avatars.ImageUrlAsync("alice")).ShouldNotBeNull();

		_api.AvatarLookups.Count.ShouldBe(1);
	}

	[Fact]
	public async Task ABlankName_ResolvesToNothing_AndAsksNobody()
	{
		using RiderAvatars avatars = Build();

		(await avatars.ImageUrlAsync(null)).ShouldBeNull();
		(await avatars.ImageUrlAsync("   ")).ShouldBeNull();

		_api.AvatarLookups.ShouldBeEmpty();
	}

	/// <summary>
	/// A lookup that failed must not become a permanent "no photograph" for the rest of the
	/// session - the phone came back into signal, and the next render should find the face.
	/// </summary>
	[Fact]
	public async Task AFailedLookup_IsNotCachedAsNoPhotograph()
	{
		using RiderAvatars avatars = Build();

		_api.GetRiderAvatarsException = new HttpRequestException("in a tunnel");

		Task<string?> first = avatars.ImageUrlAsync("Alice");
		OpenTheWindow();

		// Answered rather than left hanging: a waiter nobody completes is a row that never renders.
		(await first).ShouldBeNull();

		_api.GetRiderAvatarsException = null;
		_api.AvatarsByUserName["Alice"] = Guid.NewGuid();

		Task<string?> second = avatars.ImageUrlAsync("Alice");
		OpenTheWindow();

		(await second).ShouldNotBeNull("the failure was not remembered, so the retry found it");
		_api.AvatarLookups.Count.ShouldBe(2);
	}

	[Fact]
	public async Task Forget_MakesTheNextRenderAskAgain_AndSaysSo()
	{
		using RiderAvatars avatars = Build();

		Task<string?> first = avatars.ImageUrlAsync("Alice");
		OpenTheWindow();
		(await first).ShouldBeNull();

		bool told = false;
		avatars.Changed += () => told = true;

		avatars.Forget("Alice");

		told.ShouldBeTrue("rendered avatars re-resolve off this - it is how a rider's own change shows up");

		_api.AvatarsByUserName["Alice"] = Guid.NewGuid();

		Task<string?> second = avatars.ImageUrlAsync("Alice");
		OpenTheWindow();

		(await second).ShouldNotBeNull();
		_api.AvatarLookups.Count.ShouldBe(2);
	}

	/// <summary>
	/// A component awaiting a task that can never complete is a leak on a page being torn down,
	/// so disposal answers the waiters rather than abandoning them.
	/// </summary>
	[Fact]
	public async Task Disposing_AnswersWhoeverIsStillWaiting()
	{
		RiderAvatars avatars = Build();

		Task<string?> pending = avatars.ImageUrlAsync("Alice");

		avatars.Dispose();

		(await pending).ShouldBeNull();
		_api.AvatarLookups.ShouldBeEmpty("the window never opened, so nothing went up");
	}

	/// <summary>Serves any thumbnail request a tiny JPEG, and records what was asked for.</summary>
	private sealed class StubPhotoHandler : HttpMessageHandler
	{
		public List<string> Requests { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (Requests)
			{
				Requests.Add(request.RequestUri!.ToString());
			}

			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]),
			};

			response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

			return Task.FromResult(response);
		}
	}
}
