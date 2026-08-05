namespace Panko.Observability.Onboarding;

public static class ServiceTelemetryEvidenceTemplate
{
    public static ServiceTelemetryEvidenceDocument Create(
        string recipeId,
        string workloadKind,
        string service,
        string environment)
    {
        var contract = workloadKind switch
        {
            ServiceTelemetryWorkloadKind.RequestDriven => ServiceMetricCatalog.RequestDrivenContract,
            ServiceTelemetryWorkloadKind.Worker => ServiceMetricCatalog.WorkerContract,
            _ => throw new InvalidOperationException(
                "Evidence templates support workload kinds 'request-driven' and 'worker'.")
        };
        var evidence = new ServiceTelemetryEvidenceDocument
        {
            Version = ServiceTelemetryEvidenceJson.SupportedVersion,
            RecipeId = recipeId,
            Status = ServiceTelemetryEvidenceStatus.Partial,
            Workload = new ServiceTelemetryWorkloadEvidence
            {
                Kind = workloadKind,
                Service = service,
                Environment = environment
            },
            Metrics = ServiceMetricCatalog.RequiredRolesFor(contract)
                .Order(StringComparer.Ordinal)
                .Select(role => new ServiceTelemetryMetricEvidence
                {
                    Definition = new ServiceMetricDefinition
                    {
                        Id = role,
                        Role = role,
                        Requirement = "required"
                    }
                })
                .ToList(),
            Gaps =
            [
                $"Replace every {contract} role stub with reviewed metric facts and provenance, then mark status complete."
            ]
        };
        ServiceTelemetryEvidenceJson.Validate(evidence);
        return evidence;
    }
}
