using FSTService.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Api;

public sealed record PublicationReadContext(
    long PublicationId,
    long PublishedScrapeId,
    DateTime? PublishedAtUtc);

public sealed class PublicationReadLockDataSource : IAsyncDisposable
{
    private readonly bool _ownsDataSource;

    public PublicationReadLockDataSource(string connectionString)
    {
        var builder =
            new NpgsqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = "fst-publication-read-lock",
                MinPoolSize = 0,
                MaxPoolSize = 64,
            };
        DataSource = NpgsqlDataSource.Create(
            builder.ConnectionString);
        _ownsDataSource = true;
    }

    public PublicationReadLockDataSource(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }

    public ValueTask DisposeAsync() =>
        _ownsDataSource ? DataSource.DisposeAsync() : ValueTask.CompletedTask;
}

public sealed class PublicationReadContextService
{
    private readonly IMetaDatabase _metaDb;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptions<FeatureOptions> _features;

    public PublicationReadContextService(
        IMetaDatabase metaDb,
        NpgsqlDataSource dataSource,
        IOptions<FeatureOptions> features)
    {
        _metaDb = metaDb;
        _dataSource = dataSource;
        _features = features;
    }

    public PublicationReadContextService(
        IMetaDatabase metaDb,
        PublicationReadLockDataSource lockDataSource,
        IOptions<FeatureOptions> features)
        : this(metaDb, lockDataSource.DataSource, features)
    {
    }

    public bool PinningEnabled => _features.Value.EnablePublicationReadContext;

    public PublicationPointerState GetPointers() =>
        _metaDb.GetPublicationPointerState();

    public async Task<PublicationReadLease> AcquireAsync(
        CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            ct);
        try
        {
            await using (var advisoryLock = conn.CreateCommand())
            {
                advisoryLock.Transaction = tx;
                advisoryLock.CommandText =
                    "SELECT pg_advisory_xact_lock_shared(@lockKey)";
                advisoryLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                await advisoryLock.ExecuteNonQueryAsync(ct);
            }

            await using var pointers = conn.CreateCommand();
            pointers.Transaction = tx;
            pointers.CommandText = """
                SELECT current_publication_id,
                       previous_publication_id,
                       working_publication_id,
                       published_scrape_id,
                       published_at
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            await using var reader = await pointers.ExecuteReaderAsync(ct);
            var state = await reader.ReadAsync(ct)
                ? new PublicationPointerState(
                    reader.IsDBNull(0) ? null : reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4))
                : new PublicationPointerState(null, null, null, null, null);
            return new PublicationReadLease(conn, tx, state);
        }
        catch
        {
            await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }

    public void Invalidate()
    {
    }
}

public sealed class PublicationReadLease : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    internal PublicationReadLease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationPointerState pointers)
    {
        _connection = connection;
        _transaction = transaction;
        Pointers = pointers;
    }

    public PublicationPointerState Pointers { get; }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public static class PublicationReadContextHttpContextExtensions
{
    private static readonly object ContextKey = new();

    public static void SetPublicationReadContext(
        this HttpContext httpContext,
        PublicationReadContext publicationContext) =>
        httpContext.Items[ContextKey] = publicationContext;

    public static PublicationReadContext? GetPublicationReadContext(
        this HttpContext httpContext) =>
        httpContext.Items.TryGetValue(ContextKey, out var value)
            ? value as PublicationReadContext
            : null;
}

public sealed class PublicationReadContextMiddleware
{
    public const string PublicationHeader = "X-FST-Publication-Id";
    public const string PublicationQueryParameter = "publicationId";

    private readonly RequestDelegate _next;

    public PublicationReadContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        PublicationReadContextService publicationService)
    {
        if (!publicationService.PinningEnabled)
        {
            await _next(context);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<PublicationBound>() is null)
        {
            await _next(context);
            return;
        }

        if (!TryReadRequestedPublicationId(
                context.Request,
                out var requestedPublicationId,
                out var error))
        {
            await Results.BadRequest(new
            {
                error,
            }).ExecuteAsync(context);
            return;
        }

        PublicationReadLease? publicationLease =
            await publicationService.AcquireAsync(context.RequestAborted);
        try
        {
            var pointers = publicationLease.Pointers;
            if (!pointers.CurrentPublicationId.HasValue
                || !pointers.PublishedScrapeId.HasValue)
            {
                context.Response.Headers.CacheControl = "no-store";
                await Results.Problem(
                    title: "Published data unavailable",
                    detail: "No current publication generation is available.",
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                    .ExecuteAsync(context);
                return;
            }

            var currentPublicationId = pointers.CurrentPublicationId.Value;
            context.Response.Headers[PublicationHeader] =
                currentPublicationId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers.Append("Vary", PublicationHeader);

            if (requestedPublicationId.HasValue
                && requestedPublicationId.Value != currentPublicationId)
            {
                context.Response.Headers.CacheControl = "no-store";
                await Results.Json(new
                {
                    status = "publication_changed",
                    requestedPublicationId,
                    currentPublicationId,
                    previousPublicationId = pointers.PreviousPublicationId,
                    publishedScrapeId = pointers.PublishedScrapeId,
                }, statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
                return;
            }

            context.SetPublicationReadContext(new PublicationReadContext(
                currentPublicationId,
                pointers.PublishedScrapeId.Value,
                pointers.PublishedAtUtc));

            if (context.WebSockets.IsWebSocketRequest)
            {
                await publicationLease.DisposeAsync();
                publicationLease = null;
            }

            await _next(context);
        }
        finally
        {
            if (publicationLease is not null)
                await publicationLease.DisposeAsync();
        }
    }

    private static bool TryReadRequestedPublicationId(
        HttpRequest request,
        out long? requestedPublicationId,
        out string? error)
    {
        requestedPublicationId = null;
        error = null;

        var queryValue = request.Query[PublicationQueryParameter].FirstOrDefault();
        var headerValue = request.Headers[PublicationHeader].FirstOrDefault();
        if (!TryParseOptional(queryValue, out var queryId)
            || !TryParseOptional(headerValue, out var headerId))
        {
            error = "publicationId must be a positive integer.";
            return false;
        }

        if (queryId.HasValue
            && headerId.HasValue
            && queryId.Value != headerId.Value)
        {
            error = "Conflicting publication IDs were supplied.";
            return false;
        }

        requestedPublicationId = queryId ?? headerId;
        return true;
    }

    private static bool TryParseOptional(string? value, out long? publicationId)
    {
        publicationId = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        publicationId = parsed;
        return true;
    }
}
