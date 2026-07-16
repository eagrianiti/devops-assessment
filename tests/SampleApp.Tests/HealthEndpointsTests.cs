using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SampleApp.Tests;

// Boots the real app in-memory (no Kubernetes needed) and hits the same endpoints
// k8s/deployment.yaml points its probes at, so a broken health check fails the
// build before it ever reaches a cluster.
public class HealthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LivenessProbe_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessProbe_ReturnsHealthyWhenDependencyIsUp()
    {
        // DownstreamDependencyHealthCheck is hard-coded Healthy in this sample;
        // this pins that current behavior so a future change to the check is
        // a deliberate, visible decision rather than a silent regression.
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // /health/startup is intentionally NOT covered here: it depends on wall-clock
    // process uptime (10s simulated warm-up), which is timing-dependent and would
    // make this test either slow or flaky. Covered instead by the deployed pod's
    // Figure 8 verification in the documentation.

    [Fact]
    public async Task WeatherEndpoint_IsReachableThroughTheFullRequestPipeline()
    {
        var response = await _client.GetAsync("/api/Weather");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
