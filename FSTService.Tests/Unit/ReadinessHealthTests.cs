using System.Net;
using System.Text.Json;
using FSTService.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace FSTService.Tests.Unit;

public sealed class ReadinessHealthTests
{
    [Theory]
    [InlineData(HealthStatus.Degraded, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HealthStatus.Healthy, HttpStatusCode.OK)]
    public async Task Aggregate_health_preserves_global_status_mapping(HealthStatus status, HttpStatusCode expected)
    {
        var response = await ProbeAsync(new HealthCheckResult(status, "unrelated health condition"));

        Assert.Equal(expected, response.Status);
        Assert.Equal(status.ToString(), response.Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Read_only_serving_is_healthy_with_explicit_structured_detail_but_does_not_mask_other_checks()
    {
        var status = new StartupPublicationReadOnlyStatus(
            "degraded_read_only", true, false, "publication_path_artifact_validation_failed",
            [new(23, "binding_expected_count_invalid")], [new(22, "manifest_version_future")]);
        var database = HealthCheckResult.Healthy(
            "degraded_read_only: serving persisted public reads; mutations disabled: " + status.Reason,
            new Dictionary<string, object> { ["startup"] = status });

        var serving = await ProbeAsync(database);
        Assert.Equal(HttpStatusCode.OK, serving.Status);
        var startup = serving.Json.GetProperty("startup");
        Assert.Equal("degraded_read_only", startup.GetProperty("state").GetString());
        Assert.False(startup.GetProperty("mutationReady").GetBoolean());
        Assert.Equal(status.Reason, startup.GetProperty("reason").GetString());
        Assert.Single(startup.GetProperty("diagnostics").EnumerateArray());
        Assert.Single(startup.GetProperty("warnings").EnumerateArray());
        Assert.Contains("degraded_read_only",
            serving.Json.GetProperty("checks").GetProperty("database").GetProperty("description").GetString());

        var unrelatedDegraded = await ProbeAsync(database, HealthStatus.Degraded);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unrelatedDegraded.Status);
        Assert.Equal("Degraded", unrelatedDegraded.Json.GetProperty("status").GetString());
        Assert.Equal("degraded_read_only",
            unrelatedDegraded.Json.GetProperty("startup").GetProperty("state").GetString());
    }

    internal static async Task<(HttpStatusCode Status, JsonElement Json)> ProbeAsync(
        HealthCheckResult database, HealthStatus? unrelated = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var checks = builder.Services.AddHealthChecks().AddCheck("database", () => database);
        if (unrelated.HasValue)
            checks.AddCheck("unrelated", () => new HealthCheckResult(unrelated.Value, "unrelated health condition"));
        await using var app = builder.Build();
        app.MapHealthChecks("/readyz", ApiEndpoints.CreateReadinessHealthCheckOptions());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/readyz");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.Clone());
    }
}
