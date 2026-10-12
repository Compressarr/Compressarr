using Compressarr.Core.Config;
using Compressarr.Core.Updates;

namespace Compressarr.Core.Tests.Updates;

public class UpdateChannelDefaultTests
{
    /// <summary>The 2.2 betas default to "Stable and beta" so testers are offered each new beta. That default must go back to Stable when
    /// 2.2.0 is released. This test is the reminder: it passes while the build is a pre-release, and fails the moment the version has no
    /// pre-release tag (2.2.0 final) until UpdateCheckSettings.DefaultChannel is set to Stable.</summary>
    [Fact]
    public void TheDefaultChannel_IsBetaWhileThisBuildIsAPreRelease_AndStableOnceItIsAFinalRelease()
    {
        var installed = AppVersion.Installed();
        Assert.NotNull(installed);

        var expected = installed!.IsPreRelease ? UpdateChannel.Beta : UpdateChannel.Stable;

        Assert.True(UpdateCheckSettings.DefaultChannel == expected,
            installed.IsPreRelease
                ? "This is a pre-release build, so the update channel should default to Beta."
                : "This is a final release: set UpdateCheckSettings.DefaultChannel to UpdateChannel.Stable (src/Compressarr.Core/Config/CompressarrConfig.cs).");
        Assert.Equal(UpdateCheckSettings.DefaultChannel, new CompressarrConfig().UpdateCheck.Channel);
    }
}
