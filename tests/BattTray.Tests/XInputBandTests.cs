using BattTray.Devices;
using BattTray.Settings;
using BattTray.Tray;

namespace BattTray.Tests;

/// <summary>
/// The stand-in percentages XInput's four levels sort and alert by.
/// </summary>
/// <remarks>
/// These numbers are the one place three files have to agree, and nothing in the compiler
/// notices when they stop: the bands live in <see cref="XInputGamepadProvider"/>, the
/// thresholds in <see cref="AppSettings"/>, and the re-arm margin in
/// <see cref="LowBatteryNotifier"/>. Change any one of them — widen the margin, offer a 40%
/// threshold, respace the bands — and a controller either alerts on every poll or never
/// re-arms, on hardware most people testing this will not have to hand.
///
/// So these are properties rather than examples. They assert the reasoning in the provider's
/// remarks, against whatever the three files currently say.
/// </remarks>
public class XInputBandTests
{
    static int[] Bands => XInputGamepadProvider.BandPercent;

    [Fact]
    public void ThereAreFourBandsAndTheyAscend()
    {
        Assert.Equal(4, Bands.Length);
        Assert.Equal(Bands.OrderBy(p => p), Bands);
        Assert.Equal(Bands.Distinct(), Bands);
    }

    [Fact]
    public void BandsStayWithinAPercentage()
    {
        Assert.All(Bands, percent => Assert.InRange(percent, 0, 100));
    }

    [Fact]
    public void EveryOfferedThresholdIsCoveredByTheBands()
    {
        // Each threshold must have at least one band below it and one above, or it either
        // never fires for a controller or fires for it always.
        foreach (int threshold in AppSettings.Thresholds)
        {
            Assert.Contains(Bands, percent => percent <= threshold);
            Assert.Contains(Bands, percent => percent > threshold);
        }
    }

    [Fact]
    public void EmptyIsBelowTheLowestThresholdAndFullIsAboveTheHighest()
    {
        Assert.True(Bands[0] < AppSettings.Thresholds.Min());
        Assert.True(Bands[^1] > AppSettings.Thresholds.Max());
    }

    [Fact]
    public void ClimbingOneBandReArmsAtTheHigherThresholds()
    {
        // The claim in the provider's remarks: at 20% and 30% a pad that alerted can recover
        // by climbing a single band, because a band is the smallest step it can take. Written
        // as a search rather than as the answer, so respacing the bands re-checks the claim
        // instead of failing on an arithmetic detail.
        foreach (int threshold in AppSettings.Thresholds.Where(t => t > 10))
        {
            int alerted = Bands.Last(percent => percent <= threshold);
            int next = Bands.First(percent => percent > alerted);

            Assert.True(
                next >= threshold + LowBatteryNotifier.ReArmMargin,
                $"a pad alerting at {alerted}% against a {threshold}% threshold cannot re-arm by climbing to {next}%");
        }
    }

    [Fact]
    public void AtTheLowestThresholdAPadStaysLatchedThroughOneBand()
    {
        // The documented exception rather than an oversight: a pad that alerted at EMPTY
        // against a 10% threshold needs 25% to re-arm, so it stays latched through LOW and
        // clears at MEDIUM. Pinned because it is the case someone would "fix" by narrowing the
        // margin, which would undo the re-arm behaviour for every percentage-reporting device.
        int reArmsAt = 10 + LowBatteryNotifier.ReArmMargin;

        Assert.True(Bands[1] < reArmsAt);
        Assert.True(Bands[2] >= reArmsAt);
    }

    [Fact]
    public void EachThresholdSeparatesTwoAdjacentBands()
    {
        // The provider's stated placement: 10 separates EMPTY from LOW, and 20 and 30 both
        // leave LOW below and MEDIUM above. A threshold that had two bands on one side of it
        // and none on the other would be a setting the controller cannot respond to.
        Assert.Equal(1, Bands.Count(percent => percent <= 10));
        Assert.Equal(2, Bands.Count(percent => percent <= 20));
        Assert.Equal(2, Bands.Count(percent => percent <= 30));
    }
}
