using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Admin;
using DLR.Core.Contracts.Identity;
using DLR.Server.Tests.Account;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// <c>DELETE /api/v1/admin/users/{id}</c> - the one write on the administration surface (§14.6).
/// <para>
/// It shares its implementation with <c>DELETE /api/v1/me</c>, so what that endpoint erases is
/// covered by <see cref="AccountDeletionTests"/> and is not repeated here. What is here is the
/// part that is this endpoint's own: who may call it, and the three accounts it refuses.
/// </para>
/// </summary>
public sealed class AdminUserDeletionTests(PostgresFixture postgres)
{
	private const string UsersUrl = "/api/v1/admin/users";

	[Fact]
	public async Task AdminDelete_ErasesTheAccount()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		Guid riderId = await UserIdAsync(app, "DaveSmith");

		using HttpResponseMessage response = await DeleteAsync(admin, riderId, "DaveSmith");

		response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

		(await ExistsAsync(app, "DaveSmith")).ShouldBeFalse();
	}

	/// <summary>
	/// The roster names administrators by username, and a deleted username is free to register
	/// again (§7.2) - so deleting a fellow administrator would leave the roster entry waiting to
	/// promote whoever claims the name next.
	/// </summary>
	[Fact]
	public async Task AdminDelete_IsRefusedForAnAccountOnTheRoster()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: AdminRosterSettings.Roster("TheAdmin", "TheOtherAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		using HttpClient other = await SignedInAsync(app, "TheOtherAdmin");

		Guid otherId = await UserIdAsync(app, "TheOtherAdmin");

		using HttpResponseMessage response = await DeleteAsync(admin, otherId, "TheOtherAdmin");

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await ExistsAsync(app, "TheOtherAdmin")).ShouldBeTrue();
	}

	/// <summary>
	/// Sent back to <c>DELETE /api/v1/me</c>, which asks for the password. An administrator is the
	/// one caller who could otherwise erase their own account without proving they are at the
	/// keyboard.
	/// </summary>
	[Fact]
	public async Task AdminDelete_IsRefusedForTheCallersOwnAccount()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");

		Guid adminId = await UserIdAsync(app, "TheAdmin");

		using HttpResponseMessage response = await DeleteAsync(admin, adminId, "TheAdmin");

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		(await ExistsAsync(app, "TheAdmin")).ShouldBeTrue();
	}

	/// <summary>
	/// The id and the name have to describe the same account. The list is searched and paged, so a
	/// row on a screen that has been open a while can point at an id whose name has moved on.
	/// </summary>
	[Fact]
	public async Task AdminDelete_IsRefusedWhenTheNameDoesNotMatchTheId()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		Guid riderId = await UserIdAsync(app, "DaveSmith");

		using HttpResponseMessage response = await DeleteAsync(admin, riderId, "SomebodyElse");

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue();
	}

	[Fact]
	public async Task AdminDelete_IsRefusedToAnAccountNotOnTheRoster()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient ordinary = await SignedInAsync(app, "DaveSmith");
		using HttpClient victim = await SignedInAsync(app, "SamJones");

		Guid victimId = await UserIdAsync(app, "SamJones");

		using HttpResponseMessage response = await DeleteAsync(ordinary, victimId, "SamJones");

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		(await ExistsAsync(app, "SamJones")).ShouldBeTrue();
	}

	// Awaited inside the using, not returned from it: the request owns its JsonContent, and
	// disposing it while the send is still in flight fails every call with ObjectDisposedException.
	private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid userId, string userName)
	{
		using HttpRequestMessage request = new(HttpMethod.Delete, $"{UsersUrl}/{userId}")
		{
			Content = JsonContent.Create(new AdminDeleteUserRequest(userName)),
		};

		return await client.SendAsync(request);
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient().From($"198.51.100.{Random.Shared.Next(1, 250)}");

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	private static Task<bool> ExistsAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users.AnyAsync(user => user.UserName == userName));

	private static Task<Guid> UserIdAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			database.Users.Where(user => user.UserName == userName).Select(user => user.Id).SingleAsync());
}
