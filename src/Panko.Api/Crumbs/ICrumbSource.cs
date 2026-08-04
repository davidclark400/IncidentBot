using Panko.Api.Domain;
using Panko.Api.Options;

namespace Panko.Api.Crumbs;

public interface ICrumbSourceAdapter
{
    string Source { get; }
    bool SupportsWindowExpansion => false;
    Task<CrumbSourceResult> CollectAsync(
        CaseContext context,
        CrumbScope scope,
        CancellationToken cancellationToken);
}

public interface IMcpCrumbSourceAdapter
{
    Task<CrumbSourceResult> CollectAsync(
        string source,
        McpToolConfiguration configuration,
        CaseContext context,
        CrumbScope scope,
        object allowedResources,
        string? allowedBaseUrl,
        CancellationToken cancellationToken);
}
