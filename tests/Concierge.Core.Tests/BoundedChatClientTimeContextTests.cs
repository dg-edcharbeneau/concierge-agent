using Concierge.WorkflowServer.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class BoundedChatClientTimeContextTests
{
    private static readonly TimeZoneInfo Hotel = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task ActionTurnsReceiveHotelLocalNowAfterTheStaticSystemPrompts()
    {
        var inner = new CapturingChatClient();
        using var client = new BoundedChatClient(
            inner,
            TimeSpan.FromSeconds(5),
            maxConcurrency: 1,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 10, 5, 0, TimeSpan.Zero)),
            Hotel);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "base"),
                new ChatMessage(ChatRole.System, "actions"),
                new ChatMessage(ChatRole.User, "Book a ride tomorrow at 10 am."),
            ],
            ActionOptions());

        var sent = Assert.Single(inner.Requests);
        Assert.Equal(["base", "actions"], sent.Take(2).Select(message => message.Text));
        Assert.Equal(ChatRole.System, sent[2].Role);
        Assert.Equal(
            "Hotel-local now: Tuesday 2026-10-20 06:05 (UTC-04:00). Resolve relative dates and times in hotel-local time and send pickupAt with that UTC offset.",
            sent[2].Text);
        Assert.Equal(ChatRole.User, sent[3].Role);
    }

    [Fact]
    public async Task NonActionTurnsAreSentUnchanged()
    {
        var inner = new CapturingChatClient();
        using var client = new BoundedChatClient(
            inner,
            TimeSpan.FromSeconds(5),
            maxConcurrency: 1,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 10, 5, 0, TimeSpan.Zero)),
            Hotel);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "base"), new ChatMessage(ChatRole.User, "Pool hours?")],
            new ChatOptions());

        Assert.Equal(["base", "Pool hours?"], Assert.Single(inner.Requests).Select(message => message.Text));
    }

    private static ChatOptions ActionOptions()
    {
        var options = new ChatOptions { AdditionalProperties = [] };
        options.AdditionalProperties[ConciergeToolDefinitions.ModelOptionKey] = "true";
        return options;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
