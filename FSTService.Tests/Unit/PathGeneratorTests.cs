using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class PathGeneratorTests
{
    [Fact]
    public void ParseTotalScore_extracts_score_from_chopt_output()
    {
        var output = """
            Optimising...
            No SP score: 123456
            Total score: 234567
            Path: 1/2, 3/4
            """;

        var score = PathGenerator.ParseTotalScore(output);
        Assert.Equal(234567, score);
    }

    [Fact]
    public void ParseTotalScore_handles_score_on_first_line()
    {
        var score = PathGenerator.ParseTotalScore("Total score: 999999");
        Assert.Equal(999999, score);
    }

    [Fact]
    public void ParseTotalScore_returns_null_when_no_match()
    {
        var score = PathGenerator.ParseTotalScore("Some other output\nNo total here");
        Assert.Null(score);
    }

    [Fact]
    public void ParseTotalScore_returns_null_for_empty_output()
    {
        Assert.Null(PathGenerator.ParseTotalScore(""));
        Assert.Null(PathGenerator.ParseTotalScore("   "));
    }

    [Fact]
    public void ParseTotalScore_handles_whitespace_around_score()
    {
        var score = PathGenerator.ParseTotalScore("  Total score:   123456  ");
        Assert.Equal(123456, score);
    }

    [Fact]
    public void ParseTotalScore_case_insensitive()
    {
        var score = PathGenerator.ParseTotalScore("total score: 100000");
        Assert.Equal(100000, score);
    }

    [Fact]
    public void ParseTotalScore_ignores_non_numeric()
    {
        var score = PathGenerator.ParseTotalScore("Total score: abc");
        Assert.Null(score);
    }
}
