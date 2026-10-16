using System.Diagnostics;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Worker.Interceptors;

namespace Concierge.Contracts;

/// <summary>
/// <see cref="TracingInterceptor"/> that also parents each workflow update's work to the caller's
/// trace. The base interceptor parents update handlers to the workflow's start trace and only links
/// the caller, so a long-lived stay would spread every guest turn across unrelated traces. Here the
/// <c>UpdateWorkflow</c> client span parents a <c>HandleUpdate</c> span and the activities it schedules.
/// </summary>
internal sealed class UpdateTracingInterceptor : TracingInterceptor, IWorkerInterceptor
{
    /// <summary>The base workflow interceptor stays outermost; this one runs inside it.</summary>
    WorkflowInboundInterceptor IWorkerInterceptor.InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor) =>
        InterceptWorkflow(new UpdateInbound(this, nextInterceptor));

    private sealed class UpdateInbound(UpdateTracingInterceptor root, WorkflowInboundInterceptor next)
        : WorkflowInboundInterceptor(next)
    {
        public override async Task<object?> HandleUpdateAsync(HandleUpdateInput input)
        {
            if (root.HeadersToContext(input.Headers) is not { } caller)
            {
                return await base.HandleUpdateAsync(input);
            }

            // Both helpers are replay-safe: no span is emitted while replaying.
            using var attached = WorkflowDiagnosticActivity.AttachFromContext(caller.ActivityContext);
            using var handler = WorkflowsSource.TrackWorkflowDiagnosticActivity(
                "HandleUpdate:" + input.Update,
                ActivityKind.Server,
                [.. root.CreateInWorkflowTags(), new(root.Options.TagNameUpdateId ?? "temporalUpdateID", input.Id)],
                inheritParentTags: false);
            return await base.HandleUpdateAsync(input);
        }
    }
}
