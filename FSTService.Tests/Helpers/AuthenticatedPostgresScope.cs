using System.Security.Cryptography;
using System.Text;
using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence;
using Npgsql;

namespace FSTService.Tests.Helpers;

/// <summary>A disposable role on the owned TCP fixture; its password never enters Docker configuration.</summary>
internal sealed class AuthenticatedPostgresScope : IAsyncDisposable
{
    private readonly NpgsqlDataSource _administration;
    private readonly string _administrator;
    private readonly string _role;
    private readonly string _password;
    private readonly string _connectionString;
    private readonly string _wrongConnectionString;

    internal NpgsqlDataSource DataSource { get; }
    internal PostgresUnpooledConnectionFactory Connections { get; }

    private AuthenticatedPostgresScope(
        NpgsqlDataSource administration, string role, string password)
    {
        _administration = administration;
        _role = role;
        _password = password;
        var original = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString);
        _administrator = original.Username!;
        Assert.True(original.Host == "127.0.0.1", "Authenticated coverage requires the owned loopback TCP fixture.");
        original.Database = new NpgsqlConnectionStringBuilder(administration.ConnectionString).Database;
        original.Username = role;
        original.Password = password;
        original.ApplicationName = "fst-authenticated-retention-test";
        original.Timeout = 5;
        original.CommandTimeout = 20;
        original.Pooling = false;
        original.IncludeErrorDetail = false;
        original.Remove("Persist Security Info");
        Assert.False(original.PersistSecurityInfo);
        _connectionString = original.ConnectionString;
        Connections = new(_connectionString);
        DataSource = NpgsqlDataSource.Create(_connectionString);
        original.Password = password + "-wrong";
        _wrongConnectionString = original.ConnectionString;
    }

    internal static async Task<AuthenticatedPostgresScope> CreateAsync(NpgsqlDataSource administration)
    {
        var password = Environment.GetEnvironmentVariable("FST_TEST_AUTH_PASSWORD")
            ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(36));
        Assert.True(password.Length >= 32, "The owned authentication sentinel must be nonempty and random.");
        var role = "fst_auth_" + Guid.NewGuid().ToString("N");
        var result = new AuthenticatedPostgresScope(administration, role, password);
        try
        {
            await using var connection = await administration.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SET log_statement='none';
                SET log_min_duration_statement=-1;
                SET log_min_error_statement='panic';
                SET log_parameter_max_length=0;
                SET log_parameter_max_length_on_error=0;
                """;
            await command.ExecuteNonQueryAsync();
            // PostgreSQL stores only this verifier. No SQL statement contains the plaintext password.
            command.CommandText = $"CREATE ROLE \"{role}\" SUPERUSER LOGIN PASSWORD '{ScramVerifier(password)}'";
            await command.ExecuteNonQueryAsync();
            command.CommandText = """
                SELECT rolpassword LIKE 'SCRAM-SHA-256$%'
                  AND EXISTS (SELECT 1 FROM pg_catalog.pg_hba_file_rules
                    WHERE type='host' AND address='all' AND auth_method='scram-sha-256'
                      AND database=ARRAY['all'] AND user_name=ARRAY['all'])
                FROM pg_catalog.pg_authid WHERE rolname=@role
                """;
            command.Parameters.AddWithValue("role", role);
            Assert.True(await command.ExecuteScalarAsync() is true, "The owned role must use a SCRAM host rule and verifier.");
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw new InvalidOperationException("Owned SCRAM fixture admission failed.");
        }
    }

    internal OfflineReportDatabase CreateReportDatabase(bool wrongPassword = false) =>
        new(wrongPassword ? _wrongConnectionString : _connectionString);

    internal PostgresUnpooledConnectionFactory WrongConnections() => new(_wrongConnectionString);

    internal async Task<(int ExitCode, string Output)> RunSchemaCommandAsync(
        bool wrongPassword = false,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? beforeProof = null,
        Func<CancellationToken, Task>? afterServerCommit = null,
        Func<Task>? beforeConnectionDispose = null)
    {
        using var output = new StringWriter();
        var exit = await SnapshotRetentionSchemaCommand.RunAsync(
            [SnapshotRetentionSchemaCommand.Flag],
            wrongPassword ? _wrongConnectionString : _connectionString, output,
            beforeDmlAssertionForTest: beforeProof, afterServerCommitForTest: afterServerCommit,
            beforeConnectionDisposeForTest: beforeConnectionDispose);
        var text = output.ToString();
        RequireSecretFree(text);
        return (exit, text);
    }

    internal void RequireSecretFree(string text) =>
        Assert.True(!text.Contains(_password, StringComparison.Ordinal)
            && !text.Contains(_connectionString, StringComparison.Ordinal)
            && !text.Contains(_wrongConnectionString, StringComparison.Ordinal),
            "Credential material was suppressed from authentication test output.");

    internal async Task RequireAuthenticatedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current_user=@role AND inet_client_addr() IS NOT NULL
                AND current_setting('server_version_num')::INTEGER BETWEEN 170000 AND 179999
            """;
        command.Parameters.AddWithValue("role", _role);
        Assert.True(await command.ExecuteScalarAsync() is true, "A fresh owned PG17 TCP connection must authenticate as the private role.");
    }

    internal async Task RefuseNewConnectionsAsync()
    {
        await using var connection = await _administration.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER ROLE \"{_role}\" NOLOGIN";
        await command.ExecuteNonQueryAsync();
    }

    internal async Task EnforceRowSecurityAsync()
    {
        await using var administration = await _administration.OpenConnectionAsync();
        await using var grants = administration.CreateCommand();
        grants.CommandText = $"""
            GRANT pg_read_all_settings, pg_read_all_stats TO "{_role}";
            GRANT EXECUTE ON FUNCTION pg_catalog.pg_control_system() TO "{_role}";
            GRANT USAGE ON SCHEMA public TO "{_role}";
            GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO "{_role}";
            GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO "{_role}";
            ALTER ROLE "{_role}" NOSUPERUSER NOBYPASSRLS;
            """;
        await grants.ExecuteNonQueryAsync();
        await using var connection = Connections.CreateConnection();
        await connection.OpenAsync();
        await RequireAuthenticatedAsync(connection);
        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT NOT role.rolsuper AND NOT role.rolbypassrls
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class relation
                    WHERE relation.oid IN (
                        'public.snapshot_generation_retention_holds'::regclass,
                        'public.snapshot_generation_retention_cycles'::regclass)
                      AND pg_catalog.pg_has_role(current_user, relation.relowner, 'USAGE'))
            FROM pg_catalog.pg_roles role WHERE role.rolname=current_user
            """;
        Assert.True(await verify.ExecuteScalarAsync() is true,
            "The RLS fixture role must be neither superuser, bypass role nor retention-table owner.");
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await using var connection = await _administration.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=@role)";
        command.Parameters.AddWithValue("role", _role);
        if (await command.ExecuteScalarAsync() is not true)
            return;
        command.Parameters.Clear();
        command.CommandText = $"REASSIGN OWNED BY \"{_role}\" TO \"{_administrator}\"; DROP OWNED BY \"{_role}\"; DROP ROLE \"{_role}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static string ScramVerifier(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(password, salt, 4096, HashAlgorithmName.SHA256, 32);
        var client = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Client Key"));
        var stored = SHA256.HashData(client);
        var server = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Server Key"));
        CryptographicOperations.ZeroMemory(salted);
        CryptographicOperations.ZeroMemory(client);
        return $"SCRAM-SHA-256$4096:{Convert.ToBase64String(salt)}${Convert.ToBase64String(stored)}:{Convert.ToBase64String(server)}";
    }
}
