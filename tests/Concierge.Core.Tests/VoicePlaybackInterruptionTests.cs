using System.Net.WebSockets;
using System.Text;
using Concierge.Contracts;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Concierge.WorkflowServer.Workflows;

namespace Concierge.Core.Tests;

public sealed class VoicePlaybackInterruptionTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task BrowserRenderedBargeInMarksTheMappedPersistedVoiceTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (session, handle) = await StartVoiceStayAsync(host);
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-target", "Target question");
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-other", "Other question");

        await using var scope = host.App.Services.CreateAsyncScope();
        var registry = new VoiceSessionRegistry(TimeProvider.System);
        var connection = registry.Open(session, null);
        connection.Session.RegisterReplyForPlayback("voice-target");
        var relay = NewRelay(connection, scope.ServiceProvider.GetRequiredService<IConciergeService>());

        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant"}"""));
        Assert.Equal("voice-target", connection.Session.ActiveReplyPlaybackToken);
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));
        await relay.HandleInterruptionSilencedAsync(
            relay.ActiveInterruptionNumber, bargeIn: true, playbackToken: "voice-target");

        Assert.True(await ReadInterruptedAsync(host, session.StayId, "voice-target"));
        Assert.False(await ReadInterruptedAsync(host, session.StayId, "voice-other"));
        Assert.Equal("Answer: Target question", await ReadAnswerAsync(host, session.StayId, "voice-target"));
    }

    [Fact]
    public async Task FalseBargeInAcknowledgementDoesNotMarkTheVoiceTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (session, handle) = await StartVoiceStayAsync(host);
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-false", "Question");

        await using var scope = host.App.Services.CreateAsyncScope();
        var connection = new VoiceSessionRegistry(TimeProvider.System).Open(session, null);
        connection.Session.RegisterReplyForPlayback("voice-false");
        var relay = NewRelay(connection, scope.ServiceProvider.GetRequiredService<IConciergeService>());
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant"}"""));
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));

        await relay.HandleInterruptionSilencedAsync(
            relay.ActiveInterruptionNumber, bargeIn: false, playbackToken: "voice-false");

        Assert.False(await ReadInterruptedAsync(host, session.StayId, "voice-false"));
    }

    [Fact]
    public async Task OrdinarySpeechWithoutAConfirmedAcknowledgementDoesNotMarkTheVoiceTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (session, handle) = await StartVoiceStayAsync(host);
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-speech-only", "Question");

        await using var scope = host.App.Services.CreateAsyncScope();
        var connection = new VoiceSessionRegistry(TimeProvider.System).Open(session, null);
        connection.Session.RegisterReplyForPlayback("voice-speech-only");
        var relay = NewRelay(connection, scope.ServiceProvider.GetRequiredService<IConciergeService>());
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant"}"""));
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));

        Assert.False(await ReadInterruptedAsync(host, session.StayId, "voice-speech-only"));
    }

    [Fact]
    public async Task BargeInAcknowledgementWithAnUnknownTokenDoesNotMarkAnotherTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (session, handle) = await StartVoiceStayAsync(host);
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-not-playing", "Question");
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-known", "Another question");

        await using var scope = host.App.Services.CreateAsyncScope();
        var connection = new VoiceSessionRegistry(TimeProvider.System).Open(session, null);
        connection.Session.RegisterReplyForPlayback("voice-known");
        var relay = NewRelay(connection, scope.ServiceProvider.GetRequiredService<IConciergeService>());
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant"}"""));
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));

        await relay.HandleInterruptionSilencedAsync(
            relay.ActiveInterruptionNumber, bargeIn: true, playbackToken: "voice-not-playing");

        Assert.False(await ReadInterruptedAsync(host, session.StayId, "voice-not-playing"));
        Assert.False(await ReadInterruptedAsync(host, session.StayId, "voice-known"));
    }

    [Fact]
    public async Task DuplicateBargeInAcknowledgementIsSafeAndRaceAcrossReconnectMarksThePlayingTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (session, handle) = await StartVoiceStayAsync(host);
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-playing", "First question");
        await PersistVoiceTurnAsync(handle, session.StayId, "voice-pending", "Second question");

        await using var scope = host.App.Services.CreateAsyncScope();
        var registry = new VoiceSessionRegistry(TimeProvider.System);
        var firstConnection = registry.Open(session, null);
        firstConnection.Session.RegisterReplyForPlayback("voice-playing");
        var firstRelay = NewRelay(firstConnection, scope.ServiceProvider.GetRequiredService<IConciergeService>());
        firstRelay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant"}"""));

        // The next Think result can arrive while the old reply is still the rendered playback.
        firstConnection.Session.RegisterReplyForPlayback("voice-pending");
        var reconnected = registry.Open(session, firstConnection.Session.Id);
        var relay = NewRelay(reconnected, scope.ServiceProvider.GetRequiredService<IConciergeService>());
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));
        var interruptionNumber = relay.ActiveInterruptionNumber;

        await relay.HandleInterruptionSilencedAsync(interruptionNumber, bargeIn: true, playbackToken: "voice-playing");
        await relay.HandleInterruptionSilencedAsync(interruptionNumber, bargeIn: true, playbackToken: "voice-playing");

        Assert.True(await ReadInterruptedAsync(host, session.StayId, "voice-playing"));
        Assert.False(await ReadInterruptedAsync(host, session.StayId, "voice-pending"));
    }

    [Fact]
    public async Task RenderedAudioTokensMarkEachInterruptedReplyDespiteLateAudioDoneEvents()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (session, handle) = await StartVoiceStayAsync(host);
        var turnIds = new[] { "voice-barge-1", "voice-barge-2", "voice-barge-3", "voice-barge-4" };
        for (var index = 0; index < turnIds.Length; index++)
        {
            await PersistVoiceTurnAsync(handle, session.StayId, turnIds[index], $"Question {index + 1}");
        }

        await using var scope = host.App.Services.CreateAsyncScope();
        var connection = new VoiceSessionRegistry(TimeProvider.System).Open(session, null);
        var relay = NewRelay(connection, scope.ServiceProvider.GetRequiredService<IConciergeService>());

        for (var index = 0; index < turnIds.Length; index++)
        {
            var turnId = turnIds[index];
            connection.Session.RegisterReplyForPlayback(turnId);
            relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant"}"""));
            Assert.Equal(turnId, connection.Session.ActiveReplyPlaybackToken);

            if (index > 0)
            {
                // A previous response may finish after the next response has become active.
                relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"AgentAudioDone"}"""));
                Assert.Equal(turnId, connection.Session.ActiveReplyPlaybackToken);
            }

            relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));
            var ack = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "InterruptionSilenced",
                interruptionNumber = relay.ActiveInterruptionNumber,
                bargeIn = true,
                playbackToken = turnId,
            });
            Assert.True(VoiceRelay.TryReadInterruptionSilenced(
                ack, out var interruptionNumber, out var bargeIn, out var playbackToken));
            await relay.HandleInterruptionSilencedAsync(interruptionNumber, bargeIn, playbackToken);

            relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"user"}"""));
        }

        foreach (var turnId in turnIds)
        {
            Assert.True(await ReadInterruptedAsync(host, session.StayId, turnId));
        }
    }

    private static async Task<(ConciergeSessionContext Session, WorkflowHandle<ConciergeStayWorkflow> Handle)>
        StartVoiceStayAsync(ConciergeTestHost host)
    {
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Guest", "101", today, today.AddDays(1));
        var deadline = ConciergeTestHost.HotelMidnightUtc(today.AddDays(2));
        var session = new ConciergeSessionContext(
            Guid.NewGuid(),
            stayId,
            deadline,
            ConciergeTestHost.VerifiedGuest(stayId));
        return (session, await host.StartStayAsync(stayId, deadline));
    }

    private static async Task PersistVoiceTurnAsync(
        WorkflowHandle<ConciergeStayWorkflow> handle,
        Guid stayId,
        string turnId,
        string question)
    {
        await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(turnId, question, "voice", ConciergeTestHost.VerifiedGuest(stayId))));
        await handle.ExecuteUpdateAsync(workflow => workflow.EndInteractionAsync());
    }

    private static VoiceRelay NewRelay(VoiceConnection connection, IConciergeService concierge) =>
        new(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection,
            NullLogger.Instance,
            markVoiceTurnInterrupted: (turnId, cancellationToken) =>
                concierge.MarkVoiceTurnInterruptedAsync(connection.Session.Stay, turnId, cancellationToken));

    private static async Task<bool?> ReadInterruptedAsync(
        ConciergeTestHost host,
        Guid stayId,
        string turnId)
    {
        await using var command = host.DataSource.CreateCommand(
            "SELECT interrupted FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turnId);
        return await command.ExecuteScalarAsync() as bool?;
    }

    private static async Task<string?> ReadAnswerAsync(
        ConciergeTestHost host,
        Guid stayId,
        string turnId)
    {
        await using var command = host.DataSource.CreateCommand(
            "SELECT assistant_message FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turnId);
        return await command.ExecuteScalarAsync() as string;
    }
}
