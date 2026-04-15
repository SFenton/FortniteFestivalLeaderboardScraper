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
    /// and starts a new container with the same image, env, caps, devices, network,
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

            var image = inspect.Config.Image;
            var existingEnv = inspect.Config.Env ?? new List<string>();
            var hostConfig = inspect.HostConfig;

            // Build new env list: clone existing, override SERVER_CITIES + SERVER_NAMES
            var newEnv = new List<string>(existingEnv.Count + 2);
            foreach (var env in existingEnv)
            {
                if (env.StartsWith("SERVER_CITIES=") || env.StartsWith("SERVER_NAMES="))
                    continue; // skip — we'll add our overrides
                newEnv.Add(env);
            }
            newEnv.Add($"SERVER_CITIES={city}");
            newEnv.Add($"SERVER_NAMES={serverName}");

            // Determine the network to attach to (take the first one)
            string? networkName = null;
            EndpointSettings? networkEndpoint = null;
            if (inspect.NetworkSettings?.Networks is { Count: > 0 } networks)
            {
                var first = networks.First();
                networkName = first.Key;
                // Preserve the network config but clear the dynamically-assigned fields
                // so the Docker daemon assigns fresh values on the new container.
                networkEndpoint = new EndpointSettings
                {
                    Aliases = first.Value.Aliases,
                };
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
            var createParams = new CreateContainerParameters
            {
                Image = image,
                Name = containerName,
                Env = newEnv,
                // Preserve the healthcheck from the original container
                Healthcheck = inspect.Config.Healthcheck,
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

            _log.LogDebug("Creating container {Container} (image: {Image})...", containerName, image);
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

    public void Dispose()
    {
        _docker.Dispose();
    }
}
