namespace ViewLight.Models;

/// <summary>
/// An account as Imlight stores it in the player database. Presented by <see cref="Username"/> and
/// identified by <see cref="AccountId"/>, which is the GID the client is signed in with.
/// </summary>
public sealed record PlayerAccount(ulong AccountId, string Username) {
    public override string ToString() => Username;
}

/// <summary>
/// The session key Imlight keeps for an account - one per account, bound to the machine that signed in.
/// Handing this to the client is what signs in without the login screen.
/// </summary>
/// <param name="DocumentId">
/// The RavenDB document id. RavenDB assigns it itself, so replacing a key means naming the document to
/// delete rather than addressing it by account.
/// </param>
public sealed record PlayerSessionKey(
    ulong AccountId,
    ulong MachineId,
    string ClientKey2,
    string DocumentId,
    DateTimeOffset? Expires);
