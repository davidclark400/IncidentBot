using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Infrastructure;

public sealed record ProductionReadinessAssessment(
    bool Ready,
    IReadOnlyList<string> MissingEnvironmentVariables,
    IReadOnlyList<string> ConfigurationIssues);

public sealed class DeploymentReadinessChecker(
    InvestigationProfileStore profiles,
    IOptions<IncidentBotOptions> botOptions,
    IOptions<PagerDutyOptions> pagerDutyOptions,
    IOptions<EvidenceSourceOptions> evidenceSourceOptions,
    IOptions<SlackOptions> slackOptions,
    IOptions<LiteLlmOptions> liteLlmOptions,
    IOptions<IngressIdentityOptions> identityOptions,
    ICredentialProvider credentials)
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
        requiredVariables.Add(evidenceSourceOptions.Value.PagerDuty.CredentialEnv);

        if (slackOptions.Value.Enabled)
        {
            requiredVariables.Add(slackOptions.Value.BotTokenEnv);
            requiredVariables.Add(slackOptions.Value.AppTokenEnv);
            AddEndpointIssue("Slack:ApiBaseUrl", slackOptions.Value.ApiBaseUrl, issues);
        }
        else if (botOptions.Value.RequireSlackForReadiness)
        {
            issues.Add("Slack must be enabled for this deployment.");
        }

        if (botOptions.Value.CollectionEnabled)
        {
            requiredVariables.Add(liteLlmOptions.Value.ApiKeyEnv);
            AddEndpointIssue("LiteLlm:BaseUrl", liteLlmOptions.Value.BaseUrl, issues);

            foreach (var variable in profiles.RequiredCredentialEnvironmentVariables(botOptions.Value.McpEnabled))
            {
                requiredVariables.Add(variable);
            }

            if (!botOptions.Value.McpEnabled && profiles.EnabledSourceUsesMcpTransport())
            {
                issues.Add("At least one enabled evidence source uses MCP transport while IncidentBot:McpEnabled is false.");
            }

            issues.AddRange(profiles.ProductionConfigurationIssues());
        }

        if (!identityOptions.Value.Required)
        {
            issues.Add("Trusted ingress identity enforcement must be enabled.");
        }

        if (!TryGetHttpUri(botOptions.Value.PublicBaseUrl, out var publicUri)
            || publicUri.IsLoopback
            || string.Equals(publicUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IsPlaceholderHost(publicUri))
        {
            issues.Add("IncidentBot:PublicBaseUrl must be a non-loopback ingress URL.");
        }

        var missingVariables = requiredVariables
            .Where(variable => !IsConfigured(credentials.Get(variable)))
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

    private static void AddEndpointIssue(string key, string value, ICollection<string> issues)
    {
        if (!TryGetHttpUri(value, out var uri))
        {
            issues.Add($"{key} must be an absolute HTTP(S) URL.");
        }
        else if (IsPlaceholderHost(uri))
        {
            issues.Add($"{key} still uses placeholder host '{uri.Host}'.");
        }
    }

    private static bool TryGetHttpUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!)
        && uri.Scheme is "http" or "https";

    private static bool IsPlaceholderHost(Uri uri) =>
        uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "example", StringComparison.OrdinalIgnoreCase);
}
