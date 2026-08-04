using Panko.Api.Domain;

namespace Panko.Api.Crumbs;

public enum CrumbClarityReason
{
    None,
    ExplicitFailure,
    CorroboratedSignals,
    ChangePrecedesFailure
}

public sealed record CrumbClarityAssessment(
    bool IsClear,
    CrumbClarityReason Reason,
    IReadOnlyList<string> SupportingCrumbIds)
{
    public static CrumbClarityAssessment Inconclusive { get; } =
        new(false, CrumbClarityReason.None, []);
}

public enum CrumbCollectionCompletionReason
{
    ClearResult,
    MaximumWindowReached,
    NoExpandableCrumbSources,
    NoCrumbSources
}

public sealed record CrumbCollectionOutcome(
    CrumbCollectionCompletionReason CompletionReason,
    CrumbClarityAssessment Clarity,
    int PassCount,
    int FinalLookbackMinutes,
    DateTimeOffset CoverageStart,
    DateTimeOffset CoverageEnd);

public sealed record CrumbCollectionResult(
    IReadOnlyList<CrumbSourceResult> SourceResults,
    CrumbCollectionOutcome Outcome);

public sealed record CrumbCollectionPass(
    int Number,
    int LookbackMinutes,
    DateTimeOffset CoverageStart,
    DateTimeOffset CoverageEnd);

public interface ICrumbCollectionProgressObserver
{
    Task PassStartedAsync(
        CrumbCollectionPass pass,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken);

    Task SourceCompletedAsync(
        CrumbCollectionPass pass,
        CrumbSourceResult result,
        CancellationToken cancellationToken);

    Task PassCompletedAsync(
        CrumbCollectionPass pass,
        CrumbClarityAssessment clarity,
        CancellationToken cancellationToken);
}
