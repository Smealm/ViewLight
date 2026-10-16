namespace ViewLight.Services;

/// <summary>
/// Whether the game in the launcher's own folder is an older build than the one installed on this machine.
/// </summary>
/// <param name="Available">Whether there is something to say - a newer build somewhere to update from.</param>
/// <param name="SourceFolder">The install the comparison was made against, when there was one.</param>
/// <param name="SourceBuild">That install's build, when it could be read.</param>
/// <param name="CopyBuild">The launcher's own copy's build, when it could be read.</param>
/// <param name="Reason">
/// Why there is nothing to say, in one line - for the Log rather than the screen. An update is offered or it is
/// not; a launcher that explains every comparison it decided against would be talking about itself all start.
/// </param>
public sealed record PortableClientUpdateCheck(
    bool Available,
    string? SourceFolder,
    string? SourceBuild,
    string? CopyBuild,
    string Reason) {

    /// <summary>Nothing to say, and why.</summary>
    public static PortableClientUpdateCheck NothingToSay(string reason)
        => new(false, null, null, null, reason);
}

/// <summary>
/// The startup question portable mode asks itself: is the game in this folder older than the game installed on
/// this machine?
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> A portable launcher plays the game from its own folder, and that copy was made once.
/// A Steam update, or a reinstall from KingsIsle, therefore leaves the launcher playing a build the machine has
/// already moved past - and nothing else in the launcher would ever notice, because the copy is a complete,
/// valid game folder. So the check is made on a start, and the answer is a card that offers to close the gap.
/// </para>
/// <para>
/// <b>The install to compare against is found, not assumed.</b> The folder the copy was made from is remembered
/// and used first; when there is not one - an older launcher, or a folder that has since been moved - the usual
/// places are searched. A portable folder carried to another machine may well find that machine's own install,
/// which is exactly the answer wanted.
/// </para>
/// <para>
/// <b>Nothing is claimed from a build that cannot be read.</b> Both revisions come from the clients themselves
/// (<see cref="ClientBuildReader"/>), and either one being unreadable means the comparison is not made at all:
/// the client states its own build once it has run, and "I could not tell" is not "you are out of date".
/// </para>
/// </remarks>
public static class PortableClientUpdate {

    /// <summary>
    /// Checks the copy in <paramref name="gameFolder"/> against the install in <paramref name="rememberedSource"/>,
    /// or against the usual places when that is not a game folder.
    /// </summary>
    public static PortableClientUpdateCheck Check(string? gameFolder, string? rememberedSource) {
        if (string.IsNullOrWhiteSpace(gameFolder) || !GameFolderLocator.LooksLikeGameFolder(gameFolder)) {
            return PortableClientUpdateCheck.NothingToSay(
                "the launcher's own folder holds no game to compare");
        }

        var source = SourceFor(gameFolder, rememberedSource);

        if (source is null) {
            return PortableClientUpdateCheck.NothingToSay(
                "no Wizard101 install was found on this machine to compare the copy against");
        }

        var copyBuild = ClientBuildReader.Read(gameFolder);
        var sourceBuild = ClientBuildReader.Read(source);

        if (string.IsNullOrWhiteSpace(copyBuild) || string.IsNullOrWhiteSpace(sourceBuild)) {
            return new PortableClientUpdateCheck(false, source, sourceBuild, copyBuild,
                "the build of one of the two could not be read - a client states its own build once it has run");
        }

        var order = ClientBuildReader.Compare(sourceBuild, copyBuild);

        if (order is null) {
            return new PortableClientUpdateCheck(false, source, sourceBuild, copyBuild,
                $"the two builds ({sourceBuild} and {copyBuild}) could not be ordered");
        }

        if (order <= 0) {
            // Equal is not worth a card, and neither is a copy that is somehow ahead of the install: both mean
            // there is nothing for an update to do.
            return new PortableClientUpdateCheck(false, source, sourceBuild, copyBuild,
                $"the copy is the same build as the install ({copyBuild}) or newer");
        }

        return new PortableClientUpdateCheck(true, source, sourceBuild, copyBuild,
            $"{source} is {sourceBuild} and the copy is {copyBuild}");
    }

    /// <summary>
    /// The install to compare against: the one the copy came from, or the usual places.
    /// </summary>
    /// <remarks>
    /// A remembered folder that is no longer a game folder is passed over rather than reported: it means the
    /// game moved or was uninstalled, and the answer to that is to look where a game folder usually is.
    /// </remarks>
    public static string? SourceFor(string? gameFolder, string? rememberedSource) {
        if (!string.IsNullOrWhiteSpace(rememberedSource)
            && GameFolderLocator.LooksLikeGameFolder(rememberedSource)
            && !GameFolderLocator.IsInsideOrSame(rememberedSource, gameFolder)
            && !GameFolderLocator.IsInsideOrSame(gameFolder, rememberedSource)) {
            return Path.GetFullPath(rememberedSource);
        }

        var detected = GameFolderLocator.Detect();

        return detected is not null
               && !GameFolderLocator.IsInsideOrSame(detected.Path, gameFolder)
               && !GameFolderLocator.IsInsideOrSame(gameFolder, detected.Path)
            ? detected.Path
            : null;
    }

    /// <summary>
    /// What the card says: which two builds are being compared, and where the newer one is.
    /// </summary>
    public static string Describe(PortableClientUpdateCheck check)
        => check.Available
            ? $"The game in this folder is {check.CopyBuild}, and the build installed at {check.SourceFolder} "
              + $"is {check.SourceBuild}. The launcher plays the copy, so it is playing the older one."
            : string.Empty;
}
