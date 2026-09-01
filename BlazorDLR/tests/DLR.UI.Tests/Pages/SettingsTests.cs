using BlazorDLR.Shared.Pages.Settings;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Display;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The four Settings screens that each carry a rule from §7.
/// <list type="bullet">
///   <item><c>Profile</c> - two optional fields, each with a switch off by default (§7.3).
///     Sharing the email is disabled when the address is unconfirmed.</item>
///   <item><c>Account</c> - password change surfaces per-rule server messages (§18.2).</item>
///   <item><c>Devices</c> - non-current devices are revocable; the current device is called
///     out and has no revoke button (§7.10).</item>
///   <item><c>Blocks</c> - the list, unblock, and the "they are not told" copy (§17.7).</item>
/// </list>
/// <para>
/// The home private area used to be a fifth section, on <c>Profile</c>. It lives on the Location
/// screen now, beside the receiver it gates - see <see cref="LocationSettingsTests"/>.
/// </para>
/// </summary>
public sealed class SettingsTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireCommon()
	{
		FakeApiClient api = new();
		FakeTimeProvider clock = new(FixedInstant);
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<TimeProvider>(clock);
		// Profile's marker preview names the rider by username, because that - never the display
		// name - is what a map pin carries (§7.2). Signed out here, so it falls back to "You".
		Services.AddSingleton<ITokenStore>(new FakeTokenStore());
		Services.AddSingleton(serviceProvider => new AuthState(
			serviceProvider.GetRequiredService<IApiClient>(),
			serviceProvider.GetRequiredService<ITokenStore>(),
			serviceProvider.GetRequiredService<TimeProvider>()));
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();

		// The Settings landing asks whether to offer the administration card (§14.6). The fake
		// client answers a profile with IsAdmin false, so the card stays off unless a test says
		// otherwise - which is the state nearly every account is in.
		Services.AddSingleton<AdminAccess>();
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
			// The share-email switch is the second checkbox on the page - after PhoneNumber, and
			// second rather than third because the display name no longer has a control here at all.
			// It carries the disabled attribute when the email is unconfirmed.
			AngleSharp.Dom.IElement[] switches = component.FindAll("input[type=checkbox]").ToArray();
			switches.Length.ShouldBe(2);
			switches[1].HasAttribute("disabled").ShouldBeTrue(
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
			component.FindAll("input[type=checkbox]").Count.ShouldBe(2), timeout: TimeSpan.FromSeconds(3));

		// Type a new phone number (blank spaces around it - must be trimmed).
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement phone = component.Find("input[type=tel]");
			phone.Change("  0400 123 456  ");
		});
		// Turn on share-phone-number: the first switch on the page now.
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
		sent.PhoneNumber.ShouldBe("0400 123 456", "§7.3: the field is trimmed before the wire.");
		sent.SharePhoneNumber.ShouldBeTrue("the switch's new value must reach the API.");
		sent.ShareEmail.ShouldBeFalse("the untouched share-email switch stays off - the caller does not flip it accidentally.");
	}

	/// <summary>
	/// The display name has no control on this screen any more - every name a traveller reads is
	/// the login name (§7.2). The account still holds the value though, and the update replaces the
	/// whole profile, so a save has to hand back what it loaded: a screen that dropped the field
	/// would clear a stored name that nobody asked to clear.
	/// </summary>
	[Fact]
	public async Task Profile_Save_RoundTripsTheStoredDisplayName()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile("Dave", null, "e@x", true, true, false, false);

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(() =>
			component.FindAll("input[type=checkbox]").Count.ShouldBe(2), timeout: TimeSpan.FromSeconds(3));

		component.FindAll("input[placeholder='Your name']").ShouldBeEmpty(
			"the display name is not editable here any more.");

		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => api.LastUpdateProfileRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		UpdateProfileRequest sent = api.LastUpdateProfileRequest!;
		sent.DisplayName.ShouldBe("Dave", "the stored name survives a save from a screen that no longer shows it.");
		sent.ShareDisplayName.ShouldBeTrue("and so does the switch that goes with it.");
	}

	// ---------- Profile: the map marker colour (§16.3) ----------

	[Fact]
	public void Profile_MarkerColour_OffersThePaletteAndMarksTheOneInForce()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false, MarkerColour: "#16a34a");

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement[] swatches = component.FindAll("button.swatch").ToArray();

			swatches.Length.ShouldBe(MarkerColours.Palette.Count);

			swatches.Count(swatch => swatch.GetAttribute("aria-pressed") == "true").ShouldBe(1,
				"exactly one swatch is in force, and the screen has to say which - a picker that " +
				"does not show the current answer is a picker nobody can tell they have used.");

			swatches.Single(swatch => swatch.GetAttribute("aria-pressed") == "true")
				.GetAttribute("aria-label").ShouldBe("#16a34a");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The pairing rule, on screen: the rider picks a background and the app picks the ink, so no
	/// choice can produce a marker nobody can read (§16.3).
	/// </summary>
	[Fact]
	public async Task Profile_MarkerColour_PreviewInksItselfForContrast()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false, MarkerColour: "#ffffff");

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(
			() => PreviewStyle(component).ShouldContain("#000000"),
			timeout: TimeSpan.FromSeconds(3));

		// Near-black. The ink has to flip with it.
		await component.InvokeAsync(() => component.FindAll("button.swatch")
			.Single(swatch => swatch.GetAttribute("aria-label") == "#111827")
			.Click());

		component.WaitForAssertion(() =>
		{
			string style = PreviewStyle(component);

			style.ShouldContain("#111827");
			style.ShouldContain("#ffffff", customMessage:
				"black text on a near-black label is the marker that disappears at the junction " +
				"where somebody is looking for it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>The inline style on the preview pill, which carries both the background and the ink.</summary>
	private static string PreviewStyle(IRenderedComponent<Profile> component) =>
		component.Find(".marker-preview .pill").GetAttribute("style") ?? string.Empty;

	[Fact]
	public async Task Profile_MarkerColour_ChosenSwatch_ReachesTheApiOnSave()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false);

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(() => component.FindAll("button.swatch").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button.swatch")
			.Single(swatch => swatch.GetAttribute("aria-label") == "#dc2626")
			.Click());

		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => api.LastUpdateProfileRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastUpdateProfileRequest!.MarkerColour.ShouldBe("#dc2626");
	}

	/// <summary>
	/// A swatch is not a submit button. Tapping one while comparing colours must not send the
	/// half-filled form under it.
	/// </summary>
	[Fact]
	public async Task Profile_MarkerColour_ChoosingASwatch_DoesNotSaveByItself()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false);

		IRenderedComponent<Profile> component = Render<Profile>();

		component.WaitForAssertion(() => component.FindAll("button.swatch").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button.swatch")[3].Click());

		api.LastUpdateProfileRequest.ShouldBeNull();
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
			AngleSharp.Dom.IElement form = component.Find("#password-form");
			form.Submit();
		});

		component.WaitForAssertion(() => api.LastChangePasswordRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		ChangePasswordRequest sent = api.LastChangePasswordRequest!;
		sent.CurrentPassword.ShouldBe("OldPass1", "the old password proves it is the account holder - not an attacker with a stolen token.");
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
			AngleSharp.Dom.IElement form = component.Find("#password-form");
			form.Submit();
		});

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Too short", StringComparison.Ordinal).ShouldBeTrue(
				"§7.2 v0.22: per-rule messages surface - 'The new password does not meet the requirements' is not enough.");
			markup.Contains("No digit", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- Account (recovery email, §7.7) ----------

	/// <summary>
	/// The address is set from Account, not from a password form. Profile's "add one to enable
	/// password recovery" link lands here, and a page with nowhere to type an address left that
	/// link pointing at the password change and nothing else.
	/// </summary>
	[Fact]
	public async Task Account_SetEmail_SendsTrimmedAddress_NotAPasswordChange()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false);

		IRenderedComponent<Account> component = Render<Account>();

		component.WaitForAssertion(() => component.FindAll("#email-form").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("#email-form input[type=email]").Change("  dave@example.com  "));
		await component.InvokeAsync(() => component.Find("#email-form").Submit());

		component.WaitForAssertion(() => api.LastSetEmailRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastSetEmailRequest!.Email.ShouldBe("dave@example.com", "§7.7: the address is trimmed before the wire.");
		api.LastChangePasswordRequest.ShouldBeNull(
			"setting an address must not touch the password - the two forms are separate.");
	}

	/// <summary>
	/// An address is stored unconfirmed (§7.7), so the screen has to say the link is what turns it
	/// into a recovery address - not the typing.
	/// </summary>
	[Fact]
	public async Task Account_SetEmail_SaysTheAddressDoesNothingUntilConfirmed()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false);

		IRenderedComponent<Account> component = Render<Account>();

		component.WaitForAssertion(() => component.FindAll("#email-form").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("#email-form input[type=email]").Change("dave@example.com"));
		await component.InvokeAsync(() => component.Find("#email-form").Submit());

		component.WaitForAssertion(() =>
			component.Markup.Contains("confirmation link", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"§7.7: recovery is enabled by confirming, never by typing - the status has to say so."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Account_UnconfirmedEmail_OffersToResendTheLink()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, "dave@example.com", false, false, false, false);

		IRenderedComponent<Account> component = Render<Account>();

		component.WaitForAssertion(() =>
			component.FindAll("button").Count(b => b.TextContent.Contains("Resend", StringComparison.Ordinal))
				.ShouldBe(1, "an unconfirmed address is worth nothing without another chance at the link."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button")
			.First(b => b.TextContent.Contains("Resend", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() => api.Calls.ShouldContain(nameof(IApiClient.ResendConfirmationAsync)),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Account_ConfirmedEmail_HasNothingToResend()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, "dave@example.com", true, false, false, false);

		IRenderedComponent<Account> component = Render<Account>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("confirmed", StringComparison.Ordinal).ShouldBeTrue();
			component.FindAll("button").Count(b => b.TextContent.Contains("Resend", StringComparison.Ordinal))
				.ShouldBe(0, "there is nothing left to confirm.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Account_ServerRejectsAddress_ShowsPerRuleMessages()
	{
		FakeApiClient api = WireCommon();
		api.ProfileResult = new OwnProfile(null, null, null, false, false, false, false);
		api.SetEmailException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.BadRequest,
			Title: "That address was not accepted.",
			Messages: new[] { "Email 'x' is invalid." }));

		IRenderedComponent<Account> component = Render<Account>();

		component.WaitForAssertion(() => component.FindAll("#email-form").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("#email-form input[type=email]").Change("x"));
		await component.InvokeAsync(() => component.Find("#email-form").Submit());

		component.WaitForAssertion(() =>
			component.Markup.Contains("Email 'x' is invalid.", StringComparison.Ordinal).ShouldBeTrue(
				"§18.2: the server's per-rule message is what tells the traveller what to fix."),
			timeout: TimeSpan.FromSeconds(3));
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
				"§7.10: only non-current devices have a Sign-out button - one revoke per other device, none for this one.");
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
				"§17.7: the block-vs-mute distinction is that the blocked traveller is not told - the copy must say so.");
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
	public async Task Delete_ButtonDisabled_UntilPasswordAndAckAreBothPresent()
	{
		WireDataAndExport();

		IRenderedComponent<DataAndExport> component = Render<DataAndExport>();

		component.WaitForAssertion(() =>
			component.FindAll("button.danger").Count.ShouldBeGreaterThanOrEqualTo(1),
			timeout: TimeSpan.FromSeconds(3));

		// Open the confirm panel.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement openConfirm = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("Delete my account…", StringComparison.Ordinal));
			openConfirm.Click();
		});

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement delete = component.FindAll("button.danger")
				.First(b => b.TextContent.Contains("permanently", StringComparison.Ordinal));
			delete.HasAttribute("disabled").ShouldBeTrue(
				"§6.3: the delete button is disabled until password + ack are both set - no one deletes on a stray tap.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- Settings landing (index page) ----------

	[Fact]
	public void SettingsLanding_LinksToAllFiveSubpages()
	{
		// The landing injects AdminAccess for the administration card (§14.6), so it needs the
		// common wiring the other tests in this file use rather than a bare render.
		WireCommon();

		IRenderedComponent<Settings> component = Render<Settings>();

		// Each subpage is reachable from this landing - omitting one strands its features.
		component.FindAll("a[href='/settings/profile']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/devices']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/blocks']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/account']").ShouldNotBeEmpty();
		component.FindAll("a[href='/settings/data']").ShouldNotBeEmpty(
			"§10.2: the data & export page is the store-compliance path - the landing must link to it.");
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
			"§6.3: DELETE /me carries the current password - the token alone is not enough authorisation for permanent deletion.");
	}
}
