using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace FstStoredRankRollout;

public sealed class CommandArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public static CommandArguments Parse(IEnumerable<string> args)
    {
        var parsed = new CommandArguments();
        var tokens = args.ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument: {token}");
            var key = token[2..];
            if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parsed._values[key] = tokens[++index];
            }
            else
            {
                parsed._flags.Add(key);
            }
        }

        return parsed;
    }

    public string Require(string key) =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{key} <value>.");

    public string Get(string key, string fallback) =>
        _values.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : fallback;

    public long GetLong(string key, long fallback) =>
        _values.TryGetValue(key, out var value)
            ? long.Parse(value, CultureInfo.InvariantCulture)
            : fallback;

    public bool HasFlag(string key) => _flags.Contains(key);
}

public static class EvidencePaths
{
    public const string RequiredEvidenceBase =
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence";

    public static string ResolveOutput(string path)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("FST_STORED_RANK_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                "FST_STORED_RANK_EVIDENCE_ROOT must name the configured 4 TB evidence directory.");
        }

        if (!Directory.Exists(configuredRoot))
            throw new DirectoryNotFoundException(configuredRoot);
        var requiredBase = ResolvePhysicalPath(RequiredEvidenceBase);
        var root = ResolvePhysicalPath(configuredRoot);
        EnsureUnder(root, requiredBase, "Configured evidence root");
        var fullPath = ResolvePhysicalPath(path);
        EnsureUnder(fullPath, root, "Output path");
        return fullPath;
    }

    public static string ResolveInput(string path)
    {
        var fullPath = ResolveOutput(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Evidence input file was not found.", fullPath);
        return fullPath;
    }

    public static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException($"Output has no parent directory: {path}");
        Directory.CreateDirectory(parent);
    }

    private static void EnsureUnder(string path, string root, string label)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.Ordinal))
            return;
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} must remain under {normalizedRoot}: {normalizedPath}");
    }

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
                       ?? throw new InvalidOperationException($"Path has no root: {path}");
        var current = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            var target = info?.ResolveLinkTarget(returnFinalTarget: true);
            current = target?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }
}

public static class JsonFiles
{
    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, RolloutJson.Options, cancellationToken)
               ?? throw new InvalidDataException($"JSON file did not contain {typeof(T).Name}: {path}");
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        EvidencePaths.EnsureParentDirectory(path);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, RolloutJson.Options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task WriteAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        EvidencePaths.EnsureParentDirectory(path);
        var temporaryPath =
            $"{path}.partial-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    RolloutJson.Options,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public static class ReadOnlyPostgres
{
    public const string DefaultConnectionEnvironment = "FST_STORED_RANK_CONNECTION_STRING";
    public const string VisibilityProbeConnectionEnvironment =
        "FST_STORED_RANK_VISIBILITY_PROBE_CONNECTION_STRING";

    public static NpgsqlDataSource CreateDataSource(
        string? connectionEnvironment = null,
        int statementTimeoutSeconds = 30,
        int maxPoolSize = 16)
    {
        var environmentName = string.IsNullOrWhiteSpace(connectionEnvironment)
            ? DefaultConnectionEnvironment
            : connectionEnvironment;
        var raw = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"{environmentName} is required.");

        var builder = new NpgsqlConnectionStringBuilder(raw)
        {
            ApplicationName = "fst-stored-rank-rollout",
            Timeout = Math.Min(15, Math.Max(1, statementTimeoutSeconds)),
            CommandTimeout = statementTimeoutSeconds,
            MinPoolSize = 0,
            MaxPoolSize = maxPoolSize,
        };
        if ((builder.Host ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length != 1
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException(
                $"{environmentName} must specify one PostgreSQL host, database, and username.");
        }
        var safeguards =
            $"-c statement_timeout={statementTimeoutSeconds * 1000} " +
            "-c lock_timeout=2000 " +
            "-c idle_in_transaction_session_timeout=60000";
        builder.Options = string.IsNullOrWhiteSpace(builder.Options)
            ? safeguards
            : $"{builder.Options} {safeguards}";
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public static NpgsqlDataSource CreateVisibilityProbeDataSource(
        int statementTimeoutSeconds = 15)
    {
        var raw = Environment.GetEnvironmentVariable(
            VisibilityProbeConnectionEnvironment);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                $"{VisibilityProbeConnectionEnvironment} is required.");
        }
        var builder = new NpgsqlConnectionStringBuilder(raw)
        {
            ApplicationName = "fst-stored-rank-visibility-probe",
            Timeout = Math.Min(15, Math.Max(1, statementTimeoutSeconds)),
            CommandTimeout = statementTimeoutSeconds,
            MinPoolSize = 0,
            MaxPoolSize = 2,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public static async Task<bool> ValidateSelectTempOnlyRoleAsync(
        NpgsqlDataSource dataSource,
        NpgsqlDataSource visibilityProbeDataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                role.rolsuper,
                EXISTS (
                    SELECT 1
                    FROM pg_namespace schema
                    WHERE schema.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND schema.nspname NOT LIKE 'pg_toast%'
                      AND schema.nspname NOT LIKE 'pg_temp_%'
                      AND has_schema_privilege(current_user, schema.oid, 'CREATE')
                ),
                has_database_privilege(current_user, current_database(), 'CREATE'),
                has_database_privilege(current_user, current_database(), 'TEMP'),
                EXISTS (
                    SELECT 1
                    FROM pg_class relation
                    JOIN pg_namespace schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND schema.nspname NOT LIKE 'pg_toast%'
                      AND schema.nspname NOT LIKE 'pg_temp_%'
                      AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                      AND (
                          has_table_privilege(
                              current_user,
                              format('%I.%I', schema.nspname, relation.relname),
                              'INSERT')
                          OR has_table_privilege(
                              current_user,
                              format('%I.%I', schema.nspname, relation.relname),
                              'UPDATE')
                          OR has_table_privilege(
                              current_user,
                              format('%I.%I', schema.nspname, relation.relname),
                              'DELETE')
                          OR has_table_privilege(
                              current_user,
                              format('%I.%I', schema.nspname, relation.relname),
                              'TRUNCATE')
                      )
                ),
                EXISTS (
                    SELECT 1
                    FROM pg_class sequence
                    JOIN pg_namespace schema ON schema.oid = sequence.relnamespace
                    WHERE sequence.relkind = 'S'
                      AND schema.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND schema.nspname NOT LIKE 'pg_toast%'
                      AND schema.nspname NOT LIKE 'pg_temp_%'
                      AND has_sequence_privilege(
                          current_user,
                          format('%I.%I', schema.nspname, sequence.relname),
                          'UPDATE')
                ),
                pg_has_role(current_user, 'pg_monitor', 'USAGE'),
                pg_has_role(current_user, 'pg_read_all_stats', 'USAGE'),
                current_user::TEXT
            FROM pg_roles role
            WHERE role.rolname = current_user
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Unable to inspect rollout database role.");
        var failures = new List<string>();
        if (reader.GetBoolean(0)) failures.Add("role-is-superuser");
        if (reader.GetBoolean(1)) failures.Add("role-can-create-in-durable-schema");
        if (reader.GetBoolean(2)) failures.Add("role-can-create-database-objects");
        if (!reader.GetBoolean(3)) failures.Add("role-lacks-temp-privilege");
        if (reader.GetBoolean(4)) failures.Add("role-has-durable-table-write-privileges");
        if (reader.GetBoolean(5)) failures.Add("role-has-durable-sequence-update-privileges");
        if (!reader.GetBoolean(6) && !reader.GetBoolean(7))
            failures.Add("role-lacks-pg-monitor-or-pg-read-all-stats");
        var monitorUser = reader.GetString(8);
        await reader.DisposeAsync();
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Rollout connection must be SELECT+TEMP plus monitoring only: " +
                string.Join(", ", failures));
        }
        await ValidateCrossRoleVisibilityAsync(
            connection,
            monitorUser,
            visibilityProbeDataSource,
            cancellationToken);
        return true;
    }

    private static async Task ValidateCrossRoleVisibilityAsync(
        NpgsqlConnection monitorConnection,
        string monitorUser,
        NpgsqlDataSource visibilityProbeDataSource,
        CancellationToken cancellationToken)
    {
        var marker = $"fst-visibility-{Guid.NewGuid():N}";
        await using var probeConnection =
            await visibilityProbeDataSource.OpenConnectionAsync(cancellationToken);
        await using var probeCommand = probeConnection.CreateCommand();
        probeCommand.CommandText =
            $"SELECT pg_backend_pid(), current_user::TEXT /* {marker} */";
        await using var probeReader =
            await probeCommand.ExecuteReaderAsync(cancellationToken);
        if (!await probeReader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Unable to start monitoring visibility probe.");
        var probePid = probeReader.GetInt32(0);
        var probeUser = probeReader.GetString(1);
        await probeReader.DisposeAsync();
        if (string.Equals(probeUser, monitorUser, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Monitoring visibility probe must use a distinct PostgreSQL role.");
        }

        await using var visibilityCommand = monitorConnection.CreateCommand();
        visibilityCommand.CommandText = """
            SELECT usename, application_name, query
            FROM pg_stat_activity
            WHERE pid = @pid
              AND datname = current_database()
            """;
        visibilityCommand.Parameters.AddWithValue("pid", probePid);
        await using var visibilityReader =
            await visibilityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await visibilityReader.ReadAsync(cancellationToken)
            || visibilityReader.IsDBNull(0)
            || visibilityReader.IsDBNull(1)
            || visibilityReader.IsDBNull(2)
            || !string.Equals(
                visibilityReader.GetString(0),
                probeUser,
                StringComparison.Ordinal)
            || !string.Equals(
                visibilityReader.GetString(1),
                "fst-stored-rank-visibility-probe",
                StringComparison.Ordinal)
            || !visibilityReader.GetString(2).Contains(
                marker,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Rollout role cannot inspect controlled cross-role pg_stat_activity fields.");
        }
    }

    public static async Task<NpgsqlTransaction> BeginRepeatableReadOnlyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET TRANSACTION READ ONLY";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public static async Task<DatabaseIdentityEvidence> ReadDatabaseIdentityAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(
            cancellationToken);
        return await ReadDatabaseIdentityAsync(
            connection,
            transaction: null,
            cancellationToken);
    }

    private static async Task<DatabaseIdentityEvidence> ReadDatabaseIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current_database(),
                   control.system_identifier::TEXT,
                   COALESCE(host(inet_server_addr()), ''),
                   COALESCE(inet_server_port(), current_setting('port')::INT),
                   COALESCE((
                       SELECT setting
                       FROM pg_settings
                       WHERE name = 'unix_socket_directories'
                   ), '')
            FROM pg_control_system() control
            """;
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Unable to read PostgreSQL identity.");
        return new DatabaseIdentityEvidence
        {
            DatabaseName = reader.GetString(0),
            SystemIdentifier = reader.GetString(1),
            ServerAddress = reader.GetString(2),
            ServerPort = reader.GetInt32(3),
            UnixSocketDirectories = reader.GetString(4),
        };
    }

    public static DatabaseAttestationReport CompareDatabaseIdentity(
        DatabaseIdentityEvidence expected,
        DatabaseIdentityEvidence observed)
    {
        var failures = new List<string>();
        if (!string.Equals(
                expected.DatabaseName,
                observed.DatabaseName,
                StringComparison.Ordinal))
        {
            failures.Add("database-name");
        }
        if (!string.Equals(
                expected.SystemIdentifier,
                observed.SystemIdentifier,
                StringComparison.Ordinal))
        {
            failures.Add("system-identifier");
        }
        if (!string.Equals(
                expected.ServerAddress,
                observed.ServerAddress,
                StringComparison.Ordinal))
        {
            failures.Add("server-address");
        }
        if (expected.ServerPort != observed.ServerPort)
            failures.Add("server-port");
        if (!string.Equals(
                expected.UnixSocketDirectories,
                observed.UnixSocketDirectories,
                StringComparison.Ordinal))
        {
            failures.Add("unix-socket-directories");
        }
        return new DatabaseAttestationReport
        {
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Expected = expected,
            Observed = observed,
            Passed = failures.Count == 0,
            Failures = failures,
        };
    }

    public static DatabaseAttestationReport CompareDatabaseIdentity(
        RolloutManifest manifest,
        DatabaseIdentityEvidence observed)
    {
        var identity = CompareDatabaseIdentity(manifest.DatabaseIdentity, observed);
        var failures = identity.Failures.ToList();
        if (string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.DatabaseName)
            || string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.SystemIdentifier)
            || string.IsNullOrWhiteSpace(manifest.DatabaseIdentity.ServerAddress)
            || manifest.DatabaseIdentity.ServerPort <= 0
            || string.IsNullOrWhiteSpace(
                manifest.DatabaseIdentity.UnixSocketDirectories))
        {
            failures.Add("database-identity-incomplete");
        }
        if (string.IsNullOrWhiteSpace(manifest.ServiceDatabaseTarget.Host)
            || manifest.ServiceDatabaseTarget.Port <= 0
            || string.IsNullOrWhiteSpace(
                manifest.ServiceDatabaseTarget.Database)
            || string.IsNullOrWhiteSpace(
                manifest.ServiceDatabaseTarget.Username))
        {
            failures.Add("service-database-target-incomplete");
        }
        if (!string.Equals(
                manifest.DatabaseIdentity.DatabaseName,
                manifest.ServiceDatabaseTarget.Database,
                StringComparison.Ordinal))
        {
            failures.Add("service-database-name");
        }
        if (manifest.DatabaseIdentity.ServerPort != manifest.ServiceDatabaseTarget.Port)
            failures.Add("service-database-port");
        if (!manifest.PostgresNetworkAliases.Contains(
                manifest.ServiceDatabaseTarget.Host,
                StringComparer.OrdinalIgnoreCase))
        {
            failures.Add("service-host-network-alias");
        }
        if (!manifest.PostgresServerAddresses.Contains(
                manifest.DatabaseIdentity.ServerAddress,
                StringComparer.OrdinalIgnoreCase))
        {
            failures.Add("database-address-container-binding");
        }
        if (string.IsNullOrWhiteSpace(manifest.PostgresContainerId))
            failures.Add("postgres-container-id");
        if (string.IsNullOrWhiteSpace(manifest.PostgresImageReference)
            || !RolloutImagePin.IsValidImageId(manifest.PostgresImageId))
        {
            failures.Add("postgres-image-pin");
        }
        if (manifest.PostgresNetworkNames.Count == 0
            || manifest.PostgresNetworkNames.Any(string.IsNullOrWhiteSpace))
            failures.Add("postgres-network-names");
        if (manifest.PostgresNetworkAliases.Count == 0
            || manifest.PostgresNetworkAliases.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("postgres-network-aliases");
        }
        if (manifest.PostgresServerAddresses.Count == 0
            || manifest.PostgresServerAddresses.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("postgres-server-addresses");
        }
        if (manifest.PostgresNetworkBindings.Count != 1)
        {
            failures.Add("exclusive-service-network-binding-count");
        }
        else
        {
            var binding = manifest.PostgresNetworkBindings[0];
            if (string.IsNullOrWhiteSpace(binding.NetworkName)
                || string.IsNullOrWhiteSpace(binding.NetworkId)
                || string.IsNullOrWhiteSpace(binding.ServiceAlias)
                || string.IsNullOrWhiteSpace(binding.ExclusiveOwnerContainerId)
                || binding.ServerAddresses.Count == 0
                || binding.ServerAddresses.Any(string.IsNullOrWhiteSpace))
            {
                failures.Add("exclusive-service-network-binding-incomplete");
            }
            if (!string.Equals(
                    binding.ServiceAlias,
                    manifest.ServiceDatabaseTarget.Host,
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("exclusive-service-network-alias");
            }
            if (!string.Equals(
                    binding.ExclusiveOwnerContainerId,
                    manifest.PostgresContainerId,
                    StringComparison.Ordinal))
            {
                failures.Add("exclusive-service-network-owner");
            }
            if (manifest.PostgresNetworkNames.Count != 1
                || !string.Equals(
                    binding.NetworkName,
                    manifest.PostgresNetworkNames[0],
                    StringComparison.Ordinal))
            {
                failures.Add("exclusive-service-network-name");
            }
            if (!binding.ServerAddresses
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        manifest.PostgresServerAddresses.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal))
            {
                failures.Add("exclusive-service-network-addresses");
            }
        }

        return new DatabaseAttestationReport
        {
            ObservedAtUtc = identity.ObservedAtUtc,
            Expected = identity.Expected,
            Observed = identity.Observed,
            ServiceDatabaseTarget = manifest.ServiceDatabaseTarget,
            PostgresContainerId = manifest.PostgresContainerId,
            PostgresImageReference = manifest.PostgresImageReference,
            PostgresImageId = manifest.PostgresImageId,
            PostgresNetworkNames = manifest.PostgresNetworkNames,
            PostgresNetworkAliases = manifest.PostgresNetworkAliases,
            PostgresServerAddresses = manifest.PostgresServerAddresses,
            PostgresNetworkBindings = manifest.PostgresNetworkBindings,
            Passed = failures.Count == 0,
            Failures = failures,
        };
    }

    public static Task<RolloutPreflightReport> ReadPreflightAsync(
        NpgsqlDataSource dataSource,
        long expectedPublishedScrapeId,
        CancellationToken cancellationToken) =>
        ReadPreflightCoreAsync(
            dataSource,
            expectedPublishedScrapeId,
            manifest: null,
            cancellationToken);

    public static Task<RolloutPreflightReport> ReadPreflightAsync(
        NpgsqlDataSource dataSource,
        long expectedPublishedScrapeId,
        RolloutManifest manifest,
        CancellationToken cancellationToken) =>
        ReadPreflightCoreAsync(
            dataSource,
            expectedPublishedScrapeId,
            manifest,
            cancellationToken);

    private static async Task<RolloutPreflightReport> ReadPreflightCoreAsync(
        NpgsqlDataSource dataSource,
        long expectedPublishedScrapeId,
        RolloutManifest? manifest,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginRepeatableReadOnlyAsync(connection, cancellationToken);
        var databaseAttestation = manifest is null
            ? null
            : CompareDatabaseIdentity(
                manifest,
                await ReadDatabaseIdentityAsync(
                    connection,
                    transaction,
                    cancellationToken));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COALESCE(publication.published_scrape_id, 0),
                COALESCE(publication.public_reads_frozen, FALSE),
                COALESCE(scrape.status, 'missing'),
                scrape.completed_at,
                COUNT(source.*) FILTER (WHERE source.is_complete),
                COUNT(source.*) FILTER (WHERE NOT source.is_complete),
                COALESCE((
                    SELECT MAX(active.id)
                    FROM scrape_log active
                    WHERE active.status = 'running'
                      AND active.completed_at IS NULL
                ), 0),
                COALESCE((
                    SELECT (
                        COALESCE(worker.current_operation_json->>'Status',
                                 worker.current_operation_json->>'status') = 'running'
                    )
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                ), FALSE),
                COALESCE((
                    SELECT LOWER(worker.status)
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                ), 'missing'),
                (
                    SELECT worker.last_heartbeat_at
                    FROM service_worker_status worker
                    WHERE worker.worker_key = 'scraper'
                ),
                (
                    SELECT COUNT(*)
                    FROM pg_stat_activity activity
                    WHERE activity.datname = current_database()
                      AND (
                          activity.application_name LIKE 'fstworker-%'
                          OR activity.application_name LIKE 'fst-%worker%'
                          OR activity.application_name = 'fst-path-generation-admission'
                      )
                ),
                (
                    SELECT COALESCE(
                        ARRAY_AGG(
                            DISTINCT activity.application_name
                            ORDER BY activity.application_name),
                        ARRAY[]::TEXT[])
                    FROM pg_stat_activity activity
                    WHERE activity.datname = current_database()
                      AND (
                          activity.application_name LIKE 'fstworker-%'
                          OR activity.application_name LIKE 'fst-%worker%'
                          OR activity.application_name = 'fst-path-generation-admission'
                      )
                ),
                (
                    SELECT COUNT(*)
                    FROM pg_locks locks
                    JOIN pg_stat_activity activity ON activity.pid = locks.pid
                    WHERE locks.locktype = 'advisory'
                      AND locks.granted
                      AND activity.datname = current_database()
                      AND (
                          activity.application_name LIKE 'fstworker-%'
                          OR activity.application_name LIKE 'fst-%worker%'
                          OR activity.application_name = 'fst-path-generation-admission'
                      )
                ),
                (
                    (SELECT COUNT(*) FROM backfill_status
                     WHERE status IN ('pending', 'in_progress', 'deferred'))
                    + (SELECT COUNT(*) FROM history_recon_status
                       WHERE status IN ('pending', 'in_progress'))
                    + (SELECT COUNT(*) FROM rivals_status
                       WHERE status IN ('pending', 'in_progress'))
                    + (SELECT COUNT(*) FROM deep_scrape_queue
                       WHERE status IN ('pending', 'running'))
                ),
                (
                    SELECT COUNT(*)
                    FROM pg_locks locks
                    LEFT JOIN pg_stat_activity activity ON activity.pid = locks.pid
                    WHERE NOT locks.granted
                      AND (
                          locks.database = (
                              SELECT oid
                              FROM pg_database
                              WHERE datname = current_database()
                          )
                          OR activity.datname = current_database()
                      )
                ),
                (
                    SELECT COUNT(*)
                    FROM pg_stat_activity activity
                    WHERE activity.pid <> pg_backend_pid()
                      AND activity.datname = current_database()
                      AND activity.state = 'active'
                      AND activity.query_start < now() - INTERVAL '5 minutes'
                ),
                pg_has_role(current_user, 'pg_monitor', 'USAGE'),
                pg_has_role(current_user, 'pg_read_all_stats', 'USAGE')
            FROM scrape_publication_state publication
            LEFT JOIN scrape_log scrape
              ON scrape.id = publication.published_scrape_id
            LEFT JOIN leaderboard_published_scope_source source
              ON source.published_scrape_id = publication.published_scrape_id
             AND source.scope_kind = 'alltime'
            WHERE publication.id = TRUE
            GROUP BY publication.published_scrape_id,
                     publication.public_reads_frozen,
                     scrape.status,
                     scrape.completed_at
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var failures = new List<string>();
        if (databaseAttestation is { Passed: false })
        {
            failures.AddRange(databaseAttestation.Failures.Select(
                static failure => $"database-identity:{failure}"));
        }
        long publishedScrapeId = 0;
        var frozen = true;
        var status = "missing";
        DateTimeOffset? completedAt = null;
        long completeScopes = 0;
        long incompleteScopes = 0;
        long activeScrapeId = 0;
        var workerHasActiveOperation = false;
        var workerStatus = "missing";
        DateTimeOffset? workerLastHeartbeatAt = null;
        long activeWorkerConnections = 0;
        string[] activeWorkerApplications = [];
        long grantedMutationLeases = 0;
        long activeDurableJobs = 0;
        long ungrantedLocks = 0;
        long longRunningQueries = 0;
        var hasPgMonitor = false;
        var hasPgReadAllStats = false;
        if (await reader.ReadAsync(cancellationToken))
        {
            publishedScrapeId = reader.GetInt64(0);
            frozen = reader.GetBoolean(1);
            status = reader.GetString(2);
            completedAt = reader.IsDBNull(3)
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
            completeScopes = reader.GetInt64(4);
            incompleteScopes = reader.GetInt64(5);
            activeScrapeId = reader.GetInt64(6);
            workerHasActiveOperation = reader.GetBoolean(7);
            workerStatus = reader.GetString(8);
            workerLastHeartbeatAt = reader.IsDBNull(9)
                ? null
                : new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc));
            activeWorkerConnections = reader.GetInt64(10);
            activeWorkerApplications = reader.GetFieldValue<string[]>(11);
            grantedMutationLeases = reader.GetInt64(12);
            activeDurableJobs = reader.GetInt64(13);
            ungrantedLocks = reader.GetInt64(14);
            longRunningQueries = reader.GetInt64(15);
            hasPgMonitor = reader.GetBoolean(16);
            hasPgReadAllStats = reader.GetBoolean(17);
        }
        else
        {
            failures.Add("publication-state-missing");
        }
        await reader.DisposeAsync();

        await using (var optionalJobsProbe = connection.CreateCommand())
        {
            optionalJobsProbe.Transaction = transaction;
            optionalJobsProbe.CommandText =
                "SELECT to_regclass('public.band_rank_history_jobs') IS NOT NULL";
            if (Convert.ToBoolean(await optionalJobsProbe.ExecuteScalarAsync(
                    cancellationToken)))
            {
                await using var optionalJobs = connection.CreateCommand();
                optionalJobs.Transaction = transaction;
                optionalJobs.CommandText =
                    "SELECT COUNT(*) FROM band_rank_history_jobs " +
                    "WHERE status IN ('queued', 'running')";
                activeDurableJobs += Convert.ToInt64(
                    await optionalJobs.ExecuteScalarAsync(cancellationToken));
            }
        }

        if (publishedScrapeId != expectedPublishedScrapeId)
            failures.Add($"published-scrape:{publishedScrapeId}:expected:{expectedPublishedScrapeId}");
        if (frozen)
            failures.Add("public-reads-frozen");
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) || completedAt is null)
            failures.Add($"scrape-not-complete:{status}");
        if (completeScopes == 0)
            failures.Add("complete-published-source-map-empty");
        if (incompleteScopes != 0)
            failures.Add($"incomplete-published-scopes:{incompleteScopes}");
        if (activeScrapeId != 0)
            failures.Add($"active-scrape:{activeScrapeId}");
        if (workerHasActiveOperation)
            failures.Add("worker-active-operation");
        var workerIsStale = workerLastHeartbeatAt.HasValue
            && DateTimeOffset.UtcNow - workerLastHeartbeatAt.Value > TimeSpan.FromSeconds(90);
        if (!string.Equals(workerStatus, "offline", StringComparison.OrdinalIgnoreCase)
            && !workerIsStale)
        {
            failures.Add($"worker-ledger-not-offline-or-stale:{workerStatus}");
        }
        if (activeWorkerConnections != 0)
            failures.Add($"active-worker-connections:{activeWorkerConnections}");
        if (grantedMutationLeases != 0)
            failures.Add($"granted-worker-mutation-leases:{grantedMutationLeases}");
        if (activeDurableJobs != 0)
            failures.Add($"active-durable-worker-jobs:{activeDurableJobs}");
        if (!hasPgMonitor && !hasPgReadAllStats)
            failures.Add("monitoring-privilege-missing");
        if (ungrantedLocks != 0)
            failures.Add($"ungranted-locks:{ungrantedLocks}");
        if (longRunningQueries != 0)
            failures.Add($"long-running-queries:{longRunningQueries}");

        await transaction.CommitAsync(cancellationToken);
        return new RolloutPreflightReport
        {
            ObservedAtUtc = DateTimeOffset.UtcNow,
            ExpectedPublishedScrapeId = expectedPublishedScrapeId,
            PublishedScrapeId = publishedScrapeId,
            PublicReadsFrozen = frozen,
            ScrapeStatus = status,
            ScrapeCompletedAtUtc = completedAt,
            CompletePublishedScopeCount = completeScopes,
            IncompletePublishedScopeCount = incompleteScopes,
            ActiveScrapeId = activeScrapeId,
            WorkerHasActiveOperation = workerHasActiveOperation,
            WorkerStatus = workerStatus,
            WorkerLastHeartbeatAtUtc = workerLastHeartbeatAt,
            ActiveWorkerConnectionCount = activeWorkerConnections,
            ActiveWorkerApplications = activeWorkerApplications,
            GrantedMutationLeaseCount = grantedMutationLeases,
            ActiveDurableJobCount = activeDurableJobs,
            HasPgMonitor = hasPgMonitor,
            HasPgReadAllStats = hasPgReadAllStats,
            MonitoringPrivilegeAttested = hasPgMonitor || hasPgReadAllStats,
            UngrantedLockCount = ungrantedLocks,
            LongRunningQueryCount = longRunningQueries,
            DatabaseAttestation = databaseAttestation,
            Passed = failures.Count == 0,
            Failures = failures,
        };
    }

    public static async Task<DatabaseResourceSnapshot> ReadDatabaseResourcesAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(blks_read, 0),
                   COALESCE(temp_bytes, 0),
                   COALESCE(temp_files, 0),
                   stats_reset
            FROM pg_stat_database
            WHERE datname = current_database()
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new DatabaseResourceSnapshot();
        return new DatabaseResourceSnapshot
        {
            BlocksRead = reader.GetInt64(0),
            TempBytes = reader.GetInt64(1),
            TempFiles = reader.GetInt64(2),
            StatsResetAtUtc = reader.IsDBNull(3)
                ? null
                : new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
        };
    }
}

public static class DockerStats
{
    public const int DefaultCommandTimeoutSeconds = 10;

    public static async Task<ContainerResourceSample?> ReadAsync(
        string container,
        CancellationToken cancellationToken,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(container))
            return null;
        if (commandTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds));

        var intervalStartedAt = DateTimeOffset.UtcNow;
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("stats");
        startInfo.ArgumentList.Add("--no-stream");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{json .}}");
        startInfo.ArgumentList.Add(container);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Unable to start docker stats.");
        using var commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCancellation.CancelAfter(TimeSpan.FromSeconds(commandTimeoutSeconds));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(commandCancellation.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(commandCancellation.Token);
        try
        {
            await process.WaitForExitAsync(commandCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"docker stats exceeded {commandTimeoutSeconds} seconds for {container}.");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker stats failed: {stderr.Trim()}");

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var cpuText = root.GetProperty("CPUPerc").GetString() ?? "0%";
        var memoryText = root.GetProperty("MemUsage").GetString() ?? "0B / 0B";
        var intervalCompletedAt = DateTimeOffset.UtcNow;
        return new ContainerResourceSample
        {
            IntervalStartedAtUtc = intervalStartedAt,
            IntervalCompletedAtUtc = intervalCompletedAt,
            ObservedAtUtc = intervalCompletedAt,
            CpuPercent = double.Parse(
                cpuText.Trim().TrimEnd('%'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture),
            MemoryCurrentBytes = ParseByteSize(memoryText.Split('/', 2)[0].Trim()),
        };
    }

    public static long ParseByteSize(string value)
    {
        var text = value.Trim();
        var split = 0;
        while (split < text.Length
               && (char.IsDigit(text[split]) || text[split] is '.' or ',' or '+' or '-'))
        {
            split++;
        }

        if (split == 0)
            throw new FormatException($"Invalid byte size: {value}");
        var number = double.Parse(
            text[..split].Replace(",", "", StringComparison.Ordinal),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
        var unit = text[split..].Trim();
        var multiplier = unit.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1_000d,
            "KIB" => 1_024d,
            "MB" => 1_000_000d,
            "MIB" => 1_048_576d,
            "GB" => 1_000_000_000d,
            "GIB" => 1_073_741_824d,
            "TB" => 1_000_000_000_000d,
            "TIB" => 1_099_511_627_776d,
            _ => throw new FormatException($"Unsupported byte-size unit: {unit}"),
        };
        return checked((long)Math.Round(number * multiplier, MidpointRounding.AwayFromZero));
    }
}
