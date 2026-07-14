using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// --- Health checks: split into "liveness" and "readiness" tags ---
// Liveness = "is the process alive / not deadlocked" -> should almost never fail once started.
// Readiness = "can this instance safely receive traffic right now" -> can flip false/true during runtime
// (e.g. while a downstream dependency is unavailable), without the pod being restarted.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<DownstreamDependencyHealthCheck>("downstream_dependency", tags: new[] { "ready" });

builder.Services.AddSingleton<DownstreamDependencyHealthCheck>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();

// --- Probe endpoints ---
// /health/live  -> Kubernetes livenessProbe (process-level, cheap, no external calls)
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

// /health/ready -> Kubernetes readinessProbe (checks dependencies, DB, cache, etc.)
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// /health/startup -> Kubernetes startupProbe (used only during boot; disables liveness checks
// until the app has finished its (simulated) warm-up, so slow-starting containers aren't killed).
app.MapGet("/health/startup", () =>
{
    var upTime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
    var warmupSeconds = 10; // simulate a slow warm-up (e.g. JIT, cache priming)
    return upTime.TotalSeconds >= warmupSeconds
        ? Results.Ok(new { status = "started", upTimeSeconds = upTime.TotalSeconds })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();

// Simulates a dependency (e.g. DB/Redis) that can go unhealthy at runtime.
// In a real app this would ping the actual dependency (DB connection, Redis PING, etc.)
public class DownstreamDependencyHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Replace with a real check, e.g.:
        // await using var conn = new SqlConnection(connString);
        // await conn.OpenAsync(cancellationToken);
        return Task.FromResult(HealthCheckResult.Healthy("Downstream dependency reachable"));
    }
}
