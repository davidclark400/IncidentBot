using System.Diagnostics;
using System.Net.Http.Headers;
using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Microsoft.Extensions.Options;

namespace Panko.Api.Infrastructure;

public sealed class DataSourceConnectivityChecker(
    IHttpClientFactory httpClientFactory,
    RecipeStore recipes,
    McpCrumbSourceClient mcp,
    IOptions<PankoOptions> options,
    ICredentialProvider credentials,
    ILogger<DataSourceConnectivityChecker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.CrumbCollectionEnabled)
        {
            logger.LogInformation("Crumb-source connectivity checks skipped because Crumb collection is disabled");
            return;
        }

        var sources = recipes.ConfiguredCrumbSources();
        if (sources.Count == 0)
        {
            logger.LogInformation("Crumb-source connectivity checks found no configured Crumb sources");
            return;
        }

        logger.LogInformation(
            "Testing connectivity to {CrumbSourceCount} configured Crumb sources",
            sources.Count);
        await Task.WhenAll(sources.Select(source => CheckAsync(source, cancellationToken)));
        logger.LogInformation("Configured Crumb-source connectivity checks completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CheckAsync(
        ConfiguredCrumbSource source,
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
                        "Crumb source {Source} connectivity check skipped because MCP is disabled for Recipes {RecipeIds}",
                        source.Source,
                        string.Join(',', source.RecipeIds));
                    return;
                }

                await mcp.CheckConnectionAsync(source.Transport.Mcp!, timeout.Token);
                logger.LogInformation(
                    "Crumb source {Source} connection succeeded via MCP server {McpServerHost} using tool {McpTool} in {DurationMilliseconds} ms for Recipes {RecipeIds}",
                    source.Source,
                    Host(source.Transport.Mcp!.ServerUrl),
                    source.Transport.Mcp.ToolName,
                    stopwatch.ElapsedMilliseconds,
                    string.Join(',', source.RecipeIds));
                return;
            }

            var path = ProbePath(source.Source);
            using var request = CrumbSourceUtilities.CreateRequest(
                HttpMethod.Get,
                CrumbSourceUtilities.Url(source.Transport, path),
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
                    "Crumb source {Source} connection succeeded via {Transport} at {CrumbSourceHost} using {ProbePath} with HTTP {StatusCode} in {DurationMilliseconds} ms for Recipes {RecipeIds}",
                    source.Source,
                    source.Transport.Mode,
                    Host(source.Transport.BaseUrl),
                    path,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    string.Join(',', source.RecipeIds));
            }
            else
            {
                logger.LogWarning(
                    "Crumb source {Source} connection failed via {Transport} at {CrumbSourceHost} using {ProbePath} with HTTP {StatusCode} in {DurationMilliseconds} ms for Recipes {RecipeIds}",
                    source.Source,
                    source.Transport.Mode,
                    Host(source.Transport.BaseUrl),
                    path,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    string.Join(',', source.RecipeIds));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Crumb source {Source} connection timed out after {DurationMilliseconds} ms for Recipes {RecipeIds}",
                source.Source,
                stopwatch.ElapsedMilliseconds,
                string.Join(',', source.RecipeIds));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Crumb source {Source} connection failed via {Transport} at {CrumbSourceHost} after {DurationMilliseconds} ms with {FailureType} for Recipes {RecipeIds}",
                source.Source,
                source.Transport.Mode,
                Host(source.Transport.Mode == "mcp"
                    ? source.Transport.Mcp!.ServerUrl
                    : source.Transport.BaseUrl),
                stopwatch.ElapsedMilliseconds,
                exception.GetType().Name,
                string.Join(',', source.RecipeIds));
        }
    }

    private static string ProbePath(string source) => source switch
    {
        CrumbSourceRegistry.PagerDuty => "users/me",
        CrumbSourceRegistry.Nomad => "v1/agent/self",
        CrumbSourceRegistry.Consul => "v1/status/leader",
        CrumbSourceRegistry.GitLab => "api/v4/user",
        CrumbSourceRegistry.Grafana => "api/health",
        CrumbSourceRegistry.Kafka => "api/health",
        CrumbSourceRegistry.VictoriaLogs => "health",
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

        if (source == CrumbSourceRegistry.PagerDuty)
        {
            request.Headers.Authorization = string.IsNullOrWhiteSpace(credential)
                ? null
                : new AuthenticationHeaderValue("Token", $"token={credential}");
        }
        else if (source is CrumbSourceRegistry.Nomad or CrumbSourceRegistry.Consul)
        {
            request.Headers.Authorization = null;
            if (!string.IsNullOrWhiteSpace(credential))
            {
                request.Headers.TryAddWithoutValidation(
                    source == CrumbSourceRegistry.Nomad ? "X-Nomad-Token" : "X-Consul-Token",
                    credential);
            }
        }
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid-host";
}
