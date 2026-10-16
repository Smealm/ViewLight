using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>
/// One revision the patch server has fetched, as the manual list in Settings shows it.
/// </summary>
/// <remarks>
/// A row rather than the record itself, because the one thing the list adds is a decision - <see cref="Keep"/>
/// - and that is per row and has to be able to change on its own. Everything else is the record's own answer,
/// read through rather than copied, so what a row says cannot drift from what is on disk.
/// </remarks>
public sealed partial class PatchServerRevisionRow : ObservableObject {

    /// <param name="revision">What was read off the disk.</param>
    /// <param name="keep">Whether it starts ticked.</param>
    /// <param name="requestDownload">
    /// What pressing Download does, given to the row rather than reached for through the list: a command on
    /// the view model would be an ancestor binding out of a data template, and this is the same thing without
    /// one. <c>null</c> leaves the button doing nothing, which is what a row built by a test without a launcher
    /// gets.
    /// </param>
    public PatchServerRevisionRow(
        PatchServerRevisionOnDisk revision,
        bool keep,
        Func<PatchServerRevisionRow, Task>? requestDownload = null) {

        Revision = revision;
        _keep = keep;

        if (requestDownload is not null) {
            RequestDownloadCommand = new AsyncRelayCommand(() => requestDownload(this));
        }
    }

    /// <summary>What is on disk, as it was read.</summary>
    public PatchServerRevisionOnDisk Revision { get; }

    /// <summary>Asks the launcher about fetching this revision, which is mostly a way of being told why not.</summary>
    public IAsyncRelayCommand? RequestDownloadCommand { get; }

    public string Name => Revision.Name;

    /// <summary>The revision itself, without the folder's <c>V_</c>.</summary>
    public string GameRevision => Revision.GameRevision ?? Revision.Name;

    public string SizeText => Revision.SizeText;

    public string FilesText => Revision.FilesText;

    /// <summary>What it is for, in the words the catalogue would say it in.</summary>
    public string RoleText => Revision.RoleText;

    /// <summary>Whether the launcher may remove it at all - false while the patch server is writing into it.</summary>
    public bool CanBeDeleted => Revision.CanBeDeleted;

    /// <summary>
    /// Whether to keep it: ticked means leave it alone, unticked means the Delete button would take it.
    /// </summary>
    /// <remarks>
    /// Defaults to ticked for the two revisions in use and unticked for the rest, so the list opens on the
    /// recommendation and pressing Delete once is \"remove what nobody is using\" - which is the same answer
    /// the automatic mode arrives at on its own.
    /// </remarks>
    [ObservableProperty] private bool _keep;
}
