namespace Panko.Observability.Onboarding;

public static class ServiceMetricPackDecision
{
    public const string Reuse = "reuse";
    public const string NewPackFromContract = "new-pack-from-contract";
    public const string Blocked = "blocked";
    public const string ContractDesignReview = "contract-design-review";
}

public sealed class ServiceMetricPackAssessment
{
    public int Version { get; init; } = 1;
    public string RecipeId { get; init; } = "";
    public string Decision { get; init; } = "";
    public string? Contract { get; init; }
    public string? SelectedMetricPackId { get; init; }
    public List<string> MatchingMetricPackIds { get; init; } = [];
    public Dictionary<string, ServiceMetricThresholdOverride> ThresholdOverrides { get; init; } = [];
    public List<ServiceMetricRoleMapping> RoleMappings { get; init; } = [];
    public List<string> Blockers { get; init; } = [];
    public List<string> OutstandingLiveVerification { get; init; } = [];
    public List<ServiceMetricDefinition> ProposedMetrics { get; init; } = [];
}

public sealed class ServiceMetricRoleMapping
{
    public string Role { get; init; } = "";
    public string EvidenceMetricId { get; init; } = "";
    public string PackMetricId { get; init; } = "";
    public List<string> SourceRefs { get; init; } = [];
}
