using BattTray.Settings;

namespace BattTray.Tests;

/// <summary>
/// Clamping hand-edited values back onto the options the dialog offers.
/// </summary>
/// <remarks>
/// The settings file is plain JSON in a folder the user can open, so it will be edited by
/// hand, and a value the dialog cannot represent is worse than a wrong one: the dialog shows
/// the nearest option, the user closes it, and their edit is silently rewritten. Normalizing
/// on load makes that rewrite happen at load rather than at the next OK, so what the dialog
/// shows is what is in force.
/// </remarks>
public class AppSettingsTests
{
    [Fact]
    public void DefaultsAreAlreadyNormal()
    {
        var settings = new AppSettings();

        Assert.Equal(settings, settings.Normalized());
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    [InlineData(30, 30)]
    public void OfferedThresholdsSurviveUnchanged(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { LowBatteryThreshold = stored }.Normalized().LowBatteryThreshold);

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-40, 10)]
    [InlineData(13, 10)]
    [InlineData(16, 20)]
    [InlineData(25, 20)]     // Ties go to the first option scanned, which is the lower one.
    [InlineData(26, 30)]
    [InlineData(100, 30)]
    [InlineData(int.MaxValue, 30)]
    public void OutOfRangeThresholdsSnapToTheNearestOffered(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { LowBatteryThreshold = stored }.Normalized().LowBatteryThreshold);

    [Theory]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    public void OfferedIntervalsSurviveUnchanged(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { RefreshIntervalSeconds = stored }.Normalized().RefreshIntervalSeconds);

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(-1, 15)]
    [InlineData(100, 60)]
    [InlineData(200, 300)]
    [InlineData(86_400, 300)]
    public void OutOfRangeIntervalsSnapToTheNearestOffered(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { RefreshIntervalSeconds = stored }.Normalized().RefreshIntervalSeconds);

    [Fact]
    public void ZeroIntervalCannotSurviveAsABusyLoop()
    {
        // The one clamp with teeth: the interval drives a timer, and 0 or a negative would
        // either throw when the timer is configured or spin.
        Assert.True(new AppSettings { RefreshIntervalSeconds = 0 }.Normalized().RefreshIntervalSeconds > 0);
        Assert.True(new AppSettings { RefreshIntervalSeconds = -5 }.Normalized().RefreshIntervalSeconds > 0);
    }

    [Fact]
    public void NormalizingLeavesTheBooleansAlone()
    {
        var settings = new AppSettings { LowBatteryNotifications = false, HideDisconnected = true };

        var normalized = settings.Normalized();

        Assert.False(normalized.LowBatteryNotifications);
        Assert.True(normalized.HideDisconnected);
    }

    [Fact]
    public void EveryOfferedThresholdIsOnATenStepBoundary()
    {
        // Coarse sources report in ten-point buckets, so a threshold of 25 would mean "the
        // 3rd bucket" in practice and mislead about its own precision.
        Assert.All(AppSettings.Thresholds, threshold => Assert.Equal(0, threshold % 10));
    }

    [Fact]
    public void NormalizingIsIdempotent()
    {
        var once = new AppSettings { LowBatteryThreshold = 47, RefreshIntervalSeconds = 3 }.Normalized();

        Assert.Equal(once, once.Normalized());
    }
}
