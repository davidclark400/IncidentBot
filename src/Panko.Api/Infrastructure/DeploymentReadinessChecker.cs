using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using Panko.Api.Cases;
using Microsoft.Extensions.Options;

namespace Panko.Api.Infrastructure;

public sealed record ProductionReadinessAssessment(
    bool Ready,
    IReadOnlyList<string> MissingEnvironmentVariables,
    IReadOnlyList<string> ConfigurationIssues);

public sealed class DeploymentReadinessChecker(
    RecipeStore recipes,
    IOptions<PankoOptions> pankoOptions,
    IOptions<PagerDutyOptions> pagerDutyOptions,
    IOptions<CrumbSourceOptions> crumbSourceOptions,
    IOptions<SlackOptions> slackOptions,
    IOptions<LiteLlmOptions> liteLlmOptions,
    IOptions<JwtIdentityOptions> identityOptions,
    IOptions<TeamAuthorizationOptions> teamAuthorizationOptions,
    IOptions<TrustedProxyOptions> trustedProxyOptions,
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
        requiredVariables.Add(crumbSourceOptions.Value.PagerDuty.CredentialEnv);

        if (slackOptions.Value.Enabled)
        {
            requiredVariables.Add(slackOptions.Value.BotTokenEnv);
            requiredVariables.Add(slackOptions.Value.AppTokenEnv);
            AddEndpointIssue("Slack:ApiBaseUrl", slackOptions.Value.ApiBaseUrl, issues);

            foreach (var ownership in recipes.All)
            {
                var recipe = recipes.ResolveById(ownership.RecipeId);
                if (!IsSlackChannelId(recipe.SlackChannel))
                {
                    issues.Add(
                        $"Recipe '{ownership.RecipeId}' must use an exact Slack channel ID rather than a channel name.");
                }
                var channelTeam = SlackChannelAuthorization.ResolveTeam(
                    slackOptions.Value,
                    recipe.SlackChannel);
                if (!string.Equals(channelTeam, ownership.Team, StringComparison.Ordinal))
                {
                    issues.Add(
                        $"Slack channel '{recipe.SlackChannel}' must map to team '{ownership.Team}' for Recipe '{ownership.RecipeId}'.");
                }
            }

            if (slackOptions.Value.PromptMentionsEnabled)
            {
                foreach (var channelId in slackOptions.Value.PromptChannelRecipes.Keys)
                {
                    if (!IsSlackChannelId(channelId))
                    {
                        issues.Add(
                            $"Slack prompt channel '{channelId}' must be an exact Slack channel ID.");
                    }
                    var access = SlackChannelAuthorization.ResolvePrompt(
                        slackOptions.Value,
                        recipes,
                        channelId);
                    if (!access.IsAuthorized)
                    {
                        issues.Add(
                            $"Slack prompt channel '{channelId}' must map to a Recipe owned by the same team.");
                    }
                }
            }
        }
        else if (pankoOptions.Value.RequireSlackForReadiness)
        {
            issues.Add("Slack must be enabled for this deployment.");
        }

        if (pankoOptions.Value.CrumbCollectionEnabled)
        {
            requiredVariables.Add(liteLlmOptions.Value.ApiKeyEnv);
            AddEndpointIssue("LiteLlm:BaseUrl", liteLlmOptions.Value.BaseUrl, issues);

            foreach (var variable in recipes.RequiredCredentialEnvironmentVariables(pankoOptions.Value.McpEnabled))
            {
                requiredVariables.Add(variable);
            }

            if (!pankoOptions.Value.McpEnabled && recipes.EnabledSourceUsesMcpTransport())
            {
                issues.Add("At least one enabled Crumb source uses MCP transport while Panko:McpEnabled is false.");
            }

            issues.AddRange(recipes.ProductionConfigurationIssues());
        }

        if (!identityOptions.Value.Required)
        {
            issues.Add("JWT identity enforcement must be enabled.");
        }
        if (!identityOptions.Value.RequireHttpsMetadata)
        {
            issues.Add("OIDC discovery metadata must require HTTPS.");
        }
        AddHttpsEndpointIssue("JwtIdentity:Authority", identityOptions.Value.Authority, issues);
        AddHttpsEndpointIssue("JwtIdentity:Issuer", identityOptions.Value.Issuer, issues);
        if (!IsConfigured(identityOptions.Value.Audience))
        {
            issues.Add("JwtIdentity:Audience must identify the Panko resource.");
        }

        if (teamAuthorizationOptions.Value.TeamClaimTypes.Count == 0
            && (teamAuthorizationOptions.Value.GroupClaimTypes.Count == 0
                || teamAuthorizationOptions.Value.GroupTeamMappings.Count == 0))
        {
            issues.Add("Team authorization needs a signed team claim or directory-group mapping.");
        }
        var knownTeams = recipes.All
            .Select(recipe => recipe.Team)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var mapping in teamAuthorizationOptions.Value.GroupTeamMappings)
        {
            if (!knownTeams.Contains(mapping.Value))
            {
                issues.Add(
                    $"TeamAuthorization group '{mapping.Key}' maps to unknown team '{mapping.Value}'.");
            }
        }

        if (trustedProxyOptions.Value.KnownProxies.Count == 0
            && trustedProxyOptions.Value.KnownNetworks.Count == 0)
        {
            issues.Add("At least one trusted proxy IP address or CIDR network must be configured.");
        }
        else if (!TrustedProxyConfiguration.IsValid(trustedProxyOptions.Value))
        {
            issues.Add("Trusted proxy addresses must be explicit and must not use catch-all networks.");
        }

        if (!TryGetHttpUri(pankoOptions.Value.PublicBaseUrl, out var publicUri)
            || publicUri.IsLoopback
            || string.Equals(publicUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IsPlaceholderHost(publicUri))
        {
            issues.Add("Panko:PublicBaseUrl must be a non-loopback ingress URL.");
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

    private static void AddHttpsEndpointIssue(string key, string value, ICollection<string> issues)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            issues.Add($"{key} must be an absolute HTTPS URL.");
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

    private static bool IsSlackChannelId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= 9
        && value[0] is 'C' or 'G' or 'D'
        && value.Skip(1).All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9');
}
