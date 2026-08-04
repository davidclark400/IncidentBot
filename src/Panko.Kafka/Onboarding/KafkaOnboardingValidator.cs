using System.Text.Json.Nodes;

namespace Panko.Kafka.Onboarding;

public sealed record KafkaOnboardingValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class KafkaOnboardingValidator(KafkaDashboardGenerator dashboards)
{
    public KafkaOnboardingValidationResult Validate(
        KafkaApplicationInventory inventory,
        string recipeId,
        KafkaRecipeScope scope,
        KafkaMetricCatalog catalog,
        string dashboardJson,
        KafkaResourceMappingDocument? mappingDocument = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<string>();
        IReadOnlyList<KafkaResourceMapping> mappings = [];
        KafkaMetricPlan? plan = null;

        try
        {
            inventory.EnsureSupportedVersion();
        }
        catch (InvalidOperationException exception)
        {
            return new KafkaOnboardingValidationResult([exception.Message]);
        }

        if (mappingDocument is not null)
        {
            try
            {
                KafkaResourceMappingLoader.Validate(mappingDocument);
                mappings = mappingDocument.Mappings;
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
            }
        }

        try
        {
            plan = catalog.CompilePlan(scope);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
        }

        foreach (var unresolved in inventory.UnresolvedReferences.Where(item => item.Required))
        {
            var evidence = unresolved.Evidence
                .OrderBy(item => item.File, StringComparer.Ordinal)
                .ThenBy(item => item.Line)
                .FirstOrDefault();
            errors.Add(
                $"Unresolved required Kafka {unresolved.Kind} '{unresolved.Expression}'"
                + (evidence is null ? "" : $" at {evidence.File}:{evidence.Line}")
                + $": {unresolved.Reason}");
        }

        ValidateRequiredInventory(inventory, "cluster", errors);
        ValidateRequiredInventory(inventory, "topic", errors);
        ValidateCoverage(inventory, "cluster", [scope.Cluster], mappings, errors);
        ValidateCoverage(inventory, "topic", scope.Topics, mappings, errors);
        ValidateCoverage(inventory, "consumer-group", scope.ConsumerGroups, mappings, errors);
        ValidateMappings(inventory, scope, mappings, errors);

        if (plan is not null)
        {
            try
            {
                var expected = JsonNode.Parse(dashboards.Generate(recipeId, plan));
                var actual = JsonNode.Parse(dashboardJson);
                if (expected is null || actual is null || !JsonNode.DeepEquals(expected, actual))
                {
                    errors.Add(
                        "Kafka dashboard does not match the Recipe allowlists and shared metric-pack definitions; regenerate it.");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
            {
                errors.Add($"Kafka dashboard validation failed: {exception.Message}");
            }
        }

        return new KafkaOnboardingValidationResult(
            errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateRequiredInventory(
        KafkaApplicationInventory inventory,
        string kind,
        ICollection<string> errors)
    {
        if (!inventory.Resources.Any(resource => resource.Kind == kind))
        {
            errors.Add($"Kafka scan inventory contains no resolved {kind} mapping.");
        }
    }

    private static void ValidateCoverage(
        KafkaApplicationInventory inventory,
        string kind,
        IEnumerable<string> allowed,
        IReadOnlyList<KafkaResourceMapping> mappings,
        ICollection<string> errors)
    {
        var allowlist = allowed.ToHashSet(StringComparer.Ordinal);
        var mapped = mappings
            .Where(mapping => mapping.Kind == kind)
            .ToDictionary(mapping => mapping.InventoryResource, StringComparer.Ordinal);
        foreach (var resource in inventory.Resources.Where(resource => resource.Kind == kind))
        {
            if (allowlist.Contains(resource.Name))
            {
                continue;
            }

            if (mapped.TryGetValue(resource.Name, out var mapping)
                && allowlist.Contains(mapping.RecipeResource))
            {
                continue;
            }

            var evidence = resource.Evidence.OrderBy(item => item.File, StringComparer.Ordinal)
                .ThenBy(item => item.Line)
                .FirstOrDefault();
            errors.Add(
                $"Inventory Kafka {kind} '{resource.Name}' is absent from the Recipe allowlist"
                + (mapped.ContainsKey(resource.Name) ? " and has no mapping to an allowlisted Recipe resource" : " and has no reviewed resource mapping")
                + (evidence is null ? "." : $" ({evidence.File}:{evidence.Line})."));
        }
    }

    private static void ValidateMappings(
        KafkaApplicationInventory inventory,
        KafkaRecipeScope scope,
        IReadOnlyList<KafkaResourceMapping> mappings,
        ICollection<string> errors)
    {
        var inventoryResources = inventory.Resources
            .Select(resource => (resource.Kind, resource.Name))
            .ToHashSet();
        var allowlists = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [KafkaInventoryResourceKinds.Cluster] = new([scope.Cluster], StringComparer.Ordinal),
            [KafkaInventoryResourceKinds.Topic] = new(scope.Topics, StringComparer.Ordinal),
            [KafkaInventoryResourceKinds.ConsumerGroup] = new(scope.ConsumerGroups, StringComparer.Ordinal)
        };

        foreach (var mapping in mappings)
        {
            var evidence = $"{mapping.EvidenceFile}:{mapping.EvidenceLine}";
            if (!inventoryResources.Contains((mapping.Kind, mapping.InventoryResource)))
            {
                errors.Add(
                    $"Kafka resource mapping for {mapping.Kind} '{mapping.InventoryResource}' at {evidence} "
                    + "does not match a scan inventory resource.");
            }
            if (!allowlists[mapping.Kind].Contains(mapping.RecipeResource))
            {
                errors.Add(
                    $"Kafka resource mapping for {mapping.Kind} '{mapping.InventoryResource}' at {evidence} "
                    + $"targets '{mapping.RecipeResource}', which is outside the Recipe allowlist.");
            }
        }
    }
}
