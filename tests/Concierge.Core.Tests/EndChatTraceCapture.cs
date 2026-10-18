using System.Collections.Concurrent;
using System.Diagnostics;

namespace Concierge.Core.Tests;

internal sealed class EndChatTraceCapture : IDisposable
{
    private readonly ConcurrentQueue<Activity> activities = new();
    private readonly ActivityListener listener;

    public EndChatTraceCapture()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Concierge.Web",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
    }

    public Activity AssertEvent(string name, Guid sessionId)
    {
        var activity = Assert.Single(activities, activity =>
            activity.OperationName == name
            && Equals(activity.GetTagItem("concierge.voice.session_id"), sessionId.ToString()));
        Assert.True(activity.Recorded);
        var traceEvent = Assert.Single(activity.Events);
        Assert.Equal(name, traceEvent.Name);
        Assert.Equal(sessionId.ToString(), traceEvent.Tags.Single(tag => tag.Key == "concierge.voice.session_id").Value);
        return activity;
    }

    public void Dispose() => listener.Dispose();
}
