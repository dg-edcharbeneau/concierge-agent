using System.Reflection;
using Concierge.Contracts;
using Concierge.Web.Components.Pages;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Concierge.Core.Tests;

public sealed class HomeSessionTests
{
    /// <summary>Preserves retry IDs and drafts only for the verified original stay.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverificationOnlyRecoversTheSameStay(bool sameStay)
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("old", stay);
        harness.Set("pending", new PendingTurnState("retry-id", "Old guest message", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Old guest draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", sameStay ? stay : Guid.NewGuid());
        await harness.Call("VerifyAsync");

        Assert.Equal(sameStay ? "Old guest draft" : "", harness.Get<string>("draft"));
        Assert.Equal(sameStay ? 1 : 0, harness.Service.Submissions.Count);
        if (sameStay)
        {
            Assert.Equal("retry-id", Assert.Single(harness.Service.Submissions).TurnId);
        }
    }

    /// <summary>Rejects both successful and unauthorized history from an earlier sign-in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldHistoryCannotChangeANewSession(bool unauthorized)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = credential => credential == "old"
            ? response.Task
            : Task.FromResult(History("new-turn"));
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        await harness.Call("VerifyAsync");
        if (unauthorized)
        {
            response.SetException(new ConciergeApiException("unauthorized", "Expired", 401));
        }
        else
        {
            response.SetResult(History("old-turn"));
        }
        await oldRead;

        Assert.Equal("new-turn", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
        Assert.False(harness.Get<bool>("hasExpired"));
    }

    /// <summary>Prevents a history response from repopulating an ended interaction.</summary>
    [Fact]
    public async Task EndInvalidatesOutstandingHistory()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = _ => response.Task;
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.Call("EndInteractionAsync");
        response.SetResult(History("late-turn"));
        await oldRead;
        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
        Assert.True(harness.Get<bool>("chatEnded"));
    }

    /// <summary>Rejects an earlier credential's turn completion and failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSubmissionCannotChangeANewSession(bool unauthorized)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        harness.Set("pending", new PendingTurnState("old-turn", "Old", "web", DateTimeOffset.UtcNow));
        var response = new TaskCompletionSource<ConciergeTurnResult>();
        var submitted = false;
        harness.Service.Submission = request =>
        {
            if (submitted) return Task.FromResult(new ConciergeTurnResult(request.TurnId, "Reply", 1));
            submitted = true;
            return response.Task;
        };
        var oldSubmit = harness.Call("SubmitPendingAsync");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        await harness.Call("VerifyAsync");
        if (unauthorized)
        {
            response.SetException(new ConciergeApiException("unauthorized", "Expired", 401));
        }
        else
        {
            response.SetResult(new ConciergeTurnResult("old-turn", "Old reply", 1));
        }
        await oldSubmit;
        Assert.False(harness.Get<bool>("hasExpired"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.Null(harness.Get<object?>("turnError"));
    }

    /// <summary>Reconnects the replaced transcript on cancel and disconnects on end.</summary>
    [Fact]
    public async Task CancelRewiresTranscriptAndEndDisconnectsIt()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        harness.Set("draft", "Keep this");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(1, harness.JavaScript.Calls.Count(call => call == "conciergeChat.wireTranscript"));
        await harness.Call("EndChatAsync");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Contains("conciergeChat.unwireTranscript", harness.JavaScript.Calls);
        await harness.Call("CancelEndChat");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(2, harness.JavaScript.Calls.Count(call => call == "conciergeChat.wireTranscript"));
        await harness.Call("EndInteractionAsync");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(2, harness.JavaScript.Calls.Count(call => call == "conciergeChat.unwireTranscript"));
    }

    /// <summary>Does not accept verification or end responses after another sign-in starts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSessionTransitionCannotReplaceANewSession(bool ending)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var verification = new TaskCompletionSource<ConciergeSessionResponse>();
        var end = new TaskCompletionSource();
        harness.Service.Verify = _ => verification.Task;
        harness.Service.End = _ => end.Task;
        harness.Set("lastName", "Old");
        harness.Set("roomNumber", "101");
        var oldTransition = harness.Call(ending ? "EndInteractionAsync" : "VerifyAsync");
        await harness.Call("VerifyAgain");
        harness.Service.Verify = null;
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        harness.Set("lastName", "New");
        harness.Set("roomNumber", "102");
        await harness.Call("VerifyAsync");
        verification.SetResult(SessionResponse("stale", Guid.NewGuid()));
        end.SetResult();
        await oldTransition;
        Assert.Equal("new", harness.Get<ChatSessionState>("session").Credential);
        Assert.False(harness.Get<bool>("chatEnded"));
        Assert.False(harness.Get<bool>("isVerifying"));
        Assert.False(harness.Get<bool>("isEnding"));
    }

    /// <summary>Drops legacy unbound recovery even if verification cannot supply an identity.</summary>
    [Fact]
    public async Task UnknownStayIdentityNeverReplaysPending()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.Empty);
        harness.Set("pending", new PendingTurnState("old-turn", "Old", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Old draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.Empty);
        await harness.Call("VerifyAsync");
        Assert.Empty(harness.Service.Submissions);
        Assert.Equal("", harness.Get<string>("draft"));
    }

    /// <summary>Reconciliation clears an already committed same-stay retry without resubmitting.</summary>
    [Fact]
    public async Task SameStayCommittedPendingIsNotResubmitted()
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("old", stay);
        harness.Set("pending", new PendingTurnState("committed", "Old", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Keep draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", stay);
        harness.Service.History = _ => Task.FromResult(History("committed"));
        await harness.Call("VerifyAsync");
        Assert.Empty(harness.Service.Submissions);
        Assert.Null(harness.Get<object?>("pending"));
        Assert.Equal("Keep draft", harness.Get<string>("draft"));
    }

    /// <summary>Offers same-ID recovery after failed End discards an outstanding reply callback.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedEndKeepsPendingReplyRecoverable(bool replyCompleted, bool unexpectedFailure)
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var pending = new PendingTurnState("pending-id", "Original message", "web", DateTimeOffset.UtcNow);
        harness.Set("pending", pending);
        harness.Set("draft", "Separate draft");
        var response = new TaskCompletionSource<ConciergeTurnResult>();
        harness.Service.Submission = _ => response.Task;
        var submission = harness.Call("SubmitPendingAsync");
        await harness.Call("EndChatAsync");
        harness.Service.End = _ => Task.FromException(unexpectedFailure
            ? new InvalidOperationException("Connection failed")
            : new ConciergeApiException("unavailable", "End unavailable", 503));
        await harness.Call("EndInteractionAsync");
        if (replyCompleted)
        {
            response.SetResult(new ConciergeTurnResult(pending.TurnId, "Reply", 1));
            await submission;
        }

        await harness.Call("CancelEndChat");
        Assert.False(harness.Get<bool>("showEndConfirmation"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.False(harness.Get<bool>("isEnding"));
        Assert.Contains("Retry", harness.Get<string>("turnError"));
        Assert.Equal(pending, harness.Get<PendingTurnState>("pending"));
        Assert.Equal("Separate draft", harness.Get<string>("draft"));

        harness.Service.History = _ => Task.FromResult(replyCompleted
            ? History(pending.TurnId)
            : new ConciergeHistoryResult([]));
        var retry = harness.Call("RetryAsync");
        if (!replyCompleted)
        {
            Assert.Equal(2, harness.Service.Submissions.Count);
            harness.Service.History = _ => Task.FromResult(History(pending.TurnId));
            response.SetResult(new ConciergeTurnResult(pending.TurnId, "Reply", 1));
            await submission;
        }
        await retry;

        Assert.All(harness.Service.Submissions, request =>
            Assert.Equal(new ConciergeTurnRequest(pending.TurnId, pending.UserMessage, pending.Channel), request));
        Assert.Equal(replyCompleted ? 1 : 2, harness.Service.Submissions.Count);
        Assert.Null(harness.Get<object?>("pending"));
        Assert.Null(harness.Get<object?>("turnError"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.Equal("Separate draft", harness.Get<string>("draft"));
        Assert.Equal(pending.TurnId, Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    private static ConciergeSessionResponse SessionResponse(string credential, Guid stay)
        => new(credential, DateTimeOffset.UtcNow.AddHours(1), stay);

    private static ConciergeHistoryResult History(string id) =>
        new([new ConciergeTranscriptTurn(id, 1, "web", "Guest", "Reply", DateTimeOffset.UtcNow)]);

#pragma warning disable BL0006 // The fixture intentionally uses Blazor's renderer instead of a new test dependency.
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Home home = new();
        private readonly TestRenderer renderer = new();
        public FakeService Service { get; } = new();
        public FakeJavaScript JavaScript { get; } = new();

        /// <summary>Attaches Home without triggering implicit browser callbacks.</summary>
        public Harness()
        {
            Set("Concierge", Service);
            Set("JavaScript", JavaScript);
            Set("ready", true);
            renderer.Attach(home);
        }

        /// <summary>Seeds a verified session for the test.</summary>
        public void SetSession(string credential, Guid stay)
        {
            var session = new ChatSessionState { Credential = credential, StayId = stay, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
            Set("session", session);
        }

        /// <summary>Sets private component state without adding production test surfaces.</summary>
        public void Set(string name, object value)
        {
            var property = typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (property?.SetMethod is not null)
            {
                property.SetValue(home, value);
                return;
            }

            if (property is not null)
            {
                var transition = name switch
                {
                    "session" => ("Restore", new[] { value, (object)DateTimeOffset.UtcNow }),
                    "pending" => ("StartTurn", new[] { value, (object)false }),
                    "draft" => ("SetDraft", new[] { value }),
                    _ => throw new InvalidOperationException($"No state transition is available for '{name}'.")
                };
                var modelField = typeof(Home).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var model = modelField.GetValue(home)!;
                var modelTransition = model.GetType().GetMethod(transition.Item1)!;
                modelField.SetValue(home, modelTransition.Invoke(model, transition.Item2));
                return;
            }

            typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, value);
        }

        /// <summary>Reads private state for focused assertions.</summary>
        public T Get<T>(string name)
        {
            var property = typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            var value = property is not null
                ? property.GetValue(home)
                : typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home);
            return (T)value!;
        }

        /// <summary>Runs a component event on its renderer dispatcher with a bounded timeout.</summary>
        public Task Call(string name, params object[] args) =>
            renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, args);
                if (result is Task task) await task;
            }).WaitAsync(TimeSpan.FromSeconds(10));

        /// <summary>Disposes the attached component and renderer.</summary>
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }

    // Suppress automatic after-render callbacks so each interop lifecycle step is deterministic.
    private sealed class TestRenderer() : Renderer(new EmptyServices(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        /// <summary>Attaches the component without an initial render.</summary>
        public void Attach(IComponent component) => AssignRootComponentId(component);
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.FromCanceled(new CancellationToken(true));
    }
#pragma warning restore BL0006

    private sealed class EmptyServices : IServiceProvider
    {
        /// <summary>Provides no services; test dependencies are injected explicitly.</summary>
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeJavaScript : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        /// <summary>Records browser interop with default cancellation.</summary>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        /// <summary>Records browser interop and returns a deterministic scroll position.</summary>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult(identifier == "conciergeChat.isNearBottom" ? (TValue)(object)true : default!);
        }
    }

    private sealed class FakeService : IConciergeService
    {
        public ConciergeSessionResponse Verification { get; set; } = SessionResponse("new", Guid.NewGuid());
        public Func<ConciergeVerificationRequest, Task<ConciergeSessionResponse>>? Verify { get; set; }
        public Func<string?, Task> End { get; set; } = _ => Task.CompletedTask;
        public Func<string?, Task<ConciergeHistoryResult>> History { get; set; } = _ => Task.FromResult(new ConciergeHistoryResult([]));
        public Func<ConciergeTurnRequest, Task<ConciergeTurnResult>> Submission { get; set; } =
            request => Task.FromResult(new ConciergeTurnResult(request.TurnId, "Reply", 1));
        public List<ConciergeTurnRequest> Submissions { get; } = [];
        /// <summary>Returns the arranged verification response.</summary>
        public Task<ConciergeSessionResponse> VerifyAsync(ConciergeVerificationRequest request, CancellationToken cancellationToken = default) =>
            Verify?.Invoke(request) ?? Task.FromResult(Verification);
        /// <summary>Returns the arranged history task for this credential.</summary>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            History(bearerCredential);
        /// <summary>Records the retry identity and returns the arranged turn task.</summary>
        public Task<ConciergeTurnResult> SubmitTurnAsync(string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default)
        {
            Submissions.Add(request);
            return Submission(request);
        }
        /// <summary>Completes the interaction without external services.</summary>
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) => End(bearerCredential);
    }
}
