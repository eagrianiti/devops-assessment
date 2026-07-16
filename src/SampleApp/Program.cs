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

// Swagger enabled in all environments (useful for demoing this assessment project)
app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();  // serves wwwroot/index.html at "/"
app.UseStaticFiles();

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

// Makes the implicit Program class (generated from the top-level statements above)
// public instead of the default internal, so tests/SampleApp.Tests can reference it
// as WebApplicationFactory<Program> for in-process integration testing. This is the
// standard fix documented by Microsoft for testing minimal-API/top-level-statement
// apps -- InternalsVisibleTo alone isn't sufficient here because Program is used as a
// generic type argument in a public constructor (HealthEndpointsTests), which C#'s
// accessibility check (CS0051) evaluates independently of InternalsVisibleTo.
public partial class Program { }
