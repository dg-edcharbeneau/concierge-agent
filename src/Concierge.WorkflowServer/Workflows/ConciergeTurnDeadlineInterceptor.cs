using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;

namespace Concierge.WorkflowServer.Workflows;

/// <summary>Bounds durable AI activities, including queue time, to the admitted turn.</summary>
internal sealed class ConciergeTurnDeadlineInterceptor : IWorkerInterceptor
{
    public WorkflowInboundInterceptor InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor) =>
        new Inbound(nextInterceptor);

    private sealed class Inbound(WorkflowInboundInterceptor next) : WorkflowInboundInterceptor(next)
    {
        public override void Init(WorkflowOutboundInterceptor outbound) =>
            base.Init(new Outbound(outbound));
    }

    private sealed class Outbound(WorkflowOutboundInterceptor next) : WorkflowOutboundInterceptor(next)
    {
        public override Task<TResult> ScheduleActivityAsync<TResult>(ScheduleActivityInput input)
        {
            // 0.14.2's durable adapters expose only StartToClose. Intercept the SDK schedule
            // rather than duplicating the package's internal tool input/output contracts.
            if (input.Activity is "TemporalCommunity.Extensions.AI.InvokeFunction"
                    or "TemporalCommunity.Extensions.AI.GetResponse"
                && Workflow.Instance is ConciergeStayWorkflow { ActiveActionDeadline: { } deadline })
            {
                input = input with
                {
                    Options = ConciergeStayWorkflow.BoundActivityOptionsToDeadline(
                        input.Options, Workflow.UtcNow, deadline),
                };
            }

            return base.ScheduleActivityAsync<TResult>(input);
        }
    }
}
