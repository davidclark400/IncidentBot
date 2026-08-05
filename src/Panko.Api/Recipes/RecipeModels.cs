using Panko.Kafka;
using Panko.Observability;

namespace Panko.Api.Domain;

public sealed class RecipeCatalog
{
    public int Version { get; init; }
    public string Revision { get; init; } = "";
    public string FallbackSlackChannel { get; init; } = "";
    public List<Recipe> Recipes { get; init; } = [];
}

public sealed class Recipe
{
    public string Id { get; init; } = "";
    public string PagerDutyServiceId { get; init; } = "";
    public string Team { get; init; } = "";
    public string ServiceCollection { get; init; } = ServiceCollectionKey.Default;
    public string SlackChannel { get; init; } = "";
    public Dictionary<string, string> SlackPromptLabels { get; init; } = [];
    public List<RecipeSelector> Selectors { get; init; } = [];
    public AgentCasePolicy? AgentCases { get; set; }
    public PagerDutyScope? PagerDuty { get; init; }
    public NomadScope? Nomad { get; init; }
    public ConsulScope? Consul { get; init; }
    public GitLabScope? GitLab { get; init; }
    public GrafanaScope? Grafana { get; set; }
    public ServiceMetricScope? Observability { get; init; }
    public KafkaRecipeScope? Kafka { get; init; }
    public VictoriaLogsScope? VictoriaLogs { get; init; }
}

public sealed class AgentCasePolicy
{
    public bool Enabled { get; init; }
    public bool PublishToSlack { get; init; }
    public bool AllowSourceRefresh { get; init; }
    public List<string> AllowedInputCategories { get; init; } = [];
}

public sealed class RecipeSelector
{
    public string? AlertRuleId { get; init; }
    public Dictionary<string, string> Labels { get; init; } = [];
}

public sealed class PagerDutyScope
{
}

public sealed class NomadScope
{
    public string Region { get; init; } = "global";
    public List<NomadNamespace> Namespaces { get; init; } = [];
}

public sealed class NomadNamespace
{
    public string Name { get; init; } = "default";
    public List<string> Jobs { get; init; } = [];
}

public sealed class ConsulScope
{
    public string Datacenter { get; init; } = "";
    public string Partition { get; init; } = "";
    public List<ConsulService> Services { get; init; } = [];
}

public sealed class ConsulService
{
    public string Name { get; init; } = "";
    public string Namespace { get; init; } = "";
}

public sealed class GitLabScope
{
    public List<GitLabProject> Projects { get; init; } = [];
}

public sealed class GitLabProject
{
    public string Id { get; init; } = "";
    public string Branch { get; init; } = "main";
    public List<string> Environments { get; init; } = [];
    public List<string> RelevantPaths { get; init; } = [];
}

public sealed class GrafanaScope
{
    public int OrganizationId { get; init; } = 1;
    public List<GrafanaDashboard> Dashboards { get; init; } = [];
    public List<GrafanaQuery> Queries { get; init; } = [];
    public List<string> AnnotationTags { get; init; } = [];
}

public sealed class GrafanaDashboard
{
    public string Uid { get; init; } = "";
    public List<int> PanelIds { get; init; } = [];
}

public sealed class GrafanaQuery
{
    public string Name { get; init; } = "";
    public string DatasourceUid { get; init; } = "";
    public string Expression { get; init; } = "";
    public string MetricId { get; init; } = "";
    public string Role { get; init; } = "";
    public string CrumbMode { get; init; } = "anomaly";
    public string Requirement { get; init; } = "optional";
    public string Reducer { get; init; } = "maximum";
    public double? WarningThreshold { get; init; }
    public double? CriticalThreshold { get; init; }
    public string Direction { get; init; } = "above";
    public string Unit { get; init; } = "";
}

public sealed class VictoriaLogsScope
{
    public string AccountId { get; init; } = "0";
    public string ProjectId { get; init; } = "0";
    public Dictionary<string, string> StreamFilters { get; init; } = [];
    public List<string> Fields { get; init; } = ["_time", "level", "_msg"];
    public List<VictoriaLogsQuery> Queries { get; init; } = [];
    public List<string> RedactPatterns { get; init; } = [];
}

public sealed class VictoriaLogsQuery
{
    public string Name { get; init; } = "";
    public string Expression { get; init; } = "";
    public List<VictoriaLogsAnchorPattern> AnchorPatterns { get; init; } = [];
}

public sealed class VictoriaLogsAnchorPattern
{
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
}
