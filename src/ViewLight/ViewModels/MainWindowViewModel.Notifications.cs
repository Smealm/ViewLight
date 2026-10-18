using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ViewLight.ViewModels;

/// <summary>
/// The top-left notification area: the things the launcher is doing that are worth a sentence but not a
/// dialogue.
/// </summary>
/// <remarks>
/// <para>
/// A notification is state, not an event. Each one is present exactly while the thing it describes is
/// true - the cloud sync closing the databases, or the profiles database coming up - so it cannot be left
/// on screen by a message that forgot to take itself down, and two of them cannot overlap: the area is a
/// single vertical list, and each key owns at most one row.
/// </para>
/// <para>
/// The area floats above the whole window rather than living in a page or the sidebar. The profiles
/// database starts while the sign-in screen is up, and the sign-in screen is a full-window cover, so a
/// notice in the sidebar would be behind the very screen it is explaining.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel {

    /// <summary>What the top-left corner is saying, newest state first.</summary>
    public ObservableCollection<LauncherNotification> Notifications { get; } = [];

    public bool HasNotifications => Notifications.Count > 0;

    /// <summary>
    /// How long the area stays open before it folds itself into the tray.
    /// </summary>
    /// <remarks>
    /// A notice about the profiles database starting is worth reading once, not sitting in the corner for
    /// the rest of the session. Long enough to read two lines, short enough not to become furniture.
    /// </remarks>
    public TimeSpan NotificationTrayDelay { get; set; } = TimeSpan.FromSeconds(7);

    private CancellationTokenSource? _notificationCollapse;

    /// <summary>
    /// Whether the opener was opened on purpose, and so must not be folded away by the delay.
    /// </summary>
    private bool _notificationTrayPinnedOpen;

    /// <summary>Whether the messages are folded into the tray button in the corner.</summary>
    [ObservableProperty]
    private bool _isNotificationTrayCollapsed;

    /// <summary>Whether the readable rows are on screen - the opposite of being folded into the tray.</summary>
    public bool IsNotificationListVisible => HasNotifications && !IsNotificationTrayCollapsed;

    /// <summary>What the tray button says: how much is waiting behind it.</summary>
    public string NotificationTrayText => Notifications.Count switch {
        0 => "Notifications",
        1 => "1 notification",
        var count => $"{count} notifications",
    };

    /// <summary>The arrow on the tray button: down to open, up to fold away again.</summary>
    public string NotificationTrayGlyph => IsNotificationTrayCollapsed ? "▾" : "▴";

    public string NotificationTrayHint
        => IsNotificationTrayCollapsed ? "Show notifications" : "Hide notifications";

    /// <summary>
    /// Folds the messages into the tray, or opens it again - the one arrow does both.
    /// </summary>
    /// <remarks>
    /// A deliberate open stays open: the delay only folds something nobody has touched, and it never
    /// takes the rows away underneath somebody who just asked to read them.
    /// </remarks>
    [RelayCommand]
    private void ToggleNotificationTray() {
        _notificationTrayPinnedOpen = IsNotificationTrayCollapsed;
        IsNotificationTrayCollapsed = !IsNotificationTrayCollapsed;

        CancelNotificationCollapse();
    }

    partial void OnIsNotificationTrayCollapsedChanged(bool value) {
        OnPropertyChanged(nameof(IsNotificationListVisible));
        OnPropertyChanged(nameof(NotificationTrayGlyph));
        OnPropertyChanged(nameof(NotificationTrayHint));
    }

    /// <summary>
    /// Reconciles the list with what is true now.
    /// </summary>
    /// <remarks>
    /// Each call replaces the row for a key rather than appending another, which is what lets a notice
    /// whose words changed keep its place instead of appearing twice. The order is the order the rows
    /// were first added: the profiles database usually starts before the sync, so it usually reads first.
    /// </remarks>
    private void RefreshNotifications() {
        var countBefore = Notifications.Count;
        var changed = false;

        changed |= SetNotification(
            "profiles-database",
            IsLauncherDatabaseStarting,
            "Launching the profiles database…",
            "Your profiles, playtime and badges live in the server's own database. It is starting on this "
            + "machine and can take a moment the first time; everything else in the launcher works while it does.",
            working: true);

        changed |= SetNotification(
            "cloud-sync",
            IsCloudSyncRunning,
            CloudSyncNotice,
            CloudSyncNoticeDetail,
            working: true);

        if (!changed) {
            return;
        }

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(IsNotificationListVisible));
        OnPropertyChanged(nameof(NotificationTrayText));

        if (Notifications.Count == 0) {
            // Nothing to fold: the next notice starts open, and a tray with nothing behind it is gone.
            CancelNotificationCollapse();
            _notificationTrayPinnedOpen = false;
            IsNotificationTrayCollapsed = false;
            return;
        }

        // A new notice opens the tray, because it is the thing the person needs to see. Words changing in
        // a row that is already open restart the wait rather than adding another. A tray the person opened
        // themselves is left alone.
        if (countBefore == 0 || Notifications.Count > countBefore) {
            _notificationTrayPinnedOpen = false;
            IsNotificationTrayCollapsed = false;
        }

        if (!_notificationTrayPinnedOpen) {
            ScheduleNotificationCollapse();
        }
    }

    /// <summary>Folds the area away once the delay has passed, unless somebody opened it themselves.</summary>
    private void ScheduleNotificationCollapse() {
        CancelNotificationCollapse();

        var cancellation = new CancellationTokenSource();
        _notificationCollapse = cancellation;
        var delay = NotificationTrayDelay;

        _ = Task.Run(async () => {
            try {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                return;
            }

            Post(() => {
                if (!_notificationTrayPinnedOpen && HasNotifications) {
                    IsNotificationTrayCollapsed = true;
                }
            });
        });
    }

    private void CancelNotificationCollapse() {
        var previous = _notificationCollapse;
        _notificationCollapse = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    /// <summary>
    /// Adds, replaces or removes one notification, and says whether the list actually moved.
    /// </summary>
    private bool SetNotification(string key, bool visible, string title, string detail, bool working) {
        var existing = Notifications.FirstOrDefault(notification => notification.Key == key);

        if (!visible) {
            if (existing is null) {
                return false;
            }

            Notifications.Remove(existing);
            return true;
        }

        var wanted = new LauncherNotification(key, title, detail, working);

        if (existing is null) {
            Notifications.Add(wanted);
            return true;
        }

        if (existing == wanted) {
            return false;
        }

        Notifications[Notifications.IndexOf(existing)] = wanted;
        return true;
    }
}
