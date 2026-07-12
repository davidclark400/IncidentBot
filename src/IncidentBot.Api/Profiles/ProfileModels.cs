namespace IncidentBot.Api.Domain;

public sealed class ProfileDocument
{
    public int Version { get; init; } = 1;
    public string Revision { get; init; } = "development";
    public string FallbackSlackChannel { get; init; } = "#incidents";
    public List<InvestigationProfile> Profiles { get; init; } = [];
}

public sealed class InvestigationProfile
{
    public string Id { get; init; } = "";
    public string PagerDutyServiceId { get; init; } = "";
    public string Team { get; init; } = "";
    public string SlackChannel { get; init; } = "";
    public List<ProfileSelector> Selectors { get; init; } = [];
    public PagerDutyScope? PagerDuty { get; init; }
    public NomadScope? Nomad { get; init; }
    public GitLabScope? GitLab { get; init; }
    public GrafanaScope? Grafana { get; init; }
    public VictoriaLogsScope? VictoriaLogs { get; init; }
}

public sealed class ProfileSelector
{
    public string? AlertRuleId { get; init; }
    public Dictionary<string, string> Labels { get; init; } = [];
}

public sealed class ConnectorTransport
{
    public string Mode { get; init; } = "api";
    public string BaseUrl { get; init; } = "";
    public string CredentialEnv { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxItems { get; init; } = 50;
    public int MaxBytes { get; init; } = 131072;
    public McpToolConfiguration? Mcp { get; init; }
}

public sealed class McpToolConfiguration
{
    public string ServerUrl { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string CredentialEnv { get; init; } = "";
}

public sealed class PagerDutyScope
{
    public ConnectorTransport Connector { get; init; } = new();
}

public sealed class NomadScope
{
    public ConnectorTransport Connector { get; init; } = new();
    public string Region { get; init; } = "global";
    public List<NomadNamespace> Namespaces { get; init; } = [];
}

public sealed class NomadNamespace
{
    public string Name { get; init; } = "default";
    public List<string> Jobs { get; init; } = [];
}

public sealed class GitLabScope
{
    public ConnectorTransport Connector { get; init; } = new();
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
    public ConnectorTransport Connector { get; init; } = new();
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
    public double? WarningAbove { get; init; }
}

public sealed class VictoriaLogsScope
{
    public ConnectorTransport Connector { get; init; } = new();
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
}

public sealed record ConfiguredEvidenceSource(
    string Source,
    ConnectorTransport Transport,
    IReadOnlyList<string> ProfileIds);
