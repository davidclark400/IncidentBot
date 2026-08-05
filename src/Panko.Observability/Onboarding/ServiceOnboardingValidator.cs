using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panko.Observability.Onboarding;

public sealed record ServiceOnboardingValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class ServiceOnboardingValidator(ServiceDashboardGenerator dashboards)
{
    public ServiceOnboardingValidationResult Validate(
        string recipeId,
        ServiceMetricScope scope,
        ServiceMetricCatalog catalog,
        string dashboardJson,
        ServiceTelemetryEvidenceDocument evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dashboardJson);
        ArgumentNullException.ThrowIfNull(evidence);

        var errors = new List<string>();
        try
        {
            ValidateEvidence(recipeId, scope, catalog, evidence, errors);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add($"Service telemetry evidence validation failed: {exception.Message}");
        }
        ServiceMetricPlan? plan = null;
        try
        {
            plan = catalog.CompilePlan(scope);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
        }

        if (plan is not null)
        {
            try
            {
                var expected = JsonNode.Parse(dashboards.Generate(recipeId, plan));
                var actual = JsonNode.Parse(dashboardJson);
                if (expected is null || actual is null || !JsonNode.DeepEquals(expected, actual))
                {
                    errors.Add(
                        "Service dashboard does not match the Recipe scope and shared metric-pack definitions; regenerate it.");
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or JsonException)
            {
                errors.Add($"Service dashboard validation failed: {exception.Message}");
            }
        }

        return new ServiceOnboardingValidationResult(
            errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateEvidence(
        string recipeId,
        ServiceMetricScope scope,
        ServiceMetricCatalog catalog,
        ServiceTelemetryEvidenceDocument evidence,
        ICollection<string> errors)
    {
        if (!string.Equals(evidence.RecipeId, recipeId, StringComparison.Ordinal))
        {
            errors.Add(
                $"Service telemetry evidence Recipe '{evidence.RecipeId}' does not match selected Recipe '{recipeId}'.");
        }
        if (!string.Equals(evidence.Workload.Service, scope.Service, StringComparison.Ordinal)
            || !string.Equals(evidence.Workload.Environment, scope.Environment, StringComparison.Ordinal))
        {
            errors.Add(
                "Service telemetry evidence workload scope does not match the selected Recipe service and environment.");
        }

        var assessment = new ServiceMetricPackAssessor().Assess(evidence, catalog);
        if (assessment.Decision != ServiceMetricPackDecision.Reuse)
        {
            errors.Add(
                $"Service telemetry evidence assessment is '{assessment.Decision}', not an actionable pack reuse.");
            foreach (var blocker in assessment.Blockers)
            {
                errors.Add($"Service telemetry evidence: {blocker}");
            }
            return;
        }
        if (!string.Equals(
                assessment.SelectedMetricPackId,
                scope.MetricPackId,
                StringComparison.Ordinal))
        {
            errors.Add(
                $"Service telemetry evidence selects metric pack '{assessment.SelectedMetricPackId}', "
                + $"but the Recipe selects '{scope.MetricPackId}'.");
            return;
        }

        var plan = catalog.CompilePlan(scope);
        var evidenceById = evidence.Metrics.ToDictionary(
            metric => metric.Definition.Id,
            StringComparer.Ordinal);
        var effectiveThresholdsMatch = assessment.RoleMappings.All(mapping =>
        {
            var observed = evidenceById[mapping.EvidenceMetricId].Definition;
            var planned = plan.Metrics.Single(metric => metric.Id == mapping.PackMetricId);
            return EffectiveThresholdsEqual(observed, planned.Thresholds);
        });
        if (!effectiveThresholdsMatch)
        {
            errors.Add(
                "Service Recipe effective thresholds do not match the reviewed telemetry evidence assessment.");
        }
    }

    private static bool EffectiveThresholdsEqual(
        ServiceMetricDefinition observed,
        ServiceMetricThresholds? planned)
    {
        if (!observed.WarningThreshold.HasValue && !observed.CriticalThreshold.HasValue)
        {
            return planned is null;
        }
        return planned is not null
            && observed.WarningThreshold == planned.Warning
            && observed.CriticalThreshold == planned.Critical
            && (string.IsNullOrEmpty(observed.Direction) ? "above" : observed.Direction)
            == planned.Direction;
    }
}
