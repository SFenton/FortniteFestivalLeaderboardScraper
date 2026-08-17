using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FSTService.Api;

namespace FSTService.Tests.Unit;

public sealed class PublicApiJsonContractTests
{
    public static IEnumerable<object[]> ValidStrings()
    {
        yield return ["incident-john", "Jöhn"];
        yield return ["incident-lukasz", "Łukasz"];
        yield return ["html-sensitive", "< > & ' +"];
        yield return
        [
            "controls-u0000-u001f",
            string.Concat(
                Enumerable.Range(0, 32)
                    .Select(value => (char)value)),
        ];
        yield return ["emoji-non-bmp", "😀 𝄞"];
    }

    [Theory]
    [MemberData(nameof(ValidStrings))]
    public void Endpoint_precompute_and_alias_writers_have_exact_bytes_and_etags(
        string caseName,
        string value)
    {
        var endpointOptions = CreateOptions();
        var precomputeOptions = CreateOptions();
        var firstPageSource =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    page = 1,
                    pageSize = 50,
                    entries = new[]
                    {
                        new
                        {
                            displayName = value,
                            rank = 1,
                        },
                        new
                        {
                            displayName = "second",
                            rank = 2,
                        },
                    },
                },
                precomputeOptions);
        var firstPageExpected =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    page = 1,
                    pageSize = 1,
                    entries = new[]
                    {
                        new
                        {
                            displayName = value,
                            rank = 1,
                        },
                    },
                },
                endpointOptions);
        var overviewSource =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    rankBy = "adjusted",
                    pageSize = 10,
                    instruments =
                        new Dictionary<string, object>
                    {
                        ["Solo_Guitar"] = new
                        {
                            totalAccounts = 2,
                            entries = new[]
                            {
                                new
                                {
                                    displayName = value,
                                    rank = 1,
                                },
                                new
                                {
                                    displayName =
                                        "second",
                                    rank = 2,
                                },
                            },
                        },
                    },
                },
                precomputeOptions);
        var overviewExpected =
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    rankBy = "adjusted",
                    pageSize = 1,
                    instruments =
                        new Dictionary<string, object>
                    {
                        ["Solo_Guitar"] = new
                        {
                            totalAccounts = 2,
                            entries = new[]
                            {
                                new
                                {
                                    displayName = value,
                                    rank = 1,
                                },
                            },
                        },
                    },
                },
                endpointOptions);

        var firstPageActual =
            CacheHelper.ProjectFirstPageSubset(
                firstPageSource,
                requestedPage: 1,
                requestedPageSize: 1);
        var overviewActual =
            CacheHelper.ProjectOverviewSubset(
                overviewSource,
                requestedPageSize: 1);

        Assert.Equal(
            firstPageExpected,
            firstPageActual);
        Assert.Equal(
            overviewExpected,
            overviewActual);
        Assert.Equal(
            ResponseCacheService.ComputeETag(
                firstPageExpected),
            ResponseCacheService.ComputeETag(
                firstPageActual!));
        Assert.Equal(
            ResponseCacheService.ComputeETag(
                overviewExpected),
            ResponseCacheService.ComputeETag(
                overviewActual!));
        AssertValidJsonString(
            caseName,
            value,
            firstPageActual!,
            static root => root
                .GetProperty("entries")[0]
                .GetProperty("displayName")
                .GetString());
        AssertValidJsonString(
            caseName,
            value,
            overviewActual!,
            static root => root
                .GetProperty("instruments")
                .GetProperty("Solo_Guitar")
                .GetProperty("entries")[0]
                .GetProperty("displayName")
                .GetString());
    }

    [Fact]
    public void Projection_rejects_invalid_surrogate_utf8()
    {
        var firstPagePrefix = Encoding.UTF8.GetBytes(
            "{\"page\":1,\"pageSize\":50,"
            + "\"entries\":[{\"displayName\":\"");
        var firstPageSuffix = Encoding.UTF8.GetBytes(
            "\"}]}");
        var overviewPrefix = Encoding.UTF8.GetBytes(
            "{\"rankBy\":\"adjusted\","
            + "\"pageSize\":10,\"instruments\":{"
            + "\"Solo_Guitar\":{\"totalAccounts\":1,"
            + "\"entries\":[{\"displayName\":\"");
        var overviewSuffix = Encoding.UTF8.GetBytes(
            "\"}]}}}");
        var invalidSurrogateUtf8 =
            new byte[]
            {
                0xED,
                0xA0,
                0x80,
            };
        var firstPageSource = firstPagePrefix
            .Concat(invalidSurrogateUtf8)
            .Concat(firstPageSuffix)
            .ToArray();
        var overviewSource = overviewPrefix
            .Concat(invalidSurrogateUtf8)
            .Concat(overviewSuffix)
            .ToArray();

        Assert.Null(
            CacheHelper.ProjectFirstPageSubset(
                firstPageSource,
                requestedPage: 1,
                requestedPageSize: 1));
        Assert.Null(
            CacheHelper.ProjectOverviewSubset(
                overviewSource,
                requestedPageSize: 1));
    }

    [Fact]
    public void Projection_code_uses_only_the_shared_writer_factory()
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                ".."));
        var cacheHelper = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "FSTService",
                "Api",
                "CacheHelper.cs"));
        var program = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "FSTService",
                "Program.cs"));

        Assert.DoesNotContain(
            "new Utf8JsonWriter",
            cacheHelper,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                    cacheHelper,
                    "PublicApiJsonContract"
                    + "\\s*\\.CreateProjectionWriter")
                .Count);
        Assert.Contains(
            "PublicApiJsonContract",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Configure(opts.SerializerOptions)",
            program,
            StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
        };
        PublicApiJsonContract.Configure(options);
        return options;
    }

    private static void AssertValidJsonString(
        string caseName,
        string expected,
        byte[] json,
        Func<JsonElement, string?> readValue)
    {
        using var document =
            JsonDocument.Parse(json);
        Assert.Equal(
            expected,
            readValue(document.RootElement));
        var text = Encoding.UTF8.GetString(json);
        Assert.DoesNotContain(
            text,
            character => character < 0x20);

        if (caseName
            is "incident-john"
            or "incident-lukasz"
            or "html-sensitive")
        {
            Assert.Contains(
                expected,
                text,
                StringComparison.Ordinal);
        }
        else if (caseName == "emoji-non-bmp")
        {
            Assert.Contains(
                "\\uD83D\\uDE00",
                text,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "\\uD834\\uDD1E",
                text,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
