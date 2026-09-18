using PetGPT.Models;
using PetGPT.Services;
using Xunit;

namespace PetGPT.Tests;

public sealed class WindowGeometryTests
{
    [Fact]
    public void ResizeAroundAnchor_PreservesBottomCenterAcrossPresentationSizes()
    {
        var monitor = new MonitorInfo(
            "primary",
            new ScreenRectPx(0, 0, 1000, 1000),
            new ScreenRectPx(0, 0, 1000, 1000),
            96,
            96,
            true);

        var resized = WindowPositionService.ResizeAroundAnchor(
            new ScreenRectPx(100, 100, 150, 150),
            oldAnchorX: 0.5,
            oldAnchorY: 1,
            new SizeDip(200, 100),
            newAnchorX: 0.5,
            newAnchorY: 1,
            [monitor]);

        Assert.Equal(new ScreenRectPx(75, 150, 200, 100), resized);
    }

    [Fact]
    public void ResizeAroundAnchor_ClampsResizedPresentationToWorkArea()
    {
        var monitor = new MonitorInfo(
            "primary",
            new ScreenRectPx(0, 0, 1000, 1000),
            new ScreenRectPx(0, 0, 1000, 1000),
            96,
            96,
            true);

        var resized = WindowPositionService.ResizeAroundAnchor(
            new ScreenRectPx(850, 850, 140, 140),
            oldAnchorX: 1,
            oldAnchorY: 1,
            new SizeDip(300, 300),
            newAnchorX: 0,
            newAnchorY: 0,
            [monitor]);

        Assert.Equal(new ScreenRectPx(692, 692, 300, 300), resized);
    }

    [Fact]
    public void RestorePlacement_UsesDipOffsetsAtOneHundredPercentDpi()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("primary", 100, 200),
            new SizeDip(150, 150),
            [monitor]);

        AssertRect(result, 100, 200, 150, 150);
    }

    [Fact]
    public void RestorePlacement_ConvertsDipOffsetsAtOneHundredFiftyPercentDpi()
    {
        var monitor = Monitor("scaled", 1920, 0, 2560, 1440, dpi: 144, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("scaled", 100, 200),
            new SizeDip(150, 150),
            [monitor]);

        AssertRect(result, 2070, 300, 225, 225);
    }

    [Fact]
    public void RestorePlacement_ConvertsDipOffsetsAtTwoHundredPercentDpi()
    {
        var monitor = Monitor("scaled", 0, 0, 3840, 2160, dpi: 192, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("scaled", 100, 200),
            new SizeDip(150, 150),
            [monitor]);

        AssertRect(result, 200, 400, 300, 300);
    }

    [Fact]
    public void RestorePlacement_PreservesNegativeVirtualScreenCoordinates()
    {
        var left = Monitor("left", -1920, 0, 1920, 1080, dpi: 96);
        var primary = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("left", 100, 80),
            new SizeDip(150, 150),
            [left, primary]);

        AssertRect(result, -1820, 80, 150, 150);
    }

    [Fact]
    public void RestorePlacement_SupportsMonitorLeftOfPrimary()
    {
        var left = Monitor("left", -2560, 0, 2560, 1440, dpi: 144);
        var primary = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("left", 200, 100),
            new SizeDip(200, 100),
            [left, primary]);

        AssertRect(result, -2260, 150, 300, 150);
    }

    [Fact]
    public void RestorePlacement_SupportsMonitorAbovePrimary()
    {
        var above = Monitor("above", 0, -1200, 1920, 1200, dpi: 96);
        var primary = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("above", 120, 90),
            new SizeDip(150, 150),
            [above, primary]);

        AssertRect(result, 120, -1110, 150, 150);
    }

    [Fact]
    public void RestorePlacement_RespectsLeftTaskbarWorkArea()
    {
        var monitor = MonitorWithWorkArea(
            "primary",
            new ScreenRectPx(0, 0, 1920, 1080),
            new ScreenRectPx(48, 0, 1872, 1080));

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("primary", 0, 100),
            new SizeDip(150, 150),
            [monitor]);

        Assert.Equal(56, result.Left);
    }

    [Fact]
    public void RestorePlacement_RespectsRightTaskbarWorkArea()
    {
        var monitor = MonitorWithWorkArea(
            "primary",
            new ScreenRectPx(0, 0, 1920, 1080),
            new ScreenRectPx(0, 0, 1872, 1080));

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("primary", 1800, 100),
            new SizeDip(150, 150),
            [monitor]);

        Assert.Equal(1714, result.Left);
        Assert.Equal(1864, result.Right);
    }

    [Fact]
    public void RestorePlacement_RespectsTopTaskbarWorkArea()
    {
        var monitor = MonitorWithWorkArea(
            "primary",
            new ScreenRectPx(0, 0, 1920, 1080),
            new ScreenRectPx(0, 40, 1920, 1040));

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("primary", 100, 0),
            new SizeDip(150, 150),
            [monitor]);

        Assert.Equal(48, result.Top);
    }

    [Fact]
    public void RestorePlacement_RespectsBottomTaskbarWorkArea()
    {
        var monitor = MonitorWithWorkArea(
            "primary",
            new ScreenRectPx(0, 0, 1920, 1080),
            new ScreenRectPx(0, 0, 1920, 1040));

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("primary", 100, 1000),
            new SizeDip(150, 150),
            [monitor]);

        Assert.Equal(882, result.Top);
        Assert.Equal(1032, result.Bottom);
    }

    [Fact]
    public void RestorePlacement_UsesPrimaryWhenSavedMonitorDisappeared()
    {
        var secondary = Monitor("secondary", 1920, 0, 1920, 1080, dpi: 96);
        var primary = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("missing", 100, 100),
            new SizeDip(150, 150),
            [secondary, primary]);

        AssertRect(result, 100, 100, 150, 150);
    }

    [Fact]
    public void RestorePlacement_UsesNearestMonitorForLegacyGlobalCoordinates()
    {
        var left = Monitor("left", -1920, 0, 1920, 1080, dpi: 96);
        var primary = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement(null, -1700, 100),
            new SizeDip(150, 150),
            [left, primary]);

        AssertRect(result, -1700, 100, 150, 150);
    }

    [Fact]
    public void RestorePlacement_ClampsPetBackOntoWorkArea()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("primary", -500, 5000),
            new SizeDip(150, 150),
            [monitor]);

        AssertRect(result, 8, 922, 150, 150);
    }

    [Fact]
    public void RestorePlacement_KeepsChatTitleStripReachable()
    {
        var monitor = Monitor("small", 0, 0, 800, 600, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("small", 100, 500),
            new SizeDip(500, 650),
            [monitor]);

        Assert.Equal(8, result.Top);
        Assert.True(result.Top + 36 <= monitor.WorkAreaPx.Bottom);
    }

    [Fact]
    public void RestorePlacement_ClampsBubbleSizeToAvailableWorkArea()
    {
        var monitor = Monitor("small", 0, 0, 800, 600, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement("small", 0, 0),
            new SizeDip(1200, 900),
            [monitor]);

        AssertRect(result, 8, 8, 784, 584);
    }

    [Fact]
    public void PositionFollowPet_PrefersAboveWhenSpaceExists()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);
        var pet = new ScreenRectPx(800, 700, 150, 150);

        var result = WindowPositionService.PositionFollowPet(
            pet,
            new SizeDip(500, 300),
            [monitor]);

        AssertRect(result, 625, 390, 500, 300);
    }

    [Fact]
    public void PositionFollowPet_ChoosesBelowWhenAboveDoesNotFit()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);
        var pet = new ScreenRectPx(800, 100, 150, 150);

        var result = WindowPositionService.PositionFollowPet(
            pet,
            new SizeDip(500, 300),
            [monitor]);

        AssertRect(result, 625, 260, 500, 300);
    }

    [Fact]
    public void PositionFollowPet_ClampsInsideCurrentWorkArea()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1040, dpi: 96, primary: true);
        var pet = new ScreenRectPx(1850, 850, 150, 150);

        var result = WindowPositionService.PositionFollowPet(
            pet,
            new SizeDip(500, 650),
            [monitor]);

        Assert.Equal(1412, result.Left);
        Assert.Equal(1920 - 8, result.Right);
        Assert.True(result.Top >= 8);
        Assert.True(result.Bottom <= 1032);
    }

    [Fact]
    public void RestorePlacement_FreeChatPositionIsIndependentFromPetPosition()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);
        var chatPlacement = new WindowPlacement("primary", 900, 200);

        var beforePetMove = WindowPositionService.RestorePlacement(
            chatPlacement,
            new SizeDip(500, 650),
            [monitor]);
        var afterPetMove = WindowPositionService.RestorePlacement(
            chatPlacement,
            new SizeDip(500, 650),
            [monitor]);

        AssertRect(beforePetMove, 900, 200, 500, 650);
        Assert.Equal(beforePetMove, afterPetMove);
    }

    [Fact]
    public void SettingsLoad_AcceptsPersistedFreeChatPlacement()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PetGPT.GeometryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "settings.v2.json"),
                """
                {
                  "SchemaVersion":2,
                  "SelectedPetId":"legacy",
                  "SelectedPackVersions":{},
                  "ChatHomeUrl":null,
                  "PetPlacement":{"MonitorId":"primary","XWithinWorkAreaDip":10,"YWithinWorkAreaDip":20},
                  "ChatWindow":{"PlacementMode":"Free","WidthDip":500,"HeightDip":650,"MonitorId":"primary","XWithinWorkAreaDip":250,"YWithinWorkAreaDip":100},
                  "CompactMode":true,
                  "ThemesEnabled":true,
                  "Roleplay":{"Enabled":false,"ActivationMode":"ReviewThenSend"},
                  "ReactionsEnabled":false,
                  "ShowControlMarkers":false,
                  "PetOptions":{},
                  "SuspendHiddenBrowser":false
                }
                """);

            var result = new SettingsService(folder, TimeSpan.FromMilliseconds(1)).Load();

            Assert.Equal(SettingsLoadSource.V2, result.Source);
            Assert.Equal("Free", result.Settings.ChatWindow.PlacementMode);
            Assert.Equal(250, result.Settings.ChatWindow.XWithinWorkAreaDip);
            Assert.Equal(100, result.Settings.ChatWindow.YWithinWorkAreaDip);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(96, 37.5, 37.5)]
    [InlineData(144, 37.5, 56.25)]
    [InlineData(192, 37.5, 75)]
    public void DipPixelConversion_RoundTripsAtExplicitBoundary(double dpi, double dip, double expectedPx)
    {
        var pixels = WindowPositionService.DipToPx(dip, dpi);
        var roundTripDip = WindowPositionService.PxToDip(pixels, dpi);

        Assert.Equal(expectedPx, pixels, precision: 6);
        Assert.Equal(dip, roundTripDip, precision: 6);
    }

    [Fact]
    public void MoveByScreenDelta_UsesPhysicalCursorDeltaWithoutDpiMixing()
    {
        var atOneHundredPercent = WindowPositionService.MoveByScreenDelta(
            new ScreenRectPx(100, 100, 150, 150),
            new ScreenPointPx(400, 400),
            new ScreenPointPx(496, 448));
        var atTwoHundredPercent = WindowPositionService.MoveByScreenDelta(
            new ScreenRectPx(1000, 100, 300, 300),
            new ScreenPointPx(1400, 400),
            new ScreenPointPx(1592, 496));

        Assert.Equal(96, atOneHundredPercent.Left - 100);
        Assert.Equal(48, atOneHundredPercent.Top - 100);
        Assert.Equal(192, atTwoHundredPercent.Left - 1000);
        Assert.Equal(96, atTwoHundredPercent.Top - 100);
    }

    [Fact]
    public void CreatePlacement_PersistsMonitorRelativeDipOffsets()
    {
        var monitor = Monitor("scaled", 1920, 0, 2560, 1440, dpi: 144, primary: true);
        var window = new ScreenRectPx(2220, 225, 225, 225);

        var result = WindowPositionService.CreatePlacement(window, [monitor]);

        Assert.Equal("scaled", result.MonitorId);
        Assert.Equal(200, result.XWithinWorkAreaDip);
        Assert.Equal(150, result.YWithinWorkAreaDip);
    }

    [Fact]
    public void RestorePlacement_ClampsLegacyT2GlobalCoordinatesSafely()
    {
        var monitor = Monitor("primary", 0, 0, 1920, 1080, dpi: 96, primary: true);

        var result = WindowPositionService.RestorePlacement(
            new WindowPlacement(null, 9000, -400),
            new SizeDip(150, 150),
            [monitor]);

        AssertRect(result, 1762, 8, 150, 150);
    }

    private static MonitorInfo Monitor(
        string id,
        double left,
        double top,
        double width,
        double height,
        double dpi,
        bool primary = false) =>
        MonitorWithWorkArea(
            id,
            new ScreenRectPx(left, top, width, height),
            new ScreenRectPx(left, top, width, height),
            dpi,
            primary);

    private static MonitorInfo MonitorWithWorkArea(
        string id,
        ScreenRectPx bounds,
        ScreenRectPx workArea,
        double dpi = 96,
        bool primary = true) =>
        new(id, bounds, workArea, dpi, dpi, primary);

    private static void AssertRect(
        ScreenRectPx actual,
        double left,
        double top,
        double width,
        double height)
    {
        Assert.Equal(left, actual.Left, precision: 6);
        Assert.Equal(top, actual.Top, precision: 6);
        Assert.Equal(width, actual.Width, precision: 6);
        Assert.Equal(height, actual.Height, precision: 6);
    }
}
