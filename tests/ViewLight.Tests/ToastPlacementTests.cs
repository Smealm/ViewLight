using ViewLight.Services;
using Xunit;

namespace ViewLight.Tests;

public class ToastPlacementTests {

    // A 1920x1080 monitor with a 40px taskbar along the bottom, which is what the working area excludes.
    private const int AreaX = 0;
    private const int AreaY = 0;
    private const int AreaWidth = 1920;
    private const int AreaHeight = 1040;

    [Fact]
    public void SitsInTheBottomRightCornerWithAMargin() {
        var (x, y) = ToastPlacement.BottomRight(AreaX, AreaY, AreaWidth, AreaHeight, 320, 120);

        Assert.Equal(1920 - 320 - ToastPlacement.DefaultMargin, x);
        Assert.Equal(1040 - 120 - ToastPlacement.DefaultMargin, y);
    }

    [Fact]
    public void AccountsForTheMonitorsScaleFactor() {
        // On a 150% display the card is drawn a third larger, so twice the pixels have to be left between
        // it and the corner - which is what stops it hanging half off the screen.
        var (x, y) = ToastPlacement.BottomRight(AreaX, AreaY, AreaWidth, AreaHeight, 320, 120, 24, 1.5);

        Assert.Equal(1920 - 480 - 36, x);
        Assert.Equal(1040 - 180 - 36, y);
    }

    [Fact]
    public void UsesTheSecondMonitorsWorkingAreaAsGiven() {
        // A second monitor's area does not start at zero, and the toast belongs on the monitor it is told
        // about rather than back on the primary one.
        var (x, y) = ToastPlacement.BottomRight(1920, 0, 1280, 1000, 320, 120, 24, 1.0);

        Assert.Equal(1920 + 1280 - 320 - 24, x);
        Assert.Equal(1000 - 120 - 24, y);
    }

    [Fact]
    public void AToastBiggerThanTheMonitorIsPinnedOnScreenRatherThanPushedOff() {
        var (x, y) = ToastPlacement.BottomRight(AreaX, AreaY, 400, 300, 900, 700);

        Assert.Equal(AreaX, x);
        Assert.Equal(AreaY, y);
    }

    [Fact]
    public void NonsenseScalingFallsBackToUnscaledRatherThanProducingNonsense() {
        var (x, y) = ToastPlacement.BottomRight(AreaX, AreaY, AreaWidth, AreaHeight, 320, 120, 24, 0);

        Assert.Equal(1920 - 320 - 24, x);
        Assert.Equal(1040 - 120 - 24, y);
    }
}
