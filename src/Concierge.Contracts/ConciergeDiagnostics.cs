using Temporalio.Exceptions;

namespace Concierge.Contracts;

/// <summary>Safe diagnostic values shared by the Web service, HTTP fallback handlers, and workflows.</summary>
public static class ConciergeDiagnostics
{
    /// <summary>
    /// Returns exception type names from outermost to innermost, without messages, stacks, or identifiers.
    /// Temporal application failures include only allowlisted remote exception types; arbitrary
    /// <see cref="ApplicationFailureException.ErrorType"/> values are never logged.
    /// </summary>
    public static string ExceptionTypeChain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var types = new List<string>();
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            types.Add(cause.GetType().Name);
            if (cause is ApplicationFailureException failure
                && failure.ErrorType is nameof(InvalidOperationException) or nameof(HttpRequestException)
                    or nameof(TimeoutException) or "PostgresException" or "NpgsqlException")
            {
                types.Add(failure.ErrorType);
            }
        }

        return string.Join(" -> ", types);
    }
}
