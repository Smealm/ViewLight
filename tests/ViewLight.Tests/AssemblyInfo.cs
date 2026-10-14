using Xunit;

// LauncherPaths.DataRoot is a process-wide setting that each TempWorkspace redirects into its own
// scratch folder, so tests must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
