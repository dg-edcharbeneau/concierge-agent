using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Concierge.Contracts;
using Concierge.Web.Services;
using Concierge.WorkflowServer.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;

namespace Concierge.Core.Tests;

/// <summary>Regressions using Web's real entry point, not the shared host's client registration.</summary>
public sealed class ProductionWebBackendTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    /// <summary>Worker results retain their deadline and authorize subsequent Web turns.</summary>
    [Fact]
    public async Task ProductionRegistrationReadsWorkerSessionAndTurnResults()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("Rivera", "412", today, today.AddDays(1));
        using var factory = CreateFactory(worker);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        // No test converter or replacement ITemporalClient is installed in Web.
        Assert.NotSame(worker.Environment.Client, factory.Services.GetRequiredService<ITemporalClient>());
        var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("Rivera", "412"));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        Assert.Equal(ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)), session.ExpiresAtUtc);
        Assert.Equal(stayId, session.StayId);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        var response = await http.PostAsJsonAsync(
            "/api/concierge/turns", new ConciergeTurnRequest("production", "Breakfast?", "web"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var turn = (await response.Content.ReadFromJsonAsync<ConciergeTurnResult>())!;
        Assert.Equal("production", turn.TurnId);
        Assert.Equal("Answer: Breakfast?", turn.AssistantMessage);
        Assert.Equal(0, turn.Sequence);
        Assert.Contains(worker.ChatClient.AnswerRequests[^1].Messages,
            message => message.Text.StartsWith("Verified guest context:", StringComparison.Ordinal)
                && message.Text.Contains("\"GivenName\":\"Test\"", StringComparison.Ordinal)
                && message.Text.Contains("\"FamilyName\":\"Rivera\"", StringComparison.Ordinal)
                && message.Text.Contains("\"RoomNumber\":\"412\"", StringComparison.Ordinal)
                && message.Text.Contains("\"Floor\":4", StringComparison.Ordinal));
    }

    /// <summary>Invalid identity has the exact requested guest-facing wording.</summary>
    [Fact]
    public async Task UnknownGuestSaysGuestNotFound()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        using var factory = CreateFactory(worker);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("Unknown", "412"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
        Assert.Equal("guest_not_found", error.Code);
        Assert.Equal("Guest not found", error.Message);
    }

    /// <summary>
    /// Session operations run as Standalone Activities started by name, exactly as Web starts them,
    /// and malformed input fails fast as a non-retryable activity failure.
    /// </summary>
    [Fact]
    public async Task SessionCommandsRunAsStandaloneActivities()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("Rivera", "412", today, today.AddDays(1));
        var sessionId = Guid.NewGuid();
        var hash = SHA256.HashData(RandomNumberGenerator.GetBytes(32));
        var client = worker.Environment.Client;

        var verify = await client.StartActivityAsync(
            ConciergeActivityNames.VerifySession,
            [new VerifyConciergeSessionActivityRequest(sessionId, hash, "Rivera", "412")],
            SessionActivityOptions("verify"));
        var verified = await verify.GetResultAsync<ConciergeSessionContext?>();
        Assert.NotNull(verified);
        Assert.Equal(sessionId, verified.SessionId);
        Assert.Equal(stayId, verified.StayId);
        Assert.Equal(new ConciergeVerifiedGuest(stayId, "Test", "Rivera", "412"), verified.VerifiedGuest);
        Assert.Equal(ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)), verified.EligibilityDeadlineUtc);
        Assert.Equal(ConciergeActivityNames.VerifySession, (await verify.DescribeAsync()).ActivityType);

        Assert.Equal(verified, await client.ExecuteActivityAsync<ConciergeSessionContext?>(
            ConciergeActivityNames.AuthenticateSession,
            [new AuthenticateConciergeSessionActivityRequest(hash)],
            SessionActivityOptions("authenticate")));
        await client.ExecuteActivityAsync(
            ConciergeActivityNames.InvalidateSession,
            [new InvalidateConciergeSessionActivityRequest(sessionId, stayId)],
            SessionActivityOptions("invalidate"));
        Assert.Null(await client.ExecuteActivityAsync<ConciergeSessionContext?>(
            ConciergeActivityNames.AuthenticateSession,
            [new AuthenticateConciergeSessionActivityRequest(hash)],
            SessionActivityOptions("authenticate")));

        var failed = await Assert.ThrowsAsync<ActivityFailedException>(() =>
            client.ExecuteActivityAsync<ConciergeSessionContext?>(
                ConciergeActivityNames.AuthenticateSession,
                [new AuthenticateConciergeSessionActivityRequest(new byte[31])],
                SessionActivityOptions("authenticate")));
        var cause = Assert.IsType<ApplicationFailureException>(failed.InnerException);
        Assert.Equal(ConciergeActivityNames.SessionInvalidRequestErrorType, cause.ErrorType);
        Assert.True(cause.NonRetryable);
    }

    private static StartActivityOptions SessionActivityOptions(string operation) =>
        new($"concierge-session-test-{operation}-{Guid.NewGuid():N}", ConciergeWorkflowNames.TaskQueue)
        {
            StartToCloseTimeout = TimeSpan.FromSeconds(20),
        };

    /// <summary>Server diagnostics retain cause types while both logs and guest errors omit sensitive values.</summary>
    [Fact]
    public async Task ModelFailureLogsSafeCauseAndReturnsGenericGuestError()
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var logs = new ServiceLogProvider();
        using var factory = CreateFactory(worker, logs);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("PrivateGuestName", "412"));
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        worker.ChatClient.Fail = true;

        var response = await http.PostAsJsonAsync(
            "/api/concierge/turns", new ConciergeTurnRequest("private-turn", "PrivateGuestQuestion", "web"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEmpty(worker.ChatClient.AnswerRequests);
        var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
        Assert.Equal("unavailable", error.Code);
        Assert.Equal("The concierge is temporarily unavailable. Please try again.", error.Message);
        var entry = Assert.Single(logs.Entries);
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
        Assert.Contains("SubmitTurnAsync", entry, StringComparison.Ordinal);
        Assert.Contains("WorkflowUpdateFailedException", entry, StringComparison.Ordinal);
        Assert.Contains("ActivityFailureException", entry, StringComparison.Ordinal);
        Assert.Contains("ApplicationFailureException", entry, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", entry, StringComparison.Ordinal);
        Assert.All(
            new[] { "secret-internal-detail", "10.0.0.7", "PrivateGuestName", "PrivateGuestQuestion", session.Credential },
            value =>
            {
                Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
                Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
            });
    }

    /// <summary>Session activity failures are mapped and logged without exposing database diagnostics.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionFailureLogsSafeCauseAndReturnsGenericGuestError(bool verify)
    {
        await using var worker = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var logs = new ServiceLogProvider();
        using var factory = CreateFactory(worker, logs);
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var verified = await http.PostAsJsonAsync(
            "/api/concierge/sessions", new ConciergeVerificationRequest("PrivateGuestName", "412"));
        verified.EnsureSuccessStatusCode();
        var session = (await verified.Content.ReadFromJsonAsync<ConciergeSessionResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Credential);
        // Only the isolated test database is changed; subsequent session activities fail.
        await worker.ExecuteAsync("DROP TABLE concierge_sessions;");

        var response = verify
            ? await http.PostAsJsonAsync(
                "/api/concierge/sessions", new ConciergeVerificationRequest("PrivateGuestName", "412"))
            : await http.GetAsync("/api/concierge/history");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ConciergeApiError>())!;
        Assert.Equal("unavailable", error.Code);
        var entry = Assert.Single(logs.Entries);
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
        Assert.Contains(verify ? "Verify" : "Authenticate", entry, StringComparison.Ordinal);
        Assert.Contains("ActivityFailedException", entry, StringComparison.Ordinal);
        Assert.Contains("PostgresException", entry, StringComparison.Ordinal);
        foreach (var value in new[] { "concierge_sessions", "PrivateGuestName", session.Credential })
        {
            Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Both stay recovery catch blocks log safe diagnostics without losing completed replies.</summary>
    [Fact]
    public async Task StayRecoveryLogsDoNotExposeSummaryOrDatabaseFailures()
    {
        var logs = new ServiceLogProvider();
        await using var worker = await ConciergeTestHost.StartAsync(
            postgreSql,
            flushIntervalSeconds: 600,
            logProvider: logs,
            patchActivation: input => !input.PatchId.StartsWith("stay-memory-v1-", StringComparison.Ordinal));
        var today = ConciergeTestHost.HotelToday;
        var stayId = await worker.SeedStayAsync("PrivateGuestName", "412", today, today.AddDays(1));
        var handle = await worker.StartStayAsync(stayId, ConciergeTestHost.HotelMidnightUtc(today.AddDays(2)));
        // Compaction now starts after answer 7, not on the next question's critical path.
        worker.ChatClient.FailSummary = true;
        for (var index = 0; index <= ConciergeStayWorkflow.LegacyRecentTurnWindow; index++)
        {
            await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission(
                    $"private-turn-{index}", "PrivateGuestQuestion", "web", ConciergeTestHost.VerifiedGuest(stayId))));
        }

        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            while (!logs.Entries.Any(entry => entry.Contains("CompactContextAsync", StringComparison.Ordinal)))
            {
                await Task.Delay(20, bound.Token);
            }
        }
        worker.ChatClient.FailSummary = false;
        var answer = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission(
                "private-last-turn", "PrivateGuestQuestion", "web", ConciergeTestHost.VerifiedGuest(stayId))));
        Assert.Equal("Answer: PrivateGuestQuestion", answer.AssistantMessage);
        await worker.ExecuteAsync("ALTER TABLE transcript_turns RENAME TO private_transcript_turns;");
        try
        {
            await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
                handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync()));
        }
        finally
        {
            await worker.ExecuteAsync("ALTER TABLE private_transcript_turns RENAME TO transcript_turns;");
        }

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(ConciergeStayWorkflow.LegacyRecentTurnWindow + 2,
            (await worker.ReadPersistedTurnIdsAsync(stayId)).Length);
        var entries = logs.Entries.ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, entry => entry.Contains("CompactContextAsync", StringComparison.Ordinal)
            && entry.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("FlushPendingAsync", StringComparison.Ordinal)
            && entry.Contains("PostgresException", StringComparison.Ordinal));
        Assert.All(entries, entry =>
        {
            Assert.Contains("ActivityFailureException", entry, StringComparison.Ordinal);
            foreach (var value in new[]
                { "secret-internal-detail", "10.0.0.7", "PrivateGuestName", "PrivateGuestQuestion",
                  "transcript_turns", "private-turn", stayId.ToString() })
            {
                Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
            }
        });
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));

        var replayLogs = new ServiceLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(replayLogs));
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
            LoggerFactory = loggerFactory,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
        Assert.Empty(replayLogs.Entries);
    }

    /// <summary>Both HTTP fallback handlers redact unexpected failures from replacement service implementations.</summary>
    [Theory]
    [InlineData("/api/concierge/history", "ReadHistoryAsync")]
    [InlineData("/api/concierge/end", "EndAsync")]
    public async Task EndpointFallbackLogsOnlyOperationAndCauseTypes(string path, string operation)
    {
        var logs = new ServiceLogProvider();
        using var factory = new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IConciergeService, FailingConciergeService>();
                services.AddLogging(logging => logging.AddProvider(logs));
            });
        });
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "PrivateCredential");
        var response = path.EndsWith("/end", StringComparison.Ordinal)
            ? await http.PostAsync(path, null)
            : await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var entry = Assert.Single(logs.Entries);
        Assert.Contains(operation, entry, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", entry, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", entry, StringComparison.Ordinal);
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
        foreach (var value in new[] { "secret-internal-detail", "10.0.0.7", "PrivateCredential" })
        {
            Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        }
    }

    private sealed class FailingConciergeService : IConciergeService
    {
        private static Exception Failure() => new InvalidOperationException(
            "secret-internal-detail PrivateCredential",
            new HttpRequestException("provider host 10.0.0.7"));

        /// <inheritdoc/>
        public Task<ConciergeSessionResponse> VerifyAsync(
            ConciergeVerificationRequest request, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeTurnResult> SubmitTurnAsync(
            string? bearerCredential, ConciergeTurnRequest request,
            CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task EndAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) => throw Failure();
        /// <inheritdoc/>
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
            ConciergeSessionContext session, string turnId, string userMessage,
            CancellationToken cancellationToken = default) => throw Failure();
    }

    private static WebApplicationFactory<ConciergeService> CreateFactory(
        ConciergeTestHost worker,
        ServiceLogProvider? logs = null) =>
        new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                // Only redirect the production client to the test server; leave its converter untouched.
                services.PostConfigure<TemporalClientConnectOptions>(options =>
                {
                    options.TargetHost = worker.Environment.Client.Connection.Options.TargetHost;
                    options.Namespace = worker.Environment.Client.Options.Namespace;
                });
                if (logs is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(logs));
                }
            });
        });

    private sealed class ServiceLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ConcurrentQueue<Exception?> Exceptions { get; } = new();

        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName) =>
            new ServiceLogger(
                categoryName == typeof(ConciergeService).FullName
                || categoryName == typeof(IConciergeService).FullName
                || categoryName == $"Temporalio.Workflow:{ConciergeWorkflowNames.StayWorkflow}"
                    ? this : null);

        /// <inheritdoc/>
        public void Dispose() { }

        private sealed class ServiceLogger(ServiceLogProvider? provider) : ILogger
        {
            /// <inheritdoc/>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <inheritdoc/>
            public bool IsEnabled(LogLevel logLevel) => provider is not null && logLevel >= LogLevel.Warning;

            /// <inheritdoc/>
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (provider is not null && IsEnabled(logLevel))
                {
                    // Passing a raw exception to logging would expose its message and stack.
                    provider.Exceptions.Enqueue(exception);
                    provider.Entries.Enqueue(formatter(state, exception) + (exception?.ToString() ?? ""));
                }
            }
        }
    }
}
