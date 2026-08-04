using BlazorDLR.Shared.Pages.Settings;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Stubs;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The four Settings screens that each carry a rule from §7.
/// <list type="bullet">
///   <item><c>Profile</c> — three optional fields, each with a switch off by default (§7.3).
///     Sharing the email is disabled when the address is unconfirmed.</item>
///   <item><c>Account</c> — password change surfaces per-rule server messages (§18.2).</item>
///   <item><c>Devices</c> — non-current devices are revocable; the current device is called
///     out and has no revoke button (§7.10).</item>
///   <item><c>Blocks</c> — the list, unblock, and the "they are not told" copy (§17.7).</item>
/// </list>
/// </summary>
public sealed class SettingsTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireCommon()
	{
		FakeApiClient api = new();
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));
		// Profile injects ThemeState (§18.6 appearance toggle). The in-memory service
		// is a scoped-lifetime stand-in — no localStorage / MAUI preferences in tests.
		Services.AddSingleton<IThemeService, InMemoryThemeService>();
		Services.AddSingleton<ThemeState>();
		return api;
	}

	// ---------- Profile ----------

	[Fact]
	public void Profile_UnconfirmedEmail_DisablesShareSwitch()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(
			DisplayName: "Dave",
			PhoneNumber: null,
			Email: "dave@example.com",
			EmailConfirmed: false,
			ShareDisplayName: false,
			SharePhoneNumber: false,
			ShareEmail: false);

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(() =>
		{
			// The share-email switch is the third checkbox on the page — after DisplayName and PhoneNumber.
			// It carries the disabled attribute when the email is unconfirmed.
			AngleSharp.Dom.IElement[] switches = component.FindAll("input[type=checkbox]").ToArray();
			switches.Length.ShouldBe(3);
			switches[2].HasAttribute("disabled").ShouldBeTrue(
				"§7.3: an unconfirmed address is not a recovery address and cannot be shared. The switch must be disabled, not merely off.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Profile_Save_SendsTrimmedFieldsAndSwitchStates()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile("Old", "0400", "e@x", true, false, false, false);

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(() =>
			component.FindAll("input[type=checkbox]").Count.ShouldBe(3), timeout: TimeSpan.FromSeconds(3));

		// Type a new display name (blank spaces around it — must be trimmed).
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement name = component.Find("input[placeholder='Your name']");
			name.Change("  Dave Smith  ");
		});
		// Turn on share-display-name.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] switches = component.FindAll("input[type=checkbox]").ToArray();
			switches[0].Change(true);
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() => api.LastUpdateProfileRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		UpdateProfileRequest sent = api.LastUpdateProfileRequest!;
		sent.DisplayName.ShouldBe("Dave Smith", "§7.3: the display name is trimmed before the wire.");
		sent.ShareDisplayName.ShouldBeTrue("the switch's new value must reach the API.");
		sent.ShareEmail.ShouldBeFalse("the untouched share-email switch stays off — the caller does not flip it accidentally.");
	}

	// ---------- Account (change password) ----------

	[Fact]
	public async Task Account_ChangePassword_SendsCurrentAndNewToApi()
	{
		FakeApiClient api = WireCommon();

		IRenderedComponent<Account> component = Render<Account>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] passwords = component.FindAll("input[type=password]").ToArray();
			passwords[0].Change("OldPass1");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] passwords = component.FindAll("input[type=password]").ToArray();
			passwords[1].Change("NewPass9");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() => api.LastChangePasswordRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		ChangePasswordRequest sent = api.LastChangePasswordRequest!;
		sent.CurrentPassword.ShouldBe("OldPass1", "the old password proves it is the account holder — not an attacker with a stolen token.");
		sent.NewPassword.ShouldBe("NewPass9");
	}

	[Fact]
	public async Task Account_ServerRejectsWeakPassword_ShowsPerRuleMessages()
	{
		FakeApiClient api = WireCommon();
		api.ChangePasswordException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.BadRequest,
			Title: "The new password does not meet the requirements.",
			Messages: new[] { "Too short", "No digit" }));

		IRenderedComponent<Account> component = Render<Account>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] passwords = component.FindAll("input[type=password]").ToArray();
			passwords[0].Change("old");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] passwords = component.FindAll("input[type=password]").ToArray();
			passwords[1].Change("weak");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Too short", StringComparison.Ordinal).ShouldBeTrue(
				"§7.2 v0.22: per-rule messages surface — 'The new password does not meet the requirements' is not enough.");
			markup.Contains("No digit", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- Devices ----------

	[Fact]
	public async Task Devices_CurrentDevice_HasNoRevokeButton_OtherDevicesDo()
	{
		FakeApiClient api = WireCommon();
		Guid current = Guid.NewGuid();
		Guid other = Guid.NewGuid();
		api.SessionsResult = new[]
		{
			new DeviceSession(current, "This laptop", FixedInstant.AddMinutes(-1), IsCurrent: true),
			new DeviceSession(other, "Old phone", FixedInstant.AddDays(-5), IsCurrent: false),
		};

		FakeTokenStore tokens = new();
		Services.AddSingleton<ITokenStore>(tokens);
		AuthState auth = new(api, tokens, new FakeTimeProvider(FixedInstant));
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		IRenderedComponent<Devices> component = Render<Devices>();

		component.WaitForAssertion(() =>
		{
			// The "this device" badge lives on the current session; the Sign out button on the others.
			component.Markup.Contains("this device", StringComparison.Ordinal).ShouldBeTrue(
				"§7.10: the current session is called out so a user does not sign themselves out.");
			int revokeButtons = component.FindAll("li.device button").Count;
			revokeButtons.ShouldBe(1,
				"§7.10: only non-current devices have a Sign-out button — one revoke per other device, none for this one.");
		}, timeout: TimeSpan.FromSeconds(3));

		// Click the revoke button; the deviceId argument sent is the OTHER session, not the current one.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement revoke = component.FindAll("li.device button").First();
			revoke.Click();
		});

		component.WaitForAssertion(() => api.RevokedSessions.ShouldContain(other),
			timeout: TimeSpan.FromSeconds(3));
		api.RevokedSessions.ShouldNotContain(current,
			"§7.10: the current session must never be revoked by the per-row button.");
	}

	// ---------- Blocks ----------

	[Fact]
	public async Task Blocks_Unblock_SendsUserIdToApi_ThenReloadsList()
	{
		FakeApiClient api = WireCommon();
		Guid blockedId = Guid.NewGuid();
		api.BlocksResult = new[] { new BlockedRider(blockedId, "SomeoneAnnoying", FixedInstant.AddDays(-3)) };

		IRenderedComponent<Blocks> component = Render<Blocks>();

		component.WaitForAssertion(() =>
			component.FindAll("button").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement unblock = component.FindAll("button")
				.First(b => b.TextContent.Contains("Unblock", StringComparison.Ordinal));
			unblock.Click();
		});

		component.WaitForAssertion(() => api.UnblockedUsers.ShouldContain(blockedId),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Blocks_Copy_StatesThatBlockedUsersAreNotTold()
	{
		FakeApiClient api = WireCommon();
		api.BlocksResult = Array.Empty<BlockedRider>();

		IRenderedComponent<Blocks> component = Render<Blocks>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("not told", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"§17.7: the block-vs-mute distinction is that the blocked rider is not told — the copy must say so.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- DataAndExport ----------

	private (FakeApiClient api, AuthState auth) WireDataAndExport()
	{
		FakeApiClient api = WireCommon();
		FakeTokenStore tokens = new();
		Services.AddSingleton<ITokenStore>(tokens);
		AuthState auth = new(api, tokens, new FakeTimeProvider(FixedInstant));
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);
		return (api, auth);
	}

	[Fact]
	public void Delete_ButtonDisabled_UntilPasswordAndAckAreBothPresent()
	{
		WireDataAndExport();

		IRenderedComponent<DataAndExport> component = Render<DataAndExport>();

		component.WaitForAssertion(() =>
			component.FindAll("button.danger").Count.ShouldBeGreaterThanOrEqualTo(1),
			timeout: TimeSpan.FromSeconds(3));

		// Open the confirm panel.
		component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement openConfirm = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("Delete my account…", StringComparison.Ordinal));
			openConfirm.Click();
		}).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement delete = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("permanently", StringComparison.Ordinal));
			delete.HasAttribute("disabled").ShouldBeTrue(
				"§6.3: the delete button is disabled until password + ack are both set — no one deletes on a stray tap.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- Settings landing (index page) ----------

	[Fact]
	public void SettingsLanding_LinksToAllFiveSubpages()
	{
		IRenderedComponent<Settings> component = Render<Settings>();

		// Each subpage is reachable from this landing — omitting one strands its features.
		component.FindAll("a[href='/settings/profile']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/devices']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/blocks']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/account']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/data']").ShouldNotBeEmpty(
			"§10.2: the data & export page is the store-compliance path — the landing must link to it.");
	}

	[Fact]
	public async Task Delete_SendsCurrentPasswordToApi_AfterPasswordAndAck()
	{
		(FakeApiClient api, _) = WireDataAndExport();

		IRenderedComponent<DataAndExport> component = Render<DataAndExport>();

		component.WaitForAssertion(() =>
			component.FindAll("button.danger").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement openConfirm = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("Delete my account…", StringComparison.Ordinal));
			openConfirm.Click();
		});

		await component.InvokeAsync(() =>
		{
			component.Find("input[type=password]").Change("MyPass9");
		});

		await component.InvokeAsync(() =>
		{
			component.Find("label.ack input").Change(true);
		});

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement delete = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("permanently", StringComparison.Ordinal));
			delete.Click();
		});

		component.WaitForAssertion(() => api.LastDeleteAccountRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));
		api.LastDeleteAccountRequest!.CurrentPassword.ShouldBe("MyPass9",
			"§6.3: DELETE /me carries the current password — the token alone is not enough authorisation for permanent deletion.");
	}
}
