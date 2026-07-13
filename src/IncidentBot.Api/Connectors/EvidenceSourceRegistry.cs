using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;

namespace IncidentBot.Api.Connectors;

public sealed class EvidenceSourceRegistry
{
    public const string PagerDuty = "pagerduty";
    public const string Nomad = "nomad";
    public const string GitLab = "gitlab";
    public const string Grafana = "grafana";
    public const string VictoriaLogs = "victorialogs";

    private static readonly EvidenceSourceDefinition[] Definitions =
    [
        new(PagerDuty, profile => profile.PagerDuty is not null),
        new(Nomad, profile => profile.Nomad is not null),
        new(GitLab, profile => profile.GitLab is not null),
        new(Grafana, profile => profile.Grafana is not null),
        new(VictoriaLogs, profile => profile.VictoriaLogs is not null)
    ];

    private readonly IReadOnlyDictionary<string, IIncidentEvidenceConnector> connectors;
    private readonly EvidenceSourceConfiguration configuration;

    public EvidenceSourceRegistry(
        IEnumerable<IIncidentEvidenceConnector> connectors,
        EvidenceSourceConfiguration configuration)
    {
        this.configuration = configuration;
        var registered = connectors.ToArray();
        var duplicate = registered.GroupBy(connector => connector.Source, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate evidence connector source '{duplicate.Key}'.");
        }

        this.connectors = registered.ToDictionary(connector => connector.Source, StringComparer.Ordinal);
        var known = Definitions.Select(definition => definition.Source).ToHashSet(StringComparer.Ordinal);
        var unknown = this.connectors.Keys.FirstOrDefault(source => !known.Contains(source));
        if (unknown is not null)
        {
            throw new InvalidOperationException($"Evidence connector source '{unknown}' has no registry definition.");
        }
    }

    public IReadOnlyList<string> EnabledSources(InvestigationProfile profile) =>
        Definitions.Where(definition => definition.Enabled(profile))
            .Select(definition => definition.Source)
            .ToArray();

    public IReadOnlyList<IIncidentEvidenceConnector> Select(InvestigationProfile profile) =>
        EnabledSources(profile)
            .Select(source => connectors.TryGetValue(source, out var connector)
                ? connector
                : throw new InvalidOperationException($"Enabled evidence source '{source}' has no registered connector."))
            .ToArray();

    public IEnumerable<(string Source, ConnectorTransport Transport)> ConfiguredTransports(
        InvestigationProfile profile) =>
        Definitions.Where(definition => definition.Enabled(profile))
            .Select(definition => (definition.Source, configuration.For(definition.Source)));

    private sealed record EvidenceSourceDefinition(
        string Source,
        Func<InvestigationProfile, bool> Enabled);
}

public static class EvidenceSourceRegistration
{
    public static IServiceCollection AddIncidentEvidenceSources(this IServiceCollection services)
    {
        services.AddSingleton<IIncidentEvidenceConnector, PagerDutyEvidenceConnector>();
        services.AddSingleton<IIncidentEvidenceConnector, NomadEvidenceConnector>();
        services.AddSingleton<IIncidentEvidenceConnector, GitLabEvidenceConnector>();
        services.AddSingleton<IIncidentEvidenceConnector, GrafanaEvidenceConnector>();
        services.AddSingleton<IIncidentEvidenceConnector, VictoriaLogsEvidenceConnector>();
        services.AddSingleton<EvidenceSourceRegistry>();
        return services;
    }
}
