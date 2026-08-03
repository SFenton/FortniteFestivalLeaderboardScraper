using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class DiskStagingWriterTests
{
    [Fact]
    public async Task StagesWithinConfiguredDirectoryAndCleansCreatedDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"fst-staging-test-{Guid.NewGuid():N}");
        var stagingDirectory = Path.Combine(root, "precompute-staging");

        try
        {
            string stagingPath;
            await using (var writer = new DiskStagingWriter(
                             NullLogger<DiskStagingWriter>.Instance,
                             stagingDirectory))
            {
                writer.Write("key", [1, 2, 3], "etag");
                writer.Complete();
                await writer.WaitForDrainAsync();

                stagingPath = writer.StagingPath;
                Assert.Equal(
                    Path.GetFullPath(stagingDirectory),
                    Path.GetDirectoryName(stagingPath));
                Assert.True(File.Exists(stagingPath));
            }

            Assert.False(File.Exists(stagingPath));
            Assert.False(Directory.Exists(stagingDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DiskStagingWriter(
                NullLogger<DiskStagingWriter>.Instance,
                Path.GetTempPath(),
                channelCapacity: 0));
    }
}
