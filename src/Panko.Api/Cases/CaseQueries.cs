using System.Text.Json;
using Panko.Api.Security;
using Panko.Contracts;
using ContractOriginKind = Panko.Contracts.CaseOriginKind;
using DomainOriginKind = Panko.Api.Domain.CaseOriginKind;

namespace Panko.Api.Cases;

public interface ICaseQueries
{
    Task<CaseStatusResponse> GetAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken);

    Task<Page<Panko.Contracts.CaseInput>> ListInputsAsync(
        Guid caseId,
        int offset,
        int limit,
        CallerIdentity caller,
        CancellationToken cancellationToken);

    Task<RecentCases> ListRecentAsync(
        int limit,
        CallerIdentity caller,
        CancellationToken cancellationToken);
}

public sealed class CaseQueries(
    ICaseInputStore repository,
    ICaseAccessAuthorizer caseAccess,
    ITeamAuthorization? teams = null) : ICaseQueries
{
    public async Task<CaseStatusResponse> GetAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var caseRecord = await repository.GetCaseAsync(caseId, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);
        if (await caseAccess.AuthorizeAsync(
                caller.Principal, caseId, CaseAccessKind.CaseFile, cancellationToken) is null)
        {
            throw new CaseAuthorizationException(
                $"Case '{caseId}' is not accessible to the caller's teams.");
        }
        var caseFile = await repository.GetCaseFileAsync(caseId, cancellationToken);
        return new CaseStatusResponse(
            caseRecord.Id,
            ToContract(caseRecord.Origin.Kind),
            caseRecord.Status,
            caseRecord.RecipeId,
            caseRecord.ServiceId,
            caseRecord.Title,
            caseRecord.InputVersion,
            caseRecord.ProjectedInputVersion,
            caseRecord.Version,
            caseRecord.CreatedBy,
            caseRecord.UpdatedAt,
            caseFile?.DeterministicSummary is { Length: > 4000 } summary
                ? summary[..4000]
                : caseFile?.DeterministicSummary,
            $"/cases/{caseRecord.Id}");
    }

    public async Task<Page<Panko.Contracts.CaseInput>> ListInputsAsync(
        Guid caseId,
        int offset,
        int limit,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var caseRecord = await repository.GetCaseAsync(caseId, cancellationToken)
            ?? throw new CaseNotFoundException(caseId);
        if (await caseAccess.AuthorizeAsync(
                caller.Principal, caseId, CaseAccessKind.Crumbs, cancellationToken) is null)
        {
            throw new CaseAuthorizationException(
                $"Case '{caseId}' is not accessible to the caller's teams.");
        }
        var inputs = await repository.ListInputsAsync(
            caseId, throughInputVersion: null, includeInactive: true, cancellationToken);
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 500);
        var supersededByCrumbId = inputs
            .Where(item => item.SupersedesCrumbId is not null)
            .GroupBy(item => item.SupersedesCrumbId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Sequence).First().Id);
        var items = inputs.Skip(safeOffset).Take(safeLimit)
            .Select(item => ToContract(
                item,
                caseRecord.ProjectedInputVersion,
                supersededByCrumbId.GetValueOrDefault(item.Id)))
            .ToArray();
        return new Page<Panko.Contracts.CaseInput>(inputs.Count, items);
    }

    public async Task<RecentCases> ListRecentAsync(
        int limit,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var scope = teams?.ResolveScope(caller.Principal) ?? TeamAccessScope.Unrestricted;
        var candidates = await repository.ListRecentAsync(
            Math.Clamp(limit, 1, 500), scope, cancellationToken);
        var authorized = new List<RecentCase>();
        foreach (var caseRecord in candidates)
        {
            if (await caseAccess.AuthorizeAsync(
                    caller.Principal, caseRecord.Id, CaseAccessKind.CaseFile, cancellationToken) is null)
            {
                continue;
            }
            authorized.Add(new RecentCase(
                caseRecord.Id,
                ToContract(caseRecord.Origin.Kind),
                caseRecord.RecipeId,
                caseRecord.ServiceId,
                caseRecord.Title,
                caseRecord.Status,
                caseRecord.InputVersion,
                caseRecord.ProjectedInputVersion,
                caseRecord.Version,
                caseRecord.CreatedBy,
                caseRecord.UpdatedAt,
                $"/cases/{caseRecord.Id}"));
        }
        return new RecentCases(authorized.Count, authorized);
    }

    private static Panko.Contracts.CaseInput ToContract(
        CaseInput item,
        long projectedInputVersion,
        Guid? supersededByCrumbId)
    {
        var attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                item.Attributes.ToJsonString())
            ?? [];
        var active = item.RetractedInputVersion is null;
        var presentInProjection = item.InputVersion <= projectedInputVersion
            && (item.RetractedInputVersion is null
                || item.RetractedInputVersion > projectedInputVersion);
        return new Panko.Contracts.CaseInput(
            item.Id,
            item.Sequence,
            item.InputVersion,
            item.ClientCrumbId,
            item.ProducerPrincipal,
            item.ReceivedAt,
            item.OccurredAt,
            item.Kind,
            item.Category,
            item.Severity,
            item.Summary,
            item.Excerpt,
            item.DeclaredSource,
            item.SourceReference,
            item.Url,
            item.Actor,
            item.ObjectType,
            item.ObjectId,
            attributes,
            item.TrustLevel,
            item.SupersedesCrumbId,
            supersededByCrumbId,
            item.RetractedAt,
            item.RetractedInputVersion,
            active,
            presentInProjection ? projectedInputVersion : null);
    }

    private static ContractOriginKind ToContract(DomainOriginKind kind) => kind switch
    {
        DomainOriginKind.Agent => ContractOriginKind.Agent,
        DomainOriginKind.Manual => ContractOriginKind.Manual,
        _ => ContractOriginKind.PagerDuty
    };
}
