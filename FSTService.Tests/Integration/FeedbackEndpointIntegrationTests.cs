using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FSTService.Feedback;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace FSTService.Tests.Integration;

[Collection(ApiIntegrationCollection.Name)]
public sealed class FeedbackEndpointIntegrationTests(ApiEndpointIntegrationTests.FstWebApplicationFactory factory)
    : IClassFixture<ApiEndpointIntegrationTests.FstWebApplicationFactory>
{
    private readonly IFeedbackIssueClient _issues = Substitute.For<IFeedbackIssueClient>();
    private readonly IFeedbackMediaProcessor _processor = Substitute.For<IFeedbackMediaProcessor>();

    private WebApplicationFactory<Program> EnabledFactory(int maxAttachments = 4, long maxRequestBytes = 90L * 1024 * 1024)
        => factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<FeatureOptions>(options => options.Feedback = true);
            services.PostConfigure<FeedbackOptions>(options =>
            {
                options.GitHubRepository = "example/tracker";
                options.GitHubToken = "test-token";
                options.MaxAttachments = maxAttachments;
                options.MaxRequestBytes = maxRequestBytes;
            });
            services.RemoveAll<IFeedbackIssueClient>();
            services.AddSingleton(_issues);
            services.RemoveAll<IFeedbackMediaProcessor>();
            services.AddSingleton(_processor);
        }));

    private static MultipartFormDataContent BugForm(params (string Name, byte[] Bytes, string ContentType)[] media)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("bug"), "kind" },
            { new StringContent("web"), "platform" },
            { new StringContent("[Bug] Scores missing"), "title" },
            { new StringContent("Scores vanished"), "description" },
            { new StringContent("Open the song page"), "repro" },
            { new StringContent("Scores show"), "expected" },
            { new StringContent("1.2.3"), "appVersion" },
        };
        foreach (var (name, bytes, contentType) in media)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(content, "media", name);
        }

        return form;
    }

    [Fact]
    public async Task Features_ReportFeedbackOffByDefault()
    {
        using var client = factory.CreateClient();
        var json = await client.GetFromJsonAsync<JsonElement>("/api/features");
        Assert.False(json.GetProperty("feedback").GetBoolean());
    }

    [Fact]
    public async Task Disabled_ReturnsNotFound()
    {
        using var client = factory.CreateClient();

        var post = await client.PostAsync("/api/feedback", BugForm());
        var get = await client.GetAsync($"/api/feedback/{new string('a', 32)}");

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal("feedback_disabled", (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task FlagWithoutCredentials_StaysDisabled()
    {
        using var flagOnly = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<FeatureOptions>(options => options.Feedback = true)));
        using var client = flagOnly.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/features");
        Assert.False(json.GetProperty("feedback").GetBoolean());
    }

    [Fact]
    public async Task Enabled_AcceptsSubmissionAndReportsIssueNumber()
    {
        _processor.PrepareAsync(Arg.Any<FeedbackAttachmentUpload>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var upload = call.Arg<FeedbackAttachmentUpload>();
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(upload.FilePath));
                return new FeedbackPreparedMedia(upload.FilePath, "shot.png", "image/png", "image", false, null);
            });
        _issues.UploadAttachmentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://github.com/user-attachments/assets/1");
        _issues.CreateIssueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(42);
        using var enabled = EnabledFactory();
        using var client = enabled.CreateClient();

        var features = await client.GetFromJsonAsync<JsonElement>("/api/features");
        Assert.True(features.GetProperty("feedback").GetBoolean());

        var response = await client.PostAsync("/api/feedback", BugForm(("C:\\Users\\me\\shot.png", [0x89, 0x50, 0x4E, 0x47], "image/png")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", accepted.GetProperty("status").GetString());
        var id = accepted.GetProperty("id").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", id);

        JsonElement status = default;
        for (var i = 0; i < 200; i++)
        {
            status = await client.GetFromJsonAsync<JsonElement>($"/api/feedback/{id}");
            if (status.GetProperty("status").GetString() is "submitted" or "failed")
                break;
            await Task.Delay(25);
        }

        Assert.Equal("submitted", status.GetProperty("status").GetString());
        Assert.Equal(42, status.GetProperty("issueNumber").GetInt32());
        Assert.False(status.TryGetProperty("error", out _));
        var attachment = Assert.Single(status.GetProperty("attachments").EnumerateArray());
        Assert.Equal("shot.png", attachment.GetProperty("name").GetString());
        Assert.Equal("attached", attachment.GetProperty("outcome").GetString());
        await _issues.Received(1).CreateIssueAsync(
            "[Bug] Scores missing",
            Arg.Is<string>(body => body.Contains("![shot.png](https://github.com/user-attachments/assets/1)")),
            Arg.Is<IReadOnlyList<string>>(labels => labels.SequenceEqual(new[] { "surface:web" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_RejectsInvalidSubmissions()
    {
        using var enabled = EnabledFactory(maxAttachments: 1);
        using var client = enabled.CreateClient();

        async Task<string?> CodeFor(HttpContent content, HttpStatusCode expectedStatus)
        {
            var response = await client.PostAsync("/api/feedback", content);
            Assert.Equal(expectedStatus, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        }

        using var invalidKind = new MultipartFormDataContent { { new StringContent("question"), "kind" } };
        Assert.Equal("invalid_kind", await CodeFor(invalidKind, HttpStatusCode.BadRequest));
        Assert.Equal("too_many_attachments", await CodeFor(
            BugForm(("a.png", [1], "image/png"), ("b.png", [1], "image/png")), HttpStatusCode.BadRequest));
        Assert.Equal("unsupported_media", await CodeFor(BugForm(("notes.txt", [1], "text/plain")), HttpStatusCode.BadRequest));
        Assert.Equal("unsupported_media", await CodeFor(BugForm(("empty.png", [], "image/png")), HttpStatusCode.BadRequest));
        Assert.Equal("invalid_form", await CodeFor(JsonContent.Create(new { kind = "bug" }), HttpStatusCode.BadRequest));
        await _issues.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Enabled_RejectsOversizedUpload()
    {
        using var enabled = EnabledFactory(maxRequestBytes: 4096);
        using var client = enabled.CreateClient();

        var response = await client.PostAsync("/api/feedback", BugForm(("big.png", new byte[8192], "image/png")));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("payload_too_large", json.GetProperty("code").GetString());
        Assert.Equal(4096, json.GetProperty("maxBytes").GetInt64());
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public async Task Status_UnknownIdIsNotFound(string id)
    {
        using var enabled = EnabledFactory();
        using var client = enabled.CreateClient();

        var response = await client.GetAsync($"/api/feedback/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
