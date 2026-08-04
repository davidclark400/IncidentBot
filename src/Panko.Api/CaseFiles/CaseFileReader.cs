using Panko.Api.Domain;

namespace Panko.Api.CaseFiles;

public sealed record CaseFileState(
    Guid CaseId,
    string? PagerDutyIncidentId,
    string ServiceId,
    string RecipeId,
    string Title,
    string Urgency,
    PagerDutyIncidentState PagerDutyState,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt,
    int CaseFileVersion,
    bool IsFrozen,
    CaseFile? CaseFile)
{
    public string Team { get; init; } = "unmapped";

    public CaseOrigin Origin { get; init; } = new(
        CaseOriginKind.PagerDuty,
        PagerDutyIncidentId);

    public long InputVersion { get; init; }

    public long ProjectedInputVersion { get; init; }

    public string? CreatedBy { get; init; }

    public static CaseFileState From(CaseRecord caseRecord, CaseFile? caseFile = null) => new(
        caseRecord.Id, caseRecord.PagerDutyIncidentId, caseRecord.ServiceId, caseRecord.RecipeId, caseRecord.Title,
        caseRecord.Urgency, caseRecord.PagerDutyState, caseRecord.Status, caseRecord.OpenedAt, caseRecord.UpdatedAt,
        caseRecord.Version, caseRecord.IsFrozen, caseFile)
    {
        Team = caseRecord.Team,
        Origin = caseRecord.Origin,
        InputVersion = caseRecord.InputVersion,
        ProjectedInputVersion = caseRecord.ProjectedInputVersion,
        CreatedBy = caseRecord.CreatedBy
    };

    public static CaseFileState From(CaseFile caseFile, bool isFrozen = false) => new(
        caseFile.CaseId, caseFile.PagerDutyIncidentId, caseFile.ServiceId, caseFile.RecipeId, caseFile.Title,
        caseFile.Urgency, caseFile.PagerDutyState, caseFile.Status, caseFile.OpenedAt, caseFile.UpdatedAt,
        caseFile.CaseFileVersion, isFrozen, caseFile)
    {
        Origin = caseFile.Origin,
        InputVersion = caseFile.InputVersion,
        ProjectedInputVersion = caseFile.ProjectedInputVersion,
        CreatedBy = caseFile.CreatedBy
    };
}

public interface ICaseFileReader
{
    Task<CaseFileState?> GetAsync(Guid caseId, CancellationToken cancellationToken);
}

public interface ICaseProgressReader
{
    Task<CaseProgress?> GetProgressAsync(
        Guid caseId,
        CancellationToken cancellationToken);
}

public sealed class RepositoryCaseFileReader(ICaseStore repository) :
    ICaseFileReader,
    ICaseProgressReader
{
    public async Task<CaseFileState?> GetAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var @case = await repository.GetCaseAsync(caseId, cancellationToken);
        if (@case is null) return null;
        var caseFile = await repository.GetCaseFileAsync(caseId, cancellationToken);
        if (caseFile is not null)
        {
            caseFile = caseFile with
            {
                Origin = @case.Origin,
                InputVersion = @case.InputVersion,
                ProjectedInputVersion = @case.ProjectedInputVersion,
                CreatedBy = @case.CreatedBy
            };
        }
        return CaseFileState.From(@case, caseFile);
    }

    public Task<CaseProgress?> GetProgressAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        repository.GetProgressAsync(caseId, cancellationToken);
}
