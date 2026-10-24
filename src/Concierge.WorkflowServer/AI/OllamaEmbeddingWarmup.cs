using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Loads the Ollama embedding model once at worker start so the first guest question does not pay
/// the model load (~3 s). Startup setup only re-embeds changed knowledge, so after an Ollama restart
/// nothing else would load the model before the first retrieval. The request carries the same
/// <c>keep_alive</c> as every other embedding call, so the model then stays resident.
/// Failure is non-fatal: the first retrieval simply loads the model as before.
/// </summary>
public sealed class OllamaEmbeddingWarmup(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<EmbeddingProviderOptions> options,
    ILogger<OllamaEmbeddingWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var embeddingOptions = options.Value;
        if (!StringComparer.OrdinalIgnoreCase.Equals(embeddingOptions.Provider, AIProviderNames.Ollama))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // A cold load can outlast the short retrieval bound; use the ingestion bound.
            await embeddingGenerator.ForIngestion()
                .GenerateAsync(["warm-up"], cancellationToken: stoppingToken);
            logger.LogInformation(
                "Warmed Ollama embedding model {EmbeddingModel} in {ElapsedMs} ms.",
                embeddingOptions.Model,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Ollama embedding warm-up for {EmbeddingModel} failed after {ElapsedMs} ms; "
                + "the first retrieval will load the model instead.",
                embeddingOptions.Model,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
