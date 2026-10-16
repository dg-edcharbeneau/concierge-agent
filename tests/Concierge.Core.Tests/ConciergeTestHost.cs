using System.Collections.Concurrent;
using Concierge.WorkflowServer;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Concierge.Web.Endpoints;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Concierge.Core.Tests;

/// <summary>
/// PostgreSQL + local Temporal + the Web concierge API and WorkflowServer worker, wired
/// like Program.cs but with a scripted chat client and zero-vector embeddings.
/// </summary>
internal sealed class ConciergeTestHost : IAsyncDisposable
{
    private readonly PostgreSqlTestDatabase database;

    private ConciergeTestHost(
        PostgreSqlTestDatabase database,
        NpgsqlDataSource dataSource,
        WorkflowEnvironment environment,
        WebApplication app,
        ScriptedChatClient chatClient,
        int flushIntervalSeconds,
        int flushBatchSize)
    {
        this.database = database;
        DataSource = dataSource;
        Environment = environment;
        App = app;
        ChatClient = chatClient;
        FlushIntervalSeconds = flushIntervalSeconds;
        FlushBatchSize = flushBatchSize;
        Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public NpgsqlDataSource DataSource { get; }

    public WorkflowEnvironment Environment { get; }

    public WebApplication App { get; }

    public ScriptedChatClient ChatClient { get; }

    public HttpClient Http { get; }

    public int FlushIntervalSeconds { get; }

    public int FlushBatchSize { get; }

    public static DateOnly HotelToday =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow,
                HotelTimeZone)
            .DateTime);

    private static readonly TimeZoneInfo HotelTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>UTC instant of Hotel ZGY local midnight starting <paramref name="date"/>.</summary>
    public static DateTimeOffset HotelMidnightUtc(DateOnly date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            HotelTimeZone));

    public static async Task<ConciergeTestHost> StartAsync(
        PostgreSqlFixture postgreSql,
        int flushIntervalSeconds = 30,
        IReadOnlyList<string>? devServerArgs = null,
        int flushBatchSize = ConciergePersistenceOptions.DefaultFlushBatchSize,
        ILoggerProvider? logProvider = null,
        Func<PatchActivationInput, bool>? patchActivation = null)
    {
        var database = await postgreSql.CreateDatabaseAsync();
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await new DatabaseMigrator(dataSource, NullLogger<DatabaseMigrator>.Instance)
            .MigrateAsync();

        var environment = await WorkflowEnvironment.StartLocalAsync(new()
        {
            DevServerOptions = new() { ExtraArgs = devServerArgs?.ToList() },
        });
        environment.Client.Options.DataConverter = DurableAIDataConverter.Instance;

        var chatClient = new ScriptedChatClient();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (logProvider is not null)
        {
            builder.Logging.AddProvider(logProvider);
        }
        builder.Services.AddSingleton<ITemporalClient>(environment.Client);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton<ConciergeSessionService>();
        builder.Services.Configure<ConciergePersistenceOptions>(
            options =>
            {
                options.FlushIntervalSeconds = flushIntervalSeconds;
                options.FlushBatchSize = flushBatchSize;
            });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Concierge.Web.Voice.VoiceSessionRegistry>();
        builder.Services.AddScoped<IConciergeService, ConciergeService>();
        builder.Services.AddSingleton<IChatClient>(chatClient);
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new ZeroEmbeddingGenerator());
        builder.Services.AddSingleton<IOptions<EmbeddingProviderOptions>>(
            Options.Create(new EmbeddingProviderOptions
            {
                Provider = AIProviderNames.Ollama,
                Model = "test-embedding",
                Dimensions = EmbeddingModelIdentity.MigratedSchemaDimensions,
            }));
        builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
            .ConfigureOptions(options => options.PatchActivationCallback = patchActivation ?? (_ => true))
            .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
            .AddWorkflow<ConciergeStayWorkflow>()
            .AddScopedActivities<HotelKnowledgeActivities>()
            .AddScopedActivities<ConciergeTranscriptActivities>()
            .AddScopedActivities<ConciergeSessionActivities>();

        var app = builder.Build();
        app.MapConciergeApi();
        await app.StartAsync();
        return new ConciergeTestHost(
            database,
            dataSource,
            environment,
            app,
            chatClient,
            flushIntervalSeconds,
            flushBatchSize);
    }

    public async Task<Guid> SeedStayAsync(
        string familyName,
        string roomNumber,
        DateOnly checkInDate,
        DateOnly checkOutDate)
    {
        var guestId = Guid.NewGuid();
        var stayId = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO guests (guest_id, given_name, family_name) VALUES ($1, 'Test', $2);",
            guestId,
            familyName);
        await ExecuteAsync(
            """
            INSERT INTO stays (
                stay_id, external_stay_key, guest_id, room_number,
                check_in_utc, check_out_utc, eligibility_ends_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """,
            stayId,
            $"stay-{stayId:N}",
            guestId,
            roomNumber,
            HotelMidnightUtc(checkInDate),
            HotelMidnightUtc(checkOutDate),
            HotelMidnightUtc(checkOutDate.AddDays(1)));
        return stayId;
    }

    /// <summary>The verified guest profile the Web attaches to every turn for <paramref name="stayId"/>.</summary>
    public static ConciergeVerifiedGuest VerifiedGuest(Guid stayId) => new(stayId, "Test", "Guest", "101");

    /// <summary>Starts the stay workflow exactly as the API does for a verified session.</summary>
    public async Task<WorkflowHandle<ConciergeStayWorkflow>> StartStayAsync(
        Guid stayId,
        DateTimeOffset deadline,
        string? continuitySummary = null)
    {
        var workflowId = $"concierge-stay-{stayId:N}";
        await Environment.Client.StartWorkflowAsync(
            (ConciergeStayWorkflow workflow) => workflow.RunAsync(
                new ConciergeStayWorkflowInput(
                    stayId, deadline, FlushIntervalSeconds, 0, continuitySummary, null, FlushBatchSize)),
            new WorkflowOptions(workflowId, TemporalWorkerConnection.TaskQueue)
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
            });

        // A handle without a run ID always targets the latest run after Continue-As-New.
        return Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(workflowId);
    }

    public async Task<string[]> ReadPersistedTurnIdsAsync(Guid stayId)
    {
        await using var command = DataSource.CreateCommand(
            "SELECT turn_id FROM transcript_turns WHERE stay_id = $1 ORDER BY turn_sequence;");
        command.Parameters.AddWithValue(stayId);
        await using var reader = await command.ExecuteReaderAsync();
        var turnIds = new List<string>();
        while (await reader.ReadAsync())
        {
            turnIds.Add(reader.GetString(0));
        }

        return [.. turnIds];
    }

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await Environment.ShutdownAsync();
        await DataSource.DisposeAsync();
        await database.DisposeAsync();
    }
}

/// <summary>
/// Deterministic chat client. Answers "Answer: {last guest message}"; summary requests
/// (identified by the summary instructions) return "Summary {n}".
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly ConcurrentQueue<RecordedChatRequest> requests = new();
    private readonly TaskCompletionSource firstCall = NewSource();
    private int summaryCount;

    public IReadOnlyList<RecordedChatRequest> Requests => [.. requests];

    public IReadOnlyList<RecordedChatRequest> AnswerRequests =>
        [.. requests.Where(request => !request.IsSummary)];

    /// <summary>When set, every model call waits for it to complete.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource? SummaryGate { get; set; }

    /// <summary>When set, every model call throws this internal-looking failure.</summary>
    public bool Fail { get; set; }

    /// <summary>Fails summary calls while still allowing guest answers to succeed.</summary>
    public bool FailSummary { get; set; }

    /// <summary>Overrides the deterministic answer text for isolation tests.</summary>
    public Func<IReadOnlyList<ChatMessage>, string>? AnswerFactory { get; set; }

    /// <summary>When set, answers report this finish reason (for example, Length).</summary>
    public ChatFinishReason? AnswerFinishReason { get; set; }

    public Task WaitForCallAsync() => firstCall.Task;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToArray();
        var isSummary = snapshot.FirstOrDefault()?.Text.StartsWith(
            "You maintain a short continuity summary",
            StringComparison.Ordinal) == true;
        requests.Enqueue(new RecordedChatRequest(snapshot, options?.MaxOutputTokens, isSummary));
        firstCall.TrySetResult();
        if (isSummary && SummaryGate is { } summaryGate)
        {
            await summaryGate.Task.WaitAsync(cancellationToken);
        }
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Fail || (FailSummary && isSummary))
        {
            throw new InvalidOperationException("secret-internal-detail: provider host 10.0.0.7");
        }

        var text = isSummary
            ? $"Summary {Interlocked.Increment(ref summaryCount)}"
            : AnswerFactory?.Invoke(snapshot)
                ?? $"Answer: {snapshot.Last(message => message.Role == ChatRole.User).Text}";
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
        {
            FinishReason = isSummary ? ChatFinishReason.Stop : AnswerFinishReason ?? ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record RecordedChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    int? MaxOutputTokens,
    bool IsSummary)
{
    public string LastUserText => Messages.Last(message => message.Role == ChatRole.User).Text;

    public IEnumerable<string> UserTexts =>
        Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text);
}

internal sealed class ZeroEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(
                    new float[EmbeddingModelIdentity.MigratedSchemaDimensions]))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
