using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Infrastructure;

public interface IEnvironmentVariableSource
{
    string? Get(string name);
}

public sealed class ProcessEnvironmentVariableSource : IEnvironmentVariableSource
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed record ProductionReadinessAssessment(
    bool Ready,
    IReadOnlyList<string> MissingEnvironmentVariables,
    IReadOnlyList<string> ConfigurationIssues);

public sealed class DeploymentReadinessChecker(
    InvestigationProfileStore profiles,
    IOptions<IncidentBotOptions> botOptions,
    IOptions<PagerDutyOptions> pagerDutyOptions,
    IOptions<SlackOptions> slackOptions,
    IOptions<LiteLlmOptions> liteLlmOptions,
    IOptions<IngressIdentityOptions> identityOptions,
    IEnvironmentVariableSource environment)
{
    public ProductionReadinessAssessment CheckProduction()
    {
        var requiredVariables = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<string>();

        if (pagerDutyOptions.Value.RequireSignature)
        {
            requiredVariables.Add(pagerDutyOptions.Value.WebhookSecretEnv);
        }
        else
        {
            issues.Add("PagerDuty webhook signature validation must be enabled.");
        }

        if (slackOptions.Value.Enabled)
        {
            requiredVariables.Add(slackOptions.Value.BotTokenEnv);
            requiredVariables.Add(slackOptions.Value.AppTokenEnv);
        }
        else if (botOptions.Value.RequireSlackForReadiness)
        {
            issues.Add("Slack must be enabled for this deployment.");
        }

        requiredVariables.Add(liteLlmOptions.Value.ApiKeyEnv);

        if (botOptions.Value.CollectionEnabled)
        {
            foreach (var variable in profiles.RequiredCredentialEnvironmentVariables(botOptions.Value.McpEnabled))
            {
                requiredVariables.Add(variable);
            }
        }

        if (!botOptions.Value.McpEnabled && profiles.UsesMcpTransport())
        {
            issues.Add("At least one profile uses MCP transport while IncidentBot:McpEnabled is false.");
        }

        if (!identityOptions.Value.Required)
        {
            issues.Add("Trusted ingress identity enforcement must be enabled.");
        }

        if (!Uri.TryCreate(botOptions.Value.PublicBaseUrl, UriKind.Absolute, out var publicUri)
            || publicUri.IsLoopback
            || string.Equals(publicUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("IncidentBot:PublicBaseUrl must be a non-loopback ingress URL.");
        }

        issues.AddRange(profiles.ProductionConfigurationIssues());

        var missingVariables = requiredVariables
            .Where(variable => !IsConfigured(environment.Get(variable)))
            .OrderBy(variable => variable, StringComparer.Ordinal)
            .ToList();
        var configurationIssues = issues
            .Distinct(StringComparer.Ordinal)
            .OrderBy(issue => issue, StringComparer.Ordinal)
            .ToList();

        return new ProductionReadinessAssessment(
            missingVariables.Count == 0 && configurationIssues.Count == 0,
            missingVariables,
            configurationIssues);
    }

    private static bool IsConfigured(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "replace-me", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "change-me", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "changeme", StringComparison.OrdinalIgnoreCase);
}
