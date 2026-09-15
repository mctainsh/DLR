using System.Text.RegularExpressions;
using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// The three halves of a hub message have to agree: what the server <em>declares</em>, what it
/// actually <em>broadcasts</em>, and what the client <em>listens</em> for.
/// <para>
/// <strong>This rule exists because every way they can disagree is silent.</strong> SignalR
/// dispatches on the method name <em>and</em> the argument count, so a server sending
/// <c>MarkerAdded(MarkerDto)</c> into a client that registered
/// <c>connection.On&lt;Guid, MarkerDto&gt;("MarkerAdded", …)</c> matches nothing: the handler never
/// runs, nothing throws, and no log line appears anywhere anybody would look. The map just stops
/// updating. A message nobody broadcasts fails the same way, and so does one nobody listens for.
/// </para>
/// <para>
/// <strong>It shipped that way.</strong> All three marker messages and
/// <c>RidePermissionsChanged</c> were broadcast into handlers that could not receive them - a
/// marker placed by one member never appeared on anybody else's map until they reopened the
/// adventure - and the only reason it was not obvious is that positions, whose contract matched,
/// kept flowing beside them.
/// </para>
/// <para>
/// <strong>Nothing else can catch it.</strong> The two sides live in different assemblies joined by
/// a string, so the compiler sees no pair; bUnit raises the client's events directly and never goes
/// near SignalR's dispatch; and the server tests assert what was <em>sent</em>.
/// <c>MarkerBroadcastTests</c> covers one message end to end over a real connection - this covers
/// all of them, cheaply.
/// </para>
/// </summary>
public sealed class HubContractRules
{
	private const string Contract = "src/BlazorDLR.Web/Hubs/RideHub.cs";
	private const string Client = "src/BlazorDLR.Shared/Services/SignalRRideHubClient.cs";
	private const string ServerCode = "src/BlazorDLR.Web/";

	/// <summary>Every <c>Task Name(args);</c> on the <c>IRideClient</c> interface.</summary>
	private static readonly Regex Declared =
		new(@"^\tTask (?<name>\w+)\((?<args>[^)]*)\);", RegexOptions.Multiline);

	/// <summary>Every <c>connection.On&lt;…&gt;("Name", …)</c> the client registers.</summary>
	private static readonly Regex Registered =
		new(@"connection\.On(?:<(?<generics>[^>]*)>)?\(""(?<name>\w+)""");

	/// <summary>
	/// What the server declares it can send, by argument count. Parsed once - two facts read it.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, int> Sends = Declared
		.Matches(Text(Contract))
		.ToDictionary(match => match.Groups["name"].Value, match => Count(match.Groups["args"].Value));

	/// <summary>What the client has a handler for, by argument count.</summary>
	private static readonly IReadOnlyDictionary<string, int> Listens = Registered
		.Matches(Code(Client))
		.ToDictionary(match => match.Groups["name"].Value, match => Count(match.Groups["generics"].Value));

	/// <summary>
	/// Messages the server never sends, whether or not it declares them.
	/// <para>
	/// <c>MemberLeft</c> is deliberate and recorded at the handler that waits for it - see
	/// <c>RideSession.OnMemberLeft</c>, which explains that a removed rider finds out on their next
	/// load and that the client is wired so the day the server does say it, nothing else changes.
	/// </para>
	/// <para>
	/// <strong><c>MemberSharingChanged</c> is not deliberate.</strong> It is declared, listened for
	/// and handled, and <c>MembershipController.SetSharingAsync</c> does not send it - so a rider
	/// turning sharing off is not reflected on anybody else's member list until they reload. Listed
	/// rather than fixed because starting to send it is a behaviour change, not a cleanup.
	/// </para>
	/// </summary>
	private static readonly string[] NeverBroadcast = ["MemberLeft", "MemberSharingChanged"];

	[Fact]
	public void EveryHubMessage_IsListenedForWithTheArgumentCountItIsSentWith()
	{
		Sends.ShouldNotBeEmpty("the contract parse found nothing - IRideClient has moved or changed shape");
		Listens.ShouldNotBeEmpty("the client parse found nothing - the registrations have moved");

		List<string> offences =
		[
			.. Sends
				.Where(sent => Listens.TryGetValue(sent.Key, out int taken) && taken != sent.Value)
				.Select(sent =>
					$"{sent.Key}: IRideClient sends {sent.Value} argument(s), SignalRRideHubClient "
					+ $"listens for {Listens[sent.Key]}. SignalR matches on name AND argument count, "
					+ "so this handler never fires."),
		];

		offences.ShouldBeEmpty(string.Join("\n", offences));
	}

	/// <summary>
	/// Both directions, because a message with no handler is exactly as silent as one with the
	/// wrong handler - and comparing only the intersection waves it through.
	/// </summary>
	[Fact]
	public void EveryHubMessage_IsDeclaredAndListenedFor()
	{
		List<string> offences =
		[
			.. Sends.Keys
				.Where(name => !Listens.ContainsKey(name))
				.Select(name =>
					$"{name}: IRideClient declares it, SignalRRideHubClient has no connection.On for "
					+ "it. The server can broadcast it and no client will ever hear it."),

			.. Listens.Keys
				.Where(name => !Sends.ContainsKey(name) && !NeverBroadcast.Contains(name))
				.Select(name =>
					$"{name}: SignalRRideHubClient listens for it, IRideClient never declares it. "
					+ "Either the server should send it or the handler should go."),
		];

		offences.ShouldBeEmpty(string.Join("\n", offences));
	}

	/// <summary>
	/// A declaration nobody broadcasts is dead wiring that reads as a working feature. This is the
	/// half the rule missed first time round: it measured what the server <em>could</em> send
	/// rather than what it does, so <c>MemberSharingChanged</c> - declared, listened for, handled,
	/// broadcast by nobody - passed green while the sharing switch silently did not propagate.
	/// </summary>
	[Fact]
	public void EveryDeclaredMessage_IsActuallyBroadcastSomewhere()
	{
		string server = string.Join("\n", SourceTree.Under(ServerCode).Select(file => string.Join("\n", file.CodeLines)));

		List<string> offences =
		[
			.. Sends.Keys
				.Where(name => !NeverBroadcast.Contains(name))
				.Where(name => !Regex.IsMatch(server, $@"\.{Regex.Escape(name)}\("))
				.Select(name =>
					$"{name}: declared on IRideClient and broadcast by nothing in BlazorDLR.Web. "
					+ "Either send it or drop it - the client is listening for something that never "
					+ "arrives."),
		];

		offences.ShouldBeEmpty(string.Join("\n", offences));
	}

	/// <summary>The file's text, through <see cref="SourceTree"/> so a moved file fails by name.</summary>
	private static string Text(string path) => File(path).Text;

	/// <summary>
	/// The file's text with comments blanked. The client's registrations are matched against this,
	/// because <c>SignalRRideHubClient</c> is heavily commented and a <c>connection.On</c> written
	/// inside a doc comment would otherwise count as a handler.
	/// </summary>
	private static string Code(string path) => string.Join("\n", File(path).CodeLines);

	private static SourceFile File(string path) =>
		SourceTree.Files.SingleOrDefault(file => file.Path == path)
			?? throw new InvalidOperationException(
				$"{path} is not in the source tree. It has moved, and this rule now guards nothing.");

	/// <summary>
	/// How many arguments a parameter list or a generic list carries. Naive on purpose - a comma
	/// inside a nested generic would miscount, and the day a hub message takes a
	/// <c>Dictionary&lt;K, V&gt;</c> this should be replaced rather than patched.
	/// </summary>
	private static int Count(string list) =>
		string.IsNullOrWhiteSpace(list) ? 0 : list.Split(',').Length;
}
