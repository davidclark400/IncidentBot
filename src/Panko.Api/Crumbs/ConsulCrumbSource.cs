using System.Text.Json;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Options;

namespace Panko.Api.Crumbs;

public sealed class ConsulCrumbSource(
    IHttpClientFactory httpClientFactory,
    IMcpCrumbSourceAdapter mcp,
    CrumbSourceConfiguration crumbSources,
    ICredentialProvider credentials) : ICrumbSourceAdapter
{
    public string Source => CrumbSourceRegistry.Consul;
    public bool SupportsWindowExpansion => false;

    public Task<CrumbSourceResult> CollectAsync(
        CaseContext context,
        CrumbScope scope,
        CancellationToken cancellationToken)
    {
        var configuration = context.Recipe.Consul;
        if (configuration is null) return Task.FromResult(CrumbSourceResult.Excluded(Source));
        var transport = crumbSources.For(Source);
        return CrumbSourceUtilities.CollectAsync(
            Source,
            transport,
            mcp,
            context,
            scope,
            new
            {
                configuration.Datacenter,
                configuration.Partition,
                services = configuration.Services.Select(service => new
                {
                    service.Name,
                    service.Namespace
                })
            },
            ct => CollectNativeAsync(context, scope, configuration, transport, ct),
            cancellationToken);
    }

    private async Task<CrumbSourceResult> CollectNativeAsync(
        CaseContext context,
        CrumbScope scope,
        ConsulScope configuration,
        ConnectorTransport transport,
        CancellationToken cancellationToken)
    {
        var crumbs = new List<Crumb>();
        var links = new List<SourceLink>();
        var services = configuration.Services
            .DistinctBy(service => ServiceIdentity(service.Namespace, service.Name), StringComparer.Ordinal)
            .ToList();
        var budget = new CrumbSourceResponseBudget(scope.MaxBytes, transport.MaxBytes, services.Count);
        var client = httpClientFactory.CreateClient();

        foreach (var service in services)
        {
            var url = ServiceHealthUrl(transport, configuration, service);
            var operation = $"GET /v1/health/service/{{service}} ({ServiceLabel(service)})";
            var json = await budget.TryReadJsonAsync(
                operation,
                async operationCancellationToken =>
                {
                    using var request = CrumbSourceUtilities.CreateRequest(
                        HttpMethod.Get,
                        url,
                        transport,
                        credentials);
                    SetConsulToken(request, transport, credentials);
                    return await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        operationCancellationToken);
                },
                cancellationToken);
            if (json is null) continue;

            using (json)
            {
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Consul health response must be a JSON array.");
                }

                AddServiceCrumbs(
                    crumbs,
                    links,
                    configuration,
                    service,
                    json.RootElement,
                    url,
                    scope.End);
            }
        }

        var sourceMaximumItems = Math.Min(
            Math.Max(0, scope.MaxItems),
            Math.Max(0, transport.MaxItems));
        var rankedCrumbs = CrumbRankingPolicy.Rank(crumbs, context.OpenedAt);
        var distinctLinks = links.Distinct().ToList();
        var itemsTruncated = rankedCrumbs.Count > sourceMaximumItems
                             || distinctLinks.Count > sourceMaximumItems;
        var diagnostic = CrumbSourceUtilities.CombineDiagnostics(
            budget.Diagnostic,
            itemsTruncated
                ? $"Source item limit {sourceMaximumItems} truncated Consul Crumbs or links."
                : null);
        return new CrumbSourceResult(
            Source,
            budget.IsPartial || itemsTruncated ? CrumbSourceHealth.Partial : CrumbSourceHealth.Complete,
            rankedCrumbs.Take(sourceMaximumItems).ToList(),
            [],
            distinctLinks.Take(sourceMaximumItems).ToList(),
            0,
            diagnostic);
    }

    private void AddServiceCrumbs(
        ICollection<Crumb> crumbs,
        ICollection<SourceLink> links,
        ConsulScope configuration,
        ConsulService expectedService,
        JsonElement registrations,
        string url,
        DateTimeOffset observedAt)
    {
        var instances = registrations
            .EnumerateArray()
            .Select(ParseInstance)
            .ToList();
        var passing = instances.Count(instance => instance.Status == "passing");
        var warning = instances.Count(instance => instance.Status == "warning");
        var critical = instances.Count(instance => instance.Status == "critical");
        var unknown = instances.Count - passing - warning - critical;
        var status = instances.Count == 0
            ? "unregistered"
            : critical > 0
                ? "critical"
                : warning > 0
                    ? "warning"
                    : unknown > 0
                        ? "unknown"
                        : "passing";
        var severity = status switch
        {
            "unregistered" or "critical" => "critical",
            "warning" or "unknown" => "warning",
            _ => "info"
        };
        var summary = instances.Count == 0
            ? $"Consul service {ServiceLabel(expectedService)} is not registered"
            : $"Consul service {ServiceLabel(expectedService)} is registered with "
              + $"{instances.Count} instance(s): {passing} passing, {warning} warning, "
              + $"{critical} critical, {unknown} unknown";
        var serviceIdentity = ServiceIdentity(expectedService.Namespace, expectedService.Name);
        crumbs.Add(new Crumb(
            CrumbSourceUtilities.Id(Source, "service", serviceIdentity),
            Source,
            observedAt,
            null,
            "service-registration",
            severity,
            summary,
            null,
            url,
            status == "passing" ? 0.9 : 1,
            Provenance(configuration, expectedService, status, instances.Count, passing, warning, critical, unknown),
            ObjectType: "consul-service",
            ObjectId: serviceIdentity));
        links.Add(new SourceLink($"Consul {ServiceLabel(expectedService)}", url));

        foreach (var instance in instances.Where(instance => instance.Status != "passing"))
        {
            var instanceSeverity = instance.Status == "critical" ? "critical" : "warning";
            var instanceIdentity = $"{serviceIdentity}/{instance.Node}/{instance.Id}";
            crumbs.Add(new Crumb(
                CrumbSourceUtilities.Id(Source, "instance", instanceIdentity),
                Source,
                observedAt,
                null,
                "service-health",
                instanceSeverity,
                $"Consul service {ServiceLabel(expectedService)} instance {instance.Id} "
                + $"on node {instance.Node} is {instance.Status}",
                null,
                url,
                instance.Status == "critical" ? 1 : 0.9,
                Provenance(configuration, expectedService, instance.Status, 1,
                    instance.Status == "passing" ? 1 : 0,
                    instance.Status == "warning" ? 1 : 0,
                    instance.Status == "critical" ? 1 : 0,
                    instance.Status is not ("passing" or "warning" or "critical") ? 1 : 0),
                ObjectType: "consul-service-instance",
                ObjectId: instanceIdentity));
        }
    }

    private static ConsulInstance ParseInstance(JsonElement registration)
    {
        var service = registration.TryGetProperty("Service", out var serviceElement)
            ? serviceElement
            : default;
        var node = registration.TryGetProperty("Node", out var nodeElement)
            ? nodeElement
            : default;
        var serviceId = service.ValueKind == JsonValueKind.Object
            ? BoundedText(service, "ID", "")
            : "";
        var serviceName = service.ValueKind == JsonValueKind.Object
            ? BoundedText(service, "Service", "unknown")
            : "unknown";
        var nodeName = node.ValueKind == JsonValueKind.Object
            ? BoundedText(node, "Node", "unknown")
            : "unknown";
        var statuses = registration.TryGetProperty("Checks", out var checks)
                       && checks.ValueKind == JsonValueKind.Array
            ? checks.EnumerateArray()
                .Select(check => CrumbSourceUtilities.Text(check, "Status", "unknown").ToLowerInvariant())
                .ToList()
            : [];
        var status = statuses.Contains("critical", StringComparer.Ordinal)
            ? "critical"
            : statuses.Contains("warning", StringComparer.Ordinal)
                ? "warning"
                : statuses.Count > 0 && statuses.All(item => item == "passing")
                    ? "passing"
                    : "unknown";
        return new ConsulInstance(
            string.IsNullOrWhiteSpace(serviceId) ? serviceName : serviceId,
            nodeName,
            status);
    }

    private static string BoundedText(JsonElement element, string name, string fallback)
    {
        var value = CrumbSourceUtilities.Text(element, name, fallback);
        var withoutControls = new string(value.Where(character => !char.IsControl(character)).ToArray());
        return CrumbSourceUtilities.Truncate(
            string.IsNullOrWhiteSpace(withoutControls) ? fallback : withoutControls,
            200);
    }

    private static System.Text.Json.Nodes.JsonObject Provenance(
        ConsulScope configuration,
        ConsulService service,
        string status,
        int totalInstances,
        int passingInstances,
        int warningInstances,
        int criticalInstances,
        int unknownInstances) => CrumbSourceUtilities.Provenance(
        "GET /v1/health/service/{service}",
        new
        {
            configuration.Datacenter,
            configuration.Partition,
            service.Namespace,
            service = service.Name,
            status,
            totalInstances,
            passingInstances,
            warningInstances,
            criticalInstances,
            unknownInstances
        });

    private static string ServiceHealthUrl(
        ConnectorTransport transport,
        ConsulScope configuration,
        ConsulService service)
    {
        var parameters = new List<string>();
        AddQueryParameter(parameters, "dc", configuration.Datacenter);
        AddQueryParameter(parameters, "ns", service.Namespace);
        AddQueryParameter(parameters, "partition", configuration.Partition);
        var query = parameters.Count == 0 ? "" : "?" + string.Join('&', parameters);
        return CrumbSourceUtilities.Url(
            transport,
            $"v1/health/service/{Uri.EscapeDataString(service.Name)}{query}");
    }

    private static void AddQueryParameter(ICollection<string> parameters, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static void SetConsulToken(
        HttpRequestMessage request,
        ConnectorTransport transport,
        ICredentialProvider credentials)
    {
        request.Headers.Authorization = null;
        var token = credentials.Get(transport.CredentialEnv);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Consul-Token", token);
        }
    }

    private static string ServiceIdentity(string serviceNamespace, string name) =>
        string.IsNullOrWhiteSpace(serviceNamespace) ? name : $"{serviceNamespace}/{name}";

    private static string ServiceLabel(ConsulService service) =>
        ServiceIdentity(service.Namespace, service.Name);

    private sealed record ConsulInstance(string Id, string Node, string Status);
}
