using Concierge.WorkflowServer;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Health;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Extensions.Hosting;
using Temporalio.Testing;

namespace Concierge.Core.Tests;

public sealed class HotelSetupWorkflowTests(PostgreSqlFixture postgres)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task RepeatedSetupIsIdempotentAndOnlyChangedSectionIsReembedded()
    {
        await using var fixture = await SetupFixture.StartAsync(
            postgres,
            [
                ("policies.md", """
                    ---
                    sourceId: zgy-policies
                    title: Hotel ZGY policies
                    ---

                    # Hotel ZGY policies

                    ## Checkout

                    Checkout is 11 a.m.

                    ## Late checkout

                    Ask the front desk about availability.
                    """),
            ],
            new FakeEmbeddingGenerator(failFirstRequest: true));

        var first = await fixture.RunSetupAsync(OverwriteExistingSeedRecords: false);
        Assert.Equal(2, first.SectionCount);
        Assert.Equal(3, fixture.EmbeddingGenerator.CallCount);
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM hotel_facts;"));

        var firstCheckoutHash = await fixture.SectionHashAsync("checkout");
        var firstLateCheckoutHash = await fixture.SectionHashAsync("late-checkout");
        var oldGivenName = await fixture.ScalarAsync<string>(
            "SELECT given_name FROM guests WHERE guest_id = $1;",
            Guid.Parse(SetupFixture.DefaultGuestId));
        fixture.ChangeFirstGuestName("Casey");

        await fixture.RunSetupAsync(OverwriteExistingSeedRecords: false);
        Assert.Equal(3, fixture.EmbeddingGenerator.CallCount);
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM guests;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM stays;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(oldGivenName, await fixture.ScalarAsync<string>(
            "SELECT given_name FROM guests WHERE guest_id = $1;",
            Guid.Parse(SetupFixture.DefaultGuestId)));

        fixture.WriteMarkdown(
            "policies.md",
            """
            ---
            sourceId: zgy-policies
            title: Hotel ZGY policies
            ---

            # Hotel ZGY policies

            ## Checkout

            Checkout is 11 a.m.

            ## Late checkout

            Ask the front desk about current availability before departure.
            """);
        var changed = await fixture.RunSetupAsync(OverwriteExistingSeedRecords: true);

        Assert.Equal(4, fixture.EmbeddingGenerator.CallCount);
        Assert.Equal(firstCheckoutHash, await fixture.SectionHashAsync("checkout"));
        Assert.NotEqual(firstLateCheckoutHash, await fixture.SectionHashAsync("late-checkout"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM ingestion_versions;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM guests;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM stays;"));
        Assert.Equal(1, changed.EmbeddedSectionCount);
        Assert.Equal("Casey", await fixture.ScalarAsync<string>(
            "SELECT given_name FROM guests WHERE guest_id = $1;",
            Guid.Parse(SetupFixture.DefaultGuestId)));

        fixture.WriteMarkdown(
            "policies.md",
            """
            ---
            sourceId: zgy-policies
            title: Hotel ZGY policies
            ---

            # Hotel ZGY policies

            ## Checkout

            Checkout is 11 a.m.
            """);
        var removedSection = await fixture.RunSetupAsync();

        Assert.Equal(1, removedSection.SectionCount);
        Assert.Equal(0, removedSection.EmbeddedSectionCount);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ingestion_versions;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM hotel_facts;"));
        Assert.Equal(4, fixture.EmbeddingGenerator.CallCount);
    }

    [Fact]
    public async Task MovingUnchangedMarkdownRefreshesFactLocatorWithoutReembedding()
    {
        await using var fixture = await SetupFixture.StartAsync(
            postgres,
            [
                ("policies.md", """
                    ---
                    sourceId: zgy-policies
                    title: Hotel ZGY policies
                    ---

                    ## Checkout

                    Checkout is 11 a.m.
                    """),
            ]);

        await fixture.RunSetupAsync();
        Assert.Equal(
            "policies.md#checkout",
            await fixture.ScalarAsync<string>("SELECT source_locator FROM hotel_facts;"));

        fixture.MoveMarkdown("policies.md", "moved/policies.md");
        var outcome = await fixture.RunSetupAsync();

        Assert.Equal(0, outcome.EmbeddedSectionCount);
        Assert.Equal(1, fixture.EmbeddingGenerator.CallCount);
        Assert.Equal(
            "moved/policies.md#checkout",
            await fixture.ScalarAsync<string>("SELECT source_locator FROM hotel_facts;"));
    }

    [Fact]
    public async Task StarterDoesNotMarkReadyAfterJoiningAnOlderGeneration()
    {
        var firstEmbeddingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEmbeddingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstEmbedding = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondEmbedding = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeEmbeddingGenerator(beforeGenerate: async call =>
        {
            if (call == 1)
            {
                firstEmbeddingStarted.TrySetResult();
                await releaseFirstEmbedding.Task;
            }
            else if (call == 2)
            {
                secondEmbeddingStarted.TrySetResult();
                await releaseSecondEmbedding.Task;
            }
        });
        await using var fixture = await SetupFixture.StartAsync(
            postgres,
            [
                ("policies.md", """
                    ---
                    sourceId: zgy-policies
                    title: Hotel ZGY policies
                    ---

                    ## Checkout

                    Checkout is 11 a.m.
                    """),
            ],
            generator);

        var existingRequest = fixture.CreateRequest();
        var existingRun = await fixture.StartSetupAsync(existingRequest);
        await firstEmbeddingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.ChangeFirstGuestName("Current");
        fixture.SetOverwriteExistingSeedRecords(true);
        fixture.WriteMarkdown(
            "policies.md",
            """
            ---
            sourceId: zgy-policies
            title: Hotel ZGY policies
            ---

            ## Checkout

            Checkout is 11 a.m. and late checkout requires approval.
            """);
        var readiness = new SetupReadinessState();
        var starter = fixture.CreateStarter(readiness);
        await starter.StartAsync(CancellationToken.None);

        releaseFirstEmbedding.TrySetResult();
        var joinedOutcome = await existingRun.GetResultAsync();
        Assert.Equal(existingRequest.StartupGeneration, joinedOutcome.StartupGeneration);

        await secondEmbeddingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("running", readiness.Snapshot.Status);

        releaseSecondEmbedding.TrySetResult();
        await WaitForReadyAsync(readiness);
        await starter.StopAsync(CancellationToken.None);

        Assert.Equal("ready", readiness.Snapshot.Status);
        Assert.Equal(
            "Current",
            await fixture.ScalarAsync<string>(
                "SELECT given_name FROM guests WHERE guest_id = $1;",
                Guid.Parse(SetupFixture.DefaultGuestId)));
    }

    [Theory]
    [InlineData("0412")]
    [InlineData("0")]
    [InlineData("12345")]
    [InlineData("41a")]
    public async Task SeedRejectsInvalidRoomNumbersBeforeTouchingTheDatabase(string roomNumber)
    {
        // Validation runs before any connection is opened, so the data source is never used.
        await using var unusedDataSource = NpgsqlDataSource.Create("Host=unused.invalid");
        var activities = new SetupActivities(
            unusedDataSource,
            new KnowledgeMarkdownCatalog(
                new TestHostEnvironment(Path.GetTempPath()),
                Options.Create(new SetupOptions())),
            new ZeroEmbeddingGenerator(),
            new EmbeddingModelIdentityStore(unusedDataSource),
            Options.Create(new EmbeddingProviderOptions()),
            NullLogger<SetupActivities>.Instance);
        var defaults = new SetupOptions();
        defaults.Stays[0].RoomNumber = roomNumber;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ActivityEnvironment().RunAsync(() => activities.SeedFictionalStaysAsync(
                new SeedDemoDataRequest(false, defaults.Guests, defaults.Stays))));

        Assert.Contains("room number", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedStayIdForExistingExternalKeyFailsSetupWithoutWritingSeedChanges()
    {
        await using var fixture = await SetupFixture.StartAsync(postgres, [MinimalPolicies]);
        await fixture.RunSetupAsync();
        var externalKey = fixture.FirstStay.ExternalStayKey;
        var originalStayId = Guid.Parse(fixture.FirstStay.StayId);
        var changedStayId = Guid.Parse("11111111-2222-5333-8444-555555555555");
        var originalGivenName = await fixture.ScalarAsync<string>(
            "SELECT given_name FROM guests WHERE guest_id = $1;",
            Guid.Parse(SetupFixture.DefaultGuestId));

        fixture.FirstStay.StayId = changedStayId.ToString();
        fixture.ChangeFirstGuestName("Casey");
        var mismatch = await AssertStayIdentityMismatchAsync(
            fixture.RunSetupAsync(OverwriteExistingSeedRecords: true));

        Assert.Contains(externalKey, mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(changedStayId.ToString(), mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(originalStayId.ToString(), mismatch.Message, StringComparison.Ordinal);
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM stays;"));
        Assert.Equal(originalStayId, await fixture.ScalarAsync<Guid>(
            "SELECT stay_id FROM stays WHERE external_stay_key = $1;",
            externalKey));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM stays WHERE stay_id = $1;",
            changedStayId));
        Assert.Equal(originalGivenName, await fixture.ScalarAsync<string>(
            "SELECT given_name FROM guests WHERE guest_id = $1;",
            Guid.Parse(SetupFixture.DefaultGuestId)));
    }

    [Fact]
    public async Task ChangedExternalKeyForExistingStayIdFailsSetupWithoutWritingSeedChanges()
    {
        await using var fixture = await SetupFixture.StartAsync(postgres, [MinimalPolicies]);
        await fixture.RunSetupAsync();
        var stayId = Guid.Parse(fixture.FirstStay.StayId);
        var originalKey = fixture.FirstStay.ExternalStayKey;
        const string changedKey = "zgy-demo-rm-9999";

        fixture.FirstStay.ExternalStayKey = changedKey;
        var mismatch = await AssertStayIdentityMismatchAsync(fixture.RunSetupAsync());

        Assert.Contains(changedKey, mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(originalKey, mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(stayId.ToString(), mismatch.Message, StringComparison.Ordinal);
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM stays;"));
        Assert.Equal(originalKey, await fixture.ScalarAsync<string>(
            "SELECT external_stay_key FROM stays WHERE stay_id = $1;",
            stayId));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM stays WHERE external_stay_key = $1;",
            changedKey));
    }

    [Fact]
    public async Task DatabaseActivityFailureRetriesWithoutPartialChunkOrFactRows()
    {
        await using var fixture = await SetupFixture.StartAsync(
            postgres,
            [
                ("policies.md", """
                    ---
                    sourceId: zgy-policies
                    title: Hotel ZGY policies
                    ---

                    ## Checkout

                    Checkout is 11 a.m.
                    """),
            ]);
        await fixture.ExecuteAsync(
            """
            CREATE SEQUENCE setup_fail_once;
            CREATE FUNCTION fail_first_knowledge_chunk() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('setup_fail_once') = 1 THEN
                    RAISE EXCEPTION 'deterministic one-time insert failure';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER trg_fail_first_knowledge_chunk
            BEFORE INSERT ON knowledge_chunks
            FOR EACH ROW EXECUTE FUNCTION fail_first_knowledge_chunk();
            """);

        var result = await fixture.RunSetupAsync();

        Assert.Equal(1, result.EmbeddedSectionCount);
        Assert.Equal(1, fixture.EmbeddingGenerator.CallCount);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ingestion_versions;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM hotel_facts;"));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM knowledge_chunks WHERE embedding IS NULL;"));
    }

    [Fact]
    public async Task RemovedMarkdownSourceAndItsIndexedDataAreDeletedAndRecreatedWhenRestored()
    {
        await using var fixture = await SetupFixture.StartAsync(
            postgres,
            [
                ("first.md", """
                    ---
                    sourceId: zgy-first
                    title: First
                    ---

                    ## Alpha

                    Alpha fact.
                    """),
                ("second.md", """
                    ---
                    sourceId: zgy-second
                    title: Second
                    ---

                    ## Beta

                    Beta fact.
                    """),
            ]);

        await fixture.RunSetupAsync();
        fixture.DeleteMarkdown("second.md");
        var outcome = await fixture.RunSetupAsync();

        Assert.Equal(1, outcome.RemovedSourceCount);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM hotel_facts;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ingestion_versions;"));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM knowledge_sources WHERE source_key = 'zgy-second';"));

        fixture.WriteMarkdown(
            "second.md",
            """
            ---
            sourceId: zgy-second
            title: Second
            ---

            ## Beta

            Beta fact restored.
            """);
        var restored = await fixture.RunSetupAsync();
        Assert.Equal(0, restored.RemovedSourceCount);
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM hotel_facts;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM ingestion_versions;"));
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM knowledge_sources WHERE source_key = 'zgy-second';"));
    }

    [Fact]
    public async Task EmbeddingIdentityChangeClearsOldVectorsAndReembedsBeforeIdentityIsUpdated()
    {
        await using var fixture = await SetupFixture.StartAsync(
            postgres,
            [
                ("policies.md", """
                    ---
                    sourceId: zgy-policies
                    title: Hotel ZGY policies
                    ---

                    ## Checkout

                    Checkout is 11 a.m.

                    ## Late checkout

                    Ask the front desk.
                    """),
            ]);

        await fixture.RunSetupAsync();
        Assert.Equal("fake-embedding-v1", await fixture.ScalarAsync<string>(
            "SELECT model FROM knowledge_embedding_identity WHERE identity_id = 1;"));

        await fixture.RestartWorkerAsync("fake-embedding-v2", new FakeEmbeddingGenerator());
        var outcome = await fixture.RunSetupAsync();

        Assert.Equal(2, outcome.EmbeddedSectionCount);
        Assert.Equal(2, fixture.EmbeddingGenerator.CallCount);
        Assert.Equal("fake-embedding-v2", await fixture.ScalarAsync<string>(
            "SELECT model FROM knowledge_embedding_identity WHERE identity_id = 1;"));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM knowledge_chunks WHERE embedding IS NULL;"));
        Assert.Equal(2, await fixture.ScalarAsync<long>("SELECT count(*) FROM knowledge_chunks;"));
    }

    [Fact]
    public async Task SetupReadinessHealthCheckGatesGuestAvailabilityUntilWorkflowSuccess()
    {
        var state = new SetupReadinessState();
        var check = new SetupHealthCheck(state);
        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext();

        Assert.Equal(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            (await check.CheckHealthAsync(context)).Status);

        state.MarkFailed(new InvalidOperationException("embedding request failed"));
        Assert.Equal(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            (await check.CheckHealthAsync(context)).Status);

        state.MarkReady();
        var ready = await check.CheckHealthAsync(context);
        Assert.Equal(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            ready.Status);
        Assert.Contains(SetupWorkflowIdentity.WorkflowId, ready.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void StarterDoesNotDependOnMarkdownCatalog()
    {
        var constructor = Assert.Single(typeof(SetupWorkflowStarter).GetConstructors());

        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(KnowledgeMarkdownCatalog));
    }

    private static async Task WaitForReadyAsync(SetupReadinessState readiness)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (readiness.Snapshot.Status != "ready")
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static readonly (string FileName, string Content) MinimalPolicies = ("policies.md", """
        ---
        sourceId: zgy-policies
        title: Hotel ZGY policies
        ---

        ## Checkout

        Checkout is 11 a.m.
        """);

    private static async Task<ApplicationFailureException> AssertStayIdentityMismatchAsync(
        Task<SetupOutcome> setup)
    {
        var failed = await Assert.ThrowsAsync<WorkflowFailedException>(() => setup);
        var activity = Assert.IsType<ActivityFailureException>(failed.InnerException);
        var mismatch = Assert.IsType<ApplicationFailureException>(activity.InnerException);
        Assert.Equal(SetupActivities.StayIdentityMismatchErrorType, mismatch.ErrorType);
        Assert.True(mismatch.NonRetryable);
        Assert.Contains("restore the original Setup stay ID/key pairing", mismatch.Message, StringComparison.Ordinal);
        return mismatch;
    }

    private sealed class SetupFixture : IAsyncDisposable
    {
        public const string DefaultGuestId = "bca99f5d-8e64-5f1b-bc77-a12d9385b104";

        private readonly PostgreSqlTestDatabase database;
        private readonly NpgsqlDataSource dataSource;
        private readonly WorkflowEnvironment environment;
        private readonly string contentRoot;
        private readonly SetupOptions setupOptions;
        private IHost host = null!;
        private string model = "fake-embedding-v1";

        private SetupFixture(
            PostgreSqlTestDatabase database,
            NpgsqlDataSource dataSource,
            WorkflowEnvironment environment,
            string contentRoot,
            SetupOptions setupOptions)
        {
            this.database = database;
            this.dataSource = dataSource;
            this.environment = environment;
            this.contentRoot = contentRoot;
            this.setupOptions = setupOptions;
        }

        public FakeEmbeddingGenerator EmbeddingGenerator { get; private set; } = null!;

        public static async Task<SetupFixture> StartAsync(
            PostgreSqlFixture postgres,
            IReadOnlyList<(string FileName, string Content)> markdown,
            FakeEmbeddingGenerator? generator = null)
        {
            var database = await postgres.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await new DatabaseMigrator(dataSource, NullLogger<DatabaseMigrator>.Instance)
                .MigrateAsync();

            var contentRoot = Path.Combine(Path.GetTempPath(), $"hotel-zgy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(contentRoot, "knowledge"));
            var options = new SetupOptions { KnowledgeDirectory = "knowledge" };
            var environment = await WorkflowEnvironment.StartLocalAsync();
            var fixture = new SetupFixture(database, dataSource, environment, contentRoot, options);
            foreach (var source in markdown)
            {
                fixture.WriteMarkdown(source.FileName, source.Content);
            }

            await fixture.StartWorkerAsync(fixture.model, generator ?? new FakeEmbeddingGenerator());
            return fixture;
        }

        public void WriteMarkdown(string fileName, string content) =>
            File.WriteAllText(Path.Combine(contentRoot, "knowledge", fileName), content);

        public void DeleteMarkdown(string fileName) =>
            File.Delete(Path.Combine(contentRoot, "knowledge", fileName));

        public void MoveMarkdown(string sourceFileName, string destinationFileName)
        {
            var destinationPath = Path.Combine(contentRoot, "knowledge", destinationFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(Path.Combine(contentRoot, "knowledge", sourceFileName), destinationPath);
        }

        public void ChangeFirstGuestName(string givenName) =>
            setupOptions.Guests[0].GivenName = givenName;

        public StaySeedOptions FirstStay => setupOptions.Stays[0];

        public void SetOverwriteExistingSeedRecords(bool overwrite) =>
            setupOptions.OverwriteExistingSeedRecords = overwrite;

        public SetupWorkflowRequest CreateRequest()
        {
            var seedData = new SeedDemoDataRequest(
                setupOptions.OverwriteExistingSeedRecords,
                setupOptions.Guests,
                setupOptions.Stays);
            var identity = CurrentIdentity();
            return new SetupWorkflowRequest(
                Guid.NewGuid().ToString("N"),
                seedData,
                identity);
        }

        public SetupWorkflowStarter CreateStarter(SetupReadinessState readiness) =>
            new(
                environment.Client,
                Options.Create(setupOptions),
                Options.Create(new EmbeddingProviderOptions
                {
                    Provider = "Ollama",
                    Model = model,
                    Dimensions = EmbeddingModelIdentity.MigratedSchemaDimensions,
                }),
                readiness,
                NullLogger<SetupWorkflowStarter>.Instance);

        public async Task<SetupOutcome> RunSetupAsync(bool OverwriteExistingSeedRecords = false)
        {
            var originalOverwrite = setupOptions.OverwriteExistingSeedRecords;
            setupOptions.OverwriteExistingSeedRecords = OverwriteExistingSeedRecords;
            try
            {
                var handle = await StartSetupAsync(CreateRequest());
                return await handle.GetResultAsync();
            }
            finally
            {
                setupOptions.OverwriteExistingSeedRecords = originalOverwrite;
            }
        }

        public Task<WorkflowHandle<HotelSetupWorkflow, SetupOutcome>> StartSetupAsync(
            SetupWorkflowRequest request) =>
            environment.Client.StartWorkflowAsync(
                (HotelSetupWorkflow workflow) => workflow.RunAsync(request),
                new WorkflowOptions(
                    SetupWorkflowIdentity.WorkflowId,
                    TemporalWorkerConnection.TaskQueue)
                {
                    IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                });

        public async Task RestartWorkerAsync(string nextModel, FakeEmbeddingGenerator generator)
        {
            await host.StopAsync();
            host.Dispose();
            model = nextModel;
            await StartWorkerAsync(nextModel, generator);
        }

        public async Task<T> ScalarAsync<T>(string sql, object? parameter = null)
        {
            await using var command = dataSource.CreateCommand(sql);
            if (parameter is not null)
            {
                command.Parameters.AddWithValue(parameter);
            }

            return Assert.IsType<T>(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> SectionHashAsync(string sectionId)
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT content_sha256
                FROM ingestion_versions
                WHERE version_key = $1;
                """);
            command.Parameters.AddWithValue(sectionId);
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        private EmbeddingModelIdentity CurrentIdentity() =>
            new("Ollama", model, EmbeddingModelIdentity.MigratedSchemaDimensions);

        private KnowledgeMarkdownCatalog CreateCatalog() =>
            new(new TestHostEnvironment(contentRoot), Options.Create(setupOptions));

        private async Task StartWorkerAsync(string configuredModel, FakeEmbeddingGenerator generator)
        {
            EmbeddingGenerator = generator;
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton(dataSource);
            builder.Services.AddSingleton<ITemporalClient>(environment.Client);
            builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
            builder.Services.AddSingleton<IOptions<SetupOptions>>(Options.Create(setupOptions));
            builder.Services.AddSingleton<IOptions<EmbeddingProviderOptions>>(Options.Create(
                new EmbeddingProviderOptions
                {
                    Provider = "Ollama",
                    Model = configuredModel,
                    Dimensions = EmbeddingModelIdentity.MigratedSchemaDimensions,
                }));
            builder.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(contentRoot));
            builder.Services.AddSingleton<KnowledgeMarkdownCatalog>();
            builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
                .AddWorkflow<HotelSetupWorkflow>()
                .AddScopedActivities<SetupActivities>();
            host = builder.Build();
            await host.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
            await environment.ShutdownAsync();
            await dataSource.DisposeAsync();
            await database.DisposeAsync();
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private sealed class FakeEmbeddingGenerator(
        bool failFirstRequest = false,
        Func<int, Task>? beforeGenerate = null)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref callCount);
            if (beforeGenerate is not null)
            {
                await beforeGenerate(call);
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (failFirstRequest && call == 1)
            {
                throw new InvalidOperationException("deterministic transient embedding failure");
            }

            return new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[768])));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Concierge.Setup.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

public sealed class KnowledgeMarkdownCatalogTests
{
    [Fact]
    public void ParserUsesStableSectionIdsAndReportsDuplicateIds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hotel-markdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "source.md"),
                """
                ---
                sourceId: zgy-policies
                title: Hotel policies
                ---

                # Policies

                ## Late checkout

                Ask the desk.
                """);
            var environment = new TestCatalogEnvironment(root);
            var catalog = new KnowledgeMarkdownCatalog(
                environment,
                Options.Create(new SetupOptions { KnowledgeDirectory = "." }));

            var first = Assert.Single(catalog.DiscoverAndParse());
            Assert.Equal("late-checkout", Assert.Single(first.Sections).SectionId);
            Assert.Equal(64, first.Sections[0].ContentSha256.Length);

            File.WriteAllText(
                Path.Combine(root, "duplicate.md"),
                """
                ---
                sourceId: zgy-policies
                title: Duplicate
                ---

                ## Another

                Duplicate source.
                """);
            var error = Assert.Throws<InvalidOperationException>(() => catalog.DiscoverAndParse());
            Assert.Contains("declared more than once", error.Message, StringComparison.Ordinal);
        }

        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParserChunksFaqEntriesAsStableSubsectionsOfAnEmptyFaqContainer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hotel-faq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "faq.md"),
                """
                ---
                sourceId: zgy-faq
                title: Hotel FAQ
                ---

                ## FAQ

                ### Can I check out later?

                Ask the front desk about availability.

                ### Where is the gym?

                The gym is on the fourth floor.
                """);
            var catalog = new KnowledgeMarkdownCatalog(
                new TestCatalogEnvironment(root),
                Options.Create(new SetupOptions { KnowledgeDirectory = "." }));

            var sections = Assert.Single(catalog.DiscoverAndParse()).Sections;

            Assert.Equal(
                ["faq-can-i-check-out-later", "faq-where-is-the-gym"],
                sections.Select(section => section.SectionId));
            Assert.All(sections, section => Assert.False(string.IsNullOrWhiteSpace(section.Content)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestCatalogEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Concierge.Markdown.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
