using Compressarr.Core.Config;

namespace Compressarr.Core.Tests.Config;

public class LaneProcessingTests
{
    private static CompressarrConfig Config() => new()
    {
        Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MinSizeBytes = 5, Limit = 7, OutSameAsIn = true }
    };

    [Fact]
    public void ALaneWithNoOverride_GetsTheVeryGlobalBlock_AndTheVeryConfig()
    {
        var config = Config();
        var lane = new LaneConfig { Id = "a" };

        Assert.Same(config.Processing, LaneProcessing.For(config, lane));
        Assert.Same(config, LaneProcessing.ConfigFor(config, lane));      // nothing changes for a lane that follows the global settings
        Assert.False(LaneProcessing.IsCustom(lane));
    }

    [Fact]
    public void ALaneWithAnOverride_UsesItsOwnValues_ButKeepsTheGlobalLimitAndOutputNextToInput()
    {
        var config = Config();
        var lane = new LaneConfig { Id = "a", Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MinSizeBytes = 99, Limit = 1, OutSameAsIn = false } };

        var p = LaneProcessing.For(config, lane);

        Assert.Equal(DeleteAfterConvertMode.Maintain, p.DeleteAfterConvert);
        Assert.Equal(99, p.MinSizeBytes);
        Assert.Equal(7, p.Limit);                  // a run-wide throttle: stays global
        Assert.True(p.OutSameAsIn);                // interacts with the lane's Output: stays global
        Assert.True(LaneProcessing.IsCustom(lane));
    }

    [Fact]
    public void TheConfigViewForAnOverridingLane_SharesEverythingElse_AndLeavesTheOriginalAlone()
    {
        var config = Config();
        var lane = new LaneConfig { Id = "a", Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain } };
        config.Lanes.Add(lane);

        var view = LaneProcessing.ConfigFor(config, lane);

        Assert.NotSame(config, view);
        Assert.Same(config.Lanes, view.Lanes);
        Assert.Same(config.HandBrake, view.HandBrake);
        Assert.Equal(DeleteAfterConvertMode.Maintain, view.Processing.DeleteAfterConvert);
        Assert.Equal(DeleteAfterConvertMode.Delete, config.Processing.DeleteAfterConvert);
    }

    [Fact]
    public void AnUnknownLane_FollowsTheGlobalSettings()
    {
        var config = Config();

        Assert.Same(config, LaneProcessing.ConfigFor(config, "no-such-lane"));
        Assert.Same(config.Processing, LaneProcessing.For(config, null));
    }

    [Fact]
    public void ACopyOfTheGlobalSettings_IsIndependent()
    {
        var config = Config();

        var copy = LaneProcessing.CopyOfGlobal(config);
        copy.VidTypes.Add("webm");
        copy.CompanionExtensions.Clear();
        copy.DeleteAfterConvert = DeleteAfterConvertMode.Maintain;

        Assert.DoesNotContain("webm", config.Processing.VidTypes);
        Assert.NotEmpty(config.Processing.CompanionExtensions);
        Assert.Equal(DeleteAfterConvertMode.Delete, config.Processing.DeleteAfterConvert);
    }
}
