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
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
                CapAdd = ["NET_ADMIN"],
                Init = false,
                NetworkMode = "festivalservicetracker_default",
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
        Assert.Equal(["/var/run/docker.sock:/var/run/docker.sock"], create.HostConfig.Binds);
        Assert.Equal(["NET_ADMIN"], create.HostConfig.CapAdd);
        Assert.True(create.HostConfig.Init);
        Assert.Equal("festivalservicetracker_default", create.HostConfig.NetworkMode);
        Assert.Equal(RestartPolicyKind.UnlessStopped, create.HostConfig.RestartPolicy.Name);
        Assert.NotNull(create.NetworkingConfig);
        var endpoint = Assert.Single(create.NetworkingConfig.EndpointsConfig);
        Assert.Equal("festivalservicetracker_default", endpoint.Key);
        Assert.Equal(["gluetun-3", "vpn"], endpoint.Value.Aliases);
    }
    [Fact]
    public async Task Restart_StartsAfterAStopThatCompleted()
    {
        var calls = new List<string>();

        var canceled = await GluetunContainerRecycler.RestartWithoutLeavingStoppedAsync(
            stop: _ => { calls.Add("stop"); return Task.CompletedTask; },
            isRunning: _ => { calls.Add("inspect"); return Task.FromResult(false); },
            start: _ => { calls.Add("start"); return Task.CompletedTask; },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(canceled);
        Assert.Equal(["stop", "start"], calls);
    }

    [Fact]
    public async Task Restart_StillStartsWhenTheCallerCancelsDuringStop()
    {
        using var caller = new CancellationTokenSource();
        var calls = new List<string>();
        var running = new Queue<bool>([true, true, false]);

        var canceled = await GluetunContainerRecycler.RestartWithoutLeavingStoppedAsync(
            stop: token =>
            {
                calls.Add("stop");
                caller.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            isRunning: token =>
            {
                Assert.False(token.IsCancellationRequested);
                calls.Add("inspect");
                return Task.FromResult(running.Dequeue());
            },
            start: token =>
            {
                Assert.False(token.IsCancellationRequested);
                calls.Add("start");
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            caller.Token,
            delay: (_, _) => Task.CompletedTask);

        Assert.True(canceled);
        Assert.Equal(["stop", "inspect", "inspect", "inspect", "start"], calls);
    }

    [Fact]
    public async Task Restart_DoesNotStartWhenTheStopFailsForAnotherReason()
    {
        var started = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GluetunContainerRecycler.RestartWithoutLeavingStoppedAsync(
                stop: _ => throw new InvalidOperationException("daemon error"),
                isRunning: _ => Task.FromResult(false),
                start: _ => { started = true; return Task.CompletedTask; },
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.False(started);
    }

    [Fact]
    public async Task Restart_ReportsATimeoutWhenTheCanceledStopNeverSettles()
    {
        using var caller = new CancellationTokenSource();
        var started = false;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            GluetunContainerRecycler.RestartWithoutLeavingStoppedAsync(
                stop: token => { caller.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                isRunning: _ => Task.FromResult(true),
                start: _ => { started = true; return Task.CompletedTask; },
                TimeSpan.FromMilliseconds(50),
                caller.Token,
                delay: (interval, token) => Task.Delay(TimeSpan.FromMilliseconds(5), token)));

        Assert.False(started);
    }
}
