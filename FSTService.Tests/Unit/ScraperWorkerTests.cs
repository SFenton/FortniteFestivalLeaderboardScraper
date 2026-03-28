using System.Reflection;
using System.Text.Json;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="ScraperWorker"/> — focuses on static/internal helpers
/// and mode-switching logic that can be tested without full HTTP orchestration.
/// </summary>
public class ScraperWorkerTests : IDisposable
{
    private readonly string _tempDir;

    public ScraperWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fst_worker_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // Use reflection to invoke private static methods
    private static IReadOnlyList<string> InvokeGetEnabledInstruments(ScraperOptions opts)
    {
        var method = typeof(ScraperWorker).GetMethod(
            "GetEnabledInstruments",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyList<string>)method.Invoke(null, [opts])!;
    }

    private static int InvokeLoadCachedPageEstimate(ScraperOptions opts)
    {
        var method = typeof(ScrapeOrchestrator).GetMethod(
            "LoadCachedPageEstimate",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, [opts])!;
    }

    private static void InvokeSaveCachedPageEstimate(ScraperOptions opts, int totalPages)
    {
        var method = typeof(ScrapeOrchestrator).GetMethod(
            "SaveCachedPageEstimate",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [opts, totalPages]);
    }

    // ─── GetEnabledInstruments ──────────────────────────────────

    [Fact]
    public void GetEnabledInstruments_AllEnabled_Returns6()
    {
        var opts = new ScraperOptions(); // All default to true
        var result = InvokeGetEnabledInstruments(opts);
        Assert.Equal(6, result.Count);
        Assert.Contains("Solo_Guitar", result);
        Assert.Contains("Solo_Bass", result);
        Assert.Contains("Solo_Vocals", result);
        Assert.Contains("Solo_Drums", result);
        Assert.Contains("Solo_PeripheralGuitar", result);
        Assert.Contains("Solo_PeripheralBass", result);
    }

    [Fact]
    public void GetEnabledInstruments_NoneEnabled_ReturnsEmpty()
    {
        var opts = new ScraperOptions
        {
            QueryLead = false,
            QueryBass = false,
            QueryVocals = false,
            QueryDrums = false,
            QueryProLead = false,
            QueryProBass = false,
        };
        var result = InvokeGetEnabledInstruments(opts);
        Assert.Empty(result);
    }

    [Fact]
    public void GetEnabledInstruments_Partial_ReturnsSubset()
    {
        var opts = new ScraperOptions
        {
            QueryLead = true,
            QueryDrums = true,
            QueryBass = false,
            QueryVocals = false,
            QueryProLead = false,
            QueryProBass = false,
        };
        var result = InvokeGetEnabledInstruments(opts);
        Assert.Equal(2, result.Count);
        Assert.Contains("Solo_Guitar", result);
        Assert.Contains("Solo_Drums", result);
    }

    // ─── Page estimate persistence ──────────────────────────────

    [Fact]
    public void LoadCachedPageEstimate_NoFile_Returns0()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        Assert.Equal(0, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void SaveAndLoad_PageEstimate_RoundTrips()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };

        InvokeSaveCachedPageEstimate(opts, 42);

        var loaded = InvokeLoadCachedPageEstimate(opts);
        Assert.Equal(42, loaded);
    }

    [Fact]
    public void SaveCachedPageEstimate_OverwritesPrevious()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };

        InvokeSaveCachedPageEstimate(opts, 100);
        InvokeSaveCachedPageEstimate(opts, 200);

        Assert.Equal(200, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void LoadCachedPageEstimate_MalformedJson_Returns0()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var path = Path.Combine(_tempDir, "page-estimate.json");
        File.WriteAllText(path, "not json at all");

        Assert.Equal(0, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void LoadCachedPageEstimate_MissingProperty_Returns0()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        var path = Path.Combine(_tempDir, "page-estimate.json");
        File.WriteAllText(path, """{"something":"else"}""");

        Assert.Equal(0, InvokeLoadCachedPageEstimate(opts));
    }

    [Fact]
    public void SaveCachedPageEstimate_WritesValidJson()
    {
        var opts = new ScraperOptions { DataDirectory = _tempDir };
        InvokeSaveCachedPageEstimate(opts, 999);

        var path = Path.Combine(_tempDir, "page-estimate.json");
        Assert.True(File.Exists(path));

        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(999, doc.RootElement.GetProperty("totalPages").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("savedAt", out _));
    }

    // ─── ScraperOptions defaults ────────────────────────────────

    [Fact]
    public void ScraperOptions_Defaults_Reasonable()
    {
        var opts = new ScraperOptions();
        Assert.Equal(TimeSpan.FromHours(4), opts.ScrapeInterval);
        Assert.Equal(16, opts.DegreeOfParallelism);
        Assert.True(opts.QueryLead);
        Assert.True(opts.QueryDrums);
        Assert.True(opts.QueryVocals);
        Assert.True(opts.QueryBass);
        Assert.True(opts.QueryProLead);
        Assert.True(opts.QueryProBass);
        Assert.False(opts.ApiOnly);
        Assert.False(opts.SetupOnly);
        Assert.False(opts.RunOnce);
        Assert.False(opts.ResolveOnly);
        Assert.Null(opts.TestSongQuery);
    }
}
