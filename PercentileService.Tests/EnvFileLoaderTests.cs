namespace PercentileService.Tests;

public sealed class EnvFileLoaderTests : IDisposable
{
    private readonly string _envPath;

    public EnvFileLoaderTests()
    {
        _envPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.env");
    }

    public void Dispose()
    {
        try { File.Delete(_envPath); } catch { }
        // Clean up env vars we set
        Environment.SetEnvironmentVariable("TEST_KEY_1", null);
        Environment.SetEnvironmentVariable("TEST_KEY_2", null);
        Environment.SetEnvironmentVariable("QUOTED_VAL", null);
        Environment.SetEnvironmentVariable("NOVAL", null);
    }

    [Fact]
    public void Load_sets_environment_variables()
    {
        File.WriteAllText(_envPath, "TEST_KEY_1=hello\nTEST_KEY_2=world\n");

        EnvFileLoader.Load(_envPath);

        Assert.Equal("hello", Environment.GetEnvironmentVariable("TEST_KEY_1"));
        Assert.Equal("world", Environment.GetEnvironmentVariable("TEST_KEY_2"));
    }

    [Fact]
    public void Load_strips_quotes_from_values()
    {
        File.WriteAllText(_envPath, "QUOTED_VAL=\"some value\"\n");

        EnvFileLoader.Load(_envPath);

        Assert.Equal("some value", Environment.GetEnvironmentVariable("QUOTED_VAL"));
    }

    [Fact]
    public void Load_skips_comments_and_blank_lines()
    {
        File.WriteAllText(_envPath, "# comment\n\nTEST_KEY_1=yes\n  \n# another comment\n");

        EnvFileLoader.Load(_envPath);

        Assert.Equal("yes", Environment.GetEnvironmentVariable("TEST_KEY_1"));
    }

    [Fact]
    public void Load_skips_lines_without_equals()
    {
        File.WriteAllText(_envPath, "NOEQUALSSIGN\nTEST_KEY_1=ok\n");

        EnvFileLoader.Load(_envPath);

        Assert.Equal("ok", Environment.GetEnvironmentVariable("TEST_KEY_1"));
    }

    [Fact]
    public void Load_handles_nonexistent_file()
    {
        // Should not throw when file doesn't exist
        EnvFileLoader.Load("/nonexistent/path/.env");
    }

    [Fact]
    public void Load_handles_equals_in_value()
    {
        File.WriteAllText(_envPath, "TEST_KEY_1=a=b=c\n");

        EnvFileLoader.Load(_envPath);

        Assert.Equal("a=b=c", Environment.GetEnvironmentVariable("TEST_KEY_1"));
    }

    [Fact]
    public void Load_skips_line_starting_with_equals()
    {
        File.WriteAllText(_envPath, "=value\nTEST_KEY_1=good\n");

        EnvFileLoader.Load(_envPath);

        Assert.Equal("good", Environment.GetEnvironmentVariable("TEST_KEY_1"));
    }
}
