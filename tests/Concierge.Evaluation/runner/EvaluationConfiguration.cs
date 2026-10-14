using Microsoft.Extensions.Configuration;

namespace Concierge.Evaluation;

internal static class EvaluationConfiguration
{
    internal static IConfigurationBuilder AddEvaluationDefaults(
        this IConfigurationBuilder configuration,
        string repoRoot)
    {
        // Setup belongs to the worker; AI defaults belong to AppHost. Environment
        // overrides allow evaluation credentials without loading or copying secrets.
        return configuration
            .AddJsonFile(Path.Combine(repoRoot, "src/Concierge.WorkflowServer/appsettings.json"))
            .AddJsonFile(Path.Combine(repoRoot, "src/Concierge.WorkflowServer/appsettings.Development.json"))
            .AddJsonFile(Path.Combine(repoRoot, "src/Concierge.AppHost/appsettings.json"))
            .AddJsonFile(Path.Combine(repoRoot, "src/Concierge.AppHost/appsettings.Development.json"))
            .AddEnvironmentVariables();
    }
}
