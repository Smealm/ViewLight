using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// The launcher's own copy of the RavenDB the server ships: where it lives, what it keeps, and when it is
/// made again. The point of it is that the launcher's own notes no longer run out of the server's folder -
/// which is what made an Imlight update fail against the launcher's own database.
/// </summary>
public class LauncherRavenEngineTests {

    // ------------------------------------------------------------------ where it lives

    [Fact]
    public void TheCopyLivesUnderTheLaunchersOwnRootAndNeverInTheServer() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = workspace.Path("server");

        // One folder in the launcher's own root, beside its database and its settings - so a portable folder
        // carries it and a normal one has it in %LOCALAPPDATA%\ViewLight.
        Assert.Equal(Path.Combine(root, "ravendb"), LauncherRavenEngine.DirectoryFor(root));
        Assert.Equal(Path.Combine(root, "ravendb", "Raven.Server.dll"),
            LauncherRavenEngine.AssemblyFor(root));

        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(LauncherRavenEngine.DirectoryFor(root)),
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(Path.GetFullPath(server),
            Path.GetFullPath(LauncherRavenEngine.DirectoryFor(root)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APortableLauncherKeepsItsEngineCopyInItsOwnFolder() {
        using var workspace = new TempWorkspace();

        var portable = workspace.CreateDirectory("ViewLight");
        File.WriteAllText(Path.Combine(portable, "portable.txt"), string.Empty);

        var wasOn = PortableMode.IsOn;
        var previousRoot = LauncherPaths.DataRoot;

        try {
            Assert.True(PortableMode.Apply(portable));

            // The same one rule in both modes - the copy sits beside the launcher's database under the root
            // in use - which in portable mode means it travels with the folder it was made in.
            Assert.Equal(Path.Combine(portable, "ravendb"),
                LauncherRavenEngine.DirectoryFor(LauncherPaths.DataRoot));
            Assert.StartsWith(portable, LauncherRavenEngine.AssemblyFor(LauncherPaths.DataRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        finally {
            LauncherPaths.DataRoot = previousRoot;

            if (!wasOn) {
                PortableMode.Apply(Path.Combine(workspace.Root, "no-marker"));
            }
        }
    }

    // ------------------------------------------------------------------ what it keeps

    [Fact]
    public void OnlyOtherPlatformsAreDropped() {
        var mine = LauncherRavenEngine.PlatformRuntimeIdentifiers();

        // This machine's own runtime folder is kept, and so is the fallback the host resolves through.
        foreach (var identifier in mine) {
            Assert.True(LauncherRavenEngine.Keep(Path.Combine("runtimes", identifier, "native", "probe")),
                identifier);
        }

        // Every other platform's is dropped - measured at 422 MB of the 567 MB the engine ships as.
        foreach (var other in new[] { "linux-x64", "osx-arm64", "win-arm64" }.Except(mine)) {
            Assert.False(LauncherRavenEngine.Keep(Path.Combine("runtimes", other, "native", "probe")), other);
        }

        // Everything that is not another platform is kept, including folders nobody here has heard of: the
        // rule is about other platforms rather than about a list of wanted files.
        Assert.True(LauncherRavenEngine.Keep("Raven.Server.dll"));
        Assert.True(LauncherRavenEngine.Keep("Raven.Server.runtimeconfig.json"));
        Assert.True(LauncherRavenEngine.Keep(Path.Combine("Raven.Studio.zip")));
        Assert.True(LauncherRavenEngine.Keep(Path.Combine("LocalEmbeddings", "model.onnx")));
        Assert.True(LauncherRavenEngine.Keep(Path.Combine("runtimes", "some-new-platform", "native", "probe")));
        Assert.True(LauncherRavenEngine.Keep(string.Empty));
    }

    // ------------------------------------------------------------------ making it

    [Fact]
    public void ItIsMadeFromTheInstallPrunedAndStamped() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = Install(workspace);

        var config = ImlightConfig.TryLoad(server)!;
        var result = LauncherRavenEngine.MakeFresh(config, server, root);

        Assert.True(result.Copied, result.Problem);
        Assert.Null(result.Problem);
        Assert.Equal(LauncherRavenEngine.DirectoryFor(root), result.Directory);

        // The engine, its config file, and this platform's libraries.
        Assert.True(File.Exists(LauncherRavenEngine.AssemblyFor(root)));
        Assert.True(File.Exists(Path.Combine(result.Directory, "Raven.Server.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(result.Directory,
            Path.Combine("runtimes", LauncherRavenEngine.PlatformRuntimeIdentifiers()[0], "native", "kept"))));

        // And not another platform's, nor the staging folder the copy was built in.
        Assert.False(File.Exists(Path.Combine(result.Directory,
            Path.Combine("runtimes", "linux-x64", "native", "dropped"))));
        Assert.False(Directory.Exists(result.Directory + LauncherRavenEngine.PartialSuffix));

        // Finished, and nothing left behind to be mistaken for a finished copy.
        Assert.True(LauncherRavenEngine.IsComplete(root));
        Assert.True(File.Exists(LauncherRavenEngine.StampPath(root)));
        Assert.Equal(LauncherRavenEngine.VersionOf(LauncherRavenEngine.DirectoryFor(root)),
            LauncherRavenEngine.StampedVersion(root));
    }

    [Fact]
    public void AFinishedCopyIsNotMadeAgainAndComparesInTheLog() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = Install(workspace);
        var config = ImlightConfig.TryLoad(server)!;

        Assert.True(LauncherRavenEngine.MakeFresh(config, server, root).Copied);

        // The same engine: no second copy, which is what keeps a start from rewriting 179 MB every time.
        var again = LauncherRavenEngine.MakeFresh(config, server, root);
        Assert.False(again.Copied);
        Assert.Null(again.Problem);
    }

    [Fact]
    public void AnInstallWithADifferentEngineReplacesTheCopy() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");

        // A real assembly, because the version read off the file is what the rule turns on.
        var server = Install(workspace, File.Exists(VersionedAssembly) ? File.ReadAllBytes(VersionedAssembly) : [0]);
        var config = ImlightConfig.TryLoad(server)!;

        Assert.True(LauncherRavenEngine.MakeFresh(config, server, root).Copied);

        var version = LauncherRavenEngine.VersionOf(LauncherRavenEngine.DirectoryFor(root));
        Assert.False(string.IsNullOrWhiteSpace(version));

        // An engine that says something else is an install that has moved on - or a copy made from one.
        File.WriteAllText(LauncherRavenEngine.StampPath(root),
            $"{{\"version\":\"0.0.0.0\",\"source\":\"elsewhere\"}}");

        var replaced = LauncherRavenEngine.MakeFresh(config, server, root);

        Assert.True(replaced.Copied);
        Assert.Equal(version, LauncherRavenEngine.StampedVersion(root));
    }

    [Fact]
    public void AFolderWithoutItsStampIsNotACopy() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");
        var server = Install(workspace);
        var config = ImlightConfig.TryLoad(server)!;

        Assert.True(LauncherRavenEngine.MakeFresh(config, server, root).Copied);

        // The stamp is what says a copy finished. Without it the folder is what a machine that lost power
        // halfway through would have left, and it must not be run.
        File.Delete(LauncherRavenEngine.StampPath(root));

        Assert.False(LauncherRavenEngine.IsComplete(root));

        var chosen = LauncherRavenEngine.Choose(config, server, root);

        Assert.NotNull(chosen);
        Assert.False(chosen!.IsOwnCopy);
        Assert.Equal(config.ResolveRavenServerDirectory(server), chosen.Directory);
    }

    [Fact]
    public void WithNoInstallThereIsNothingToCopyOrRun() {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("data");

        var config = ImlightConfig.TryLoad(workspace.CreateDirectory("empty-server"))!;

        var result = LauncherRavenEngine.MakeFresh(config, workspace.Path("empty-server"), root);

        Assert.False(result.Copied);
        Assert.Contains("no Imlight install", result.Problem);
        Assert.False(LauncherRavenEngine.IsComplete(root));

        Assert.Null(LauncherRavenEngine.Choose(config, workspace.Path("empty-server"), root));
        Assert.Null(LauncherRavenEngine.Choose(config, null, root));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An assembly with a version on it, for the rule that turns on the version.</summary>
    private static string VersionedAssembly => typeof(LauncherRavenEngineTests).Assembly.Location;

    /// <summary>
    /// An Imlight install with an engine in it: the assembly, its runtimeconfig, and two platforms' native
    /// folders - one this machine's, one that must not be copied.
    /// </summary>
    private static string Install(TempWorkspace workspace, byte[]? engineAssembly = null) {
        var server = workspace.CreateDirectory("server");
        var raven = Path.Combine("server", ImlightConfig.RelativeRavenServerDirectory);

        workspace.WriteFile(Path.Combine(server, ImlightConfig.RelativeConfigPath),
            "[Global Settings]\nGameRevision = r806919.Wizard_1_610\n");
        workspace.WriteFile(Path.Combine(server, ImlightConfig.ServerExecutableName), [0]);
        workspace.WriteFile(Path.Combine(raven, "Raven.Server.dll"), engineAssembly ?? [0]);
        workspace.WriteFile(Path.Combine(raven, "Raven.Server.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"frameworks\":[]}}");

        workspace.WriteFile(
            Path.Combine(raven, "runtimes", LauncherRavenEngine.PlatformRuntimeIdentifiers()[0], "native", "kept"),
            "this platform");
        workspace.WriteFile(Path.Combine(raven, "runtimes", "linux-x64", "native", "dropped"), "another platform");

        return server;
    }
}
