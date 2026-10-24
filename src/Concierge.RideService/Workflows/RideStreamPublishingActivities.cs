using Concierge.Contracts;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.WorkflowStreams;

namespace Concierge.RideService.Workflows;

public sealed class RideStreamPublishingActivities(
    ITemporalClient temporalClient,
    ILogger<RideStreamPublishingActivities> logger)
{
    [Activity(RideWorkflowNames.PublishRideEntryActivity)]
    public async Task PublishRideEntryAsync(RideActivityPublication publication)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            await using var streamClient = new WorkflowStreamClient(
                temporalClient,
                publication.StayWorkflowId,
                new WorkflowStreamClientOptions
                {
                    MaxRetryDuration = TimeSpan.FromSeconds(5),
                });

            streamClient
                .GetTopic<RideActivityEntry>(RideActivityFeed.Topic)
                .Publish(publication.Entry, forceFlush: true);
            await streamClient.FlushAsync(cancellationToken);

            // Keep the stay stream bounded. Publishing is acknowledged before reading the
            // global offset, and the stay clamps stale concurrent trim requests.
            var offset = await streamClient.GetOffsetAsync(cancellationToken);
            var truncateBefore = offset - RideActivityFeed.MaxRetainedEntries;
            if (truncateBefore > 0)
            {
                await temporalClient
                    .GetWorkflowHandle(publication.StayWorkflowId)
                    .SignalAsync(
                        ConciergeStayWorkflowIds.TruncateRideActivityStreamSignal,
                        [new TruncateRideActivityStreamRequest(truncateBefore)],
                        new WorkflowSignalOptions
                        {
                            Rpc = new RpcOptions { CancellationToken = cancellationToken },
                        });
            }
        }
        catch (Exception exception) when (IsClosedStay(exception))
        {
            logger.LogWarning(
                "Ride activity feed for closed stay workflow {StayWorkflowId} was dropped for ride {RideId}, sequence {Sequence}.",
                publication.StayWorkflowId,
                publication.Entry.RideId,
                publication.Entry.Sequence);
        }
    }

    private static bool IsClosedStay(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is RpcException { Code: RpcException.StatusCode.NotFound })
            {
                return true;
            }
        }

        return false;
    }
}
