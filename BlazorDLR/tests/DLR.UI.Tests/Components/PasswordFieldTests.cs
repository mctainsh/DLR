using BlazorDLR.Shared.Components;
using Bunit;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The password box used by both halves of Welcome (§7.2): a reveal button, and — where the
/// caller asks for it — a strength meter.
/// <para>
/// Three properties here are the kind that break silently. A reveal button that submits the
/// form registers the account instead of showing the password; a meter drawn on the sign-in
/// form grades a password the rider cannot change from there; and a field that only reports
/// its value on <c>change</c> leaves the meter a keystroke behind the box.
/// </para>
/// </summary>
public sealed class PasswordFieldTests : BunitContext
{
	private IRenderedComponent<PasswordField> RenderField(
		string value = "",
		bool showStrength = false,
		Action<string>? onChanged = null) =>
		Render<PasswordField>(parameters => parameters
			.Add(p => p.Id, "test-password")
			.Add(p => p.Label, "Password")
			.Add(p => p.Value, value)
			.Add(p => p.ShowStrength, showStrength)
			.Add(p => p.ValueChanged, changed => onChanged?.Invoke(changed)));

	[Fact]
	public void MaskedByDefault_AndTheButtonOffersToShowIt()
	{
		IRenderedComponent<PasswordField> component = RenderField("Ride4mountains");

		component.Find("input").GetAttribute("type").ShouldBe("password",
			"a password field that starts revealed is one read over a shoulder.");

		AngleSharp.Dom.IElement reveal = component.Find("button.pw-reveal");

		reveal.GetAttribute("aria-label").ShouldBe("Show password");
		reveal.GetAttribute("aria-pressed").ShouldBe("false");
	}

	/// <summary>
	/// <c>type="button"</c> is load-bearing, not decoration: the default inside a form is
	/// <c>submit</c>, so without it the eye icon registers the account.
	/// </summary>
	[Fact]
	public void TheRevealButton_IsNotASubmitButton()
	{
		RenderField().Find("button.pw-reveal").GetAttribute("type").ShouldBe("button");
	}

	[Fact]
	public async Task Clicking_Reveals_AndClickingAgainMasks()
	{
		IRenderedComponent<PasswordField> component = RenderField("Ride4mountains");

		await component.InvokeAsync(() => component.Find("button.pw-reveal").Click());

		component.Find("input").GetAttribute("type").ShouldBe("text");
		component.Find("button.pw-reveal").GetAttribute("aria-label").ShouldBe("Hide password");
		component.Find("button.pw-reveal").GetAttribute("aria-pressed").ShouldBe("true");

		await component.InvokeAsync(() => component.Find("button.pw-reveal").Click());

		component.Find("input").GetAttribute("type").ShouldBe("password");
		component.Find("button.pw-reveal").GetAttribute("aria-label").ShouldBe("Show password");
	}

	/// <summary>
	/// The meter has to move while the rider is typing, which means <c>oninput</c> — a field
	/// bound on <c>change</c> only updates when focus leaves, by which time the advice is
	/// about a password they have finished choosing.
	/// </summary>
	[Fact]
	public async Task TypingRaisesValueChanged_AndMovesTheMeterImmediately()
	{
		string? last = null;

		IRenderedComponent<PasswordField> component =
			RenderField(showStrength: true, onChanged: value => last = value);

		await component.InvokeAsync(() => component.Find("input").Input("Ride4mountains"));

		last.ShouldBe("Ride4mountains");
		component.Markup.ShouldContain("Good");
	}

	[Fact]
	public async Task AWeakPassword_NamesTheRulesItStillBreaks()
	{
		IRenderedComponent<PasswordField> component = RenderField(showStrength: true);

		await component.InvokeAsync(() => component.Find("input").Input("abcdef"));

		string markup = component.Markup;

		markup.ShouldContain("Weak");
		markup.ShouldContain("an uppercase letter");
		markup.ShouldContain("a digit");
	}

	[Fact]
	public void WithNothingTyped_NoMeterIsDrawn()
	{
		IRenderedComponent<PasswordField> component = RenderField(showStrength: true);

		component.FindAll(".pw-strength").ShouldBeEmpty(
			"an empty field has nothing to grade, and a full red bar before the first keystroke is a scolding.");
	}

	[Fact]
	public void WithoutShowStrength_TheMeterIsAbsentHoweverStrongThePasswordIs()
	{
		IRenderedComponent<PasswordField> component = RenderField("Ride4mountainsEveryWeekend");

		component.FindAll(".pw-strength").ShouldBeEmpty(
			"§7.2: sign-in grades nothing — the password is already chosen and cannot be changed from that form.");
	}

	/// <summary>The label points at the field, so a tap on the word focuses the box.</summary>
	[Fact]
	public void TheLabelIsTiedToTheInput()
	{
		IRenderedComponent<PasswordField> component = RenderField();

		component.Find("label").GetAttribute("for").ShouldBe("test-password");
		component.Find("input").GetAttribute("id").ShouldBe("test-password");
		component.Find("button.pw-reveal").GetAttribute("aria-controls").ShouldBe("test-password");
	}
}
