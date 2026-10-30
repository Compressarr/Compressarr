using Compressarr.Core;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;
using Compressarr.Core.Tests.Presets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Compressarr.Core.Tests.Queue;

file sealed class EmptyCountingScanner : IVideoFileScanner
{
    public int Calls { get; private set; }
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit)
    {
        Calls++;
        return Array.Empty<FileInfo>();
    }
}

/// <summary>The web tests switch the queue's folder-listing memory off so a test that drops a file sees it at once; this checks the app's real wiring turns it on.</summary>
public class QueueWiringTests : AppDataTestBase
{
    [Fact]
    public void TheAppsOwnWiring_GivesTheQueueDisplayAShortFolderListingMemory()
    {
        var scanner = new EmptyCountingScanner();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompressarrCore();
        services.RemoveAll<IVideoFileScanner>();
        services.AddSingleton<IVideoFileScanner>(scanner);
        using var provider = services.BuildServiceProvider();

        var input = Directory.CreateDirectory(Path.Combine(AppData, "Input")).FullName;
        var config = new CompressarrConfig();
        config.Lanes.Add(new LaneConfig { Id = "lane1", DisplayName = "Lane", Enabled = true, Input = input, Output = Path.Combine(AppData, "Out"), MoviePreset = "p" });
        var queue = provider.GetRequiredService<IQueueService>();
        var run = new QueueRunContext(null, new Dictionary<string, bool>());

        queue.GetUpNext(config, run);
        queue.GetUpNext(config, run);

        Assert.Equal(TimeSpan.FromSeconds(3), provider.GetRequiredService<QueueScanOptions>().CacheFor);
        Assert.Equal(1, scanner.Calls);
    }
}
