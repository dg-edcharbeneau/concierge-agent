using Concierge.WorkflowServer.Location;
using Temporalio.Activities;

namespace Concierge.WorkflowServer.Activities;

/// <summary>Runs Geoapify driving-route lookups as retryable Temporal activities.</summary>
public sealed class DrivingRouteActivities(GeoapifyDrivingRouteService routeService)
{
    [Activity(nameof(GetRouteAsync))]
    public Task<DrivingRouteResult> GetRouteAsync(DrivingRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return routeService.GetRouteAsync(
            request,
            ActivityExecutionContext.Current.CancellationToken);
    }
}
