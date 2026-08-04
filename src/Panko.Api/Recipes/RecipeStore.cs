using System.Text.RegularExpressions;
using Panko.Api.Domain;
using Panko.Api.Crumbs;
using Panko.Api.Options;
using Panko.Api.Cases;
using Panko.Api.Security;
using Panko.Observability;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Panko.Api.Recipes;

public sealed class RecipeStore :
    IRecipeProvider,
    ISlackQueryRecipeProvider,
    IRecipeOwnershipCatalog
{
    private const int MaximumConsulServicesPerRecipe = 100;
    private const int MaximumAnchorPatternsPerVictoriaLogsQuery = 20;

    private static readonly HashSet<string> GrafanaReducers =
        new(["maximum", "minimum", "last"], StringComparer.Ordinal);
    private static readonly HashSet<string> GrafanaDirections =
        new(["above", "below"], StringComparer.Ordinal);
    private static readonly HashSet<string> GrafanaCrumbModes =
        new(["context", "anomaly"], StringComparer.Ordinal);
    private static readonly HashSet<string> GrafanaRequirements =
        new(["required", "optional"], StringComparer.Ordinal);

    private static readonly HashSet<string> StandardLabelKeys = new(StringComparer.Ordinal)
    {
        "service", "environment", "cluster", "region", "component", "alert_rule_id"
    };

    private readonly RecipeCatalog _document;
    private readonly CrumbSourceRegistry crumbSources;
    private readonly KafkaMetricPlanStore? kafkaMetricPlans;
    private readonly ServiceMetricPlanStore? serviceMetricPlans;

    public RecipeStore(
        IOptions<PankoOptions> options,
        IWebHostEnvironment environment,
        CrumbSourceRegistry crumbSources,
        KafkaMetricPlanStore? kafkaMetricPlans = null,
        ServiceMetricPlanStore? serviceMetricPlans = null)
    {
        this.crumbSources = crumbSources;
        this.kafkaMetricPlans = kafkaMetricPlans;
        this.serviceMetricPlans = serviceMetricPlans;
        var configuredPath = options.Value.RecipesPath;
        var path = ResolveRecipePath(configuredPath, environment.ContentRootPath);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Recipe file was not found: {path}");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        _document = deserializer.Deserialize<RecipeCatalog>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Recipe file is empty.");
        MaterializeServiceMetricPlans(_document);
        Validate(_document);
    }

    private static string ResolveRecipePath(string configuredPath, string contentRootPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new List<string>
        {
            Path.Combine(contentRootPath, configuredPath),
            Path.Combine(AppContext.BaseDirectory, configuredPath)
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public string Revision => _document.Revision;
    public string FallbackSlackChannel => _document.FallbackSlackChannel;
    public IReadOnlyList<RecipeOwnership> All => _document.Recipes
        .Select(recipe => new RecipeOwnership(
            recipe.Id,
            recipe.Team,
            recipe.PagerDutyServiceId,
            recipe.ServiceCollection))
        .ToArray();

    public bool TryGet(string recipeId, out RecipeOwnership ownership)
    {
        var recipe = _document.Recipes.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, recipeId, StringComparison.Ordinal));
        if (recipe is null)
        {
            ownership = null!;
            return false;
        }

        ownership = new RecipeOwnership(
            recipe.Id,
            recipe.Team,
            recipe.PagerDutyServiceId,
            recipe.ServiceCollection);
        return true;
    }

    public IReadOnlyList<ConfiguredCrumbSource> ConfiguredCrumbSources() =>
        _document.Recipes
            .SelectMany(recipe => crumbSources.ConfiguredTransports(recipe)
                .Select(value => new { value.Source, value.Transport, RecipeId = recipe.Id }))
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
            .Select(group => new ConfiguredCrumbSource(
                group.Key.Source,
                group.First().Transport,
                group.Select(value => value.RecipeId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(value => value.Source, StringComparer.Ordinal)
            .ThenBy(value => value.Transport.Mode, StringComparer.Ordinal)
            .ThenBy(value => value.Transport.BaseUrl, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<string> RequiredCredentialEnvironmentVariables(bool mcpEnabled)
    {
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transport in _document.Recipes.SelectMany(recipe =>
                     crumbSources.ConfiguredTransports(recipe).Select(value => value.Transport)))
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
        _document.Recipes.SelectMany(recipe =>
            crumbSources.ConfiguredTransports(recipe).Select(value => value.Transport))
            .Any(transport => transport.Mode == "mcp");

    public IReadOnlyList<string> ProductionConfigurationIssues()
    {
        var issues = new List<string>();
        foreach (var recipe in _document.Recipes)
        {
            foreach (var (source, transport) in crumbSources.ConfiguredTransports(recipe))
            {
                var configuredUrl = transport.Mode == "mcp" ? transport.Mcp?.ServerUrl : transport.BaseUrl;
                if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri)
                    && uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add($"Crumb source '{source}' still uses placeholder host '{uri.Host}'.");
                }
            }
        }

        return issues.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    internal IReadOnlyDictionary<string, string> FilterPersistedLabels(
        Recipe recipe,
        IReadOnlyDictionary<string, string> labels)
    {
        var allowedKeys = new HashSet<string>(StandardLabelKeys, StringComparer.Ordinal);
        foreach (var selector in recipe.Selectors)
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

    public Recipe Resolve(string serviceId, IReadOnlyDictionary<string, string> labels)
    {
        var candidates = _document.Recipes
            .Where(recipe => string.Equals(recipe.PagerDutyServiceId, serviceId, StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0)
        {
            return new Recipe
            {
                Id = "unmapped",
                PagerDutyServiceId = serviceId,
                Team = "unmapped",
                SlackChannel = _document.FallbackSlackChannel
            };
        }

        var matches = candidates
            .Select(recipe => new
            {
                Recipe = recipe,
                Score = BestSelectorScore(recipe, labels)
            })
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Recipe.Id, StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No Recipe selector matched PagerDuty service '{serviceId}'.");
        }

        if (matches.Count > 1 && matches[0].Score == matches[1].Score)
        {
            throw new InvalidOperationException(
                $"Ambiguous Recipes '{matches[0].Recipe.Id}' and '{matches[1].Recipe.Id}' for service '{serviceId}'.");
        }

        return matches[0].Recipe;
    }

    public SlackQueryRecipe Resolve(string recipeId)
    {
        return new SlackQueryRecipe(_document.Revision, ResolveById(recipeId));
    }

    public Recipe ResolveById(string recipeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        return _document.Recipes.SingleOrDefault(candidate =>
                   string.Equals(candidate.Id, recipeId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"Recipe '{recipeId}' was not found.");
    }

    private static int BestSelectorScore(Recipe recipe, IReadOnlyDictionary<string, string> labels)
    {
        if (recipe.Selectors.Count == 0)
        {
            return 0;
        }

        var scores = recipe.Selectors.Select(selector =>
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

    private void MaterializeServiceMetricPlans(RecipeCatalog document)
    {
        foreach (var recipe in document.Recipes.Where(recipe => recipe.Observability is not null))
        {
            if (recipe.Grafana?.Queries.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Id}' cannot combine observability metric packs with inline Grafana queries.");
            }
            if (serviceMetricPlans is null)
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Id}' enables service observability but no service metric catalog is available.");
            }

            var plan = serviceMetricPlans.Resolve(recipe.Observability!);
            recipe.Grafana ??= new GrafanaScope();
            var dashboardUid = ServiceDashboardIdentity.Uid(recipe.Id);
            if (recipe.Grafana.Dashboards.All(dashboard =>
                    !string.Equals(dashboard.Uid, dashboardUid, StringComparison.Ordinal)))
            {
                recipe.Grafana.Dashboards.Add(new GrafanaDashboard { Uid = dashboardUid });
            }

            foreach (var metric in plan.Metrics)
            {
                recipe.Grafana.Queries.Add(new GrafanaQuery
                {
                    Name = metric.Title,
                    DatasourceUid = metric.DatasourceUid,
                    Expression = metric.PromQl,
                    MetricId = metric.Id,
                    Role = metric.Role,
                    CrumbMode = metric.CrumbMode,
                    Requirement = metric.Requirement,
                    Reducer = metric.TimeReducer,
                    WarningThreshold = metric.Thresholds?.Warning,
                    CriticalThreshold = metric.Thresholds?.Critical,
                    Direction = metric.Thresholds?.Direction ?? "above",
                    Unit = metric.Unit
                });
            }
        }
    }

    private void Validate(RecipeCatalog document)
    {
        if (document.Version != 3)
        {
            throw new InvalidOperationException($"Unsupported Recipe schema version {document.Version}.");
        }

        if (string.IsNullOrWhiteSpace(document.Revision) || string.IsNullOrWhiteSpace(document.FallbackSlackChannel))
        {
            throw new InvalidOperationException("Recipe revision and fallbackSlackChannel are required.");
        }

        var duplicateIds = document.Recipes.GroupBy(recipe => recipe.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateIds is not null)
        {
            throw new InvalidOperationException($"Duplicate Recipe id '{duplicateIds.Key}'.");
        }

        var crossTeamService = document.Recipes
            .GroupBy(recipe => recipe.PagerDutyServiceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group
                .Select(recipe => recipe.Team)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any());
        if (crossTeamService is not null)
        {
            throw new InvalidOperationException(
                $"PagerDuty service '{crossTeamService.Key}' cannot be shared by recipes owned by different teams.");
        }

        var crossCollectionService = document.Recipes
            .GroupBy(recipe => recipe.PagerDutyServiceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group
                .Select(recipe => recipe.ServiceCollection)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any());
        if (crossCollectionService is not null)
        {
            throw new InvalidOperationException(
                $"PagerDuty service '{crossCollectionService.Key}' cannot be shared by recipes in different service collections.");
        }

        foreach (var recipe in document.Recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.Id)
                || string.IsNullOrWhiteSpace(recipe.PagerDutyServiceId)
                || string.IsNullOrWhiteSpace(recipe.SlackChannel))
            {
                throw new InvalidOperationException(
                    "Every recipe requires id, pagerDutyServiceId, and slackChannel.");
            }
            if (!TeamKey.IsCanonical(recipe.Team))
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Id}' requires a lowercase team key containing only letters, numbers, and hyphens.");
            }
            if (!ServiceCollectionKey.IsCanonical(recipe.ServiceCollection))
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Id}' requires a lowercase serviceCollection key containing only letters, numbers, and hyphens.");
            }

            if (recipe.Nomad?.Namespaces.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Jobs.Count == 0) == true)
            {
                throw new InvalidOperationException($"Recipe '{recipe.Id}' contains an empty Nomad namespace or job allowlist.");
            }

            if (recipe.Consul is not null)
            {
                if (recipe.Consul.Services.Count is 0 or > MaximumConsulServicesPerRecipe
                    || InvalidConsulScopeValue(recipe.Consul.Datacenter, required: false)
                    || InvalidConsulScopeValue(recipe.Consul.Partition, required: false)
                    || recipe.Consul.Services.Any(service =>
                        InvalidConsulScopeValue(service.Name, required: true)
                        || InvalidConsulScopeValue(service.Namespace, required: false)))
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Id}' must allowlist 1-{MaximumConsulServicesPerRecipe} "
                        + "bounded named Consul services and scope values.");
                }

                var duplicateService = recipe.Consul.Services
                    .GroupBy(
                        service => $"{service.Namespace}\u001f{service.Name}",
                        StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateService is not null)
                {
                    var service = duplicateService.First();
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Id}' contains duplicate Consul service "
                        + $"'{(string.IsNullOrWhiteSpace(service.Namespace) ? service.Name : $"{service.Namespace}/{service.Name}")}'.");
                }
            }

            if (recipe.Selectors.SelectMany(selector => selector.Labels.Keys).Any(LooksSensitive))
            {
                throw new InvalidOperationException($"Recipe '{recipe.Id}' uses a sensitive label name in a selector.");
            }

            if (recipe.AgentCases is { Enabled: true } agentCases)
            {
                if (agentCases.AllowedInputCategories.Count == 0
                    || agentCases.AllowedInputCategories.Any(category =>
                        string.IsNullOrWhiteSpace(category)
                        || category.Length > 64
                        || category.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-'))))
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Id}' must define bounded allowedInputCategories when agent Cases are enabled.");
                }

                ValidateUniqueQueryNames(
                    recipe.Id,
                    "agent-case input category",
                    agentCases.AllowedInputCategories);
            }

            if (recipe.SlackPromptLabels.Count > SlackQueryPlanCompiler.MaximumLabels)
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Id}' contains too many Slack prompt labels.");
            }
            var templateRenderer = new SafeTemplateRenderer();
            foreach (var label in recipe.SlackPromptLabels)
            {
                templateRenderer.Render("{{" + label.Key + "}}", recipe.SlackPromptLabels);
            }

            if (recipe.Grafana?.Queries.Any(query => string.IsNullOrWhiteSpace(query.DatasourceUid)
                    || string.IsNullOrWhiteSpace(query.Expression)
                    || string.IsNullOrWhiteSpace(query.Name)) == true)
            {
                throw new InvalidOperationException($"Recipe '{recipe.Id}' contains an invalid Grafana query.");
            }
            ValidateUniqueQueryNames(
                recipe.Id,
                "Grafana",
                recipe.Grafana?.Queries.Select(query => query.Name) ?? []);
            foreach (var query in recipe.Grafana?.Queries ?? [])
            {
                ValidateGrafanaQuery(recipe.Id, query);
            }

            if (recipe.Kafka is not null)
            {
                if (kafkaMetricPlans is null)
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Id}' enables Kafka but no Kafka metric catalog is available.");
                }
                _ = kafkaMetricPlans.Resolve(recipe.Kafka);
            }

            if (recipe.VictoriaLogs?.Queries.Any(query => string.IsNullOrWhiteSpace(query.Name)
                    || string.IsNullOrWhiteSpace(query.Expression)) == true)
            {
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Id}' contains an invalid VictoriaLogs query.");
            }
            ValidateUniqueQueryNames(
                recipe.Id,
                "VictoriaLogs",
                recipe.VictoriaLogs?.Queries.Select(query => query.Name) ?? []);

            foreach (var query in recipe.VictoriaLogs?.Queries ?? [])
            {
                if (query.AnchorPatterns.Count > MaximumAnchorPatternsPerVictoriaLogsQuery)
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Id}' contains more than {MaximumAnchorPatternsPerVictoriaLogsQuery} VictoriaLogs anchor patterns in query '{query.Name}'.");
                }

                if (query.AnchorPatterns.Any(anchor => string.IsNullOrWhiteSpace(anchor.Name)
                        || string.IsNullOrWhiteSpace(anchor.Pattern)))
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Id}' contains an invalid VictoriaLogs anchor pattern in query '{query.Name}'.");
                }

                ValidateUniqueQueryNames(
                    recipe.Id,
                    $"VictoriaLogs anchor pattern in query '{query.Name}'",
                    query.AnchorPatterns.Select(anchor => anchor.Name));
                foreach (var anchor in query.AnchorPatterns)
                {
                    try
                    {
                        _ = new Regex(
                            anchor.Pattern,
                            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new InvalidOperationException(
                            $"Recipe '{recipe.Id}' contains invalid VictoriaLogs anchor regex '{anchor.Name}' in query '{query.Name}'.",
                            exception);
                    }
                    catch (NotSupportedException exception)
                    {
                        throw new InvalidOperationException(
                            $"Recipe '{recipe.Id}' contains unsupported VictoriaLogs anchor regex '{anchor.Name}' in query '{query.Name}'.",
                            exception);
                    }
                }
            }

            if (recipe.VictoriaLogs is { StreamFilters.Count: 0 })
            {
                throw new InvalidOperationException($"Recipe '{recipe.Id}' must scope VictoriaLogs with streamFilters.");
            }
        }
    }

    private static void ValidateUniqueQueryNames(
        string recipeId,
        string source,
        IEnumerable<string> queryNames)
    {
        var duplicate = queryNames.GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' contains duplicate {source} query name '{duplicate.Key}'.");
        }
    }

    private static void ValidateGrafanaQuery(string recipeId, GrafanaQuery query)
    {
        if (!GrafanaReducers.Contains(query.Reducer))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' reducer must be one of: {string.Join(", ", GrafanaReducers.Order())}.");
        }
        if (!GrafanaDirections.Contains(query.Direction))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' direction must be one of: {string.Join(", ", GrafanaDirections.Order())}.");
        }
        if (!GrafanaCrumbModes.Contains(query.CrumbMode))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' crumbMode must be one of: {string.Join(", ", GrafanaCrumbModes.Order())}.");
        }
        if (!GrafanaRequirements.Contains(query.Requirement))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' requirement must be one of: {string.Join(", ", GrafanaRequirements.Order())}.");
        }
        if (query.Unit is null || query.Unit.Length > 64 || query.Unit.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' unit must be at most 64 characters and contain no control characters.");
        }
        if (query.WarningThreshold is { } warningThreshold && !double.IsFinite(warningThreshold)
            || query.CriticalThreshold is { } criticalThreshold && !double.IsFinite(criticalThreshold))
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' thresholds must be finite numbers.");
        }
        var effectiveWarning = query.WarningThreshold;
        if (effectiveWarning is not { } effective || query.CriticalThreshold is not { } critical)
        {
            return;
        }
        if (query.Direction == "above" && critical < effective
            || query.Direction == "below" && critical > effective)
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeId}' Grafana query '{query.Name}' warning/critical thresholds conflict with direction '{query.Direction}'.");
        }
    }

    private static bool InvalidConsulScopeValue(string? value, bool required)
    {
        if (value is null) return required;
        return (required && string.IsNullOrWhiteSpace(value))
            || value.Length > 256
            || value.Any(char.IsControl)
            || (value.Length > 0 && string.IsNullOrWhiteSpace(value));
    }

}
