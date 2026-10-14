using ViewLight.Services;
using ViewLight.ViewModels;
using Xunit;

namespace ViewLight.Tests;

/// <summary>
/// Imlight is a requirement of this launcher rather than an option - the server is what it configures and
/// runs, and the RavenDB the server ships is where the launcher's own profiles live - so what counts as an
/// install it can work with, and what the screen says when there is not one, is pinned down here.
/// </summary>
public class ImlightRequirementTests {

    // ------------------------------------------------------------------ what is missing

    [Fact]
    public void TheFolderIsTheFirstThingThatHasToBeThere() {
        Assert.Equal(ImlightGap.NoFolder, ImlightRequirements.For(null).Gap);
        Assert.Equal(ImlightGap.NoFolder, ImlightRequirements.For("   ").Gap);
        Assert.Contains("No server folder is set", ImlightRequirements.For(null).Missing);
    }

    [Fact]
    public void AFolderWithNoImlightInItSaysSo() {
        using var workspace = new TempWorkspace();

        var empty = workspace.CreateDirectory("server");
        var missing = ImlightRequirements.For(empty);

        Assert.Equal(ImlightGap.NoInstall, missing.Gap);
        Assert.Contains(ImlightConfig.RelativeConfigPath, missing.Missing);

        // A folder that is not there at all is the same answer: there is nowhere for an install to be.
        Assert.Equal(ImlightGap.NoInstall, ImlightRequirements.For(workspace.Path("nowhere")).Gap);
    }

    [Fact]
    public void AnInstallWithoutTheServersOwnExecutableSaysSo() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace, executable: false);

        var missing = ImlightRequirements.For(server);

        Assert.Equal(ImlightGap.NoExecutable, missing.Gap);
        Assert.Contains(ImlightConfig.ServerExecutableName, missing.Missing);
    }

    [Fact]
    public void AnInstallWithoutTheDatabaseTheLauncherKeepsItsDataInSaysSo() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace, database: false);

        var missing = ImlightRequirements.For(server);

        Assert.Equal(ImlightGap.NoDatabase, missing.Gap);

        // It names where to look and what the user's own data has to do with it, because this is the half
        // of the requirement nobody would guess from the outside.
        Assert.Contains(ImlightConfig.RelativeRavenServerDirectory, missing.Missing);
        Assert.Contains("profiles", missing.Missing);
    }

    [Fact]
    public void ADatabaseThatCarriesItsOwnRuntimeNeedsNothingFromThisMachine() {
        using var machine = new NoDotnet();
        using var workspace = new TempWorkspace();

        // An Imlight that shipped the database self-contained would ask this machine for nothing at all.
        var server = Install(workspace, selfContained: true);

        Assert.True(ImlightRequirements.For(server).IsMet);
    }

    [Fact]
    public void ADatabaseThatAsksForAFrameworkWithNoRuntimeOnTheMachineSaysWhich() {
        using var machine = new NoDotnet();
        using var workspace = new TempWorkspace();

        var server = Install(workspace, selfContained: false, frameworks: [
            ("Microsoft.NETCore.App", "10.0.8"), ("Microsoft.AspNetCore.App", "10.0.8")]);

        var missing = ImlightRequirements.For(server);

        Assert.Equal(ImlightGap.NoDotnet, missing.Gap);
        Assert.Contains("Microsoft.NETCore.App 10.0.8", missing.Missing);

        // Somewhere to go about it, and it is the runtime the database asked for rather than a guess.
        Assert.Equal("Get the .NET runtime", missing.ActionLabel);
        Assert.Equal("https://dotnet.microsoft.com/download/dotnet/10.0", missing.ActionUrl);
    }

    [Fact]
    public void ARuntimeWithoutTheFrameworkTheDatabaseAsksForNamesWhichIsMissing() {
        using var workspace = new TempWorkspace();

        var server = Install(workspace, selfContained: false, frameworks: [
            ("Microsoft.NETCore.App", "10.0.8"), ("Microsoft.AspNetCore.App", "10.0.8")]);

        using (new MachineWith(@"C:\Program Files\dotnet\dotnet.exe", ("Microsoft.NETCore.App", "10.0.10"))) {
            var missing = ImlightRequirements.For(server);

            Assert.Equal(ImlightGap.NoRuntime, missing.Gap);
            Assert.Contains("Microsoft.AspNetCore.App 10.0.8", missing.Missing);
            Assert.Contains("dotnet.exe", missing.Missing);
            Assert.Contains("the launcher's own data has no database", missing.Missing);
        }

        // A runtime that has both, at a higher version than the one asked for, is what the database needs.
        using (new MachineWith(@"C:\Program Files\dotnet\dotnet.exe",
                   ("Microsoft.NETCore.App", "10.0.10"), ("Microsoft.AspNetCore.App", "10.0.10"))) {
            Assert.True(ImlightRequirements.For(server).IsMet);
        }
    }

    [Fact]
    public void TheRuntimeGapSaysHowToInstallItAndThatTheLauncherStartsWithoutIt() {
        using var machine = new NoDotnet();
        using var workspace = new TempWorkspace();

        var server = Install(workspace, selfContained: false,
            frameworks: [("Microsoft.AspNetCore.App", "10.0.8")]);

        var missing = ImlightRequirements.For(server);

        // A launcher that got this far without .NET says so, rather than leaving somebody stopped by a
        // dependency wondering how the launcher ran at all: only the server's database needs it.
        Assert.Contains("ViewLight itself starts without .NET", missing.Missing);

        // The link is a version's page of downloads rather than an installer, and the page does not say
        // which download is the wanted one - so the screen does.
        Assert.Equal(ImlightRequirements.DotnetGuide, missing.Guide);
        Assert.Contains("ASP.NET Core Runtime", missing.Guide);
        Assert.Contains("x64", missing.Guide);
        Assert.Contains("Check again", missing.Guide);

        // A runtime that is there but too old is the same page and the same steps.
        using (new MachineWith(@"C:\Program Files\dotnet\dotnet.exe", ("Microsoft.NETCore.App", "10.0.10"))) {
            var old = ImlightRequirements.For(server);

            Assert.Equal(ImlightGap.NoRuntime, old.Gap);
            Assert.Equal(ImlightRequirements.DotnetGuide, old.Guide);
        }
    }

    [Fact]
    public void TheGapsFixedOnThisScreenNeedNoSteps() {
        using var workspace = new TempWorkspace();

        // Downloading, or choosing another folder - the two buttons on this screen are the whole answer.
        Assert.Null(ImlightRequirements.For(null).Guide);
        Assert.Null(ImlightRequirements.For(workspace.CreateDirectory("empty")).Guide);
        Assert.Null(ImlightRequirements.For(Install(workspace, executable: false)).Guide);
        Assert.Null(ImlightRequirements.For(Install(workspace, "server2", database: false)).Guide);

        // And nothing missing means nothing to explain at all.
        Assert.Null(ImlightRequirement.Met.Guide);
    }

    // ------------------------------------------------------------------ the framework rule

    [Fact]
    public void TheSameMajorAtLeastTheVersionAskedForIsAMatch() {
        var asked = new[] { new DotnetFramework("Microsoft.AspNetCore.App", "10.0.8") };

        // Roll-forward, the rule the runtime itself uses: a higher patch or minor inside the same major.
        Assert.Null(DotnetHost.Unmet(asked, [new DotnetFramework("Microsoft.AspNetCore.App", "10.0.10")]));
        Assert.Null(DotnetHost.Unmet(asked, [new DotnetFramework("Microsoft.AspNetCore.App", "10.0.8")]));

        // Another major, an older version, or another framework entirely is not.
        Assert.NotNull(DotnetHost.Unmet(asked, [new DotnetFramework("Microsoft.AspNetCore.App", "9.0.18")]));
        Assert.NotNull(DotnetHost.Unmet(asked, [new DotnetFramework("Microsoft.AspNetCore.App", "10.0.7")]));
        Assert.NotNull(DotnetHost.Unmet(asked, [new DotnetFramework("Microsoft.NETCore.App", "10.0.10")]));

        // And a database that asks for nothing is a database nothing can be missing from.
        Assert.Null(DotnetHost.Unmet([], []));
    }

    [Fact]
    public void WhatAHostOffersIsReadFromWhatItPrints() {
        // The shape a real dotnet prints, paths and all - which is why only the first two words are read.
        const string listing =
            "Microsoft.AspNetCore.App 10.0.10 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]\r\n"
            + "Microsoft.NETCore.App 10.0.7 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\r\n"
            + "\r\n"
            + "Something that is not a runtime at all\r\n";

        var frameworks = DotnetHost.ParseList(listing);

        Assert.Equal(2, frameworks.Count);
        Assert.Equal("Microsoft.AspNetCore.App", frameworks[0].Name);
        Assert.Equal("10.0.10", frameworks[0].Version);
        Assert.Equal(10, frameworks[0].Major);
        Assert.Equal(new DotnetFramework("Microsoft.NETCore.App", "10.0.7"), frameworks[1]);
    }

    [Fact]
    public void WhatADatabaseAsksForIsReadFromItsOwnFile() {
        using var workspace = new TempWorkspace();

        var raven = workspace.CreateDirectory(Path.Combine("server", ImlightConfig.RelativeRavenServerDirectory));

        // No runtimeconfig at all: nothing to ask a machine for.
        Assert.Empty(DotnetHost.RequiredBy(raven));

        File.WriteAllText(DotnetHost.RuntimeConfigPath(raven), RuntimeConfig(selfContained: true));
        Assert.Empty(DotnetHost.RequiredBy(raven));

        File.WriteAllText(DotnetHost.RuntimeConfigPath(raven),
            RuntimeConfig(selfContained: false, ("Microsoft.NETCore.App", "10.0.8")));

        var required = DotnetHost.RequiredBy(raven);

        Assert.Equal(new[] { new DotnetFramework("Microsoft.NETCore.App", "10.0.8") }, required);
    }

    // ------------------------------------------------------------------ the launcher's first screen

    [Fact]
    public void TheLauncherWillNotShowItselfUntilThereIsAnImlight() {
        using var workspace = new TempWorkspace();
        var launcher = Launcher(workspace, serverFolder: workspace.CreateDirectory("server"));

        Assert.False(launcher.RequiresSetup);
        Assert.True(launcher.RequiresImlight);
        Assert.False(launcher.ShowMainInterface);
        Assert.Contains("does not hold an Imlight install", launcher.ImlightProblem);
        Assert.Equal(ImlightGap.NoInstall, launcher.ImlightGap);

        // What the screen is waiting for is an install, and noticing one must not need a restart: this is
        // the Check again button.
        Install(workspace, selfContained: true);
        launcher.CheckImlightAgainCommand.Execute(null);

        Assert.False(launcher.RequiresImlight);
        Assert.True(launcher.ShowMainInterface);
        Assert.Equal(string.Empty, launcher.ImlightProblem);
        Assert.Equal(ImlightGap.None, launcher.ImlightGap);

        // And the steps go away with the problem they were for.
        Assert.False(launcher.HasImlightGuide);
    }

    [Fact]
    public void TheScreenShowsTheStepsOnlyWhenThereAreSome() {
        using var workspace = new TempWorkspace();

        // A folder that holds no install: a problem, but not one with steps to follow.
        var installless = Launcher(workspace, workspace.CreateDirectory("empty"));
        Assert.True(installless.RequiresImlight);
        Assert.False(installless.HasImlightGuide);
        Assert.Equal(string.Empty, installless.ImlightGuide);

        // The same launcher with an install and no runtime on the machine: now there are steps, and they
        // are the ones the rule handed over rather than anything the screen made up.
        var server = Install(workspace, selfContained: false,
            frameworks: [("Microsoft.AspNetCore.App", "10.0.8")]);

        using var machine = new NoDotnet();
        var runtime = Launcher(workspace, server);

        Assert.Equal(ImlightGap.NoDotnet, runtime.ImlightGap);
        Assert.True(runtime.HasImlightGuide);
        Assert.Equal(ImlightRequirements.DotnetGuide, runtime.ImlightGuide);
    }

    [Fact]
    public void AnInstallMissingItsExecutableStillStopsTheLauncher() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace, executable: false, database: false, selfContained: true);

        var launcher = Launcher(workspace, server);

        // The config is there, which is all the rest of the launcher asks before it reads it - and which is
        // not enough to work with, which is what this screen is for.
        Assert.True(launcher.ServerInstalled);
        Assert.True(launcher.RequiresImlight);
        Assert.Equal(ImlightGap.NoExecutable, launcher.ImlightGap);
    }

    [Fact]
    public void TheFoldersComeBeforeTheImlightScreen() {
        using var workspace = new TempWorkspace();

        // Nothing chosen yet: the setup screen is the one that shows, and it has the server folder box on
        // it - so this screen waiting behind it is not asked about a folder nobody has picked.
        var launcher = Launcher(workspace, serverFolder: string.Empty);

        Assert.True(launcher.RequiresSetup);
        Assert.False(launcher.RequiresImlight);
        Assert.False(launcher.ShowMainInterface);
    }

    [Fact]
    public void ALaterVersionOfTheRuntimeIsNoticedWhenAskedForAgain() {
        using var workspace = new TempWorkspace();
        var server = Install(workspace, selfContained: false,
            frameworks: [("Microsoft.AspNetCore.App", "10.0.8")]);

        using var machine = new NoDotnet();
        var launcher = Launcher(workspace, server);

        Assert.Equal(ImlightGap.NoDotnet, launcher.ImlightGap);

        // Somebody installs .NET, comes back, and presses the button: the answer is asked for again rather
        // than remembered from the start of the run.
        var installed = new MachineWith(@"C:\Program Files\dotnet\dotnet.exe",
            ("Microsoft.AspNetCore.App", "10.0.10"));

        try {
            launcher.CheckImlightAgainCommand.Execute(null);
            Assert.False(launcher.RequiresImlight);
        }
        finally {
            installed.Dispose();
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A launcher pointed at a folder, with the update check off so nothing reaches GitHub.</summary>
    private static MainWindowViewModel Launcher(TempWorkspace workspace, string serverFolder) {
        var store = new LauncherSettingsStore(workspace.Path("launcher.json"));
        store.Settings.ClientPath = workspace.CreateDirectory("game");
        store.Settings.ServerPath = serverFolder;
        store.Settings.CheckForUpdatesOnStartup = false;
        store.Save();

        return new MainWindowViewModel(store, fetchArt: false);
    }

    /// <summary>An Imlight install, built piece by piece so one of them can be left out.</summary>
    private static string Install(
        TempWorkspace workspace,
        string relative = "server",
        bool executable = true,
        bool database = true,
        bool selfContained = true,
        params (string Name, string Version)[] frameworks) {

        var server = workspace.CreateDirectory(relative);
        var raven = Path.Combine(relative, ImlightConfig.RelativeRavenServerDirectory);

        // A free port rather than the stock 8080: once nothing is missing, Check again settles the launcher
        // in the way a normal start does - which includes opening the server's own RavenDB - and this keeps
        // that off whatever port a server on this machine happens to be using.
        workspace.WriteFile(Path.Combine(relative, ImlightConfig.RelativeConfigPath),
            "[Global Settings]\nGameRevision = r806919.Wizard_1_610\n"
            + $"\n[{ImlightConfig.SectionDatabase}]\n"
            + $"EmbeddedDatabasePort = {PortAllocator.FindFreePort() ?? ImlightConfig.DefaultEmbeddedDatabasePort}\n");

        if (executable) {
            workspace.WriteFile(Path.Combine(relative, ImlightConfig.ServerExecutableName), [0]);
        }

        if (database) {
            workspace.WriteFile(Path.Combine(raven, "Raven.Server.dll"), [0]);
            workspace.WriteFile(Path.Combine(raven, "Raven.Server.runtimeconfig.json"),
                RuntimeConfig(selfContained, frameworks));
        }

        return server;
    }

    /// <summary>A database's own runtimeconfig, in the two shapes one comes in.</summary>
    private static string RuntimeConfig(bool selfContained, params (string Name, string Version)[] frameworks) {
        var asked = string.Join(',', frameworks.Select(framework =>
            $"{{\"name\":\"{framework.Name}\",\"version\":\"{framework.Version}\"}}"));

        return selfContained
            ? "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.12\"}]}}"
            : $"{{\"runtimeOptions\":{{\"tfm\":\"net10.0\",\"frameworks\":[{asked}]}}}}";
    }

    /// <summary>Puts the machine's own answers back, so one test cannot decide another's.</summary>
    private static void Restore(
        Func<string?> find, Func<string, IReadOnlyList<DotnetFramework>> list) {
        DotnetHost.Find = find;
        DotnetHost.ListFrameworks = list;
        DotnetHost.Forget();
    }

    /// <summary>A machine with no .NET on it at all.</summary>
    private sealed class NoDotnet : IDisposable {

        private readonly Func<string?> _find = DotnetHost.Find;
        private readonly Func<string, IReadOnlyList<DotnetFramework>> _list = DotnetHost.ListFrameworks;

        public NoDotnet() {
            DotnetHost.Find = () => null;
            DotnetHost.ListFrameworks = _ => [];
            DotnetHost.Forget();
        }

        public void Dispose() => Restore(_find, _list);
    }

    /// <summary>
    /// A machine with a host at a path and exactly these frameworks. What a real machine has is read out of
    /// a process it starts, so the answer is decided here rather than depending on whose machine this is.
    /// </summary>
    private sealed class MachineWith : IDisposable {

        private readonly Func<string?> _find = DotnetHost.Find;
        private readonly Func<string, IReadOnlyList<DotnetFramework>> _list = DotnetHost.ListFrameworks;

        public MachineWith(string host, params (string Name, string Version)[] frameworks) {
            DotnetHost.Find = () => host;
            DotnetHost.ListFrameworks = _ => frameworks.Select(f => new DotnetFramework(f.Name, f.Version)).ToList();
            DotnetHost.Forget();
        }

        public void Dispose() => Restore(_find, _list);
    }
}
