namespace Concierge.WorkflowServer.Setup;

public sealed record SetupReadinessSnapshot(
    string Status,
    string WorkflowId,
    string? Failure);

public sealed class SetupReadinessState
{
    private readonly object gate = new();
    private SetupReadinessSnapshot snapshot = new(
        "pending",
        SetupWorkflowIdentity.WorkflowId,
        null);

    public SetupReadinessSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    /// <summary>
    /// Records that the setup workflow has started.
    /// </summary>
    public void MarkRunning()
    {
        lock (gate)
        {
            snapshot = new SetupReadinessSnapshot(
                "running",
                SetupWorkflowIdentity.WorkflowId,
                null);
        }
    }

    /// <summary>
    /// Records that setup completed; the setup health check becomes healthy.
    /// </summary>
    public void MarkReady()
    {
        lock (gate)
        {
            snapshot = new SetupReadinessSnapshot(
                "ready",
                SetupWorkflowIdentity.WorkflowId,
                null);
        }
    }

    /// <summary>
    /// Records a setup failure. Only the exception type is exposed; details stay in logs and Temporal history.
    /// </summary>
    public void MarkFailed(Exception exception)
    {
        lock (gate)
        {
            snapshot = new SetupReadinessSnapshot(
                "failed",
                SetupWorkflowIdentity.WorkflowId,
                $"{exception.GetType().Name}; detailed diagnostics are available in Temporal history and WorkflowServer logs.");
        }
    }
}
