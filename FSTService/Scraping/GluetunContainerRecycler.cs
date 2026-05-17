using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace FSTService.Scraping;

/// <summary>
/// Recycles gluetun VPN containers by stopping, removing, and recreating them
/// with updated <c>SERVER_CITIES</c> and <c>SERVER_NAMES</c> environment variables.
/// Uses the Docker Engine API over the Unix socket — more reliable than the gluetun
/// control API when gluetun is crash-looping or the WireGuard tunnel is wedged.
/// </summary>
public sealed class GluetunContainerRecycler : IDisposable
{
    private readonly DockerClient _docker;
    private readonly ILogger<GluetunContainerRecycler> _log;

    /// <summary>How long to wait for the container to stop before killing it.</summary>
    private const uint StopWaitSeconds = 15;

    public GluetunContainerRecycler(ILogger<GluetunContainerRecycler> logger)
    {
        _log = logger;
        _docker = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
    }

    /// <summary>
    /// Recreates a gluetun container targeting a specific VPN server.
    /// Inspects the existing container's config, stops+removes it, then creates
    /// and starts a new container with the same image, env, labels, caps, devices, network,
    /// and restart policy — but with <c>SERVER_CITIES</c> and <c>SERVER_NAMES</c>
    /// overridden to target the specified server.
    /// </summary>
    /// <returns>True if the container was successfully recreated and started.</returns>
    public async Task<bool> RecycleAsync(string containerName, string city, string serverName)
    {
        try
        {
            _log.LogInformation("Recycling container {Container} → {Server}@{City}",
                containerName, serverName, city);

            // Phase 1: Inspect current container to clone its config
            ContainerInspectResponse inspect;
            try
            {
                inspect = await _docker.Containers.InspectContainerAsync(containerName);
            }
            catch (DockerContainerNotFoundException)
            {
                _log.LogError("Container {Container} not found — cannot recycle", containerName);
                return false;
            }

            // Phase 2: Stop and remove the old container
            _log.LogDebug("Stopping container {Container}...", containerName);
            try
            {
                await _docker.Containers.StopContainerAsync(containerName,
                    new ContainerStopParameters { WaitBeforeKillSeconds = StopWaitSeconds });
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone
            }

            _log.LogDebug("Removing container {Container}...", containerName);
            try
            {
                await _docker.Containers.RemoveContainerAsync(containerName,
                    new ContainerRemoveParameters { Force = true });
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone
            }

            // Phase 3: Create and start the new container with same config + updated env
            var createParams = BuildCreateContainerParameters(inspect, containerName, city, serverName);

            _log.LogDebug("Creating container {Container} (image: {Image})...", containerName, createParams.Image);
            var createResponse = await _docker.Containers.CreateContainerAsync(createParams);

            _log.LogDebug("Starting container {Container} (id: {Id})...",
                containerName, createResponse.ID[..12]);
            await _docker.Containers.StartContainerAsync(createResponse.ID,
                new ContainerStartParameters());

            _log.LogInformation("Container {Container} recreated → {Server}@{City}",
                containerName, serverName, city);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Failed to recycle container {Container}: {Error}",
                containerName, ex.Message);
            return false;
        }
    }

    internal static CreateContainerParameters BuildCreateContainerParameters(
        ContainerInspectResponse inspect,
        string containerName,
        string city,
        string serverName)
    {
        var existingEnv = inspect.Config.Env ?? [];
        var hostConfig = inspect.HostConfig;

        var newEnv = new List<string>(existingEnv.Count + 2);
        foreach (var env in existingEnv)
        {
            if (env.StartsWith("SERVER_CITIES=", StringComparison.Ordinal)
                || env.StartsWith("SERVER_NAMES=", StringComparison.Ordinal))
            {
                continue;
            }

            newEnv.Add(env);
        }
        newEnv.Add($"SERVER_CITIES={city}");
        newEnv.Add($"SERVER_NAMES={serverName}");

        string? networkName = null;
        EndpointSettings? networkEndpoint = null;
        if (inspect.NetworkSettings?.Networks is { Count: > 0 } networks)
        {
            var first = networks.First();
            networkName = first.Key;
            networkEndpoint = new EndpointSettings
            {
                Aliases = first.Value.Aliases,
            };
        }

        var createParams = new CreateContainerParameters
        {
            Image = inspect.Config.Image,
            Name = containerName,
            Env = newEnv,
            Labels = inspect.Config.Labels is null
                ? null
                : new Dictionary<string, string>(inspect.Config.Labels),
            HostConfig = new HostConfig
            {
                CapAdd = hostConfig.CapAdd,
                Devices = hostConfig.Devices,
                RestartPolicy = hostConfig.RestartPolicy,
            },
            NetworkingConfig = networkName is not null
                ? new NetworkingConfig
                {
                    EndpointsConfig = new Dictionary<string, EndpointSettings>
                    {
                        [networkName] = networkEndpoint!,
                    },
                }
                : null,
        };

        TryCopyHealthcheck(inspect.Config, createParams);
        return createParams;
    }

    private static void TryCopyHealthcheck(Config sourceConfig, CreateContainerParameters target)
    {
        try
        {
            var sourceProperty = sourceConfig.GetType().GetProperty("Healthcheck");
            var targetProperty = target.GetType().GetProperty("Healthcheck");
            if (sourceProperty is null || targetProperty is null)
                return;

            if (sourceProperty.PropertyType != targetProperty.PropertyType || !targetProperty.CanWrite)
                return;

            targetProperty.SetValue(target, sourceProperty.GetValue(sourceConfig));
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMethodException)
        {
            // Docker.DotNet model shape differs across consumers; healthcheck preservation is best-effort.
        }
    }

    public void Dispose()
    {
        _docker.Dispose();
    }
}
