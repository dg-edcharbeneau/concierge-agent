using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class ThinkEndpointTests : IDisposable
{
    private const int ThinkPort = 5443;
    private const string Path = "/v1/chat/completions";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly ConciergeSessionContext Stay = new(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(2));

    private readonly FakeTimeProvider time = new(Now);
    private readonly FakeConcierge concierge = new();
    private readonly BodyTracker bodies = new();
    private readonly WebApplicationFactory<ConciergeService> factory;
    private readonly HttpClient http;
    private readonly VoiceSessionRegistry registry;

    public ThinkEndpointTests()
    {
        factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("THINK_PORT", ThinkPort.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
                services.RemoveAll<IConciergeService>();
                services.AddSingleton<IConciergeService>(concierge);
                services.AddSingleton<IStartupFilter>(new ThinkListenerFilter(bodies));
            });
        });
        http = factory.CreateClient(new() { BaseAddress = new Uri("https://hotel.example") });
        registry = factory.Services.GetRequiredService<VoiceSessionRegistry>();
    }

    public void Dispose()
    {
        http.Dispose();
        factory.Dispose();
    }

    [Fact]
    public async Task RejectsMissingUnknownRevokedExpiredAndEndedBearersWithoutReadingBody()
    {
        var revoked = registry.Open(Stay, null);
        registry.Close(revoked);
        var ended = registry.Open(Stay with { SessionId = Guid.NewGuid() }, null);
        registry.EndSession(ended.Session.Stay.SessionId);
        var expired = registry.Open(Stay with { SessionId = Guid.NewGuid() }, null);
        time.Advance(VoiceSessionRegistry.ConnectionLifetime);

        foreach (var authorization in new[]
        {
            null, "Bearer", "Bearer not-a-voice-bearer", "Basic " + revoked.Bearer,
            "Bearer " + revoked.Bearer, "Bearer " + ended.Bearer, "Bearer " + expired.Bearer,
        })
        {
            using var response = await SendAsync(Json(Chat(("user", "Hello"))), authorization);

            await AssertOpenAIError(response, HttpStatusCode.Unauthorized, "invalid_request_error", "invalid_api_key");
        }

        Assert.Equal(0, bodies.BytesRead);
        Assert.Empty(concierge.Submissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedBodyReturns413Json(bool sendContentLength)
    {
        var connection = registry.Open(Stay, null);
        var body = Chat(("user", new string('a', ThinkEndpoint.MaxBodyBytes)));
        HttpContent content = sendContentLength
            ? new StringContent(body, Encoding.UTF8, "application/json")
            : new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(body)));

        using var response = await PostAsync(content, connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.RequestEntityTooLarge, "invalid_request_error", "request_too_large");
        Assert.Empty(concierge.Submissions);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"model":"concierge"}""")]
    [InlineData("""{"messages":[{"role":"system","content":"Be brief."},{"role":"assistant","content":"Hi"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"Hi"}]}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"   "}]}""")]
    public async Task MalformedRequestsReturn400Json(string body)
    {
        var connection = registry.Open(Stay, null);

        using var response = await PostAsync(Json(body), connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.BadRequest, "invalid_request_error", "invalid_request");
        Assert.Empty(concierge.Submissions);
    }

    [Fact]
    public async Task AnswersWithExactSseFramingAndSubmitsOnlyTheLatestUserMessage()
    {
        var connection = registry.Open(Stay, null);
        concierge.Answer = _ => Task.FromResult("Breakfast is served 7–10 \"daily\".");

        using var response = await PostAsync(Json(Chat(("system", "Ignored prompt"), ("user", "Is there a pool?"), ("assistant", "Yes."), ("user", " When is breakfast? "))),
            connection.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var turnId = ThinkRequest.TurnId(connection.Session.Id, connection.Number, 2, "When is breakfast?");
        var created = Now.ToUnixTimeSeconds();
        string Chunk(string delta, string finish) =>
            $$"""data: {"id":"{{turnId}}","object":"chat.completion.chunk","created":{{created}},"model":"deepgram-model","choices":[{"index":0,"delta":{{delta}},"finish_reason":{{finish}}}]}""" + "\n\n";
        Assert.Equal(
            Chunk("""{"role":"assistant"}""", "null")
            + Chunk("""{"content":"Breakfast is served 7\u201310 \u0022daily\u0022."}""", "null")
            + Chunk("{}", "\"stop\"")
            + "data: [DONE]\n\n",
            await response.Content.ReadAsStringAsync());

        var submission = Assert.Single(concierge.Submissions);
        Assert.Equal(turnId, submission.TurnId);
        Assert.Equal("When is breakfast?", submission.UserMessage);
        Assert.Same(Stay, submission.Session);
        // Admitted Temporal work must not depend on the HTTP request's lifetime.
        Assert.False(submission.CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task ReconnectUsesANewConnectionNumberInTheTurnId()
    {
        var first = registry.Open(Stay, null);
        var second = registry.Open(Stay, first.Session.Id);

        using var response = await PostAsync(Json(Chat(("user", "Hello"))), second.Bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ThinkRequest.TurnId(first.Session.Id, second.Number, 1, "Hello"),
            Assert.Single(concierge.Submissions).TurnId);
        Assert.True(ThinkRequest.TurnId(Guid.NewGuid(), int.MinValue, int.MinValue, "Hello").Length
            <= ConciergeTurnLimits.MaxTurnIdLength);
    }

    [Fact]
    public async Task ConcurrentRequestsWithTheSameTurnIdShareOneSubmission()
    {
        var connection = registry.Open(Stay, null);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        concierge.Answer = _ => release.Task;

        var first = PostAsync(Json(Chat(("user", "Late checkout?"))), connection.Bearer);
        await concierge.FirstSubmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retry = PostAsync(Json(Chat(("user", "Late checkout?"))), connection.Bearer);
        await Task.Delay(100);
        release.SetResult("Until noon.");

        using var firstResponse = await first;
        using var retryResponse = await retry;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal(await firstResponse.Content.ReadAsStringAsync(), await retryResponse.Content.ReadAsStringAsync());
        Assert.Single(concierge.Submissions);
    }

    [Fact]
    public async Task ConcurrentRequestsWithChangedTextAndTheSameOrdinalStartDistinctTurns()
    {
        var connection = registry.Open(Stay, null);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        concierge.Answer = _ => release.Task;

        var first = PostAsync(Json(Chat(("user", "Is breakfast served?"))), connection.Bearer);
        await concierge.FirstSubmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var resumed = PostAsync(Json(Chat(("user", "Is breakfast served every day?"))), connection.Bearer);
        var secondStarted = await Task.WhenAny(concierge.SecondSubmission.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        release.SetResult("Yes.");

        using var firstResponse = await first;
        using var resumedResponse = await resumed;

        Assert.Same(concierge.SecondSubmission.Task, secondStarted);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resumedResponse.StatusCode);
        var submissions = concierge.Submissions;
        Assert.Equal(2, submissions.Count);
        Assert.NotEqual(submissions[0].TurnId, submissions[1].TurnId);
        Assert.Equal("Is breakfast served?", submissions[0].UserMessage);
        Assert.Equal("Is breakfast served every day?", submissions[1].UserMessage);
    }

    [Fact]
    public void TurnIdsAreStableForIdenticalTextAndIsolateTextConnectionAndOrdinal()
    {
        var voiceSessionId = Guid.NewGuid();
        const string userMessage = "Is breakfast served every day?";
        var turnId = ThinkRequest.TurnId(voiceSessionId, 3, 7, userMessage);

        Assert.Equal(turnId, ThinkRequest.TurnId(voiceSessionId, 3, 7, userMessage));
        Assert.NotEqual(turnId, ThinkRequest.TurnId(voiceSessionId, 3, 7, "Is breakfast served?"));
        Assert.NotEqual(turnId, ThinkRequest.TurnId(voiceSessionId, 4, 7, userMessage));
        Assert.NotEqual(turnId, ThinkRequest.TurnId(voiceSessionId, 3, 8, userMessage));
        Assert.True(ThinkRequest.TurnId(Guid.NewGuid(), int.MinValue, int.MinValue, userMessage).Length
            <= ConciergeTurnLimits.MaxTurnIdLength);
    }

    [Fact]
    public async Task WorkflowRejectionReturnsJsonBeforeAnySseBytes()
    {
        var connection = registry.Open(Stay, null);
        concierge.Answer = _ => throw new ConciergeApiException(
            "turn_conflict", "The turn identifier was already used.", StatusCodes.Status409Conflict);

        using var response = await PostAsync(Json(Chat(("user", "Hello"))), connection.Bearer);

        await AssertOpenAIError(response, HttpStatusCode.Conflict, "invalid_request_error", "turn_conflict");
    }

    private Task<HttpResponseMessage> PostAsync(HttpContent content, string bearer) =>
        SendAsync(content, "Bearer " + bearer);

    private Task<HttpResponseMessage> SendAsync(HttpContent content, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = content };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return http.SendAsync(request);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Chat(params (string Role, string Content)[] messages) =>
        JsonSerializer.Serialize(new
        {
            model = "deepgram-model",
            stream = true,
            messages = messages.Select(message => new { role = message.Role, content = message.Content }),
        });

    private static async Task AssertOpenAIError(
        HttpResponseMessage response, HttpStatusCode status, string type, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(type, error.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    private sealed record Submission(
        ConciergeSessionContext Session, string TurnId, string UserMessage, CancellationToken CancellationToken);

    private sealed class FakeConcierge : IConciergeService
    {
        private readonly Lock gate = new();
        private readonly List<Submission> submissions = [];

        public Func<string, Task<string>> Answer { get; set; } = message => Task.FromResult("Answer");

        public TaskCompletionSource FirstSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Submission> Submissions
        {
            get
            {
                lock (gate)
                {
                    return submissions.ToList();
                }
            }
        }

        /// <summary>Records the voice turn and returns the arranged answer.</summary>
        public async Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
            ConciergeSessionContext session, string turnId, string userMessage, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                submissions.Add(new Submission(session, turnId, userMessage, cancellationToken));
            }

            if (Submissions.Count >= 2)
            {
                SecondSubmission.TrySetResult();
            }

            FirstSubmission.TrySetResult();
            return new ConciergeTurnResult(turnId, await Answer(userMessage), 1);
        }

        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeSessionResponse> VerifyAsync(
            ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeTurnResult> SubmitTurnAsync(
            string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>Not used by the Think handler.</summary>
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BodyTracker
    {
        private long bytesRead;

        public long BytesRead => Interlocked.Read(ref bytesRead);

        public void Add(int count) => Interlocked.Add(ref bytesRead, count);
    }

    // TestServer has no real listener: set the server-observed port and observe body reads.
    private sealed class ThinkListenerFilter(BodyTracker tracker) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.LocalPort = ThinkPort;
                context.Request.Body = new TrackingStream(context.Request.Body, tracker);
                return continuation(context);
            });
            next(app);
        };
    }

    private sealed class TrackingStream(Stream inner, BodyTracker tracker) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            tracker.Add(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            tracker.Add(read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Forces a request without Content-Length so the handler's streaming cap is what rejects it.
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
