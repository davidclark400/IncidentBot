using Panko.Api.Domain;
using Panko.Api.Options;

namespace Panko.Api.Crumbs;

public sealed class CrumbSourceRegistry
{
    public const string PagerDuty = "pagerduty";
    public const string Nomad = "nomad";
    public const string Consul = "consul";
    public const string GitLab = "gitlab";
    public const string Grafana = "grafana";
    public const string Kafka = "kafka";
    public const string VictoriaLogs = "victorialogs";

    private static readonly CrumbSourceDefinition[] Definitions =
    [
        new(PagerDuty, recipe => recipe.PagerDuty is not null),
        new(Nomad, recipe => recipe.Nomad is not null),
        new(Consul, recipe => recipe.Consul is not null),
        new(GitLab, recipe => recipe.GitLab is not null),
        new(Grafana, recipe => recipe.Grafana is not null),
        new(Kafka, recipe => recipe.Kafka is not null),
        new(VictoriaLogs, recipe => recipe.VictoriaLogs is not null)
    ];

    private readonly IReadOnlyDictionary<string, ICrumbSourceAdapter> sourceAdapters;
    private readonly CrumbSourceConfiguration configuration;

    public CrumbSourceRegistry(
        IEnumerable<ICrumbSourceAdapter> sourceAdapters,
        CrumbSourceConfiguration configuration)
    {
        this.configuration = configuration;
        var registered = sourceAdapters.ToArray();
        var duplicate = registered.GroupBy(adapter => adapter.Source, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate Crumb source adapter for '{duplicate.Key}'.");
        }

        this.sourceAdapters = registered.ToDictionary(adapter => adapter.Source, StringComparer.Ordinal);
        var known = Definitions.Select(definition => definition.Source).ToHashSet(StringComparer.Ordinal);
        var unknown = this.sourceAdapters.Keys.FirstOrDefault(source => !known.Contains(source));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Crumb source adapter '{unknown}' has no registry definition.");
        }
    }

    public IReadOnlyList<string> EnabledSources(Recipe recipe) =>
        Definitions.Where(definition => definition.Enabled(recipe))
            .Select(definition => definition.Source)
            .ToArray();

    public IReadOnlyList<ICrumbSourceAdapter> Select(Recipe recipe) =>
        EnabledSources(recipe)
            .Select(source => sourceAdapters.TryGetValue(source, out var adapter)
                ? adapter
                : throw new InvalidOperationException($"Enabled Crumb source '{source}' has no registered adapter."))
            .ToArray();

    public IEnumerable<(string Source, ConnectorTransport Transport)> ConfiguredTransports(
        Recipe recipe) =>
        Definitions.Where(definition => definition.Enabled(recipe))
            .Select(definition => (definition.Source, configuration.For(definition.Source)));

    private sealed record CrumbSourceDefinition(
        string Source,
        Func<Recipe, bool> Enabled);
}

public static class CrumbSourceRegistration
{
    public static IServiceCollection AddCrumbSources(this IServiceCollection services)
    {
        services.AddSingleton<ICrumbSourceAdapter, PagerDutyCrumbSource>();
        services.AddSingleton<ICrumbSourceAdapter, NomadCrumbSource>();
        services.AddSingleton<ICrumbSourceAdapter, ConsulCrumbSource>();
        services.AddSingleton<ICrumbSourceAdapter, GitLabCrumbSource>();
        services.AddSingleton<ICrumbSourceAdapter, GrafanaCrumbSource>();
        services.AddSingleton<ICrumbSourceAdapter, KafkaCrumbSource>();
        services.AddSingleton<ICrumbSourceAdapter, VictoriaLogsCrumbSource>();
        services.AddSingleton<CrumbSourceRegistry>();
        return services;
    }
}
