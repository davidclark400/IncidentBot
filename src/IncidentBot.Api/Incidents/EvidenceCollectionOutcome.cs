using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

public enum EvidenceClarityReason
{
    None,
    ExplicitFailure,
    CorroboratedSignals,
    ChangePrecedesFailure
}

public sealed record EvidenceClarityAssessment(
    bool IsClear,
    EvidenceClarityReason Reason,
    IReadOnlyList<string> SupportingEvidenceIds)
{
    public static EvidenceClarityAssessment Inconclusive { get; } =
        new(false, EvidenceClarityReason.None, []);
}

public enum EvidenceCollectionCompletionReason
{
    ClearResult,
    MaximumWindowReached,
    NoExpandableConnectors,
    NoConnectors
}

public sealed record EvidenceCollectionOutcome(
    EvidenceCollectionCompletionReason CompletionReason,
    EvidenceClarityAssessment Clarity,
    int PassCount,
    int FinalLookbackMinutes,
    DateTimeOffset CoverageStart,
    DateTimeOffset CoverageEnd);

public sealed record EvidenceCollectionResult(
    IReadOnlyList<ConnectorResult> ConnectorResults,
    EvidenceCollectionOutcome Outcome);
