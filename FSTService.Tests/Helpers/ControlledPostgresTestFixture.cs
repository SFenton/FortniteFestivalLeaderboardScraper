using Docker.DotNet.Models;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using System.Collections.Concurrent;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace FSTService.Tests.Helpers;

public static class ControlledPostgresTestFixture
{
    private static readonly ConcurrentDictionary<string, string> Instances = new(StringComparer.Ordinal);

    public static PostgreSqlBuilder CreateBuilder(string role)
    {
        var builder = new PostgreSqlBuilder();
        var configured = Environment.GetEnvironmentVariable("FST_TEST_PGDATA_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
            return builder;
        if (!Guid.TryParseExact(Environment.GetEnvironmentVariable("FST_TEST_RESOURCE_SCOPE"), "D", out var scope)
            || Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED") != "true")
            throw new InvalidOperationException("Controlled PostgreSQL tests require an exact scope and explicit cleanup.");
        const string drive = "/mnt/docker-storage/Docker/FestivalServiceTracker/fst-data/";
        var root = Path.GetFullPath(configured);
        if (!root.StartsWith(drive, StringComparison.Ordinal)
            || !Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidOperationException("Controlled PostgreSQL PGDATA must use the owned FST-drive root.");
        var identity = Guid.NewGuid().ToString("N");
        var instance = Path.Combine(root, role + "-" + identity);
        var data = Path.Combine(instance, "data");
        var socket = Path.Combine(instance, "socket");
        var logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(socket);
        Directory.CreateDirectory(logs);
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("The controlled PostgreSQL fixture requires Linux.");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(data, mode);
        File.SetUnixFileMode(socket, mode);
        File.SetUnixFileMode(logs, mode);
        var name = "fst-controlled-tests-" + identity[..16];
        Instances[name] = instance;
        File.WriteAllText(Path.GetRelativePath(Environment.CurrentDirectory,
            Path.Combine(instance, "fixture-intent.json")), JsonSerializer.Serialize(new
        {
            scope = scope.ToString("D"), role, name, data, socket,
            postgresLogDirectory = Path.Combine(data, "logs"),
            dockerLogging = "none", bindAddress = "127.0.0.1",
        }));
        return builder
            .WithName(name)
            .WithLabel("fst.controlled-test.scope", scope.ToString("D"))
            .WithLabel("fst.controlled-test.role", role)
            .WithBindMount(data, "/var/lib/postgresql/data")
            .WithBindMount(socket, "/var/run/postgresql")
            .WithEnvironment("PGDATA", "/var/lib/postgresql/data/pgdata")
            .WithEnvironment("TMPDIR", "/var/lib/postgresql/data")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "pg_isready", "-h", "127.0.0.1", "-U", "test", "-d", "fst_tests"))
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new HostConfig();
                parameters.HostConfig.ReadonlyRootfs = true;
                parameters.HostConfig.NanoCPUs = 2_000_000_000;
                parameters.HostConfig.Memory = 4L * 1024 * 1024 * 1024;
                parameters.HostConfig.MemorySwap = parameters.HostConfig.Memory;
                parameters.HostConfig.PidsLimit = 256;
                parameters.HostConfig.LogConfig = new LogConfig { Type = "none" };
                parameters.Cmd ??= new List<string> { "postgres" };
                parameters.Cmd.Add("-c");
                parameters.Cmd.Add("logging_collector=on");
                parameters.Cmd.Add("-c");
                parameters.Cmd.Add("log_directory=/var/lib/postgresql/data/logs");
                parameters.Cmd.Add("-c");
                parameters.Cmd.Add("log_filename=postgresql.log");
                parameters.Cmd.Add("-c");
                parameters.Cmd.Add("log_file_mode=0644");
                foreach (var mappings in parameters.HostConfig.PortBindings.Values)
                    foreach (var mapping in mappings)
                        mapping.HostIP = "127.0.0.1";
            });
    }

    public static async Task RecordStartedAsync(PostgreSqlContainer container)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FST_TEST_PGDATA_ROOT")))
            return;
        var name = container.Name.TrimStart('/');
        if (!Instances.TryGetValue(name, out var instance))
            throw new InvalidOperationException("Started test container has no owned fixture intent.");
        using var client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
        var actual = await client.Containers.InspectContainerAsync(container.Id);
        var scope = Environment.GetEnvironmentVariable("FST_TEST_RESOURCE_SCOPE");
        var ports = actual.NetworkSettings.Ports;
        if (!actual.Config.Labels.TryGetValue("fst.controlled-test.scope", out var actualScope)
            || actualScope != scope
            || actual.HostConfig.LogConfig.Type != "none"
            || !string.IsNullOrEmpty(actual.LogPath)
            || actual.Mounts.Count != 2
            || actual.Mounts.Any(mount => mount.Type != "bind"
                || !Path.GetFullPath(mount.Source).StartsWith(instance + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            || actual.HostConfig.PortBindings.Values.SelectMany(value => value)
                .Any(binding => binding.HostIP != "127.0.0.1")
            || !ports.TryGetValue("5432/tcp", out var postgresBindings)
            || postgresBindings.Count != 1
            || postgresBindings.Any(binding => binding.HostIP != "127.0.0.1"
                || !ushort.TryParse(binding.HostPort, out var port) || port == 0
                || port != container.GetMappedPublicPort(5432)))
            throw new InvalidOperationException("Started test container violates controlled storage/logging/port ownership.");
        var identity = await container.ExecAsync([
            "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1", "-U", "test", "-d", "fst_tests", "-c",
            """
            SELECT jsonb_build_object('systemIdentifier',system_identifier::TEXT,
                'postmasterStartedAt',pg_postmaster_start_time(),
                'dataDirectory',current_setting('data_directory'),
                'socketDirectories',current_setting('unix_socket_directories'),
                'serverVersion',current_setting('server_version_num'))::TEXT
            FROM pg_control_system()
            """
        ]);
        if (identity.ExitCode != 0)
            throw new InvalidOperationException("Started fixture PostgreSQL identity could not be recorded.");
        using var identityJson = JsonDocument.Parse(identity.Stdout);
        File.WriteAllText(Path.GetRelativePath(Environment.CurrentDirectory,
            Path.Combine(instance, "fixture-started.json")), JsonSerializer.Serialize(new
        {
            id = actual.ID, name, scope, image = actual.Config.Image, imageId = actual.Image,
            dockerLogging = actual.HostConfig.LogConfig.Type, dockerLogPath = actual.LogPath,
            mounts = actual.Mounts.Select(mount => new { mount.Type, mount.Source, mount.Destination, mount.RW, mount.Name }),
            requestedPortBindings = actual.HostConfig.PortBindings,
            portBindings = ports,
            postgresIdentity = identityJson.RootElement,
            ryukDisabled = Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED") == "true",
        }));
    }
}
