namespace BlazorDLR.Shared.Legal;

/// <summary>One numbered clause of the terms - a heading and its body paragraphs.</summary>
/// <param name="Title">The clause heading.</param>
/// <param name="Paragraphs">Its body, one string per paragraph. Plain text, never markup.</param>
public sealed record TermsClause(string Title, IReadOnlyList<string> Paragraphs);

/// <summary>
/// The end-user licence agreement every account agrees to before it is created (§10.2).
/// <para>
/// <strong>This exists because of App Store review, and the wording is the load-bearing part.</strong>
/// Guideline 1.2 requires terms that state there is <em>no tolerance</em> for objectionable content
/// or abusive users, and a reviewer reads them for that sentence. Clause 3 is it. Editing this file
/// to soften "no tolerance", or to drop the report/block/removal commitments in clauses 4 and 5,
/// re-opens the rejection those clauses were written to close.
/// </para>
/// <para>
/// <strong>Content, not markup, and bundled rather than fetched.</strong> The page renders whatever
/// this list holds, which keeps the terms readable on a first run with no signal and before any
/// account exists - the two conditions the acceptance gate has to work under. It also means the
/// text a rider agreed to is the text in the build they agreed on, which a hosted page cannot
/// promise.
/// </para>
/// </summary>
public static class TermsOfUse
{
	/// <summary>
	/// Which edition of the terms this is, and what <see cref="State.TermsAcceptanceState"/> stores
	/// once a rider has agreed.
	/// <para>
	/// Bumping this asks every device to agree again. Do it for a change to what a rider is
	/// agreeing to; do not do it for a typo, because an agreement re-asked for no reason is one
	/// people learn to tick without reading.
	/// </para>
	/// </summary>
	public const int Version = 1;

	/// <summary>Where a report that cannot wait for the in-app flag control should go.</summary>
	public const string ModerationContact = "spam@securehub.com.au";

	/// <summary>The one line the acceptance checkbox has to carry, wherever it is shown.</summary>
	public const string AcceptanceSummary =
		"I agree to the Terms of Use, and I understand there is no tolerance for objectionable "
		+ "content or abusive behaviour.";

	/// <summary>The terms, in order.</summary>
	public static IReadOnlyList<TermsClause> Clauses { get; } =
	[
		new TermsClause("1. Agreeing to these terms",
		[
			"Dumb Luck Routes is operated by SecureHub. Creating an account, or signing in to one, "
			+ "means you agree to these terms. If you do not agree to them, do not use the app.",

			"These terms are shown in full before you register, and again at any time from "
			+ "Settings. We will ask you to agree again if we change what they require of you.",
		]),

		new TermsClause("2. Your account",
		[
			"You are responsible for what is done through your account, and for keeping your "
			+ "password to yourself. Your username is permanent and is shown to the other members "
			+ "of any adventure you join.",

			"You must be old enough to agree to a contract where you live, and you must not create "
			+ "an account after we have closed one of yours.",
		]),

		new TermsClause("3. There is no tolerance for objectionable content or abusive users",
		[
			"You may post comments, photographs, map markers, notes and polls. You must not post "
			+ "anything that is unlawful, harassing, threatening, abusive, defamatory, hateful, "
			+ "sexually explicit, violent, or that targets any person or group. You must not "
			+ "impersonate anyone, post another person's private information, post content you "
			+ "have no right to post, or use the app to stalk, follow or intimidate anybody.",

			"There is no tolerance for objectionable content or abusive behaviour. We remove "
			+ "content that breaks this clause and we suspend or permanently close the accounts "
			+ "that post it. We do this without notice and at our sole discretion, and we do not "
			+ "require a pattern of behaviour before acting on a single serious case.",

			"This app shows people where each other are. Using it to work out where somebody is "
			+ "against their wishes is abuse of the most serious kind and is dealt with as such.",
		]),

		new TermsClause("4. Reporting content, and what we do about it",
		[
			"Every comment and every map marker carries a flag control that reports it. Reported "
			+ "content is hidden from the app immediately, before anybody has looked at it, and "
			+ "stays hidden until we have reviewed it.",

			"We review reports within 24 hours. Content that breaks clause 3 stays down and the "
			+ "account that posted it is suspended or closed. Content that does not is restored.",

			"If something cannot wait for the flag control, report it to " + ModerationContact + ".",
		]),

		new TermsClause("5. Blocking other people",
		[
			"You can block any other member from the flag or block control beside their post, from "
			+ "an adventure's Live members list, or from Settings, Blocked travellers. Blocking "
			+ "takes effect immediately: their comments, reactions, markers and poll votes stop "
			+ "being shown to you, and the two of you come off each other's live maps.",

			"Blocking is not announced to the person blocked. You can undo it at any time from "
			+ "Settings, Blocked travellers.",
		]),

		new TermsClause("6. Location",
		[
			"Sharing your location is off until you turn it on, it applies to one adventure at a "
			+ "time, and it stops the moment you turn it off, leave the adventure, or are removed "
			+ "from it. You can set a private area around your home inside which nothing is sent.",

			"What is collected, how long it is kept and who can see it is set out in the Privacy "
			+ "Policy, which forms part of these terms.",
		]),

		new TermsClause("7. Ending your use of the app",
		[
			"You can delete your account and everything in it at any time from Settings, Data and "
			+ "export. We can suspend or close an account that breaks these terms, and we will "
			+ "close one that breaks clause 3.",
		]),

		new TermsClause("8. The app is not a safety system",
		[
			"Positions can be late, wrong or absent. Maps and routes can be out of date. Do not "
			+ "rely on this app to navigate, to find a person in difficulty, or in any situation "
			+ "where being wrong would matter. The app is provided as-is, without warranty, to the "
			+ "extent the law where you live allows.",

			"You are responsible for travelling safely and lawfully, including for not operating a "
			+ "vehicle while using a phone.",
		]),

		new TermsClause("9. The software itself",
		[
			"The app is free software under the GNU Affero General Public License version 3, with "
			+ "an additional permission under section 7 covering distribution through app stores. "
			+ "The source is available from the link at the foot of every screen. Nothing in these "
			+ "terms takes away a right that licence gives you.",
		]),

		new TermsClause("10. Contact",
		[
			"Content reports, support and complaints: " + ModerationContact + ".",
		]),
	];
}
