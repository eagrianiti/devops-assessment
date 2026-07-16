using Microsoft.Extensions.Logging.Abstractions;
using SampleApp.Controllers;
using Xunit;

namespace SampleApp.Tests;

public class WeatherControllerTests
{
    private static WeatherController CreateController() =>
        new(NullLogger<WeatherController>.Instance);

    [Fact]
    public void Get_ReturnsExactlyFiveForecasts()
    {
        var result = CreateController().Get().ToList();

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Get_ForecastsAreForTomorrowThroughFiveDaysOut()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var result = CreateController().Get().ToList();

        // Each forecast should be strictly in the future, and dates should be
        // in ascending, non-repeating order (index 1..5 days out).
        var previousDate = today;
        foreach (var forecast in result)
        {
            Assert.True(forecast.Date > previousDate);
            previousDate = forecast.Date;
        }
    }

    [Fact]
    public void Get_TemperatureCIsWithinTheDocumentedRange()
    {
        // WeatherController generates TemperatureC via Random.Shared.Next(-20, 55),
        // whose upper bound is exclusive.
        var result = CreateController().Get().ToList();

        Assert.All(result, forecast =>
        {
            Assert.InRange(forecast.TemperatureC, -20, 54);
        });
    }

    [Fact]
    public void Get_SummariesComeFromTheKnownList()
    {
        var validSummaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild",
            "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };

        var result = CreateController().Get().ToList();

        Assert.All(result, forecast => Assert.Contains(forecast.Summary, validSummaries));
    }
}
