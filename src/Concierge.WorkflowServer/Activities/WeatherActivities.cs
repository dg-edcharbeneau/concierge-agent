using Concierge.WorkflowServer.Location;
using Temporalio.Activities;

namespace Concierge.WorkflowServer.Activities;

/// <summary>Runs external weather and place-provider calls as retryable Temporal activities.</summary>
public sealed class WeatherActivities(OpenMeteoWeatherService weatherService)
{
    [Activity(nameof(GetWeatherAsync))]
    public Task<WeatherResult> GetWeatherAsync(WeatherRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return weatherService.GetWeatherAsync(
            request,
            ActivityExecutionContext.Current.CancellationToken);
    }
}
