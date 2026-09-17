using FSTService.Persistence;
using FSTService.Scraping;
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
    internal const int AdmissionCommandTimeoutSeconds = 5;
    internal const int AdmissionLeaseLifetimeSeconds = 20;

    private readonly IMetaDatabase _metaDb;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IOptions<FeatureOptions> _features;
    private readonly PublicationReadinessEvaluator _readinessEvaluator;
    private readonly PublishedScopeSourceReadinessService
        _publishedScopeSourceReadiness;
    private readonly PublicationCommitOptions _commitOptions;

    public PublicationReadContextService(
        IMetaDatabase metaDb,
        NpgsqlDataSource dataSource,
        IOptions<FeatureOptions> features,
        IOptions<PublicationCommitOptions>? commitOptions = null,
        PublishedScopeSourceReadinessService?
            publishedScopeSourceReadiness = null)
    {
        _metaDb = metaDb;
        _dataSource = dataSource;
        _features = features;
        _commitOptions =
            commitOptions?.Value
            ?? new PublicationCommitOptions();
        _readinessEvaluator = new PublicationReadinessEvaluator(metaDb);
        _publishedScopeSourceReadiness =
            publishedScopeSourceReadiness
            ?? new PublishedScopeSourceReadinessService(
                metaDb,
                features);
    }

    public PublicationReadContextService(
        IMetaDatabase metaDb,
        PublicationReadLockDataSource lockDataSource,
        IOptions<FeatureOptions> features,
        IOptions<PublicationCommitOptions>? commitOptions = null,
        PublishedScopeSourceReadinessService?
            publishedScopeSourceReadiness = null)
        : this(
            metaDb,
            lockDataSource.DataSource,
            features,
            commitOptions,
            publishedScopeSourceReadiness)
    {
    }

    public bool PinningConfigured =>
        _features.Value.EnablePublicationReadContext;
    public bool PublishedScopeSourceReadinessRequired =>
        _publishedScopeSourceReadiness.Required;

    public bool PinningEnabled
    {
        get
        {
            if (!PinningConfigured)
                return false;

            var pointers = GetPointers();
            return pointers.CurrentPublicationId.HasValue
                   && pointers.PublishedScrapeId.HasValue
                   && EvaluateReadiness(pointers).ReadyForPinning;
        }
    }

    public PublicationPointerState GetPointers() =>
        _metaDb.GetPublicationPointerState();

    public PublicationPointerState GetPointers(
        int commandTimeoutSeconds)
    {
        if (commandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds));
        }
        return _metaDb.GetPublicationPointerState(
            commandTimeoutSeconds);
    }

    public TimeSpan GetLeaseLifetime(HttpRequest request) =>
        PublicationReadLeasePolicy.Resolve(
            request,
            _commitOptions);

    public PublicationReadinessResult EvaluateReadiness(
        PublicationPointerState pointers)
    {
        if (!pointers.CurrentPublicationId.HasValue
            || !pointers.PublishedScrapeId.HasValue)
        {
            throw new InvalidOperationException(
                "Publication readiness requires current publication and scrape pointers.");
        }

        return _readinessEvaluator.Evaluate(
            pointers.CurrentPublicationId.Value,
            pointers.PublishedScrapeId.Value);
    }

    public PublishedScopeSourceReadinessResult
        EvaluatePublishedScopeSourceReadiness(
            PublicationPointerState pointers,
            bool forceRefresh = false) =>
        _publishedScopeSourceReadiness.Evaluate(
            pointers,
            forceRefresh);

    public PublishedScopeSourceReadinessResult
        EvaluateCurrentPublishedScopeSourceReadiness(
            bool forceRefresh = false) =>
        _publishedScopeSourceReadiness
            .EvaluateCurrent(forceRefresh);

    public PublicationBootstrapResponse BuildBootstrapResponse(
        PublicationPointerState pointers)
    {
        var readiness = EvaluateReadiness(pointers);
        return new PublicationBootstrapResponse(
            readiness.ContractVersion,
            readiness.PublicationId,
            pointers.PreviousPublicationId,
            readiness.PublishedScrapeId,
            pointers.PublishedAtUtc,
            readiness.ReadyForPinning,
            PinningConfigured && readiness.ReadyForPinning,
            readiness.UnreadySurfaces);
    }

    public async Task<PublicationReadLease> AcquireAsync(
        CancellationToken ct) =>
        await AcquireAsync(
            TimeSpan.FromSeconds(
                Math.Max(
                    1,
                    _commitOptions.DefaultReadLeaseSeconds)),
            ct);

    internal Task<PublicationReadLease>
        AcquireWebSocketAdmissionAsync(
            CancellationToken ct) =>
        AcquireAsync(
            TimeSpan.FromSeconds(
                AdmissionLeaseLifetimeSeconds),
            ct);

    public async Task<PublicationReadLease> AcquireAsync(
        TimeSpan maxLifetime,
        CancellationToken ct)
    {
        if (maxLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxLifetime));

        var conn = await _dataSource.OpenConnectionAsync(ct);
        NpgsqlTransaction? tx = null;
        try
        {
            tx = await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                ct);
            await using (var leaseTimeout = conn.CreateCommand())
            {
                var lifetimeMilliseconds = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        maxLifetime.TotalMilliseconds));
                leaseTimeout.Transaction = tx;
                leaseTimeout.CommandText = """
                    SELECT set_config(
                        'idle_in_transaction_session_timeout',
                        @leaseTimeout,
                        true);
                    SELECT set_config(
                        'transaction_timeout',
                        @leaseTimeout,
                        true);
                    SELECT set_config(
                        'statement_timeout',
                        @statementTimeout,
                        true);
                    """;
                leaseTimeout.Parameters.AddWithValue(
                    "leaseTimeout",
                    $"{lifetimeMilliseconds}ms");
                leaseTimeout.Parameters.AddWithValue(
                    "statementTimeout",
                    $"{Math.Min(lifetimeMilliseconds, 5_000)}ms");
                await leaseTimeout.ExecuteNonQueryAsync(ct);
            }
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
            if (tx is not null)
                await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }

    public void Invalidate()
    {
        _publishedScopeSourceReadiness.Invalidate();
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

    internal async Task VerifyHeldAsync(
        CancellationToken ct)
    {
        await using var command =
            _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandTimeout =
            PublicationReadContextService
                .AdmissionCommandTimeoutSeconds;
        command.CommandText = "SELECT 1";
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not 1)
        {
            throw new InvalidOperationException(
                "Publication read lease verification returned an unexpected result.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transaction.DisposeAsync();
        }
        catch (NpgsqlException)
        {
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}

internal static class PublicationReadLeasePolicy
{
    internal static TimeSpan Resolve(
        HttpRequest request,
        PublicationCommitOptions options)
    {
        var seconds = request.Path.Value?.EndsWith(
                "/export",
                StringComparison.OrdinalIgnoreCase) == true
            ? options.ExportReadLeaseSeconds
            : options.DefaultReadLeaseSeconds;
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
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

internal static class MaxScoreMaintenanceReadLeasePolicy
{
    internal static bool DeferToCacheOrRouteGate(
        HttpContext context,
        PublicReadFreezeState state)
        => state.MaxScoreMaintenance
           && state.RequiresCachedReads
           && context.GetEndpoint()?.Metadata
               .GetMetadata<PublicationBound>() is not null
           && PublicReadGateMiddleware
               .RequiresMaxScoreMaintenanceData(
                   context.Request);
}

/// <summary>
/// Holds the shared publication lock for publication-bound reads during a
/// frozen transition even while full request pinning remains disabled.
/// </summary>
public sealed class PublicationBoundaryReadLeaseMiddleware
{
    private readonly RequestDelegate _next;

    public PublicationBoundaryReadLeaseMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        PublicationReadContextService publicationService,
        PublicReadGateService publicReadGate,
        FSTService.Scraping.IPathDataStore pathStore)
    {
        if (context.GetPublicationReadContext() is not null
            || context.GetEndpoint()?.Metadata
                .GetMetadata<PublicationBound>() is null)
        {
            await _next(context);
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            var pointers = publicationService.GetPointers(
                PublicationReadContextService
                    .AdmissionCommandTimeoutSeconds);
            var readiness =
                publicationService
                    .EvaluatePublishedScopeSourceReadiness(
                        pointers);
            if (!readiness.Ready)
            {
                await PublishedScopeSourceReadinessHttpResults
                    .Unavailable(
                        context,
                        readiness);
                return;
            }

            if (pointers.CurrentPublicationId.HasValue
                && pointers.PublishedScrapeId.HasValue)
            {
                SetValidatedPublicationContext(
                    context,
                    pointers.CurrentPublicationId.Value,
                    pointers.PublishedScrapeId.Value,
                    pointers.PublishedAtUtc);
            }

            await _next(context);
            return;
        }

        var gateState = publicReadGate.GetState();
        if (gateState.PublicationCommitPending)
        {
            await PublicationCommitHttpResults.Unavailable(context);
            return;
        }

        if (MaxScoreMaintenanceReadLeasePolicy
            .DeferToCacheOrRouteGate(
                context,
                gateState))
        {
            await InvokeWithPathArtifactFailureHandlingAsync(
                context,
                publicationId: null);
            return;
        }

        await using var lease = await publicationService.AcquireAsync(
            publicationService.GetLeaseLifetime(context.Request),
            context.RequestAborted);
        if (!lease.Pointers.CurrentPublicationId.HasValue
            || !lease.Pointers.PublishedScrapeId.HasValue)
        {
            var missingSourceReadiness =
                publicationService
                    .EvaluatePublishedScopeSourceReadiness(
                        lease.Pointers);
            if (!missingSourceReadiness.Ready)
            {
                await PublishedScopeSourceReadinessHttpResults
                    .Unavailable(
                        context,
                        missingSourceReadiness);
                return;
            }

            if (publicReadGate.FailedCandidateIsolationActive)
            {
                context.Response.Headers.CacheControl = "no-store";
                await Results.Problem(
                        title: "Published data unavailable",
                        detail: "No current publication generation is available.",
                        statusCode: StatusCodes.Status503ServiceUnavailable)
                    .ExecuteAsync(context);
                return;
            }

            await InvokeWithPathArtifactFailureHandlingAsync(
                context,
                publicationId: null);
            return;
        }

        var sourceReadiness =
            publicationService
                .EvaluatePublishedScopeSourceReadiness(
                    lease.Pointers);
        if (!sourceReadiness.Ready)
        {
            await PublishedScopeSourceReadinessHttpResults
                .Unavailable(
                    context,
                    sourceReadiness);
            return;
        }

        var publicationId = lease.Pointers.CurrentPublicationId.Value;
        SetValidatedPublicationContext(
            context,
            publicationId,
            lease.Pointers.PublishedScrapeId.Value,
            lease.Pointers.PublishedAtUtc);

        try
        {
            using var pathScope =
                pathStore.BeginPublicationRead(publicationId);
            await _next(context);
        }
        catch (PublicationPathArtifactsUnavailableException)
            when (!context.Response.HasStarted)
        {
            await PublicationPathArtifactHttpResults.Unavailable(
                context,
                publicationId);
        }
    }

    private async Task InvokeWithPathArtifactFailureHandlingAsync(
        HttpContext context,
        long? publicationId)
    {
        try
        {
            await _next(context);
        }
        catch (PublicationPathArtifactsUnavailableException ex)
            when (!context.Response.HasStarted)
        {
            await PublicationPathArtifactHttpResults.Unavailable(
                context,
                publicationId ?? ex.PublicationId);
        }
    }

    private static void SetValidatedPublicationContext(
        HttpContext context,
        long publicationId,
        long publishedScrapeId,
        DateTime? publishedAtUtc)
    {
        context.Response.Headers[
            PublicationReadContextMiddleware.PublicationHeader] =
            publicationId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.Append(
            "Vary",
            PublicationReadContextMiddleware.PublicationHeader);
        context.SetPublicationReadContext(
            new PublicationReadContext(
                publicationId,
                publishedScrapeId,
                publishedAtUtc));
    }
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
        PublicationReadContextService publicationService,
        PublicReadGateService publicReadGate,
        FSTService.Scraping.IPathDataStore pathStore)
    {
        if (!publicationService.PinningConfigured)
        {
            await InvokeWithPathArtifactFailureHandlingAsync(
                context,
                publicationId: null);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<PublicationBound>() is null)
        {
            await _next(context);
            return;
        }

        var gateState = publicReadGate.GetState();
        if (gateState.PublicationCommitPending)
        {
            await PublicationCommitHttpResults.Unavailable(context);
            return;
        }

        if (MaxScoreMaintenanceReadLeasePolicy
            .DeferToCacheOrRouteGate(
                context,
                gateState))
        {
            await InvokeWithPathArtifactFailureHandlingAsync(
                context,
                publicationId: null);
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
            await publicationService.AcquireAsync(
                publicationService.GetLeaseLifetime(context.Request),
                context.RequestAborted);
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

            var sourceReadiness =
                publicationService
                    .EvaluatePublishedScopeSourceReadiness(
                        pointers);
            if (!sourceReadiness.Ready)
            {
                await PublishedScopeSourceReadinessHttpResults
                    .Unavailable(
                        context,
                        sourceReadiness);
                return;
            }

            var readiness =
                publicationService.EvaluateReadiness(pointers);
            if (!readiness.ReadyForPinning)
            {
                context.Response.Headers.CacheControl = "no-store";
                await PublicationReadinessHttpResults
                    .Unavailable(readiness)
                    .ExecuteAsync(context);
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

            try
            {
                using var pathScope =
                    pathStore.BeginPublicationRead(
                        currentPublicationId);
                await _next(context);
            }
            catch (PublicationPathArtifactsUnavailableException)
                when (!context.Response.HasStarted)
            {
                await PublicationPathArtifactHttpResults.Unavailable(
                    context,
                    currentPublicationId);
            }
        }
        finally
        {
            if (publicationLease is not null)
                await publicationLease.DisposeAsync();
        }
    }

    private async Task InvokeWithPathArtifactFailureHandlingAsync(
        HttpContext context,
        long? publicationId)
    {
        try
        {
            await _next(context);
        }
        catch (PublicationPathArtifactsUnavailableException ex)
            when (!context.Response.HasStarted)
        {
            await PublicationPathArtifactHttpResults.Unavailable(
                context,
                publicationId ?? ex.PublicationId);
        }
    }

    internal static bool TryReadRequestedPublicationId(
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

internal static class PublicationCommitHttpResults
{
    internal static async Task Unavailable(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Retry-After"] = "1";
        await Results.Problem(
                title: "Publication commit in progress",
                detail:
                    "The current publication is being atomically advanced. Retry this uncached request.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            .ExecuteAsync(context);
    }
}

internal static class PublicationPathArtifactHttpResults
{
    internal static async Task Unavailable(
        HttpContext context,
        long publicationId)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Retry-After"] = "30";
        await Results.Problem(
                title: "Published path data unavailable",
                detail:
                    $"Publication {publicationId} does not have a complete, verified path artifact snapshot. Retry after publication recovery completes.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            .ExecuteAsync(context);
    }
}

internal static class PublishedScopeSourceReadinessHttpResults
{
    internal static async Task Unavailable(
        HttpContext context,
        PublishedScopeSourceReadinessResult readiness)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Retry-After"] = "1";
        await Results.Problem(
                title: "Published leaderboard data unavailable",
                detail:
                    $"Publication {readiness.PublicationId?.ToString() ?? "unknown"} does not have an exact authoritative scope-source binding. Retry after publication recovery completes.",
                statusCode:
                    StatusCodes.Status503ServiceUnavailable)
            .ExecuteAsync(context);
    }
}
