using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class HostConnectionOwnershipTests
{
    [Theory]
    [InlineData("")]
    [InlineData("-c row_security=off")]
    [InlineData("-c row_security=off -c row_security=off")]
    public void HostConnectionsAlwaysNormalizeOneRowSecuritySetting(string options)
    {
        var factory = new PostgresUnpooledConnectionFactory(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Options = "-c row_security=on",
            SearchPath = "public",
            PersistSecurityInfo = true,
            IncludeErrorDetail = true,
        }.ConnectionString);
        using var connection = factory.CreateHostConnection(5, 5,
            options + " -c statement_timeout=5s -c lock_timeout=2s -c transaction_timeout=15s");
        var normalized = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal("-c row_security=off -c statement_timeout=5s -c lock_timeout=2s -c transaction_timeout=15s",
            normalized.Options);
        Assert.Equal(5, normalized.Timeout);
        Assert.Equal(5, normalized.CommandTimeout);
        Assert.Equal("pg_catalog,public", normalized.SearchPath);
        Assert.False(normalized.Pooling);
        Assert.False(normalized.Multiplexing);
        Assert.False(normalized.PersistSecurityInfo);
        Assert.False(normalized.IncludeErrorDetail);
    }

    [Theory]
    [InlineData("-c row_security=on")]
    [InlineData("-c row_security=off -c row_security=on")]
    [InlineData("-c row_security=true")]
    [InlineData("-c row_security=false")]
    [InlineData("-crow_security=on")]
    [InlineData("--row-security=on")]
    [InlineData("-c Row_Security=on")]
    [InlineData("-c row_security = off")]
    [InlineData("-c search_path=public")]
    [InlineData("-c statement_timeout=0s")]
    [InlineData("-c statement_timeout=-1s")]
    [InlineData("-c statement_timeout=5s -c statement_timeout=15s")]
    [InlineData("-c statement_timeout=5s\\ -c row_security=on")]
    public void HostConnectionsRejectUnsupportedOrConflictingOptions(string options)
    {
        var factory = new PostgresUnpooledConnectionFactory("Host=127.0.0.1");
        var failure = Assert.Throws<ArgumentException>(() => factory.CreateHostConnection(5, 5, options));
        Assert.DoesNotContain(options, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostConnectionsRejectMultiHostAndLoadBalancedTargets()
    {
        Assert.Throws<ArgumentException>(() =>
            new PostgresUnpooledConnectionFactory("Host=one,two"));
        Assert.Throws<ArgumentException>(() =>
            new PostgresUnpooledConnectionFactory(
                "Host=one,two;Load Balance Hosts=true"));
    }

    [Fact]
    public async Task OfflineWrapperIgnoresBashStartupHooks()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = RepositoryRoot();
        var directory = Path.Combine(
            Path.GetTempPath(),
            "offline-wrapper-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var hook = Path.Combine(directory, "bash-env");
        var marker = Path.Combine(directory, "startup-ran");
        await File.WriteAllTextAsync(
            hook,
            "/usr/bin/touch -- \"$WRAPPER_INJECTION_MARKER\"\n");
        try
        {
            var start = new ProcessStartInfo(
                Path.Combine(
                    root,
                    "tools/postgres-snapshot-generation-retention-report.sh"))
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/bin:/bin";
            start.Environment["BASH_ENV"] = hook;
            start.Environment["WRAPPER_INJECTION_MARKER"] = marker;
            start.Environment["FST_SNAPSHOT_RETENTION_REPORT_BINARY_SHA256"] =
                "invalid";
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    "Offline wrapper test process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            Assert.Equal(64, process.ExitCode);
            Assert.Contains(
                "must be a lowercase SHA-256",
                await stderr,
                StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OfflineHostDoesNotExposeDataSourceOrPlannerConstructionPublicly()
    {
        var type = typeof(OfflineReportDatabase);
        const BindingFlags instance = BindingFlags.Instance;
        Assert.Null(type.GetProperty(nameof(OfflineReportDatabase.DataSource), instance | BindingFlags.Public));
        var source = type.GetProperty(nameof(OfflineReportDatabase.DataSource), instance | BindingFlags.NonPublic);
        Assert.NotNull(source);
        Assert.True(source.GetMethod!.IsAssembly);
        Assert.Null(source.SetMethod);
        Assert.Null(type.GetMethod("CreatePlanner", instance | BindingFlags.Public));
        Assert.True(type.GetMethod("CreatePlanner", instance | BindingFlags.NonPublic)!.IsAssembly);
        Assert.DoesNotContain(type.GetFields(instance | BindingFlags.Public),
            field => field.FieldType == typeof(NpgsqlDataSource)
                || field.FieldType == typeof(PostgresUnpooledConnectionFactory));
    }

    [Fact]
    public void HostToolsNeverUseDataSourceDisplayStringsAsCredentialSources()
    {
        var root = RepositoryRoot();
        const string negativeProbe = "tools/testdata/offline-retention-report-fixture/Program.cs";
        var reconstruction = new Regex(
            @"(?:\b(?:\w*[dD]ata[Ss]ource|source|_ds|ds)|GetRequiredService<NpgsqlDataSource>\(\))\s*\.ConnectionString\b");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tools"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj"))
                continue;
            var source = File.ReadAllText(file);
            if (relative == negativeProbe)
            {
                // The owned executable probe inspects sanitization and deliberately proves reconstruction fails.
                Assert.Equal(2, reconstruction.Matches(source).Count);
                Assert.Contains("sanitizedReconstructionRefused = true", source, StringComparison.Ordinal);
                Assert.Contains("unpooledConnections: new PostgresUnpooledConnectionFactory(builder.ConnectionString)",
                    source, StringComparison.Ordinal);
                continue;
            }
            Assert.False(reconstruction.IsMatch(source), "A host tool acquired a data-source credential dependency: " + relative);
        }
    }

    [Fact]
    public void OfflineGitIdentityClearsInheritedConfiguration()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tools/FstSnapshotGenerationRetentionReport/OfflineReportCodeIdentityProvider.cs"));
        Assert.Contains("start.Environment.Clear()", source, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_NOSYSTEM", source, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_GLOBAL", source, StringComparison.Ordinal);
        Assert.Contains("core.fsmonitor=false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InitializerAndOfflinePlannerRequireOriginalPrivateFactories()
    {
        var root = RepositoryRoot();
        var initializer = File.ReadAllText(Path.Combine(root, "FSTService/Persistence/DatabaseInitializer.cs"));
        var start = initializer.IndexOf("EnsureSnapshotGenerationRetentionSchemaAsync(", StringComparison.Ordinal);
        var end = initializer.IndexOf("internal static DatabaseSchemaInitializationStep", start, StringComparison.Ordinal);
        var dedicated = initializer[start..end];
        Assert.Contains("string normalizedConnectionString", dedicated, StringComparison.Ordinal);
        Assert.Contains("new PostgresUnpooledConnectionFactory(normalizedConnectionString)", dedicated, StringComparison.Ordinal);
        Assert.DoesNotContain("dataSource.ConnectionString", dedicated, StringComparison.Ordinal);
        foreach (var path in new[]
        {
            "FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.Offline.cs",
            "FSTService/Persistence/Maintenance/SnapshotGenerationRetentionOfflineDiagnostics.cs",
            "tools/FstSnapshotGenerationRetentionReport/OfflineReportDatabase.cs",
        })
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            Assert.False(source.Contains("_dataSource.ConnectionString", StringComparison.Ordinal)
                || source.Contains("DataSource.ConnectionString", StringComparison.Ordinal),
                "Authenticated host connections must use their original private factory: " + path);
        }
        var command = File.ReadAllText(Path.Combine(root, "FSTService/Persistence/SnapshotRetentionSchemaCommand.cs"));
        Assert.DoesNotContain("NpgsqlDataSource.Create", command, StringComparison.Ordinal);
        Assert.Contains("connection.ConnectionString, deadline.Token", command, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerConstructionDoesNotAcquireOfflineFactoryOrChangeConnectionOwnership()
    {
        var root = RepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root,
            "FSTService/Persistence/Maintenance/SnapshotGenerationRetentionPlanner.cs"));
        Assert.DoesNotContain("_offlineConnections", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateForOffline", worker, StringComparison.Ordinal);
        Assert.Contains("await _dataSource.OpenConnectionAsync(ct)", worker, StringComparison.Ordinal);
        var program = File.ReadAllText(Path.Combine(root, "FSTService/Program.cs"));
        Assert.Contains("new PostgresUnpooledConnectionFactory(\n    RuntimePostgresConnection(services))",
            program.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("sp.GetRequiredService<PostgresUnpooledConnectionFactory>()", program, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FortniteFestivalLeaderboardScraper.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }
}
