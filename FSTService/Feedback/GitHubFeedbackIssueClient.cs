using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FSTService.Feedback;

public sealed class FeedbackGitHubException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public interface IFeedbackIssueClient
{
    /// <summary>Uploads a file as a GitHub user attachment and returns its URL.</summary>
    Task<string> UploadAttachmentAsync(string filePath, string fileName, string contentType, CancellationToken cancellationToken);

    /// <summary>Creates the issue and returns its number.</summary>
    Task<int> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken);
}

public sealed class GitHubFeedbackIssueClient(
    IHttpClientFactory httpClientFactory,
    IOptions<FeedbackOptions> options,
    ILogger<GitHubFeedbackIssueClient> logger) : IFeedbackIssueClient
{
    public const string HttpClientName = "FeedbackGitHub";

    private readonly SemaphoreSlim _repositoryIdLock = new(1, 1);
    private long? _repositoryId;

    private FeedbackOptions Options => options.Value;

    private HttpClient Http => httpClientFactory.CreateClient(HttpClientName);

    public async Task<string> UploadAttachmentAsync(
        string filePath,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        var repositoryId = await GetRepositoryIdAsync(cancellationToken);
        var query = $"name={Uri.EscapeDataString(fileName)}&content_type={Uri.EscapeDataString(contentType)}&repository_id={repositoryId}";
        var uri = $"{Options.GitHubUploadsBaseUrl.TrimEnd('/')}/user-attachments/assets?{query}";
        await using var stream = File.OpenRead(filePath);
        using var request = CreateRequest(HttpMethod.Post, uri);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "attachment upload", cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("url", out var url)
            && url.GetString() is { } value
            && value.StartsWith("https://", StringComparison.Ordinal))
        {
            return value;
        }

        throw new FeedbackGitHubException("GitHub attachment upload returned no URL.");
    }

    public async Task<int> CreateIssueAsync(
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PostIssueAsync(title, body, labels, cancellationToken);
        }
        catch (FeedbackGitHubException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity && labels.Count > 0)
        {
            // A token without label permission is rejected; file the report without labels rather than lose it.
            logger.LogWarning("Feedback issue labels were rejected; retrying without labels.");
            return await PostIssueAsync(title, body, [], cancellationToken);
        }
    }

    private async Task<int> PostIssueAsync(
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{ApiBase}/repos/{Options.GitHubRepository}/issues");
        request.Content = JsonContent.Create(new { title, body, labels });
        using var response = await Http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "issue creation", cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("number", out var number) && number.TryGetInt32(out var value))
            return value;
        throw new FeedbackGitHubException("GitHub issue creation returned no issue number.");
    }

    private async Task<long> GetRepositoryIdAsync(CancellationToken cancellationToken)
    {
        if (_repositoryId is { } cached)
            return cached;
        await _repositoryIdLock.WaitAsync(cancellationToken);
        try
        {
            if (_repositoryId is { } again)
                return again;
            using var request = CreateRequest(HttpMethod.Get, $"{ApiBase}/repos/{Options.GitHubRepository}");
            using var response = await Http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "repository lookup", cancellationToken);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value))
                throw new FeedbackGitHubException("GitHub repository lookup returned no id.");
            _repositoryId = value;
            return value;
        }
        finally
        {
            _repositoryIdLock.Release();
        }
    }

    private string ApiBase => Options.GitHubApiBaseUrl.TrimEnd('/');

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.GitHubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FSTService-Feedback", "1.0"));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 200)
            detail = detail[..200];
        throw new FeedbackGitHubException(
            $"GitHub {operation} failed with HTTP {(int)response.StatusCode}: {detail}",
            response.StatusCode);
    }
}
