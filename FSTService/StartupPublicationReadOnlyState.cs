using FSTService.Persistence;
using Npgsql;

namespace FSTService;

/// <summary>
/// Immutable startup selection, made before runtime pools or hosted writers exist.
/// A guarded new process is the only way to leave read-only serving mode.
/// </summary>
public sealed class StartupPublicationReadOnlyState : IAsyncDisposable
{
    private int _ready;
    private bool _sourceCreated;
    private bool _selected;
    private bool _runtimeSelection;
    private NpgsqlConnection? _selectionConnection;
    private NpgsqlTransaction? _selectionTransaction;
    private readonly List<PublicationPathArtifactInitializationFailure> _warnings = [];

    public bool IsLatched { get; private set; }
    public bool MutationsReady => Volatile.Read(ref _ready) != 0 && !IsLatched;
    public bool ReadServingReady => Volatile.Read(ref _ready) != 0;
    public string? Reason { get; private set; }
    public bool ConnectionsReadOnlyConfigured { get; private set; }
    public IReadOnlyList<PublicationPathArtifactInitializationFailure> Failures { get; private set; } = [];
    public IReadOnlyList<PublicationPathArtifactInitializationFailure> Warnings => _warnings;

    internal static StartupPublicationReadOnlyState ForInitializedDatabase(bool readOnly = false)
    {
        var state = new StartupPublicationReadOnlyState { _selected = true };
        if (readOnly)
            state.Latch("configured_read_only", []);
        return state;
    }

    internal static async Task<StartupPublicationReadOnlyState> PrepareRuntimeAsync(
        string connectionString,
        ScraperOptions options,
        ILogger log,
        CancellationToken ct = default)
    {
        var state = new StartupPublicationReadOnlyState { _runtimeSelection = true };
        if (options.RolloutReadOnlyStartup)
        {
            state.Latch("configured_read_only", []);
            state._selected = true;
            return state;
        }

        var bootstrap = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Timeout = 5,
            CommandTimeout = 20,
            ApplicationName = "fst-startup-schema-selection",
        };
        try
        {
            await using var source = NpgsqlDataSource.Create(bootstrap.ConnectionString);
            if (!options.SkipsStartupSchemaInitialization)
            {
                await DatabaseInitializer.EnsureSchemaAsync(source, ct, state.RecordWarning);
            }
            state._selectionConnection = new NpgsqlConnection(bootstrap.ConnectionString);
            await state._selectionConnection.OpenAsync(ct);
            state._selectionTransaction = await state._selectionConnection.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, ct);
            await using (var fence = state._selectionConnection.CreateCommand())
            {
                fence.Transaction = state._selectionTransaction;
                fence.CommandTimeout = 5;
                fence.CommandText = """
                    SET LOCAL lock_timeout='2s';
                    SET LOCAL statement_timeout='5s';
                    SET LOCAL transaction_timeout='30s';
                    SET LOCAL idle_in_transaction_session_timeout='10s';
                    LOCK TABLE ONLY public.scrape_publication_state,
                        ONLY public.publication_generations,
                        ONLY public.publication_surface_bindings,
                        ONLY public.publication_path_artifacts,
                        ONLY public.publication_song_catalog IN SHARE MODE;
                    """;
                await fence.ExecuteNonQueryAsync(ct);
            }
            var warnings = await PublicationPathArtifactReleaseGate.ValidateInitializedActiveBindingsAsync(
                state._selectionConnection, state._selectionTransaction, ct, requireReadyMutationPointers: true);
            foreach (var warning in warnings)
                state.RecordWarning(warning);
        }
        catch (PublicationPathArtifactInitializationException exception)
        {
            state.Latch("publication_path_artifact_validation_failed", exception.Failures);
        }
        catch (NpgsqlException exception)
        {
            state.Latch(exception is PostgresException postgres
                ? "startup_database_refused_" + postgres.SqlState
                : "startup_database_unavailable", []);
        }
        catch
        {
            await state.ReleaseSelectionAsync();
            throw;
        }
        if (state.IsLatched)
            await state.ReleaseSelectionAsync();
        state._selected = true;
        foreach (var warning in state.Warnings)
            log.LogWarning("Previous publication path binding warning {DiagnosticCode}: publication={PublicationId}, code={Code}.",
                "previous_path_binding_invalid", warning.PublicationId, warning.Code);
        if (state.IsLatched)
            log.LogError("Startup selected read-only serving before pool construction: reason={Reason}; diagnostics={Diagnostics}.",
                state.Reason, string.Join(",", state.Failures.Select(static item => $"{item.PublicationId}:{item.Code}")));
        return state;
    }

    private void RecordWarning(PublicationPathArtifactInitializationFailure warning)
    {
        if (!_warnings.Contains(warning))
            _warnings.Add(warning);
    }

    private void Latch(string reason, IReadOnlyList<PublicationPathArtifactInitializationFailure> failures)
    {
        if (IsLatched)
            return;
        if (_sourceCreated)
            throw new InvalidOperationException("Read-only selection must precede runtime pool construction.");
        Reason = reason;
        Failures = failures.ToArray();
        IsLatched = true;
    }

    public void MarkReady()
    {
        if (!_selected)
            throw new InvalidOperationException("Startup database selection has not completed.");
        Volatile.Write(ref _ready, 1);
    }

    public string ConfigureConnectionString(string connectionString)
    {
        if (!_selected)
            throw new InvalidOperationException("Startup database selection has not completed.");
        if (!IsLatched)
            return connectionString;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Options = $"{builder.Options} -c default_transaction_read_only=on".Trim();
        return builder.ConnectionString;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString, StartupPublicationReadOnlyState state)
    {
        if (state._runtimeSelection && !state._sourceCreated && !state.IsLatched)
        {
            try
            {
                var connection = state._selectionConnection
                    ?? throw new InvalidOperationException("Startup selection fence is absent.");
                var transaction = state._selectionTransaction
                    ?? throw new InvalidOperationException("Startup selection transaction is absent.");
                using var verify = connection.CreateCommand();
                verify.Transaction = transaction;
                verify.CommandTimeout = 5;
                verify.CommandText = "SELECT 1";
                _ = verify.ExecuteScalar();
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                state.Latch("startup_selection_fence_lost", []);
                state.ReleaseSelectionAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        var source = NpgsqlDataSource.Create(state.ConfigureConnectionString(connectionString));
        state._sourceCreated = true;
        state.ConnectionsReadOnlyConfigured = state.IsLatched;
        try
        {
            state.ReleaseSelectionAsync().AsTask().GetAwaiter().GetResult();
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private async ValueTask ReleaseSelectionAsync()
    {
        var transaction = Interlocked.Exchange(ref _selectionTransaction, null);
        var connection = Interlocked.Exchange(ref _selectionConnection, null);
        try
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => ReleaseSelectionAsync();
}

public sealed record StartupPublicationReadOnlyStatus(
    string State,
    bool ReadServingReady,
    bool MutationReady,
    string? Reason,
    IReadOnlyList<PublicationPathArtifactInitializationFailure> Diagnostics,
    IReadOnlyList<PublicationPathArtifactInitializationFailure> Warnings);

internal sealed class PublicationReadOnlySuppressedHostedService(
    string serviceName,
    StartupPublicationReadOnlyState state,
    ILogger log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        log.LogWarning("Hosted mutation/background service {Service} suppressed by sticky read-only startup ({Reason}).",
            serviceName, state.Reason);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class PublicationStartupHostedServiceRegistration
{
    internal static void AddPublicationStartupGatedHostedService<T>(this IServiceCollection services)
        where T : class, IHostedService =>
        services.AddSingleton<IHostedService>(provider =>
        {
            var state = provider.GetRequiredService<StartupPublicationReadOnlyState>();
            if (!state.IsLatched)
                _ = provider.GetRequiredService<NpgsqlDataSource>();
            return state.IsLatched
                ? new PublicationReadOnlySuppressedHostedService(typeof(T).Name, state,
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(T).FullName!))
                : ActivatorUtilities.GetServiceOrCreateInstance<T>(provider);
        });
}
