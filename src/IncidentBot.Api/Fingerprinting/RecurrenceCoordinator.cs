using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Fingerprinting;

public interface IRecurrenceCoordinator
{
    Task<ProblemContext> ResolveProvisionalAsync(
        IncidentRecord incident,
        bool collectionEnabled,
        CancellationToken cancellationToken);

    Task<ProblemContext> ResolveFinalAsync(
        IncidentRecord incident,
        IReadOnlyList<EvidenceFinding> evidence,
        CancellationToken cancellationToken);
}

public sealed class RecurrenceCoordinator(
    FingerprintExtractor extractor,
    FingerprintGenerator generator,
    RecurrencePolicy policy,
    IProblemRepository repository,
    ILogger<RecurrenceCoordinator> logger) : IRecurrenceCoordinator
{
    public Task<ProblemContext> ResolveProvisionalAsync(
        IncidentRecord incident,
        bool collectionEnabled,
        CancellationToken cancellationToken) =>
        ResolveSafelyAsync(
            incident,
            [],
            FingerprintStage.Provisional,
            associate: !collectionEnabled,
            cancellationToken);

    public Task<ProblemContext> ResolveFinalAsync(
        IncidentRecord incident,
        IReadOnlyList<EvidenceFinding> evidence,
        CancellationToken cancellationToken) =>
        ResolveSafelyAsync(
            incident,
            evidence,
            FingerprintStage.Final,
            associate: true,
            cancellationToken);

    private async Task<ProblemContext> ResolveSafelyAsync(
        IncidentRecord incident,
        IReadOnlyList<EvidenceFinding> evidence,
        FingerprintStage stage,
        bool associate,
        CancellationToken cancellationToken)
    {
        try
        {
            var (fingerprint, possible) = await BuildAsync(incident, evidence, stage, cancellationToken);
            return associate
                ? await AssociateAsync(incident, fingerprint, possible, cancellationToken)
                : Provisional(fingerprint, possible);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Recurrence resolution failed at {FingerprintStage} using {AlgorithmVersion}",
                stage,
                FingerprintGenerator.AlgorithmVersion);
            return Unavailable(stage, exception);
        }
    }

    private async Task<(IncidentFingerprint Fingerprint, IReadOnlyList<PossibleProblemMatch> Possible)> BuildAsync(
        IncidentRecord incident,
        IReadOnlyList<EvidenceFinding> evidence,
        FingerprintStage stage,
        CancellationToken cancellationToken)
    {
        var fingerprint = generator.Generate(extractor.Extract(incident, evidence), stage);
        await repository.SaveFingerprintAsync(incident.Id, fingerprint, cancellationToken);
        var candidates = await repository.FindCandidatesAsync(fingerprint, cancellationToken);
        var ranked = policy.RankPossible(fingerprint, candidates);
        var possible = ranked
            .Select(value => new PossibleProblemMatch(
                value.Candidate.ProblemKey,
                value.MatchType,
                value.Score,
                value.MatchedFeatures,
                value.Candidate.LastSeen))
            .ToArray();
        return (fingerprint, possible);
    }

    private async Task<ProblemContext> AssociateAsync(
        IncidentRecord incident,
        IncidentFingerprint fingerprint,
        IReadOnlyList<PossibleProblemMatch> possible,
        CancellationToken cancellationToken)
    {
        var match = await repository.MatchOrCreateAsync(incident, fingerprint, cancellationToken);
        return new ProblemContext(
            "available",
            fingerprint.AlgorithmVersion,
            fingerprint.Stage,
            match.ProblemKey,
            match.ProblemGroupId,
            match.LifecycleState,
            match.MatchType,
            match.MatchType == "new" ? null : match.Score,
            match.MatchedFeatures,
            match.OccurrenceCount,
            match.FirstSeen,
            match.LastSeen,
            match.RecentOccurrences,
            possible.Where(value => !string.Equals(value.ProblemKey, match.ProblemKey, StringComparison.Ordinal)).ToArray(),
            fingerprint.Completeness);
    }

    private static ProblemContext Provisional(
        IncidentFingerprint fingerprint,
        IReadOnlyList<PossibleProblemMatch> possible) => new(
        "provisional",
        fingerprint.AlgorithmVersion,
        fingerprint.Stage,
        null,
        null,
        null,
        null,
        null,
        [],
        0,
        null,
        null,
        [],
        possible,
        fingerprint.Completeness);

    private static ProblemContext Unavailable(FingerprintStage stage, Exception exception) => new(
        "unavailable",
        FingerprintGenerator.AlgorithmVersion,
        stage,
        null,
        null,
        null,
        null,
        null,
        [],
        0,
        null,
        null,
        [],
        [],
        0,
        exception.GetType().Name is { Length: <= 80 } name ? name : "Recurrence failure");
}

public static class RecurrenceRegistration
{
    public static IServiceCollection AddIncidentRecurrence(this IServiceCollection services)
    {
        services.AddSingleton<FingerprintNormalizer>();
        services.AddSingleton<FingerprintExtractor>();
        services.AddSingleton<FingerprintGenerator>();
        services.AddSingleton<RecurrencePolicy>();
        services.AddSingleton<ProblemRepository>();
        services.AddSingleton<IProblemRepository>(provider => provider.GetRequiredService<ProblemRepository>());
        services.AddSingleton<IRecurrenceCoordinator, RecurrenceCoordinator>();
        return services;
    }
}
