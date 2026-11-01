using Concierge.Contracts;
using Concierge.RideService.Data;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Concierge.RideService.Workflows;

[Workflow(RideWorkflowNames.BookingWorkflow)]
public sealed class RideBookingWorkflow
{
    // Admission and guest cancellation run inside a WorkflowServer HTTP call that is sent once and
    // gives up after 10 seconds (ConciergeActionToolService.RideRequestTotalTimeout). Cap their
    // store activity so the update finishes inside that budget; the idempotent store (keyed by
    // idempotency key / ride id) makes the activity retries safe. Provisional until measured.
    internal static readonly ActivityOptions StoreActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(4),
        ScheduleToCloseTimeout = TimeSpan.FromSeconds(8),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(200),
            MaximumInterval = TimeSpan.FromSeconds(2),
            MaximumAttempts = 5,
        },
    };

    internal static readonly ActivityOptions CancelActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(4),
        ScheduleToCloseTimeout = TimeSpan.FromSeconds(8),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(200),
            MaximumInterval = TimeSpan.FromSeconds(2),
            MaximumAttempts = 5,
        },
    };

    // After a failed admission store, wait out any in-flight attempt before reading the database.
    // StoreBookingAsync bounds itself to its attempt and schedule deadlines, so this grace only
    // covers commit/rollback latency. Provisional until measured.
    internal static readonly TimeSpan ReconcileGrace = TimeSpan.FromSeconds(2);

    // Reconciliation runs in the workflow after the admission update has already failed, so no
    // HTTP caller waits on it. Bounded so the workflow never waits indefinitely.
    internal static readonly ActivityOptions ReconcileActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(5),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(500),
            MaximumInterval = TimeSpan.FromSeconds(10),
        },
    };

    // Background lifecycle transitions have no HTTP caller waiting; keep their longer budget.
    private static readonly ActivityOptions TransitionActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(200),
            MaximumInterval = TimeSpan.FromSeconds(2),
            MaximumAttempts = 5,
        },
    };

    private static readonly ActivityOptions PublicationActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(10),
        ScheduleToCloseTimeout = TimeSpan.FromSeconds(20),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(250),
            MaximumInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2,
        },
    };

    private static readonly FakeDriver AssignedDriver =
        new("Alex Rivera", "ZGY Sedan", "ZGY-4821");

    private bool admissionFinished;
    private bool admissionUncertain;
    private bool admissionAbandoned;
    private bool bookedEntryPublicationFinished;
    private bool statusTransitionInProgress;
    private RideBookingCommand? admissionCommand;
    private RideResponse? booking;
    private string status = "Admitting";

    [WorkflowRun]
    public async Task RunAsync()
    {
        await Workflow.WaitConditionAsync(() => admissionFinished || admissionUncertain);
        if (!admissionFinished && !await ReconcileAdmissionAsync())
        {
            // Nothing was stored: close cleanly instead of waiting forever. Any caller already
            // received a failed admission (reported as an unknown outcome); nothing is resent.
            await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
            return;
        }

        var ride = booking
            ?? throw new InvalidOperationException("Ride admission completed without a booking.");

        await PublishBestEffortAsync(CreateEntry(ride, 1, "Booked", Workflow.UtcNow));
        bookedEntryPublicationFinished = true;
        if (IsTerminal)
        {
            return;
        }

        var pickupAtUtc = ride.PickupAt.ToUniversalTime();
        var assignAtUtc = ride.Immediate ? Workflow.UtcNow : pickupAtUtc.AddMinutes(-10);
        await WaitUntilAsync(assignAtUtc);
        if (IsTerminal)
        {
            return;
        }

        await ExecuteTransitionAndPublishAsync(new RideTransitionCommand(
            ride.Id,
            "Booked",
            "DriverAssigned",
            2,
            Workflow.UtcNow,
            AssignedDriver.Name,
            AssignedDriver.Car,
            AssignedDriver.Plate));

        if (IsTerminal)
        {
            return;
        }

        await WaitUntilAsync(pickupAtUtc);
        if (IsTerminal)
        {
            return;
        }

        var pickedUpAtUtc = Workflow.UtcNow;
        await ExecuteTransitionAndPublishAsync(new RideTransitionCommand(
            ride.Id,
            "DriverAssigned",
            "PickedUp",
            3,
            pickedUpAtUtc));

        if (IsTerminal)
        {
            return;
        }

        var completionAtUtc = pickupAtUtc.AddSeconds(ride.RouteDurationSeconds);
        await WaitUntilAsync(completionAtUtc);
        if (IsTerminal)
        {
            return;
        }

        await ExecuteTransitionAndPublishAsync(new RideTransitionCommand(
            ride.Id,
            "PickedUp",
            "Completed",
            4,
            Workflow.UtcNow));
    }

    [WorkflowUpdate(RideWorkflowNames.AdmitBookingUpdate)]
    public async Task<RideResponse> AdmitBookingAsync(RideBookingCommand command)
    {
        if (admissionFinished)
        {
            return booking!;
        }

        if (admissionUncertain)
        {
            await Workflow.WaitConditionAsync(() => admissionFinished || admissionAbandoned);
            return admissionFinished
                ? booking!
                : throw new ApplicationFailureException(
                    "The ride was not admitted.",
                    errorType: "RideNotAdmitted",
                    nonRetryable: true);
        }

        admissionCommand = command;
        try
        {
            booking = await Workflow.ExecuteActivityAsync(
                (RideBookingActivities activities) => activities.StoreBookingAsync(command),
                StoreActivityOptions);
        }
        catch (ActivityFailureException)
        {
            // A timed-out attempt may still have committed. Fail this update (the caller reports
            // an unknown outcome and never resends) and let RunAsync reconcile from the database.
            admissionUncertain = true;
            throw;
        }

        status = booking.Status;
        admissionFinished = true;
        return booking;
    }

    [WorkflowUpdate(RideWorkflowNames.CancelRideUpdate)]
    public async Task<RideCancellationResult> CancelAsync()
    {
        await Workflow.WaitConditionAsync(() => admissionFinished || admissionAbandoned);
        if (!admissionFinished)
        {
            return new RideCancellationResult(false, status, null);
        }

        await Workflow.WaitConditionAsync(() => bookedEntryPublicationFinished);
        await Workflow.WaitConditionAsync(() => !statusTransitionInProgress);
        if (status == "Cancelled")
        {
            return new RideCancellationResult(true, status, null);
        }

        if (status is "PickedUp" or "Completed")
        {
            return new RideCancellationResult(false, status, null);
        }

        var result = await Workflow.ExecuteActivityAsync(
            (RideBookingActivities activities) => activities.CancelAsync(
                booking!.Id,
                Workflow.UtcNow),
            CancelActivityOptions);
        status = result.CurrentStatus;
        if (result.Applied && result.Entry is { } entry)
        {
            await PublishBestEffortAsync(entry);
        }

        return result;
    }

    private bool IsTerminal => status is "Cancelled" or "Completed";

    private async Task<bool> ReconcileAdmissionAsync()
    {
        var command = admissionCommand
            ?? throw new InvalidOperationException("Admission failed without a command.");
        await Workflow.DelayAsync(ReconcileGrace);
        var stored = await Workflow.ExecuteActivityAsync(
            (RideBookingActivities activities) => activities.ReconcileBookingAsync(command),
            ReconcileActivityOptions);
        if (stored is null)
        {
            status = "NotAdmitted";
            admissionAbandoned = true;
            return false;
        }

        booking = stored;
        status = stored.Status;
        admissionFinished = true;
        return true;
    }

    private async Task ExecuteTransitionAndPublishAsync(RideTransitionCommand command)
    {
        statusTransitionInProgress = true;
        try
        {
            var result = await Workflow.ExecuteActivityAsync(
                (RideBookingActivities activities) => activities.TransitionAsync(command),
                TransitionActivityOptions);
            status = result.CurrentStatus;
            if (result.Applied
                && string.Equals(result.CurrentStatus, command.TargetStatus, StringComparison.Ordinal)
                && result.Entry is { } entry)
            {
                await PublishBestEffortAsync(entry);
            }
        }
        finally
        {
            statusTransitionInProgress = false;
        }
    }

    private async Task WaitUntilAsync(DateTimeOffset absoluteTimeUtc)
    {
        var remaining = absoluteTimeUtc - Workflow.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Workflow.WaitConditionAsync(() => IsTerminal, remaining);
        }
    }

    private async Task PublishBestEffortAsync(RideActivityEntry entry)
    {
        var ride = booking!;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (RideStreamPublishingActivities activities) =>
                    activities.PublishRideEntryAsync(
                        new RideActivityPublication(ride.StayWorkflowId, entry)),
                PublicationActivityOptions);
        }
        catch (ActivityFailureException exception)
        {
            Workflow.Logger.LogWarning(
                exception,
                "Ride activity feed publication failed for ride {RideId}, sequence {Sequence}; lifecycle continues.",
                ride.Id,
                entry.Sequence);
        }
    }

    private static RideActivityEntry CreateEntry(
        RideResponse ride,
        int sequence,
        string targetStatus,
        DateTimeOffset changedAtUtc) =>
        new(
            ride.Id,
            sequence,
            targetStatus,
            changedAtUtc.ToUniversalTime(),
            ride.PickupName,
            ride.DropoffName,
            ride.PickupAt.ToUniversalTime(),
            targetStatus == "DriverAssigned" ? AssignedDriver.Name : null,
            targetStatus == "DriverAssigned" ? AssignedDriver.Car : null,
            targetStatus == "DriverAssigned" ? AssignedDriver.Plate : null,
            RideArrivalEstimate.Calculate(
                ride.PickupAt, ride.RouteDurationSeconds, ride.PickedUpAtUtc, ride.CompletedAtUtc)?.ToUniversalTime());

    private sealed record FakeDriver(string Name, string Car, string Plate);
}

public sealed class RideBookingActivities(
    RideRepository repository,
    RideStreamPublishingActivities publisher)
{
    [Activity(RideWorkflowNames.StoreBookingActivity)]
    public async Task<RideResponse> StoreBookingAsync(RideBookingCommand booking)
    {
        // Stop (and roll back) before Temporal times this attempt or the schedule out, so no
        // attempt can commit after the workflow has started reconciling.
        var context = ActivityExecutionContext.Current;
        var remaining = RemainingAttemptTime(context.Info, DateTime.UtcNow);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (remaining is { } allowance)
        {
            bounded.CancelAfter(allowance);
        }

        try
        {
            return await repository.PersistBookingAsync(booking, bounded.Token);
        }
        catch (OperationCanceledException) when (
            bounded.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Ride booking store exceeded its attempt deadline.");
        }
    }

    [Activity(RideWorkflowNames.ReconcileBookingActivity)]
    public Task<RideResponse?> ReconcileBookingAsync(RideBookingCommand booking) =>
        repository.FindAdmittedBookingAsync(
            booking,
            ActivityExecutionContext.Current.CancellationToken);

    internal static readonly TimeSpan StoreDeadlineMargin = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan? RemainingAttemptTime(ActivityInfo info, DateTime utcNow)
    {
        DateTime? deadline = null;
        if (info.StartToCloseTimeout is { } startToClose && startToClose > TimeSpan.Zero)
        {
            deadline = info.StartedTime + startToClose;
        }

        if (info.ScheduleToCloseTimeout is { } scheduleToClose && scheduleToClose > TimeSpan.Zero)
        {
            var scheduleDeadline = info.ScheduledTime + scheduleToClose;
            deadline = deadline is null || scheduleDeadline < deadline ? scheduleDeadline : deadline;
        }

        if (deadline is null)
        {
            return null;
        }

        var remaining = deadline.Value - utcNow - StoreDeadlineMargin;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    [Activity(RideWorkflowNames.TransitionRideActivity)]
    public Task<RideTransitionResult> TransitionAsync(RideTransitionCommand command) =>
        repository.TransitionAsync(
            command,
            ActivityExecutionContext.Current.CancellationToken);

    [Activity(RideWorkflowNames.CancelRideActivity)]
    public async Task<RideCancellationResult> CancelAsync(Guid rideId, DateTimeOffset cancelledAtUtc)
    {
        var result = await repository.CancelAsync(
            rideId,
            cancelledAtUtc,
            ActivityExecutionContext.Current.CancellationToken);
        if (result.Applied && result.Entry is { } entry)
        {
            // Returning terminal status wakes RunAsync, which can close before the update's
            // publication starts. Publish the committed event before that wake-up, without
            // changing workflow command order. Activity retries recover the same stored event;
            // consumers deduplicate this and the update's publication by ride/sequence.
            await publisher.PublishRideEntryAsync(new RideActivityPublication(
                result.StayWorkflowId
                    ?? throw new InvalidOperationException("Cancelled ride has no owning stay workflow."),
                entry));
        }

        return result;
    }
}

public sealed record RideActivityPublication(string StayWorkflowId, RideActivityEntry Entry);
