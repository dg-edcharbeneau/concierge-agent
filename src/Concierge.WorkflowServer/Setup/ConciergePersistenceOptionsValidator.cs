using Concierge.Contracts;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Setup;

public sealed class ConciergePersistenceOptionsValidator
    : IValidateOptions<ConciergePersistenceOptions>
{
    /// <summary>
    /// Requires <see cref="ConciergePersistenceOptions.FlushIntervalSeconds"/> to be between 1 and 600.
    /// </summary>
    public ValidateOptionsResult Validate(
        string? name,
        ConciergePersistenceOptions options) =>
        options.FlushIntervalSeconds is >= 1 and <= 600
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ConciergePersistenceOptions.SectionName}:FlushIntervalSeconds must be between 1 and 600.");
}
