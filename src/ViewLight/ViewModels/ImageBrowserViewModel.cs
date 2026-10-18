using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViewLight.Services;

namespace ViewLight.ViewModels;

/// <summary>Which collection the browsing window is showing.</summary>
public enum ImageBrowserSource {

    /// <summary>The Xbox 360 gamerpic archive - square pictures, which is what a profile picture is.</summary>
    Gamerpics,

    /// <summary>Wallhaven's weekly toplist - wide pictures, which is what a banner is.</summary>
    Wallpapers,
}

/// <summary>
/// One cell in the grid: a picture, what it is, and the thing it stands for.
/// </summary>
/// <remarks>
/// A view model rather than a value, because the thumbnail arrives after the cell does. Downloading
/// thirty-two thousand gamerpics to fill a grid would be absurd; a page of them is fetched as it is
/// looked at, and each cell fills itself in when its own picture lands. Everything else about a cell is
/// known from the listing, so the grid is readable immediately and illustrated a moment later.
/// </remarks>
public sealed partial class ImageBrowserItem : ObservableObject {

    /// <param name="caption">What the cell is called.</param>
    /// <param name="detail">The smaller line under it: the category, or a wallpaper's size.</param>
    /// <param name="source">What it stands for - a <see cref="Gamerpic"/> or a <see cref="WallhavenWallpaper"/>.</param>
    public ImageBrowserItem(string caption, string detail, object source) {
        Caption = caption;
        Detail = detail;
        Source = source;
    }

    public string Caption { get; }

    public string Detail { get; }

    public object Source { get; }

    /// <summary>The picture itself, once it has been fetched.</summary>
    [ObservableProperty] private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
}

/// <summary>
/// What a person chose, ready to be kept as a profile picture or banner.
/// </summary>
/// <param name="Bytes">The picture itself.</param>
/// <param name="Extension">The type it is, with the dot, so it is kept under a name that says so.</param>
/// <param name="Description">What to call it in the Log.</param>
public sealed record ChosenImage(byte[] Bytes, string Extension, string Description);

/// <summary>
/// The browsing window: a page of pictures at a time, a search for the ones that can be searched for, and
/// the one the user settled on.
/// </summary>
/// <remarks>
/// <para>
/// The window is the same window for both collections, because the two differ in where the pictures come
/// from and nothing else: a grid, a name under each cell, a way to look at more, and a button that takes the
/// one that was clicked. Splitting it in two would be two copies of that.
/// </para>
/// <para>
/// <b>A page at a time, on purpose.</b> The gamerpic archive holds around 32,500 pictures, and both sources
/// answer in pages of a couple of dozen because that is how such collections are browsed: a page that arrives
/// at once, thumbnails that fill in as they land, and a search for anyone who knows what they are looking
/// for.
/// </para>
/// </remarks>
public sealed partial class ImageBrowserViewModel : ObservableObject {

    /// <summary>
    /// How many gamerpics are shown per page.
    /// </summary>
    /// <remarks>
    /// Fewer than a wallpaper page, because these are fetched rather than indexed: a hundred and twenty
    /// 64×64 pictures is a little over a megabyte and a wall of faces, which is the right size for picking
    /// one out of by eye.
    /// </remarks>
    public const int GamerpicsPerPage = 120;

    private readonly GamerpicArchive _gamerpics;
    private readonly WallhavenClient _wallhaven;

    private IReadOnlyList<Gamerpic> _archive = [];

    public ImageBrowserViewModel(
        ImageBrowserSource source,
        GamerpicArchive? gamerpics = null,
        WallhavenClient? wallhaven = null) {

        Source = source;
        _gamerpics = gamerpics ?? new GamerpicArchive();
        _wallhaven = wallhaven ?? new WallhavenClient();

        Filtered = [];
    }

    public ImageBrowserSource Source { get; }

    /// <summary>The pictures on screen, as the grid binds them.</summary>
    public ObservableCollection<ImageBrowserItem> Items { get; } = [];

    /// <summary>What the window is called.</summary>
    public string Title => Source == ImageBrowserSource.Gamerpics
        ? "Browse the gamerpic archive"
        : "Browse wallpapers from wallhaven";

    /// <summary>The line under the title, saying what is being browsed and where it comes from.</summary>
    public string Intro => Source == ImageBrowserSource.Gamerpics
        ? "A backup of Xbox 360 gamerpics: games and packs, around 32,500 of them. These are square, which is "
          + "what a profile picture is. Searching matches the game's name; the pictures are fetched as they "
          + "are looked at and kept, so a page browsed twice costs nothing the second time."
        : "Wallhaven's weekly toplist - general and anime, SFW only - which is a few hundred wallpapers that "
          + "change every week, and wide enough to be a banner. Anything can be searched for as well. "
          + "Pictures are fetched as they are looked at and kept.";

    public string SearchWatermark => Source == ImageBrowserSource.Gamerpics
        ? "Search by game or pack…"
        : "Search wallhaven, or leave empty for this week's toplist…";

    /// <summary>The whole archive, filtered by the search box.</summary>
    private List<Gamerpic> Filtered { get; }

    [ObservableProperty] private string _search = string.Empty;

    [ObservableProperty] private int _page = 1;

    [ObservableProperty] private int _lastPage = 1;

    [ObservableProperty] private bool _isBusy;

    /// <summary>What is happening, or why nothing is: the line at the bottom of the window.</summary>
    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _isStatusProblem;

    [ObservableProperty] private ImageBrowserItem? _selected;

    public bool HasItems => Items.Count > 0;

    public bool HasStatus => Status.Length > 0;

    public string PageText => LastPage > 1 ? $"Page {Page} of {LastPage}" : string.Empty;

    public bool HasPages => LastPage > 1;

    public bool CanGoBack => Page > 1 && !IsBusy;

    public bool CanGoForward => Page < LastPage && !IsBusy;

    /// <summary>Whether there is something to take: one cell picked, and nothing in flight.</summary>
    public bool CanChoose => Selected is not null && !IsBusy;

    partial void OnSearchChanged(string value) => RefreshCommandStates();

    partial void OnPageChanged(int value) => RefreshCommandStates();

    partial void OnLastPageChanged(int value) => RefreshCommandStates();

    partial void OnIsBusyChanged(bool value) => RefreshCommandStates();

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnSelectedChanged(ImageBrowserItem? value) => OnPropertyChanged(nameof(CanChoose));

    private void RefreshCommandStates() {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Fills the grid for the first time, which is what the window does the moment it is up.
    /// </summary>
    /// <remarks>
    /// A method as well as a command because the two callers want different things from it: the window wants
    /// the task, so it is not fire-and-forget, and the buttons want a command to bind to.
    /// </remarks>
    public Task StartAsync() => LoadAsync();

    /// <summary>
    /// Loads the page on screen - the first one at startup, and whatever a search or a page move asks for.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync() {
        if (IsBusy) {
            return;
        }

        IsBusy = true;

        try {
            if (Source == ImageBrowserSource.Gamerpics) {
                await LoadGamerpicsAsync().ConfigureAwait(true);
            }
            else {
                await LoadWallpapersAsync().ConfigureAwait(true);
            }
        }
        finally {
            IsBusy = false;
        }
    }

    /// <summary>Goes back to the first page of what is now being looked at.</summary>
    [RelayCommand]
    private async Task SearchAsync() {
        Page = 1;
        await LoadAsync().ConfigureAwait(true);
    }

    private bool CanMoveBack() => CanGoBack;

    [RelayCommand(CanExecute = nameof(CanMoveBack))]
    private async Task PreviousPageAsync() {
        if (Page > 1) {
            Page--;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private bool CanMoveForward() => CanGoForward;

    [RelayCommand(CanExecute = nameof(CanMoveForward))]
    private async Task NextPageAsync() {
        if (Page < LastPage) {
            Page++;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Takes the chosen picture, fetching the full-size copy when it is a wallpaper.
    /// </summary>
    /// <returns>The picture, or <c>null</c> with <see cref="Status"/> saying why not.</returns>
    /// <remarks>
    /// The download happens here rather than in the launcher, so the wait is visible in the window that
    /// asked for it and a picture that will not arrive leaves the launcher's own state untouched. A
    /// wallpaper is the large one: a banner is the whole point of the choice, and the thumbnail would be a
    /// blurred banner.
    /// </remarks>
    public async Task<ChosenImage?> TakeSelectedAsync(CancellationToken token = default) {
        if (Selected?.Source is not { } source || IsBusy) {
            return null;
        }

        IsBusy = true;
        Status = Source == ImageBrowserSource.Gamerpics
            ? "Fetching the gamerpic…"
            : "Fetching the wallpaper…";
        IsStatusProblem = false;

        try {
            var (path, url, description) = source switch {
                Gamerpic gamerpic => (
                    await _gamerpics.PictureAsync(gamerpic, token).ConfigureAwait(true),
                    gamerpic.Url,
                    $"{gamerpic.Caption} from the gamerpic archive"),

                WallhavenWallpaper wallpaper => (
                    await _wallhaven.WallpaperAsync(wallpaper, token).ConfigureAwait(true),
                    wallpaper.FullUrl,
                    $"a {wallpaper.Resolution} wallpaper from wallhaven ({wallpaper.Id})"),

                _ => (null, string.Empty, string.Empty),
            };

            if (path is null) {
                Status = Source == ImageBrowserSource.Gamerpics
                    ? "That gamerpic could not be fetched. Check the connection and try again."
                    : "That wallpaper could not be fetched. Check the connection and try again.";
                IsStatusProblem = true;
                return null;
            }

            try {
                var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(true);

                return new ChosenImage(bytes, ImageCache.ExtensionOf(url), description);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Status = $"The picture could not be read back ({ex.Message}).";
                IsStatusProblem = true;
                return null;
            }
        }
        finally {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fills the grid from the archive: the whole thing is read once, filtered in memory, and a page of it
    /// is shown.
    /// </summary>
    private async Task LoadGamerpicsAsync() {
        if (_archive.Count == 0) {
            Status = "Reading the archive's index…";
            IsStatusProblem = false;

            _archive = await _gamerpics.LoadAsync().ConfigureAwait(true);

            if (_archive.Count == 0) {
                Items.Clear();
                OnPropertyChanged(nameof(HasItems));
                Status = _gamerpics.Problem ?? "The archive listed nothing.";
                IsStatusProblem = true;
                return;
            }
        }

        Filtered.Clear();

        var needle = Search.Trim();

        if (needle.Length == 0) {
            Filtered.AddRange(_archive);
        }
        else {
            foreach (var gamerpic in _archive) {
                if (gamerpic.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase)) {
                    Filtered.Add(gamerpic);
                }
            }
        }

        LastPage = Math.Max(1, (Filtered.Count + GamerpicsPerPage - 1) / GamerpicsPerPage);

        if (Page > LastPage) {
            Page = LastPage;
        }

        var page = Filtered
            .Skip((Page - 1) * GamerpicsPerPage)
            .Take(GamerpicsPerPage)
            .Select(gamerpic => new ImageBrowserItem(gamerpic.Caption, gamerpic.Category, gamerpic))
            .ToList();

        Show(page, Filtered.Count == 0
            ? "Nothing in the archive matches that."
            : $"{Filtered.Count:N0} gamerpics{(needle.Length == 0 ? string.Empty : " matching that")}, "
              + $"showing {page.Count}.");

        // Thumbnails last, and not awaited: the grid is readable as soon as it is listed, and each cell
        // fills itself in as its own small picture arrives.
        _ = FillThumbnailsAsync(page);
    }

    /// <summary>Fills the grid from wallhaven: one page of the toplist, or of a search.</summary>
    private async Task LoadWallpapersAsync() {
        Status = "Asking wallhaven…";
        IsStatusProblem = false;

        var page = string.IsNullOrWhiteSpace(Search)
            ? await _wallhaven.ToplistAsync(Page).ConfigureAwait(true)
            : await _wallhaven.SearchAsync(Search, Page).ConfigureAwait(true);

        if (page.Problem is { Length: > 0 } problem) {
            Items.Clear();
            OnPropertyChanged(nameof(HasItems));
            LastPage = 1;
            Status = problem;
            IsStatusProblem = true;
            return;
        }

        LastPage = page.LastPage;

        var items = page.Walls
            .Select(wallpaper => new ImageBrowserItem(wallpaper.Caption, wallpaper.Id, wallpaper))
            .ToList();

        Show(items, items.Count == 0
            ? "Wallhaven returned nothing for that."
            : $"{page.Total:N0} wallpapers in this listing, showing {items.Count}.");

        _ = FillThumbnailsAsync(items);
    }

    private void Show(IReadOnlyList<ImageBrowserItem> items, string status) {
        Items.Clear();

        foreach (var item in items) {
            Items.Add(item);
        }

        Status = status;
        IsStatusProblem = false;
        OnPropertyChanged(nameof(HasItems));
        RefreshCommandStates();
    }

    /// <summary>
    /// Fetches one page's thumbnails, one after another.
    /// </summary>
    /// <remarks>
    /// One at a time on purpose: they come from the same host, the cache behind them serialises anyway, and
    /// a hundred simultaneous requests is how a launcher gets itself rate limited. A picture that will not
    /// arrive leaves its cell as the empty square it already is, with the caption under it.
    /// </remarks>
    private async Task FillThumbnailsAsync(IReadOnlyList<ImageBrowserItem> items) {
        foreach (var item in items) {
            try {
                var path = item.Source switch {
                    Gamerpic gamerpic => await _gamerpics.PictureAsync(gamerpic).ConfigureAwait(true),
                    WallhavenWallpaper wallpaper => await _wallhaven.ThumbnailAsync(wallpaper).ConfigureAwait(true),
                    _ => null,
                };

                if (path is not null) {
                    item.Thumbnail = SchoolIconCache.Decode(path);
                }
            }
            catch (Exception) {
                // A cell without its picture is a cell with its caption, which is enough to browse by.
            }
        }
    }
}
