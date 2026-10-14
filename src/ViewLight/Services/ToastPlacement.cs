namespace ViewLight.Services;

/// <summary>
/// Where the achievement toast sits on the screen.
/// </summary>
/// <remarks>
/// <para>
/// The toast is its own window rather than a card inside the launcher, because a badge is earned while the
/// game is being played - with the launcher behind the client, behind a full-screen game, or minimised to
/// the tray. A notification that shares the launcher's window would be missed at exactly the moment it
/// matters, so it lives on the desktop instead: always on top, in the corner of the monitor, over whatever
/// is there.
/// </para>
/// <para>
/// The arithmetic is kept here, apart from any window, so it can be checked: the corner is the working
/// area's bottom-right less the toast's own size and a margin, and the working area rather than the full
/// screen bounds is what is used, so a taskbar in the way pushes the toast above it instead of underneath.
/// </para>
/// </remarks>
public static class ToastPlacement {

    /// <summary>How far the toast sits from the corner, in device-independent pixels.</summary>
    public const int DefaultMargin = 24;

    /// <summary>
    /// The physical pixel position for the bottom-right of a working area.
    /// </summary>
    /// <param name="areaX">Left of the monitor's working area, in physical pixels.</param>
    /// <param name="areaY">Top of that area, in physical pixels.</param>
    /// <param name="areaWidth">Width of that area, in physical pixels.</param>
    /// <param name="areaHeight">Height of that area, in physical pixels.</param>
    /// <param name="toastWidth">The toast's width, in the device-independent pixels a layout is measured in.</param>
    /// <param name="toastHeight">The toast's height, on the same scale.</param>
    /// <param name="margin">Margin from the corner, on the same scale as the toast.</param>
    /// <param name="scaling">
    /// The monitor's scale factor. A layout is measured in device-independent pixels and a window is
    /// positioned in physical ones, so on a 150% display the toast would sit a third of the distance short
    /// of the corner without this.
    /// </param>
    /// <remarks>
    /// Never returns a position off the area: a toast larger than the monitor is pinned to its top-left
    /// rather than pushed off the screen altogether, so something is always visible to be read.
    /// </remarks>
    public static (int X, int Y) BottomRight(
        int areaX,
        int areaY,
        int areaWidth,
        int areaHeight,
        double toastWidth,
        double toastHeight,
        int margin = DefaultMargin,
        double scaling = 1.0) {

        var scale = scaling <= 0 ? 1.0 : scaling;

        var width = (int) Math.Ceiling(toastWidth * scale);
        var height = (int) Math.Ceiling(toastHeight * scale);
        var gap = (int) Math.Ceiling(margin * scale);

        var x = areaX + Math.Max(0, areaWidth - width - gap);
        var y = areaY + Math.Max(0, areaHeight - height - gap);

        return (x, y);
    }
}
