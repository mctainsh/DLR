namespace DLR.Core.Contracts.Maps;

/// <summary>
/// A short-lived MapKit JS credential (§4.5).
/// <para>
/// The client caches it in memory until near <paramref name="ExpiresUtc"/> and asks again. It is
/// never persisted: a token written to storage outlives the tab that needed it, for no benefit —
/// fetching another one costs a single authenticated request.
/// </para>
/// </summary>
/// <param name="Token">The signed JWT to hand to MapKit JS.</param>
/// <param name="ExpiresUtc">
/// When it stops working. Sent rather than left for the client to decode out of the token, so
/// nothing on the client has to parse a JWT it does not otherwise care about.
/// </param>
public sealed record MapToken(string Token, DateTimeOffset ExpiresUtc);
