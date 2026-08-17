using FSTService.Scraping;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class DiskStagingWriterTests
{
    [Fact]
    public async Task StagesWithinConfiguredDirectoryAndCleansCreatedDirectory()
    {
        var root = CreateTestRoot();
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
    public async Task Disposal_after_incomplete_stage_cleans_failure_artifacts()
    {
        var root = CreateTestRoot();
        var stagingDirectory =
            Path.Combine(root, "precompute-staging");

        try
        {
            string stagingPath;
            await using (var writer =
                         new DiskStagingWriter(
                             NullLogger<
                                 DiskStagingWriter>.Instance,
                             stagingDirectory))
            {
                writer.Write(
                    "partial-key",
                    [9, 8, 7],
                    "\"partial\"");
                stagingPath = writer.StagingPath;
                Assert.True(
                    SpinWait.SpinUntil(
                        () => File.Exists(
                            stagingPath),
                        TimeSpan.FromSeconds(2)));
            }

            Assert.False(File.Exists(stagingPath));
            Assert.False(
                Directory.Exists(stagingDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DiskStagingWriter(
                NullLogger<DiskStagingWriter>.Instance,
                AppContext.BaseDirectory,
                channelCapacity: 0));
    }

    private static string CreateTestRoot() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "disk-staging-tests",
            $"fst-staging-test-{Guid.NewGuid():N}");
}
