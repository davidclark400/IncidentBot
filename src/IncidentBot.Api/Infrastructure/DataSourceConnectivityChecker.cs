using System.Diagnostics;
using System.Net.Http.Headers;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Infrastructure;

public sealed class DataSourceConnectivityChecker(
    IHttpClientFactory httpClientFactory,
    InvestigationProfileStore profiles,
    McpStreamableHttpClient mcp,
    IOptions<IncidentBotOptions> options,
    ICredentialProvider credentials,
    ILogger<DataSourceConnectivityChecker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.CollectionEnabled)
        {
            logger.LogInformation("Data-source connectivity checks skipped because evidence collection is disabled");
            return;
        }

        var sources = profiles.ConfiguredEvidenceSources();
        if (sources.Count == 0)
        {
            logger.LogInformation("Data-source connectivity checks found no configured evidence sources");
            return;
        }

        logger.LogInformation(
            "Testing connectivity to {DataSourceCount} configured evidence data sources",
            sources.Count);
        await Task.WhenAll(sources.Select(source => CheckAsync(source, cancellationToken)));
        logger.LogInformation("Configured evidence data-source connectivity checks completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CheckAsync(
        ConfiguredEvidenceSource source,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(source.Transport.TimeoutSeconds, 1, 120)));

        try
        {
            if (source.Transport.Mode == "mcp")
            {
                if (!options.Value.McpEnabled)
                {
                    logger.LogWarning(
                        "Data source {Source} connectivity check skipped because MCP is disabled for profiles {ProfileIds}",
                        source.Source,
                        string.Join(',', source.ProfileIds));
                    return;
                }

                await mcp.CheckConnectionAsync(source.Transport.Mcp!, timeout.Token);
                logger.LogInformation(
                    "Data source {Source} connection succeeded via MCP server {McpServerHost} using tool {McpTool} in {DurationMilliseconds} ms for profiles {ProfileIds}",
                    source.Source,
                    Host(source.Transport.Mcp!.ServerUrl),
                    source.Transport.Mcp.ToolName,
                    stopwatch.ElapsedMilliseconds,
                    string.Join(',', source.ProfileIds));
                return;
            }

            var path = ProbePath(source.Source);
            using var request = ConnectorUtilities.CreateRequest(
                HttpMethod.Get,
                ConnectorUtilities.Url(source.Transport, path),
                source.Transport,
                credentials);
            ApplySourceAuthentication(request, source.Source, source.Transport);
            using var response = await httpClientFactory.CreateClient().SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Data source {Source} connection succeeded via {Transport} at {DataSourceHost} using {ProbePath} with HTTP {StatusCode} in {DurationMilliseconds} ms for profiles {ProfileIds}",
                    source.Source,
                    source.Transport.Mode,
                    Host(source.Transport.BaseUrl),
                    path,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    string.Join(',', source.ProfileIds));
            }
            else
            {
                logger.LogWarning(
                    "Data source {Source} connection failed via {Transport} at {DataSourceHost} using {ProbePath} with HTTP {StatusCode} in {DurationMilliseconds} ms for profiles {ProfileIds}",
                    source.Source,
                    source.Transport.Mode,
                    Host(source.Transport.BaseUrl),
                    path,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    string.Join(',', source.ProfileIds));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Data source {Source} connection timed out after {DurationMilliseconds} ms for profiles {ProfileIds}",
                source.Source,
                stopwatch.ElapsedMilliseconds,
                string.Join(',', source.ProfileIds));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Data source {Source} connection failed via {Transport} at {DataSourceHost} after {DurationMilliseconds} ms with {FailureType} for profiles {ProfileIds}",
                source.Source,
                source.Transport.Mode,
                Host(source.Transport.Mode == "mcp"
                    ? source.Transport.Mcp!.ServerUrl
                    : source.Transport.BaseUrl),
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name,
                string.Join(',', source.ProfileIds));
        }
    }

    private static string ProbePath(string source) => source switch
    {
        EvidenceSourceRegistry.PagerDuty => "users/me",
        EvidenceSourceRegistry.Nomad => "v1/agent/self",
        EvidenceSourceRegistry.GitLab => "api/v4/user",
        EvidenceSourceRegistry.Grafana => "api/health",
        EvidenceSourceRegistry.VictoriaLogs => "health",
        _ => ""
    };

    private void ApplySourceAuthentication(
        HttpRequestMessage request,
        string source,
        ConnectorTransport transport)
    {
        var credential = string.IsNullOrWhiteSpace(transport.CredentialEnv)
            ? null
            : credentials.Get(transport.CredentialEnv);

        if (source == EvidenceSourceRegistry.PagerDuty)
        {
            request.Headers.Authorization = string.IsNullOrWhiteSpace(credential)
                ? null
                : new AuthenticationHeaderValue("Token", $"token={credential}");
        }
        else if (source == EvidenceSourceRegistry.Nomad)
        {
            request.Headers.Authorization = null;
            if (!string.IsNullOrWhiteSpace(credential))
            {
                request.Headers.TryAddWithoutValidation("X-Nomad-Token", credential);
            }
        }
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid-host";
}
