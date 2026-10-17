using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Concierge.Contracts;
using Concierge.Web.Voice;
using Microsoft.Extensions.Logging;

namespace Concierge.Core.Tests;

public sealed class VoiceRelayLoggingTests
{
    private readonly CapturedLogs logs = new();
    private readonly VoiceConnection connection = new VoiceSessionRegistry(TimeProvider.System)
        .Open(new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), null);
    private readonly VoiceRelay relay;

    public VoiceRelayLoggingTests() =>
        relay = new VoiceRelay(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection,
            logs.CreateLogger("VoiceRelay"));

    [Fact]
    public void WelcomeLogsDeepgramRequestIdWithConnectionNumber()
    {
        Receive("""{"type":"Welcome","request_id":"fc553ec9-1d2b-4c1e-9b5a-3f1e2d3c4b5a"}""");

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("fc553ec9-1d2b-4c1e-9b5a-3f1e2d3c4b5a", entry.Values["DeepgramRequestId"]);
        Assert.Equal(connection.Number, entry.Values["ConnectionNumber"]);
    }

    [Fact]
    public void LatencyReportLogsEachTimingAndNullForOmittedFields()
    {
        Receive("""{"type":"LatencyReport","stt_latency":0.12,"ttt_token_latency":0.34,"ttt_text_latency":0.36,"tts_latency":0.18,"total_latency":0.64,"ttt_tool_latency":"x"}""");

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(connection.Number, entry.Values["ConnectionNumber"]);
        Assert.Equal(0.64, entry.Values["TotalLatencySeconds"]);
        Assert.Equal(0.12, entry.Values["SttLatencySeconds"]);
        Assert.Equal(0.34, entry.Values["TttTokenLatencySeconds"]);
        Assert.Equal(0.36, entry.Values["TttTextLatencySeconds"]);
        Assert.Null(entry.Values["TttToolLatencySeconds"]);
        Assert.Null(entry.Values["TttThinkingLatencySeconds"]);
        Assert.Equal(0.18, entry.Values["TtsLatencySeconds"]);
    }

    [Fact]
    public void ErrorAndWarningLogDeepgramDescription()
    {
        Receive("""{"type":"Error","code":"FAILED_TO_THINK","description":"All think providers failed."}""");
        Receive("""{"type":"Warning","code":"PROMPT_TOO_LONG","description":"The prompt was truncated."}""");

        Assert.Collection(
            logs.Entries,
            error =>
            {
                Assert.Equal(LogLevel.Warning, error.Level);
                Assert.Equal("FAILED_TO_THINK", error.Values["DeepgramCode"]);
                Assert.Equal("All think providers failed.", error.Values["DeepgramDescription"]);
            },
            warning =>
            {
                Assert.Equal(LogLevel.Information, warning.Level);
                Assert.Equal("PROMPT_TOO_LONG", warning.Values["DeepgramCode"]);
                Assert.Equal("The prompt was truncated.", warning.Values["DeepgramDescription"]);
            });
    }

    [Fact]
    public void RepeatedWarningCodeLogsOncePerUserTurnAndOtherCodesStillLog()
    {
        const string slow = """{"type":"Warning","code":"SLOW_THINK_REQUEST","description":"Think is slow."}""";
        Receive(slow);
        Receive(slow);
        Receive("""{"type":"Warning","code":"THINK_REQUEST_FAILED","description":"Retrying."}""");
        Receive(slow);
        Receive("""{"type":"AgentThinking","content":"thinking"}""");
        Receive(slow);

        Assert.Equal(
            ["SLOW_THINK_REQUEST", "THINK_REQUEST_FAILED"],
            logs.Entries.Select(entry => entry.Values["DeepgramCode"]));

        Receive("""{"type":"ConversationText","role":"user","content":"Private guest words"}""");
        Receive(slow);

        Assert.Equal(2, logs.Entries.Count(entry => Equals(entry.Values["DeepgramCode"], "SLOW_THINK_REQUEST")));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("Private guest words", StringComparison.Ordinal));
    }

    private void Receive(string json) => relay.HandleAgentEvent(Encoding.UTF8.GetBytes(json));
}

/// <summary>Captures rendered messages and structured template values.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> entries = new();

    public IReadOnlyList<CapturedLog> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    internal sealed record CapturedLog(
        string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            owner.entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values));
        }
    }
}
