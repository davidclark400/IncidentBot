using IncidentBot.Api.Domain;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Options;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Security;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IncidentBot.Api.Profiles;

public sealed class InvestigationProfileStore : IInvestigationProfileProvider, ISlackQueryProfileProvider
{
    private static readonly HashSet<string> StandardLabelKeys = new(StringComparer.Ordinal)
    {
        "service", "environment", "cluster", "region", "component", "alert_rule_id"
    };

    private readonly ProfileDocument _document;
    private readonly EvidenceSourceRegistry evidenceSources;
    private readonly KafkaMetricPlanStore? kafkaMetricPlans;

    public InvestigationProfileStore(
        IOptions<IncidentBotOptions> options,
        IWebHostEnvironment environment,
        EvidenceSourceRegistry evidenceSources,
        KafkaMetricPlanStore? kafkaMetricPlans = null)
    {
        this.evidenceSources = evidenceSources;
        this.kafkaMetricPlans = kafkaMetricPlans;
        var configuredPath = options.Value.ProfilesPath;
        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!File.Exists(path))
        {
            var outputPath = Path.Combine(AppContext.BaseDirectory, configuredPath);
            path = File.Exists(outputPath) ? outputPath : path;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Investigation profile file was not found: {path}");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        _document = deserializer.Deserialize<ProfileDocument>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Investigation profile file is empty.");
        Validate(_document);
    }

    public string Revision => _document.Revision;
    public string FallbackSlackChannel => _document.FallbackSlackChannel;

    public IReadOnlyList<ConfiguredEvidenceSource> ConfiguredEvidenceSources() =>
        _document.Profiles
            .SelectMany(profile => evidenceSources.ConfiguredTransports(profile)
                .Select(value => new { value.Source, value.Transport, ProfileId = profile.Id }))
            .GroupBy(value => new
            {
                value.Source,
                value.Transport.Mode,
                value.Transport.BaseUrl,
                value.Transport.CredentialEnv,
                value.Transport.TimeoutSeconds,
                value.Transport.MaxItems,
                value.Transport.MaxBytes,
                McpServerUrl = value.Transport.Mcp?.ServerUrl,
                McpToolName = value.Transport.Mcp?.ToolName,
                McpCredentialEnv = value.Transport.Mcp?.CredentialEnv
            })
            .Select(group => new ConfiguredEvidenceSource(
                group.Key.Source,
                group.First().Transport,
                group.Select(value => value.ProfileId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(value => value.Source, StringComparer.Ordinal)
            .ThenBy(value => value.Transport.Mode, StringComparer.Ordinal)
            .ThenBy(value => value.Transport.BaseUrl, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<string> RequiredCredentialEnvironmentVariables(bool mcpEnabled)
    {
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transport in _document.Profiles.SelectMany(profile =>
                     evidenceSources.ConfiguredTransports(profile).Select(value => value.Transport)))
        {
            var variable = transport.Mode == "mcp"
                ? mcpEnabled ? transport.Mcp?.CredentialEnv : null
                : transport.CredentialEnv;
            if (!string.IsNullOrWhiteSpace(variable))
            {
                variables.Add(variable);
            }
        }

        return variables.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    public bool EnabledSourceUsesMcpTransport() =>
        _document.Profiles.SelectMany(profile =>
            evidenceSources.ConfiguredTransports(profile).Select(value => value.Transport))
            .Any(transport => transport.Mode == "mcp");

    public IReadOnlyList<string> ProductionConfigurationIssues()
    {
        var issues = new List<string>();
        foreach (var profile in _document.Profiles)
        {
            foreach (var (source, transport) in evidenceSources.ConfiguredTransports(profile))
            {
                var configuredUrl = transport.Mode == "mcp" ? transport.Mcp?.ServerUrl : transport.BaseUrl;
                if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri)
                    && uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"Evidence source '{source}' still uses placeholder host '{uri.Host}'.");
                }
            }
        }

        return issues.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    internal IReadOnlyDictionary<string, string> FilterPersistedLabels(
        InvestigationProfile profile,
        IReadOnlyDictionary<string, string> labels)
    {
        var allowedKeys = new HashSet<string>(StandardLabelKeys, StringComparer.Ordinal);
        foreach (var selector in profile.Selectors)
        {
            foreach (var key in selector.Labels.Keys)
            {
                allowedKeys.Add(key);
            }
        }

        return labels
            .Where(pair => allowedKeys.Contains(pair.Key) && !LooksSensitive(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public InvestigationProfile Resolve(string serviceId, IReadOnlyDictionary<string, string> labels)
    {
        var candidates = _document.Profiles
            .Where(profile => string.Equals(profile.PagerDutyServiceId, serviceId, StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {
            return new InvestigationProfile
            {
                Id = "unmapped",
                PagerDutyServiceId = serviceId,
                Team = "unmapped",
                SlackChannel = _document.FallbackSlackChannel
            };
        }

        var matches = candidates
            .Select(profile => new
            {
                Profile = profile,
                Score = BestSelectorScore(profile, labels)
            })
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Profile.Id, StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No investigation profile selector matched PagerDuty service '{serviceId}'.");
        }

        if (matches.Count > 1 && matches[0].Score == matches[1].Score)
        {
            throw new InvalidOperationException(
                $"Ambiguous investigation profiles '{matches[0].Profile.Id}' and '{matches[1].Profile.Id}' for service '{serviceId}'.");
        }

        return matches[0].Profile;
    }

    public SlackQueryProfile Resolve(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var profile = _document.Profiles.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.Ordinal));
        return profile is null
            ? throw new InvalidOperationException($"Investigation profile '{profileId}' was not found.")
            : new SlackQueryProfile(_document.Revision, profile);
    }

    private static int BestSelectorScore(InvestigationProfile profile, IReadOnlyDictionary<string, string> labels)
    {
        if (profile.Selectors.Count == 0)
        {
            return 0;
        }

        var scores = profile.Selectors.Select(selector =>
        {
            var required = new Dictionary<string, string>(selector.Labels, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(selector.AlertRuleId))
            {
                required["alert_rule_id"] = selector.AlertRuleId;
            }

            return required.All(pair => labels.TryGetValue(pair.Key, out var actual)
                && string.Equals(actual, pair.Value, StringComparison.Ordinal))
                ? required.Count
                : -1;
        });
        return scores.Max();
    }

    private static bool LooksSensitive(string key)
    {
        var normalized = key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("api_key", StringComparison.Ordinal)
            || normalized.Contains("connection_string", StringComparison.Ordinal);
    }

    private void Validate(ProfileDocument document)
    {
        if (document.Version != 2)
        {
            throw new InvalidOperationException($"Unsupported profile schema version {document.Version}.");
        }

        if (string.IsNullOrWhiteSpace(document.Revision) || string.IsNullOrWhiteSpace(document.FallbackSlackChannel))
        {
            throw new InvalidOperationException("Profile revision and fallbackSlackChannel are required.");
        }

        var duplicateIds = document.Profiles.GroupBy(profile => profile.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateIds is not null)
        {
            throw new InvalidOperationException($"Duplicate investigation profile id '{duplicateIds.Key}'.");
        }

        foreach (var profile in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.PagerDutyServiceId))
            {
                throw new InvalidOperationException("Every profile requires id and pagerDutyServiceId.");
            }

            if (profile.Nomad?.Namespaces.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Jobs.Count == 0) == true)
            {
                throw new InvalidOperationException($"Profile '{profile.Id}' contains an empty Nomad namespace or job allowlist.");
            }

            if (profile.Selectors.SelectMany(selector => selector.Labels.Keys).Any(LooksSensitive))
            {
                throw new InvalidOperationException($"Profile '{profile.Id}' uses a sensitive label name in a selector.");
            }

            if (profile.SlackPromptLabels.Count > SlackQueryPlanCompiler.MaximumLabels)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' contains too many Slack prompt labels.");
            }
            var templateRenderer = new SafeTemplateRenderer();
            foreach (var label in profile.SlackPromptLabels)
            {
                templateRenderer.Render("{{" + label.Key + "}}", profile.SlackPromptLabels);
            }

            if (profile.Grafana?.Queries.Any(query => string.IsNullOrWhiteSpace(query.DatasourceUid)
                    || string.IsNullOrWhiteSpace(query.Expression)
                    || string.IsNullOrWhiteSpace(query.Name)) == true)
            {
                throw new InvalidOperationException($"Profile '{profile.Id}' contains an invalid Grafana query.");
            }
            ValidateUniqueQueryNames(
                profile.Id,
                "Grafana",
                profile.Grafana?.Queries.Select(query => query.Name) ?? []);

            if (profile.Kafka is not null)
            {
                if (kafkaMetricPlans is null)
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.Id}' enables Kafka but no Kafka metric catalog is available.");
                }
                _ = kafkaMetricPlans.Resolve(profile.Kafka);
            }

            if (profile.VictoriaLogs?.Queries.Any(query => string.IsNullOrWhiteSpace(query.Name)
                    || string.IsNullOrWhiteSpace(query.Expression)) == true)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' contains an invalid VictoriaLogs query.");
            }
            ValidateUniqueQueryNames(
                profile.Id,
                "VictoriaLogs",
                profile.VictoriaLogs?.Queries.Select(query => query.Name) ?? []);

            if (profile.VictoriaLogs is { StreamFilters.Count: 0 })
            {
                throw new InvalidOperationException($"Profile '{profile.Id}' must scope VictoriaLogs with streamFilters.");
            }
        }
    }

    private static void ValidateUniqueQueryNames(
        string profileId,
        string source,
        IEnumerable<string> queryNames)
    {
        var duplicate = queryNames.GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Profile '{profileId}' contains duplicate {source} query name '{duplicate.Key}'.");
        }
    }

}
