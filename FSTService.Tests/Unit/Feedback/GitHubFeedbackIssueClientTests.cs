using System.Net;
using System.Text;
using System.Text.Json;
using FSTService.Feedback;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit.Feedback;

public sealed class GitHubFeedbackIssueClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "test-artifacts", $"feedback_gh_{Guid.NewGuid():N}");
    private readonly StubHandler _handler = new();

    public GitHubFeedbackIssueClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private GitHubFeedbackIssueClient CreateClient()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GitHubFeedbackIssueClient.HttpClientName).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        return new GitHubFeedbackIssueClient(
            factory,
            Options.Create(new FeedbackOptions
            {
                GitHubRepository = "owner/tracker",
                GitHubToken = "secret-token",
                GitHubApiBaseUrl = "https://api.example/",
                GitHubUploadsBaseUrl = "https://uploads.example",
            }),
            NullLogger<GitHubFeedbackIssueClient>.Instance);
    }

    [Fact]
    public async Task Upload_LooksUpRepositoryOnceAndPostsFile()
    {
        var path = Path.Combine(_dir, "a.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        _handler.Respond("GET https://api.example/repos/owner/tracker", HttpStatusCode.OK, """{"id":987}""");
        _handler.Respond("POST https://uploads.example/user-attachments/assets", HttpStatusCode.Created,
            """{"url":"https://github.com/user-attachments/assets/xyz"}""");
        var client = CreateClient();

        var first = await client.UploadAttachmentAsync(path, "my shot.png", "image/png", CancellationToken.None);
        var second = await client.UploadAttachmentAsync(path, "b.png", "image/png", CancellationToken.None);

        Assert.Equal("https://github.com/user-attachments/assets/xyz", first);
        Assert.Equal(first, second);
        Assert.Single(_handler.Requests, r => r.Method == "GET");
        var upload = _handler.Requests.First(r => r.Method == "POST");
        Assert.Equal("?name=my%20shot.png&content_type=image%2Fpng&repository_id=987", upload.Query);
        Assert.Equal(new byte[] { 1, 2, 3 }, upload.Body);
        Assert.Equal("Bearer secret-token", upload.Authorization);
    }

    [Fact]
    public async Task Upload_RejectsResponseWithoutHttpsUrl()
    {
        var path = Path.Combine(_dir, "a.png");
        await File.WriteAllBytesAsync(path, [1]);
        _handler.Respond("GET https://api.example/repos/owner/tracker", HttpStatusCode.OK, """{"id":1}""");
        _handler.Respond("POST https://uploads.example/user-attachments/assets", HttpStatusCode.Created, """{"url":"javascript:alert(1)"}""");

        await Assert.ThrowsAsync<FeedbackGitHubException>(() =>
            CreateClient().UploadAttachmentAsync(path, "a.png", "image/png", CancellationToken.None));
    }

    [Fact]
    public async Task Upload_SurfacesHttpFailure()
    {
        _handler.Respond("GET https://api.example/repos/owner/tracker", HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        var error = await Assert.ThrowsAsync<FeedbackGitHubException>(() =>
            CreateClient().UploadAttachmentAsync(Path.Combine(_dir, "missing"), "a.png", "image/png", CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task CreateIssue_SendsTitleBodyAndLabels()
    {
        _handler.Respond("POST https://api.example/repos/owner/tracker/issues", HttpStatusCode.Created, """{"number":55}""");

        var number = await CreateClient().CreateIssueAsync("[Bug] X", "body", ["From App", "Web"], CancellationToken.None);

        Assert.Equal(55, number);
        using var json = JsonDocument.Parse(_handler.Requests.Single().Body);
        Assert.Equal("[Bug] X", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("body", json.RootElement.GetProperty("body").GetString());
        Assert.Equal("From App", json.RootElement.GetProperty("labels")[0].GetString());
        Assert.Equal("Web", json.RootElement.GetProperty("labels")[1].GetString());
    }

    [Fact]
    public async Task CreateIssue_RetriesWithoutLabelsWhenRejected()
    {
        _handler.Respond("POST https://api.example/repos/owner/tracker/issues", HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}""");
        _handler.Respond("POST https://api.example/repos/owner/tracker/issues", HttpStatusCode.Created, """{"number":56}""");

        var number = await CreateClient().CreateIssueAsync("[Feature] Y", "body", ["From App", "iOS"], CancellationToken.None);

        Assert.Equal(56, number);
        Assert.Equal(2, _handler.Requests.Count);
        using var retry = JsonDocument.Parse(_handler.Requests[1].Body);
        Assert.Equal(0, retry.RootElement.GetProperty("labels").GetArrayLength());
    }

    [Fact]
    public async Task CreateIssue_ThrowsWhenNumberMissing()
    {
        _handler.Respond("POST https://api.example/repos/owner/tracker/issues", HttpStatusCode.Created, "{}");

        await Assert.ThrowsAsync<FeedbackGitHubException>(() =>
            CreateClient().CreateIssueAsync("[Bug] X", "body", [], CancellationToken.None));
    }

    private sealed record RecordedRequest(string Method, string Query, byte[] Body, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _responses = new(StringComparer.Ordinal);

        public List<RecordedRequest> Requests { get; } = [];

        public void Respond(string key, HttpStatusCode status, string body)
        {
            if (!_responses.TryGetValue(key, out var queue))
                _responses[key] = queue = new Queue<(HttpStatusCode, string)>();
            queue.Enqueue((status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method.Method, uri.Query, body, request.Headers.Authorization?.ToString()));
            var key = $"{request.Method.Method} {uri.GetLeftPart(UriPartial.Path)}";
            if (!_responses.TryGetValue(key, out var queue) || queue.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var (status, text) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
