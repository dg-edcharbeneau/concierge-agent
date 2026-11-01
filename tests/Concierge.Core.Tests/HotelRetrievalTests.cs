using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Temporalio.Common;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Workflows;

namespace Concierge.Core.Tests;

public sealed class HotelRetrievalTests(PostgreSqlFixture postgres)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task ExactTermQuestionReturnsCurrentStructuredFactBeforeEmbedding()
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);
        var embeddingCallsBeforeQuery = fixture.EmbeddingGenerator.CallCount;

        var result = await fixture.RetrieveAsync("What time is checkout?");

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(HotelRetrievalPath.ExactFact, result.Path);
        Assert.Equal("zgy-policies#checkout", evidence.EvidenceKey);
        Assert.Equal("zgy-policies", evidence.SourceId);
        Assert.Equal("completed", evidence.IngestionStatus);
        Assert.Equal(64, evidence.ContentSha256.Length);
        Assert.Contains("relativePath", evidence.SourceMetadataJson, StringComparison.Ordinal);
        Assert.Equal(embeddingCallsBeforeQuery, fixture.EmbeddingGenerator.CallCount);
    }

    [Theory]
    [InlineData("What is the late checkout policy?")]
    [InlineData("What is the late-checkout policy?")]
    [InlineData("What time is LATE CHECKOUT?")]
    public async Task ExplicitLateCheckoutReturnsOnlyLateCheckoutBeforeEmbedding(string query)
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);
        var embeddingCallsBeforeQuery = fixture.EmbeddingGenerator.CallCount;

        var result = await fixture.RetrieveAsync(query);

        Assert.Equal(HotelRetrievalPath.ExactFact, result.Path);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("zgy-policies#late-checkout", evidence.EvidenceKey);
        Assert.Equal("late-checkout", evidence.VersionKey);
        Assert.Contains("subject to availability", evidence.Passage, StringComparison.Ordinal);
        Assert.Equal(embeddingCallsBeforeQuery, fixture.EmbeddingGenerator.CallCount);
    }

    [Theory]
    [InlineData("What time is checkout, and what is the late checkout policy?")]
    [InlineData("checkout and late checkout")]
    [InlineData("late checkout and checkout")]
    [InlineData("checkout and late-checkout")]
    public async Task CombinedCheckoutQuestionFallsBackAndRetainsBothSections(string query)
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);
        var embeddingCallsBeforeQuery = fixture.EmbeddingGenerator.CallCount;

        var result = await fixture.RetrieveAsync(query);

        Assert.Equal(HotelRetrievalPath.Hybrid, result.Path);
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-policies#checkout");
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-policies#late-checkout");
        Assert.Equal(embeddingCallsBeforeQuery + 1, fixture.EmbeddingGenerator.CallCount);
    }

    [Theory]
    [InlineData("checkout OR gym", "zgy-policies#checkout")]
    [InlineData("late checkout OR gym", "zgy-policies#late-checkout")]
    public async Task BroadCheckoutQuestionDoesNotDiscardOtherMatchingFacts(
        string query,
        string checkoutKey)
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);

        var result = await fixture.RetrieveAsync(query);

        Assert.Equal(HotelRetrievalPath.Hybrid, result.Path);
        Assert.Contains(result.Evidence, evidence => evidence.EvidenceKey == checkoutKey);
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenities#gym-location");
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenity-hours#gym-hours");
    }

    [Fact]
    public async Task ParaphraseFallsBackToVectorSearchWithInternalProvenance()
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);

        var result = await fixture.RetrieveAsync(
            "When do I need to leave my room on departure day?");

        Assert.Equal(HotelRetrievalPath.Hybrid, result.Path);
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-policies#checkout"
            && evidence.VectorRank is not null
            && evidence.VersionKey == "checkout");
    }

    [Fact]
    public async Task MultiSourceQuestionCanReturnBothGroundingSections()
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);

        var result = await fixture.RetrieveAsync("Where is the gym, and when can I use it?");

        Assert.Equal(HotelRetrievalPath.Hybrid, result.Path);
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenities#gym-location");
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenity-hours#gym-hours");
        Assert.All(result.Evidence, evidence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(evidence.SourceTitle));
            Assert.False(string.IsNullOrWhiteSpace(evidence.SectionAnchor));
            Assert.InRange(evidence.Passage.Length, 1, KnowledgeChunker.MaximumChunkCharacters);
            Assert.InRange(evidence.FinalRank, 1, 5);
        });
    }

    [Theory]
    [InlineData("Where is the hotel gym, and when is it open?")]
    [InlineData("Where is the gym and when is it open?")]
    public async Task FacetedQuestionMatchingOnlyOneSiblingFactFallsBackAndRetainsBothSections(
        string query)
    {
        // Full-text AND semantics let "open" exclude the location fact, leaving one
        // matching record. The sibling fact on the same authored topic still applies.
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);

        var result = await fixture.RetrieveAsync(query);

        Assert.Equal(HotelRetrievalPath.Hybrid, result.Path);
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenities#gym-location");
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenity-hours#gym-hours");
    }

    [Fact]
    public async Task AmbiguousExactTermFallsBackAndFusesLexicalAndVectorRanks()
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);

        var result = await fixture.RetrieveAsync("Where is the gym?");

        Assert.Equal(HotelRetrievalPath.Hybrid, result.Path);
        Assert.Contains(result.Evidence, evidence =>
            evidence.EvidenceKey == "zgy-amenities#gym-location"
            && evidence.LexicalRank is not null
            && evidence.VectorRank is not null);
        Assert.All(result.Evidence, evidence =>
        {
            var expectedScore =
                (evidence.LexicalRank is int lexicalRank ? 1d / (60 + lexicalRank) : 0)
                + (evidence.VectorRank is int vectorRank ? 1d / (60 + vectorRank) : 0);
            Assert.Equal(expectedScore, evidence.FusedScore);
        });
    }

    [Fact]
    public async Task VectorSearchRejectsAnEmbeddingIdentityDifferentFromTheIndexedModel()
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: true);
        fixture.ChangeConfiguredEmbeddingModel("different-retrieval-model");

        var error = await Assert.ThrowsAsync<WorkflowFailedException>(() =>
            fixture.RetrieveAsync("When do I need to leave my room on departure day?"));

        Assert.Contains(
            "do not match the indexed vector identity",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateNoEvidenceQuestionHasEmptyShapeWhenCorpusIsEmpty()
    {
        await using var fixture = await RetrievalFixture.StartAsync(postgres, ingestKnowledge: false);

        // The candidate evaluation package is still pending corpus-absence confirmation.
        // This verifies only the empty-index result shape; it does not confirm that Hotel ZGY
        // lacks this information or establish retrieval acceptance.
        var result = await fixture.RetrieveAsync("What is the guest Wi-Fi password?");

        Assert.Equal(HotelRetrievalPath.NoEvidence, result.Path);
        Assert.Empty(result.Evidence);
        Assert.False(result.HasEvidence);
    }

    [Workflow("Concierge.Tests.HotelRetrieval")]
    private sealed class RetrievalTestWorkflow
    {
        [WorkflowRun]
        public Task<HotelRetrievalResult> RunAsync(HotelRetrievalRequest request) =>
            Workflow.ExecuteActivityAsync(
                (HotelKnowledgeActivities activities) => activities.RetrieveAsync(request),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(2),
                    RetryPolicy = new RetryPolicy { MaximumAttempts = 1 },
                });
    }

    private sealed class RetrievalFixture : IAsyncDisposable
    {
        private const string TaskQueue = "Concierge.HotelRetrieval.Tests";
        private readonly PostgreSqlTestDatabase database;
        private readonly NpgsqlDataSource dataSource;
        private readonly WorkflowEnvironment environment;
        private readonly IHost host;
        private readonly SetupOptions setupOptions;
        private readonly EmbeddingProviderOptions embeddingOptions;
        private readonly string contentRoot;

        private RetrievalFixture(
            PostgreSqlTestDatabase database,
            NpgsqlDataSource dataSource,
            WorkflowEnvironment environment,
            IHost host,
            SetupOptions setupOptions,
            EmbeddingProviderOptions embeddingOptions,
            string contentRoot,
            TestEmbeddingGenerator embeddingGenerator)
        {
            this.database = database;
            this.dataSource = dataSource;
            this.environment = environment;
            this.host = host;
            this.setupOptions = setupOptions;
            this.embeddingOptions = embeddingOptions;
            this.contentRoot = contentRoot;
            EmbeddingGenerator = embeddingGenerator;
        }

        public TestEmbeddingGenerator EmbeddingGenerator { get; }

        public static async Task<RetrievalFixture> StartAsync(
            PostgreSqlFixture postgres,
            bool ingestKnowledge)
        {
            var database = await postgres.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await new DatabaseMigrator(dataSource, NullLogger<DatabaseMigrator>.Instance)
                .MigrateAsync();

            var contentRoot = Path.Combine(Path.GetTempPath(), $"hotel-retrieval-{Guid.NewGuid():N}");
            var knowledgeDirectory = Path.Combine(contentRoot, "knowledge");
            Directory.CreateDirectory(knowledgeDirectory);
            var setupOptions = new SetupOptions { KnowledgeDirectory = "knowledge" };
            var embeddingOptions = new EmbeddingProviderOptions
            {
                Provider = "Ollama",
                Model = "retrieval-test-model",
                Dimensions = EmbeddingModelIdentity.MigratedSchemaDimensions,
            };
            var embeddingGenerator = new TestEmbeddingGenerator();
            var environment = await WorkflowEnvironment.StartLocalAsync();

            if (ingestKnowledge)
            {
                WriteSources(knowledgeDirectory);
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton(dataSource);
            builder.Services.AddSingleton<ITemporalClient>(environment.Client);
            builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                embeddingGenerator);
            builder.Services.AddSingleton<IOptions<SetupOptions>>(Options.Create(setupOptions));
            builder.Services.AddSingleton<IOptions<EmbeddingProviderOptions>>(
                Options.Create(embeddingOptions));
            builder.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(contentRoot));
            builder.Services.AddSingleton<KnowledgeMarkdownCatalog>();
            builder.Services.AddHostedTemporalWorker(TaskQueue)
                .AddWorkflow<HotelSetupWorkflow>()
                .AddWorkflow<RetrievalTestWorkflow>()
                .AddScopedActivities<SetupActivities>()
                .AddScopedActivities<HotelKnowledgeActivities>();
            var host = builder.Build();
            await host.StartAsync();

            var fixture = new RetrievalFixture(
                database,
                dataSource,
                environment,
                host,
                setupOptions,
                embeddingOptions,
                contentRoot,
                embeddingGenerator);
            if (ingestKnowledge)
            {
                await fixture.IngestAsync();
            }

            return fixture;
        }

        public async Task<HotelRetrievalResult> RetrieveAsync(string query)
        {
            var handle = await environment.Client.StartWorkflowAsync(
                (RetrievalTestWorkflow workflow) =>
                    workflow.RunAsync(new HotelRetrievalRequest(query)),
                new WorkflowOptions($"hotel-retrieval-{Guid.NewGuid():N}", TaskQueue));
            return await handle.GetResultAsync();
        }

        public void ChangeConfiguredEmbeddingModel(string model) =>
            embeddingOptions.Model = model;

        private async Task IngestAsync()
        {
            var request = new SetupWorkflowRequest(
                Guid.NewGuid().ToString("N"),
                new SeedDemoDataRequest(
                    setupOptions.OverwriteExistingSeedRecords,
                    setupOptions.Guests,
                    setupOptions.Stays),
                new EmbeddingModelIdentity(
                    "Ollama",
                    "retrieval-test-model",
                    EmbeddingModelIdentity.MigratedSchemaDimensions));
            var handle = await environment.Client.StartWorkflowAsync(
                (HotelSetupWorkflow workflow) => workflow.RunAsync(request),
                new WorkflowOptions(Guid.NewGuid().ToString("N"), TaskQueue));
            await handle.GetResultAsync();
        }

        private static void WriteSources(string knowledgeDirectory)
        {
            File.WriteAllText(
                Path.Combine(knowledgeDirectory, "policies.md"),
                """
                ---
                sourceId: zgy-policies
                title: Hotel ZGY guest policies
                ---

                ## Checkout

                Hotel ZGY checkout time is 11:00 a.m. Eastern Time.

                ## Late checkout

                Late checkout is subject to availability and must be requested with the front
                desk. The concierge can explain this policy but cannot change a checkout time
                or promise that a late checkout will be granted.
                """);
            File.WriteAllText(
                Path.Combine(knowledgeDirectory, "amenities.md"),
                """
                ---
                sourceId: zgy-amenities
                title: Hotel ZGY amenities
                ---

                ## Gym location

                The Hotel ZGY fitness room is on the fourth floor, next to the east elevators.
                """);
            File.WriteAllText(
                Path.Combine(knowledgeDirectory, "amenity-hours.md"),
                """
                ---
                sourceId: zgy-amenity-hours
                title: Hotel ZGY amenity hours
                ---

                ## Gym hours

                The Hotel ZGY fitness room is open daily from 6:00 a.m. to 10:00 p.m. Eastern Time.
                """);
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

    private sealed class TestEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            cancellationToken.ThrowIfCancellationRequested();
            var embeddings = values.Select(value => new Embedding<float>(VectorFor(value)));
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static float[] VectorFor(string value)
        {
            var vector = new float[EmbeddingModelIdentity.MigratedSchemaDimensions];
            if (value.Contains("gym, and when can I use it", StringComparison.OrdinalIgnoreCase)
                || value.Contains("gym, and when is it open", StringComparison.OrdinalIgnoreCase)
                || value.Contains("gym and when is it open", StringComparison.OrdinalIgnoreCase))
            {
                vector[1] = 1;
                vector[2] = 1;
            }
            else if (value.Contains("Where is the gym?", StringComparison.OrdinalIgnoreCase))
            {
                vector[1] = 1;
            }
            else if (value.Contains("Gym location", StringComparison.OrdinalIgnoreCase))
            {
                vector[1] = 1;
            }
            else if (value.Contains("Gym hours", StringComparison.OrdinalIgnoreCase))
            {
                vector[2] = 1;
            }
            else if (value.Contains("checkout", StringComparison.OrdinalIgnoreCase)
                || value.Contains("leave my room", StringComparison.OrdinalIgnoreCase))
            {
                vector[0] = 1;
            }

            return vector;
        }
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Concierge.HotelRetrieval.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
