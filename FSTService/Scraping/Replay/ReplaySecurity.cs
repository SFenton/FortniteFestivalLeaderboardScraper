using System.Reflection;
using System.Text;
using Npgsql;

namespace FSTService.Scraping.Replay;

public sealed record ReplayRootPolicyOptions(
    string ApprovedRoot,
    bool TestOnly,
    long RollbackReserveBytes,
    string? ExpectedFileSystemDevice = null)
{
    public const long DefaultRollbackReserveBytes = 1024L * 1024 * 1024;
}

public sealed record AdmittedReplayPaths(
    string ApprovedRoot,
    string ParentPackage,
    string InputPackage,
    string OutputPackage);

public sealed class ReplayRootAdmission
{
    private static readonly string[] ProductionPrefixes =
    [
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/evidence",
        "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/replay",
    ];

    private readonly ReplayRootPolicyOptions _options;
    private readonly string _approvedRoot;

    public ReplayRootAdmission(ReplayRootPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApprovedRoot))
            throw Rejected("Replay approved root is required.");
        if (options.RollbackReserveBytes < 0)
            throw Rejected("Replay rollback reserve cannot be negative.");
        _options = options;
        _approvedRoot = RequireExistingDirectory(
            options.ApprovedRoot,
            "approved root");

        if (!options.TestOnly &&
            !ProductionPrefixes.Any(prefix =>
                IsWithin(
                    _approvedRoot,
                    Path.GetFullPath(prefix))))
        {
            throw Rejected(
                "Replay approved root is outside the production FST evidence/replay roots.");
        }
        if (!options.TestOnly)
        {
            if (string.IsNullOrWhiteSpace(
                    options.ExpectedFileSystemDevice) ||
                !string.Equals(
                    TierZeroRegularFile.GetFileSystemDeviceIdentity(
                        _approvedRoot),
                    options.ExpectedFileSystemDevice,
                    StringComparison.Ordinal))
            {
                throw Rejected(
                    "Replay approved root is not on the configured FST filesystem.");
            }
        }
        if (IsGenericTemporaryPath(_approvedRoot))
            throw Rejected("Replay approved root cannot be a generic temporary directory.");
        if (File.Exists(Path.Combine(_approvedRoot, "PG_VERSION")) ||
            _approvedRoot.Split(
                    Path.DirectorySeparatorChar,
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment =>
                    segment.Equals("pg_wal", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("pgdata", StringComparison.OrdinalIgnoreCase)))
        {
            throw Rejected("Replay approved root cannot be a PostgreSQL data directory.");
        }
    }

    public string ApprovedRoot => _approvedRoot;

    public AdmittedReplayPaths AdmitExecution(
        string parentPackage,
        string inputPackage,
        string outputPackage)
    {
        var parent = AdmitExistingPackage(parentPackage, "Tier-0 parent package");
        var input = AdmitExistingPackage(inputPackage, "Tier-1 input package");
        var output = AdmitOutputPackage(outputPackage);
        RequireDistinct(parent, input, "parent and input packages");
        RequireDistinct(parent, output, "parent and output packages");
        RequireDistinct(input, output, "input and output packages");
        RequireSameFileSystem(_approvedRoot, parent);
        RequireSameFileSystem(_approvedRoot, input);
        RequireSameFileSystem(_approvedRoot, output);
        RequireSameFileSystem(parent, input);
        RequireSameFileSystem(input, output);
        return new AdmittedReplayPaths(
            _approvedRoot,
            parent,
            input,
            output);
    }

    public (string Baseline, string Candidate, string Report) AdmitComparison(
        string baseline,
        string candidate,
        string report)
    {
        var admittedBaseline =
            AdmitExistingPackage(baseline, "baseline replay package");
        var admittedCandidate =
            AdmitExistingPackage(candidate, "candidate replay package");
        var admittedReport = AdmitOutputFile(report, "comparison report");
        RequireDistinct(
            admittedBaseline,
            admittedCandidate,
            "baseline and candidate packages");
        RequireDistinct(
            admittedBaseline,
            admittedReport,
            "baseline package and comparison report");
        RequireDistinct(
            admittedCandidate,
            admittedReport,
            "candidate package and comparison report");
        RequireSameFileSystem(_approvedRoot, admittedBaseline);
        RequireSameFileSystem(_approvedRoot, admittedCandidate);
        RequireSameFileSystem(_approvedRoot, admittedReport);
        RequireSameFileSystem(admittedBaseline, admittedCandidate);
        RequireSameFileSystem(admittedCandidate, admittedReport);
        return (admittedBaseline, admittedCandidate, admittedReport);
    }

    public void RequireCapacity(
        long inputPackageBytes,
        long estimatedOutputBytes)
    {
        if (inputPackageBytes < 0 ||
            estimatedOutputBytes < 0)
        {
            throw Rejected("Replay capacity estimates cannot be negative.");
        }
        long required;
        try
        {
            required = checked(
                inputPackageBytes +
                checked(estimatedOutputBytes * 2) +
                _options.RollbackReserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.RootRejected,
                ReplayExitCode.RootRejected,
                "Replay capacity estimate overflowed.",
                exception);
        }

        var drive = FindDrive(_approvedRoot);
        if (drive.AvailableFreeSpace < required)
        {
            throw Rejected(
                $"Replay disk admission failed: required={required}, available={drive.AvailableFreeSpace}.");
        }
    }

    private string AdmitExistingPackage(
        string path,
        string description)
    {
        var admitted = RequireExistingDirectory(path, description);
        RequireWithinApprovedRoot(admitted, description);
        return admitted;
    }

    private string AdmitOutputPackage(string path)
    {
        var admitted = AdmitOutputPath(path, "output package");
        if (File.Exists(admitted) ||
            Directory.Exists(admitted))
        {
            throw Rejected("Replay output package already exists.");
        }
        return admitted;
    }

    private string AdmitOutputFile(
        string path,
        string description)
    {
        var admitted = AdmitOutputPath(path, description);
        if (File.Exists(admitted) ||
            Directory.Exists(admitted))
        {
            throw Rejected($"Replay {description} already exists.");
        }
        return admitted;
    }

    private string AdmitOutputPath(
        string path,
        string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw Rejected($"Replay {description} is required.");
        var admitted = CanonicalizeUserPath(path, description);
        RequireWithinApprovedRoot(admitted, description);
        var parent = Path.GetDirectoryName(admitted)
            ?? throw Rejected($"Replay {description} has no parent directory.");
        if (!Directory.Exists(parent))
            throw Rejected($"Replay {description} parent directory does not exist.");
        try
        {
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                _approvedRoot,
                parent,
                includeCandidate: true);
        }
        catch (TierZeroPackageException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.RootRejected,
                ReplayExitCode.RootRejected,
                $"Replay {description} path is unsafe.",
                exception);
        }
        if (new FileInfo(admitted).LinkTarget is not null ||
            new DirectoryInfo(admitted).LinkTarget is not null)
        {
            throw Rejected($"Replay {description} cannot be a symbolic link.");
        }
        return admitted;
    }

    private string RequireExistingDirectory(
        string path,
        string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw Rejected($"Replay {description} is required.");
        var admitted = CanonicalizeUserPath(path, description);
        if (!Directory.Exists(admitted))
            throw Rejected($"Replay {description} does not exist.");
        try
        {
            TierZeroPackagePath.EnsureNoSymbolicLinkAncestors(admitted);
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                admitted,
                admitted,
                includeCandidate: true);
        }
        catch (TierZeroPackageException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.RootRejected,
                ReplayExitCode.RootRejected,
                $"Replay {description} path is unsafe.",
                exception);
        }
        return Path.TrimEndingDirectorySeparator(admitted);
    }

    private void RequireWithinApprovedRoot(
        string path,
        string description)
    {
        if (!IsWithin(path, _approvedRoot) ||
            string.Equals(
                path,
                _approvedRoot,
                PathComparison))
        {
            throw Rejected(
                $"Replay {description} must be a child of the approved root.");
        }
        if (IsGenericTemporaryPath(path))
            throw Rejected($"Replay {description} cannot use a generic temporary directory.");
    }

    private static void RequireDistinct(
        string first,
        string second,
        string description)
    {
        if (IsWithin(first, second) ||
            IsWithin(second, first))
        {
            throw Rejected(
                $"Replay {description} must be separate, non-nested paths.");
        }
    }

    private static void RequireSameFileSystem(
        string first,
        string second)
    {
        var firstDevice = DeviceIdentity(first);
        var secondDevice = DeviceIdentity(second);
        if (!string.Equals(
                firstDevice,
                secondDevice,
                StringComparison.Ordinal))
        {
            throw Rejected(
                "Replay inputs and outputs must remain on one approved filesystem.");
        }
    }

    private static string DeviceIdentity(string path)
    {
        var existing = File.Exists(path) ||
                       Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path)
              ?? throw Rejected(
                  "Replay filesystem parent could not be resolved.");
        return TierZeroRegularFile
            .GetFileSystemDeviceIdentity(existing);
    }

    private static DriveInfo FindDrive(string path)
    {
        var full = CanonicalizeUserPath(path, "filesystem path");
        var drive = DriveInfo.GetDrives()
            .Where(candidate => IsWithin(
                full,
                Path.TrimEndingDirectorySeparator(
                    candidate.RootDirectory.FullName)))
            .OrderByDescending(candidate =>
                candidate.RootDirectory.FullName.Length)
            .FirstOrDefault();
        return drive
            ?? throw Rejected("Replay filesystem identity could not be resolved.");
    }

    private static bool IsGenericTemporaryPath(string path)
    {
        var temporary = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath())
                .Normalize(NormalizationForm.FormC));
        return IsWithin(path, temporary);
    }

    private static bool IsWithin(string path, string root)
    {
        var canonicalPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
                .Normalize(NormalizationForm.FormC));
        var canonicalRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root)
                .Normalize(NormalizationForm.FormC));
        if (string.Equals(
                canonicalRoot,
                Path.GetPathRoot(canonicalRoot),
                PathComparison))
        {
            return canonicalPath.StartsWith(
                canonicalRoot,
                PathComparison);
        }
        return canonicalPath.Equals(canonicalRoot, PathComparison) ||
               canonicalPath.StartsWith(
                   canonicalRoot + Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string CanonicalizeUserPath(
        string path,
        string description)
    {
        if (!Path.IsPathFullyQualified(path))
            throw Rejected($"Replay {description} must be an absolute path.");
        if (path.Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == ".."))
        {
            throw Rejected($"Replay {description} cannot contain traversal.");
        }
        var full = Path.GetFullPath(path);
        var normalized = full.Normalize(
            NormalizationForm.FormC);
        if (!OperatingSystem.IsMacOS() &&
            !string.Equals(
                full,
                normalized,
                StringComparison.Ordinal))
        {
            throw Rejected(
                $"Replay {description} is not Unicode-normalized.");
        }
        return normalized;
    }

    private static ReplayException Rejected(string message) =>
        new(
            ReplayFailureKind.RootRejected,
            ReplayExitCode.RootRejected,
            message);
}

public sealed record ReplayExecutionEnvironment(
    ReplayRootPolicyOptions RootPolicy,
    string ReplayPostgresConnection,
    string? ProductionPostgresConnection,
    TierZeroBuildIdentity Implementation,
    string ProducerIdentity,
    bool AllowTestServerAddress = false)
{
    public static ReplayExecutionEnvironment FromProcessEnvironment()
    {
        var rootPolicy = RootPolicyFromProcessEnvironment();
        var replayConnection = Required(
            "FST_REPLAY_POSTGRES_CONNECTION");
        var gitCommit = Required("FST_REPLAY_GIT_COMMIT");
        var imageDigest = Required("FST_REPLAY_IMAGE_DIGEST");
        var imageRevision = Required("FST_REPLAY_IMAGE_REVISION");
        var version = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        return new ReplayExecutionEnvironment(
            rootPolicy,
            replayConnection,
            Environment.GetEnvironmentVariable(
                "ConnectionStrings__PostgreSQL"),
            new TierZeroBuildIdentity(
                gitCommit,
                imageDigest,
                imageRevision,
                version),
            "fstservice-isolated-replay");
    }

    public static ReplayRootPolicyOptions RootPolicyFromProcessEnvironment()
    {
        var approvedRoot = Required("FST_REPLAY_APPROVED_ROOT");
        var reserve =
            ReplayRootPolicyOptions.DefaultRollbackReserveBytes;
        var configuredReserve =
            Environment.GetEnvironmentVariable(
                "FST_REPLAY_ROLLBACK_RESERVE_BYTES");
        if (!string.IsNullOrWhiteSpace(configuredReserve) &&
            (!long.TryParse(configuredReserve, out reserve) ||
             reserve < 0))
        {
            throw new ReplayException(
                ReplayFailureKind.Usage,
                ReplayExitCode.Usage,
                "FST_REPLAY_ROLLBACK_RESERVE_BYTES must be a non-negative integer.");
        }
        return new ReplayRootPolicyOptions(
            approvedRoot,
            TestOnly: false,
            reserve,
            Required("FST_REPLAY_APPROVED_DEVICE"));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ReplayException(
                ReplayFailureKind.Usage,
                ReplayExitCode.Usage,
                $"Replay mode requires environment variable {name}.");
}

public sealed class ReplayDatabaseTargetGuard
{
    public const int MarkerVersion = 1;
    public const string CreatedStatus = "created";
    public const string ImportedStatus = "imported";
    public const string PhaseCompletedStatus = "phase-completed";
    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";

    private readonly string _connectionString;
    private readonly string? _productionConnectionString;
    private readonly bool _allowTestServerAddress;

    public ReplayDatabaseTargetGuard(
        string connectionString,
        string? productionConnectionString,
        bool allowTestServerAddress = false)
    {
        _connectionString = NormalizeConnectionString(connectionString);
        _productionConnectionString =
            string.IsNullOrWhiteSpace(productionConnectionString)
                ? null
                : productionConnectionString;
        _allowTestServerAddress = allowTestServerAddress;
        ValidateConfiguredTarget();
    }

    public NpgsqlDataSource CreateDataSource(
        TierOneReplayBounds bounds)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            _connectionString)
        {
            ApplicationName = "fstservice-isolated-replay",
            Timeout = Math.Min(bounds.StatementTimeoutSeconds, 15),
            CommandTimeout = bounds.StatementTimeoutSeconds,
            MinPoolSize = 0,
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10,
            NoResetOnClose = false,
        };
        builder["Options"] =
            $"-c statement_timeout={bounds.StatementTimeoutSeconds * 1000} " +
            $"-c lock_timeout={bounds.LockTimeoutSeconds * 1000} " +
            "-c idle_in_transaction_session_timeout=30000";
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async Task<ReplayDatabaseIdentity> ValidateAsync(
        NpgsqlDataSource dataSource,
        string replayId,
        string packageRootHash,
        string sourceSystemIdentifier,
        string expectedStatus,
        string schemaFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            cancellationToken);
        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.Transaction = transaction;
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        string databaseName;
        string systemIdentifier;
        int majorVersion;
        string readOnlyDefault;
        string? serverAddress;
        bool forbiddenTablesAbsent;
        await using (var identity = connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = """
                SELECT current_database(),
                       (SELECT system_identifier::TEXT FROM pg_control_system()),
                       current_setting('server_version_num')::INTEGER / 10000,
                       current_setting('default_transaction_read_only'),
                       host(inet_server_addr()),
                       to_regclass('public.scrape_publication_state') IS NULL
                         AND to_regclass('public.service_worker_status') IS NULL
                         AND to_regclass('public.scrape_log') IS NULL
                """;
            await using var reader =
                await identity.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw Rejected("Replay database identity query returned no row.");
            databaseName = reader.GetString(0);
            systemIdentifier = reader.GetString(1);
            majorVersion = reader.GetInt32(2);
            readOnlyDefault = reader.GetString(3);
            serverAddress = reader.IsDBNull(4)
                ? null
                : reader.GetString(4);
            forbiddenTablesAbsent = reader.GetBoolean(5);
        }

        if (!databaseName.StartsWith(
                "fst_replay_",
                StringComparison.Ordinal) ||
            string.Equals(
                databaseName,
                "fstservice",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                databaseName,
                "postgres",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Rejected("Replay database identity is not isolated.");
        }
        if (string.Equals(
                systemIdentifier,
                sourceSystemIdentifier,
                StringComparison.Ordinal))
        {
            throw Rejected(
                "Replay PostgreSQL cluster matches the captured source cluster.");
        }
        if (!string.Equals(
                readOnlyDefault,
                "off",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Rejected(
                "Replay import/phase target must default to writable transactions.");
        }
        if (!_allowTestServerAddress &&
            (serverAddress is null ||
             !System.Net.IPAddress.TryParse(
                 serverAddress,
                 out var parsedAddress) ||
             !System.Net.IPAddress.IsLoopback(parsedAddress)))
        {
            throw Rejected("Replay PostgreSQL server is not loopback-isolated.");
        }
        if (!forbiddenTablesAbsent)
            throw Rejected("Replay PostgreSQL target contains production control tables.");

        ReplayMarker marker;
        try
        {
            await using (var shapeCommand = connection.CreateCommand())
            {
                shapeCommand.Transaction = transaction;
                shapeCommand.CommandText = """
                    SELECT (
                               SELECT string_agg(
                                   column_name || ':' ||
                                   data_type || ':' ||
                                   is_nullable,
                                   ',' ORDER BY ordinal_position
                               )
                               FROM information_schema.columns
                               WHERE table_schema =
                                     'fst_replay_control'
                                 AND table_name = 'target'
                           ),
                           (
                               SELECT COUNT(*)::INTEGER
                               FROM pg_constraint
                               WHERE conrelid =
                                     'fst_replay_control.target'::regclass
                                 AND contype = 'p'
                           ),
                           (
                               SELECT COUNT(*)::INTEGER
                               FROM pg_constraint
                               WHERE conrelid =
                                     'fst_replay_control.target'::regclass
                                 AND contype = 'c'
                                 AND pg_get_constraintdef(oid) =
                                     'CHECK (singleton)'
                           ),
                           (
                               SELECT COUNT(*)::BIGINT
                               FROM fst_replay_control.target
                           )
                    """;
                await using var shapeReader =
                    await shapeCommand.ExecuteReaderAsync(
                        cancellationToken);
                if (!await shapeReader.ReadAsync(cancellationToken) ||
                    shapeReader.IsDBNull(0) ||
                    !string.Equals(
                        shapeReader.GetString(0),
                        ExpectedMarkerColumns,
                        StringComparison.Ordinal) ||
                    shapeReader.GetInt32(1) != 1 ||
                    shapeReader.GetInt32(2) != 1 ||
                    shapeReader.GetInt64(3) != 1)
                {
                    throw Rejected(
                        "Replay database marker schema is not canonical.");
                }
            }
            await using var markerCommand = connection.CreateCommand();
            markerCommand.Transaction = transaction;
            markerCommand.CommandText = """
                SELECT marker_version,
                       replay_id,
                       package_root_hash,
                       database_name,
                       system_identifier,
                       status
                FROM fst_replay_control.target
                WHERE singleton = TRUE
                """;
            await using var reader =
                await markerCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw Rejected("Replay database marker is missing.");
            marker = new ReplayMarker(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5));
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw Rejected("Replay database marker is missing.");
        }

        if (marker.MarkerVersion != MarkerVersion ||
            !string.Equals(
                marker.ReplayId,
                replayId,
                StringComparison.Ordinal) ||
            !string.Equals(
                marker.PackageRootHash,
                packageRootHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                marker.DatabaseName,
                databaseName,
                StringComparison.Ordinal) ||
            !string.Equals(
                marker.SystemIdentifier,
                systemIdentifier,
                StringComparison.Ordinal) ||
            !string.Equals(
                marker.Status,
                expectedStatus,
                StringComparison.Ordinal))
        {
            throw Rejected("Replay database marker identity or state does not match.");
        }

        var extensions = new List<string>();
        await using (var extensionCommand = connection.CreateCommand())
        {
            extensionCommand.Transaction = transaction;
            extensionCommand.CommandText =
                "SELECT extname || '@' || extversion FROM pg_extension ORDER BY extname";
            await using var reader =
                await extensionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                extensions.Add(reader.GetString(0));
        }
        await transaction.CommitAsync(cancellationToken);
        return new ReplayDatabaseIdentity(
            databaseName,
            systemIdentifier,
            majorVersion,
            extensions,
            schemaFingerprint);
    }

    public static async Task TransitionAsync(
        NpgsqlDataSource dataSource,
        string replayId,
        string packageRootHash,
        string expectedStatus,
        string nextStatus,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE fst_replay_control.target
            SET status = @nextStatus,
                updated_at = now()
            WHERE singleton = TRUE
              AND replay_id = @replayId
              AND package_root_hash = @packageRootHash
              AND status = @expectedStatus
            """;
        command.Parameters.AddWithValue("nextStatus", nextStatus);
        command.Parameters.AddWithValue("replayId", replayId);
        command.Parameters.AddWithValue(
            "packageRootHash",
            packageRootHash);
        command.Parameters.AddWithValue(
            "expectedStatus",
            expectedStatus);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Rejected("Replay database marker transition failed.");
    }

    public static async Task MarkFailedAsync(
        NpgsqlDataSource dataSource,
        string replayId,
        string packageRootHash)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(
                CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE fst_replay_control.target
            SET status = @failed,
                updated_at = now()
            WHERE singleton = TRUE
              AND replay_id = @replayId
              AND package_root_hash = @packageRootHash
              AND status IN (
                  @created,
                  @imported,
                  @phaseCompleted
              )
            """;
        command.Parameters.AddWithValue("failed", FailedStatus);
        command.Parameters.AddWithValue("replayId", replayId);
        command.Parameters.AddWithValue(
            "packageRootHash",
            packageRootHash);
        command.Parameters.AddWithValue("created", CreatedStatus);
        command.Parameters.AddWithValue("imported", ImportedStatus);
        command.Parameters.AddWithValue(
            "phaseCompleted",
            PhaseCompletedStatus);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private void ValidateConfiguredTarget()
    {
        var replay = new NpgsqlConnectionStringBuilder(
            _connectionString);
        var host = replay.Host;
        var hosts = string.IsNullOrWhiteSpace(host)
            ? []
            : host.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        var database = replay.Database;
        if (hosts.Length != 1 ||
            !IsLoopbackHost(hosts[0]) ||
            database is null ||
            !database.StartsWith(
                "fst_replay_",
                StringComparison.Ordinal) ||
            database.Equals(
                "fstservice",
                StringComparison.OrdinalIgnoreCase) ||
            database.Equals(
                "postgres",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Rejected("Replay PostgreSQL connection is not an isolated loopback target.");
        }
        if (_productionConnectionString is null)
            return;

        try
        {
            var production = new NpgsqlConnectionStringBuilder(
                _productionConnectionString);
            if (hosts[0].Equals(
                    production.Host,
                    StringComparison.OrdinalIgnoreCase) &&
                replay.Port == production.Port)
            {
                throw Rejected("Replay PostgreSQL target matches the configured production cluster endpoint.");
            }
        }
        catch (ArgumentException)
        {
            throw Rejected("Configured production PostgreSQL target is ambiguous.");
        }
    }

    private static string NormalizeConnectionString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Rejected("Replay PostgreSQL connection is required.");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            builder.ApplicationName = "fstservice-isolated-replay";
            builder.IncludeErrorDetail = false;
            return builder.ConnectionString;
        }
        catch (ArgumentException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.TargetRejected,
                ReplayExitCode.TargetRejected,
                "Replay PostgreSQL connection is invalid.",
                exception);
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.Ordinal) ||
        host.Equals("::1", StringComparison.Ordinal);

    private const string ExpectedMarkerColumns =
        "singleton:boolean:NO," +
        "marker_version:integer:NO," +
        "replay_id:text:NO," +
        "package_root_hash:text:NO," +
        "database_name:text:NO," +
        "system_identifier:text:NO," +
        "status:text:NO," +
        "created_at:timestamp with time zone:NO," +
        "updated_at:timestamp with time zone:NO";

    private static ReplayException Rejected(string message) =>
        new(
            ReplayFailureKind.TargetRejected,
            ReplayExitCode.TargetRejected,
            message);

    private sealed record ReplayMarker(
        int MarkerVersion,
        string ReplayId,
        string PackageRootHash,
        string DatabaseName,
        string SystemIdentifier,
        string Status);
}
