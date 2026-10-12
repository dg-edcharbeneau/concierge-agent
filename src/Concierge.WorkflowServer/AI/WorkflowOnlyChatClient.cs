using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Guards the workflow boundary: package middleware must replace every invocation with
/// a Temporal activity before this provider could be called.
/// </summary>
internal sealed class WorkflowOnlyChatClient : IChatClient
{
    private const string ErrorMessage =
        "A model client cannot be invoked directly from workflow code.";

    /// <summary>
    /// Always throws; durable-execution middleware must route the call to an activity first.
    /// </summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(ErrorMessage);

    /// <summary>
    /// Always throws; durable-execution middleware must route the call to an activity first.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException(ErrorMessage);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
