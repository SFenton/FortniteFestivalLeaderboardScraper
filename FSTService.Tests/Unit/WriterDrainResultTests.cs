using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class WriterDrainResultTests
{
    [Fact]
    public void EmptyResultIsSuccessful()
    {
        var result = WriterDrainResult.Empty("solo");

        Assert.True(result.IsSuccess);
        Assert.Equal("solo", result.WriterKind);
    }

    [Fact]
    public void ScrapeWriterExceptionSummarizesExactFailureCounts()
    {
        var failure = new WriterBatchFailure(
            "band",
            "Band_Duets",
            [
                new WriterFailedScope("song_1", 2, 125),
                new WriterFailedScope("song_2", 1, 25),
            ],
            "InjectedException",
            "injected",
            "/replay",
            DateTime.UtcNow);
        var result = new WriterDrainResult(
            "band",
            3,
            150,
            0,
            0,
            [failure],
            "/replay");

        var exception = new ScrapeWriterException(42, [result]);

        Assert.Equal(42, exception.ScrapeId);
        Assert.Same(result, Assert.Single(exception.Results));
        Assert.Contains("1 writer failure batch", exception.Message);
        Assert.Contains("3 page", exception.Message);
        Assert.Contains("150 row", exception.Message);
        Assert.Equal(3, failure.PageCount);
        Assert.Equal(150, failure.RowCount);
    }
}
