using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Options;

public sealed class EvidenceSourceOptions
{
    public const string SectionName = "EvidenceSources";

    public ConnectorTransport PagerDuty { get; init; } = new();
    public ConnectorTransport Nomad { get; init; } = new();
    public ConnectorTransport GitLab { get; init; } = new();
    public ConnectorTransport Grafana { get; init; } = new();
    public ConnectorTransport Kafka { get; init; } = new();
    public ConnectorTransport VictoriaLogs { get; init; } = new();

    public IEnumerable<(string Source, ConnectorTransport Transport)> All()
    {
        yield return ("pagerduty", PagerDuty);
        yield return ("nomad", Nomad);
        yield return ("gitlab", GitLab);
        yield return ("grafana", Grafana);
        yield return ("kafka", Kafka);
        yield return ("victorialogs", VictoriaLogs);
    }
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

public sealed record ConfiguredEvidenceSource(
    string Source,
    ConnectorTransport Transport,
    IReadOnlyList<string> ProfileIds);

public sealed class EvidenceSourceOptionsValidator : IValidateOptions<EvidenceSourceOptions>
{
    public ValidateOptionsResult Validate(string? name, EvidenceSourceOptions options)
    {
        var failures = new List<string>();
        foreach (var (source, transport) in options.All())
        {
            ValidateTransport(source, transport, failures);
        }

        if (options.Kafka.Mode != "api")
        {
            failures.Add("EvidenceSources:Kafka:Mode must be 'api'; Kafka MCP transport is not supported.");
        }

        if (!IsHttpUrl(options.PagerDuty.BaseUrl))
        {
            failures.Add("EvidenceSources:PagerDuty:BaseUrl must be an absolute HTTP(S) URL because incident pull uses the native API.");
        }
        if (options.PagerDuty.Mode == "mcp" && !CredentialVariableName.IsValid(options.PagerDuty.CredentialEnv))
        {
            failures.Add("EvidenceSources:PagerDuty:CredentialEnv must name the native API credential because incident pull always uses the native API.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateTransport(
        string source,
        ConnectorTransport transport,
        ICollection<string> failures)
    {
        var key = $"EvidenceSources:{source}";
        if (transport.Mode is not ("api" or "mcp"))
        {
            failures.Add($"{key}:Mode must be 'api' or 'mcp'.");
            return;
        }

        if (transport.TimeoutSeconds is < 1 or > 120
            || transport.MaxItems is < 1 or > 1000
            || transport.MaxBytes is < 1024 or > 4_194_304)
        {
            failures.Add($"{key} has invalid timeout, item, or byte limits.");
        }

        if (transport.Mode == "api")
        {
            if (!IsHttpUrl(transport.BaseUrl))
            {
                failures.Add($"{key}:BaseUrl must be an absolute HTTP(S) URL in API mode.");
            }
            if (!CredentialVariableName.IsValid(transport.CredentialEnv))
            {
                failures.Add($"{key}:CredentialEnv must be a valid environment-variable name in API mode.");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(transport.BaseUrl) && !IsHttpUrl(transport.BaseUrl))
        {
            failures.Add($"{key}:BaseUrl must be an absolute HTTP(S) URL when provided.");
        }
        if (transport.Mcp is null || !IsHttpUrl(transport.Mcp.ServerUrl)
            || string.IsNullOrWhiteSpace(transport.Mcp.ToolName)
            || !CredentialVariableName.IsValid(transport.Mcp.CredentialEnv))
        {
            failures.Add($"{key}:Mcp requires an HTTP(S) ServerUrl, ToolName, and CredentialEnv in MCP mode.");
        }
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

}
