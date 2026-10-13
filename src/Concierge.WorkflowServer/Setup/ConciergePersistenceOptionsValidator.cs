using Concierge.Contracts;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Setup;

public sealed class ConciergePersistenceOptionsValidator
    : IValidateOptions<ConciergePersistenceOptions>
{
    /// <summary>
    /// Requires <see cref="ConciergePersistenceOptions.FlushIntervalSeconds"/> to be between 1 and 600
    /// and <see cref="ConciergePersistenceOptions.FlushBatchSize"/> to be between 1 and
    /// <see cref="ConciergePersistenceOptions.MaxFlushBatchSize"/>.
    /// </summary>
    public ValidateOptionsResult Validate(
        string? name,
        ConciergePersistenceOptions options)
    {
        if (options.FlushIntervalSeconds is < 1 or > 600)
        {
            return ValidateOptionsResult.Fail(
                $"{ConciergePersistenceOptions.SectionName}:FlushIntervalSeconds must be between 1 and 600.");
        }

        if (options.FlushBatchSize < 1 || options.FlushBatchSize > ConciergePersistenceOptions.MaxFlushBatchSize)
        {
            return ValidateOptionsResult.Fail(
                $"{ConciergePersistenceOptions.SectionName}:FlushBatchSize must be between 1 and " +
                $"{ConciergePersistenceOptions.MaxFlushBatchSize}.");
        }

        return ValidateOptionsResult.Success;
    }
}
