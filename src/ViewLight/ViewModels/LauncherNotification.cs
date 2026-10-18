namespace ViewLight.ViewModels;

/// <summary>
/// One line in the launcher's notification area: something the launcher is doing that the window should
/// say out loud without taking the window over.
/// </summary>
/// <param name="Key">
/// What the notification is about, so the same fact can be replaced rather than added twice. The cloud
/// sync and the profiles database each have one, whether or not they are running at the time.
/// </param>
/// <param name="Title">The one sentence to read first.</param>
/// <param name="Detail">What it means for the rest of the window, in plain words.</param>
/// <param name="IsWorking">
/// Whether this is a wait with no known end, which is what the indeterminate bar under it says. A
/// notification that only had something to report would leave it off.
/// </param>
public sealed record LauncherNotification(string Key, string Title, string Detail, bool IsWorking);
