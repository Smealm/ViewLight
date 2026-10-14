namespace ViewLight.Services;

/// <summary>What the client is signed in as: an account, and the session key that proves it.</summary>
public sealed record ClientLogin(ulong AccountId, string ClientKey2);

/// <summary>
/// Decides whether Wizard101 can be signed in without its login screen, and learns what it needs to.
/// </summary>
/// <remarks>
/// <para>
/// The client's <c>-U</c> argument does not perform a sign-in. It continues one: given
/// <c>-U ..{accountId} {sessionKey}</c> the client skips its login screen and sends
/// <c>MSG_USER_VALIDATE</c>, which Imlight answers by looking up the session key it stored for that
/// account and machine, and checking the key the client derived from the one it was handed. So a
/// launcher can sign a player in only by handing over a key the server will recognise: the one Imlight
/// stored for this machine, or one written in the same shape.
/// </para>
/// <para>
/// That makes the machine id the whole game. A key is bound to the machine that signed in, and nothing
/// outside the database says which machine this is - so it is learned, once, from a session key that
/// appears while the player is signing in themselves, and remembered afterwards.
/// </para>
/// </remarks>
public static class AutoLogin {

    /// <summary>
    /// The key to hand the client, or null when it has to show its own login screen.
    /// </summary>
    /// <param name="contents">What the player database held; an untrusted read signs nobody in.</param>
    /// <param name="machineId">The machine id learned for this machine, if it is known yet.</param>
    /// <remarks>
    /// While the machine id is still unknown there is nothing to compare a stored key against, and the
    /// database belongs to the server running on this machine - so the key is handed over, and this is
    /// the moment a fresh install signs a player in rather than asking for a password. If it turns out
    /// to belong to another machine the client says so and the player signs in once, which is what
    /// teaches the launcher the machine id. A key for a machine that is known, and is not this one, is
    /// never handed over.
    /// </remarks>
    public static ClientLogin? Choose(
        PlayerDatabaseContents contents,
        ulong accountId,
        ulong? machineId,
        DateTimeOffset now) {

        if (!contents.IsTrustworthy || contents.Account(accountId) is null) {
            return null;
        }

        var key = contents.SessionKeyFor(accountId);
        if (key is null) {
            return null;
        }

        if (machineId is not null && key.MachineId != machineId.Value) {
            return null;
        }

        // An expired key is one the server has already dropped, or is about to.
        if (key.Expires is { } expiry && expiry <= now) {
            return null;
        }

        return new ClientLogin(accountId, key.ClientKey2);
    }

    /// <summary>
    /// The machine id to remember, read from a session key that changed while the client was running.
    /// </summary>
    /// <remarks>
    /// This is how the machine id is learned: a key that was not there before - or holds a different key
    /// than it did - was written by a sign-in that just happened on this machine. A key that has not
    /// moved says nothing, which is what keeps this from adopting another machine's id.
    /// </remarks>
    public static ulong? MachineIdFrom(
        PlayerDatabaseContents before,
        PlayerDatabaseContents after,
        ulong accountId) {

        if (!after.IsTrustworthy) {
            return null;
        }

        var current = after.SessionKeyFor(accountId);
        if (current is null) {
            return null;
        }

        var previous = before.SessionKeyFor(accountId);
        var changed = previous is null
                      || !string.Equals(previous.ClientKey2, current.ClientKey2, StringComparison.Ordinal)
                      || previous.MachineId != current.MachineId;

        return changed ? current.MachineId : null;
    }
}
