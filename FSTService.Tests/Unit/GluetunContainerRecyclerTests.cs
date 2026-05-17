using Docker.DotNet.Models;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class GluetunContainerRecyclerTests
{
    [Fact]
    public void BuildCreateContainerParameters_PreservesComposeLabelsAndOverridesServerEnv()
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = "festivalservicetracker",
            ["com.docker.compose.service"] = "gluetun-3",
            ["com.docker.compose.container-number"] = "1",
        };
        var inspect = new ContainerInspectResponse
        {
            Config = new Config
            {
                Image = "qmcgaw/gluetun",
                Env =
                [
                    "HTTPPROXY=on",
                    "VPN_TYPE=wireguard",
                    "SERVER_CITIES=Los Angeles",
                    "SERVER_NAMES=OldServer",
                ],
                Labels = labels,
            },
            HostConfig = new HostConfig
            {
                CapAdd = ["NET_ADMIN"],
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
            },
            NetworkSettings = new NetworkSettings
            {
                Networks = new Dictionary<string, EndpointSettings>
                {
                    ["festivalservicetracker_default"] = new()
                    {
                        Aliases = ["gluetun-3", "vpn"],
                    },
                },
            },
        };

        var create = GluetunContainerRecycler.BuildCreateContainerParameters(
            inspect,
            "gluetun-3",
            "Barcelona",
            "Eridanus");

        Assert.Equal("qmcgaw/gluetun", create.Image);
        Assert.Equal("gluetun-3", create.Name);
        Assert.NotNull(create.Labels);
        Assert.NotSame(labels, create.Labels);
        Assert.Equal("festivalservicetracker", create.Labels["com.docker.compose.project"]);
        Assert.Equal("gluetun-3", create.Labels["com.docker.compose.service"]);
        Assert.Equal("1", create.Labels["com.docker.compose.container-number"]);
        Assert.Contains("HTTPPROXY=on", create.Env);
        Assert.Contains("VPN_TYPE=wireguard", create.Env);
        Assert.Contains("SERVER_CITIES=Barcelona", create.Env);
        Assert.Contains("SERVER_NAMES=Eridanus", create.Env);
        Assert.DoesNotContain("SERVER_CITIES=Los Angeles", create.Env);
        Assert.DoesNotContain("SERVER_NAMES=OldServer", create.Env);
        Assert.Equal(["NET_ADMIN"], create.HostConfig.CapAdd);
        Assert.Equal(RestartPolicyKind.UnlessStopped, create.HostConfig.RestartPolicy.Name);
        Assert.NotNull(create.NetworkingConfig);
        var endpoint = Assert.Single(create.NetworkingConfig.EndpointsConfig);
        Assert.Equal("festivalservicetracker_default", endpoint.Key);
        Assert.Equal(["gluetun-3", "vpn"], endpoint.Value.Aliases);
    }
}