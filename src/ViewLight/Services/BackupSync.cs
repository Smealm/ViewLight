using ViewLight.Models;

namespace ViewLight.Services;

/// <summary>What a sync decided to do, given what is local, what is remote, and what the two last agreed on.</summary>
public enum BackupSyncAction {

    /// <summary>Nothing to do: the two sides already hold the same thing.</summary>
    Nothing,

    /// <summary>The local copy has changed and the remote has not, so it is sent up.</summary>
    Upload,

    /// <summary>The remote has changed and the local has not, so it is brought down.</summary>
    Download,

    /// <summary>Both sides changed since they last agreed. A person has to choose.</summary>
    Conflict,
}

/// <summary>What a sync decided, and the sentence to say about it.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Reason">Why, in the words the Log uses - so the decision is never a mystery.</param>
public sealed record BackupSyncPlan(BackupSyncAction Action, string Reason) {

    public bool IsNothing => Action == BackupSyncAction.Nothing;

    public bool IsConflict => Action == BackupSyncAction.Conflict;
}

/// <summary>
/// Decides what a sync should do, from three manifests and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three manifests.</b> The <i>local</i> one is what this machine holds now, hashed when the sync runs.
/// The <i>remote</i> one is what the cloud holds, read from the small manifest file beside the archive. The
/// <i>ancestor</i> is the manifest both sides were last seen equal at - the one this machine uploaded, or the
/// one it last restored from - kept between runs. A <c>null</c> ancestor means this machine has never taken
/// part in a sync: a fresh install, or a launcher whose last-synced record did not come with it.
/// </para>
/// <para>
/// <b>The rule, in the shape Steam Cloud uses.</b> A side that is there and there alone fills the other. With
/// both sides present, each is compared against the ancestor: a side that still matches it has not changed,
/// and a side that does not has. Only one side changed means the change is carried across; neither changed
/// means nothing is done; both changed is the one case that stops and asks, because there is no way to tell
/// whose copy is meant to win and picking one silently would throw the other away.
/// </para>
/// <para>
/// <b>A machine that has never synced takes the cloud's copy.</b> The one case the ancestor cannot speak to
/// is two sides that differ with no common point to be judged against - and the safe answer there is not the
/// question the plan used to ask. A launcher with no sync behind it is usually a fresh install: an empty
/// database it has just created, and at most a server database from one run. The copy in the cloud is the one
/// somebody put there on purpose. Offering that as a choice made "keep this machine's data" a button that
/// destroys a real backup, so the cloud wins and the local copy is replaced by it - once, before anything
/// here can be uploaded, after which the two sides have an ancestor and the ordinary rules apply.
/// </para>
/// <para>
/// <b>A side that is not there is not a conflict.</b> No backup in the cloud is uploaded to, and no local data
/// with a backup up there is restored from, whatever the ancestor says: with one side empty there is nothing
/// to disagree about, and the case that would otherwise look like a conflict is a backup that was deleted
/// (which the next sync puts back) or a launcher that has just been pointed at a Drive (which takes what is
/// there). The ancestor is only consulted once there are two copies to weigh against it.
/// </para>
/// <para>
/// <b>The ordinary case says nothing.</b> A launcher that has just started, with nothing changed, decides
/// <see cref="BackupSyncAction.Nothing"/> and is not heard from again - a sync that announced every upload
/// and download would be a stream of notifications about the machine doing exactly what it was told to.
/// </para>
/// <para>
/// <b>Content decides, never clocks.</b> Nothing here reads <see cref="BackupManifest.TakenAt"/>. A clock that
/// is wrong, or a machine moved across time zones, would otherwise be enough to make one copy look newer than
/// the other; the hashes are the same answer on every machine and cannot drift.
/// </para>
/// </remarks>
public static class BackupSync {

    /// <summary>
    /// The decision, from what is local, what is remote, and what the two last agreed on.
    /// </summary>
    /// <param name="local">What this machine holds now, or <c>null</c> when there is nothing to back up.</param>
    /// <param name="remote">What the cloud holds, or <c>null</c> when there is no backup up there yet.</param>
    /// <param name="ancestor">What the two sides were last equal at, or <c>null</c> when there never was one.</param>
    public static BackupSyncPlan Plan(BackupManifest? local, BackupManifest? remote, BackupManifest? ancestor) {
        var localHere = local is not null;
        var remoteHere = remote is not null;

        if (!localHere && !remoteHere) {
            return new BackupSyncPlan(BackupSyncAction.Nothing, "There is no data to back up yet.");
        }

        // One side empty: nothing to weigh against an ancestor, so the copy that exists is the one that wins.
        if (!remoteHere) {
            return new BackupSyncPlan(BackupSyncAction.Upload,
                "There is no backup in the cloud, so this machine's data is the one to keep.");
        }

        if (!localHere) {
            return new BackupSyncPlan(BackupSyncAction.Download,
                "The cloud holds a backup and this machine has none, so it is brought down.");
        }

        // Both are here from this point, which the compiler cannot see through the two flags above.
        var here = local!;
        var there = remote!;

        // Both present, and this machine has never synced with this cloud.
        if (ancestor is null) {
            if (here.HasSameContent(there)) {
                return new BackupSyncPlan(BackupSyncAction.Nothing, "The cloud already holds exactly this data.");
            }

            // The cloud's copy wins, and it is brought down before anything here goes up. What is on this
            // machine in this state is what a fresh launcher has - a database it has just created, and maybe a
            // server database from one run - while what is up there was put there deliberately, by a session on
            // another machine or by this one before its notes were lost. Asking, as this used to, made the
            // wrong answer a button: keeping "this machine's data" would write an empty database over a real
            // backup, and nothing puts that back. Restoring also settles the ancestor, so the ordinary rules
            // take over from the next sync on.
            return new BackupSyncPlan(BackupSyncAction.Download,
                "The cloud holds a backup and this machine has never synced with it, so the cloud's copy is "
                + "brought down first: nothing here is uploaded over it.");
        }

        var localChanged = !here.HasSameContent(ancestor);
        var remoteChanged = !there.HasSameContent(ancestor);

        if (!localChanged && !remoteChanged) {
            return new BackupSyncPlan(BackupSyncAction.Nothing, "Both sides are already up to date.");
        }

        if (localChanged && !remoteChanged) {
            return new BackupSyncPlan(BackupSyncAction.Upload,
                "This machine's data has changed since the last backup, so it is uploaded.");
        }

        if (!localChanged && remoteChanged) {
            return new BackupSyncPlan(BackupSyncAction.Download,
                "The cloud's backup has changed since this machine last synced, so it is brought down.");
        }

        // Both moved. Equal content does not count as a conflict even so, because the two sides agreeing is
        // exactly the state a sync wants to end in - it only looks like a change because the ancestor is older.
        return here.HasSameContent(there)
            ? new BackupSyncPlan(BackupSyncAction.Nothing, "Both sides changed to the same thing.")
            : new BackupSyncPlan(BackupSyncAction.Conflict,
                "This machine and the cloud have both changed since they last agreed, so which one to keep "
                + "has to be chosen.");
    }
}
