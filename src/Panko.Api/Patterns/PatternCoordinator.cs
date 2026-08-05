using Panko.Api.Domain;
using Panko.Api.Signatures;

namespace Panko.Api.Patterns;

public interface IPatternCoordinator
{
    Task<PatternContext> ResolveProvisionalAsync(
        CaseRecord caseRecord,
        bool collectionEnabled,
        CancellationToken cancellationToken);

    Task<PatternContext> ResolveFinalAsync(
        CaseRecord caseRecord,
        IReadOnlyList<Crumb> crumbs,
        CancellationToken cancellationToken);
}

public sealed class PatternCoordinator(
    SignatureExtractor extractor,
    SignatureGenerator generator,
    PatternPolicy policy,
    IPatternRepository repository,
    ILogger<PatternCoordinator> logger) : IPatternCoordinator
{
    public Task<PatternContext> ResolveProvisionalAsync(
        CaseRecord caseRecord,
        bool collectionEnabled,
        CancellationToken cancellationToken) =>
        ResolveSafelyAsync(
            caseRecord,
            [],
            SignatureStage.Provisional,
            associate: !collectionEnabled,
            cancellationToken);

    public Task<PatternContext> ResolveFinalAsync(
        CaseRecord caseRecord,
        IReadOnlyList<Crumb> crumbs,
        CancellationToken cancellationToken) =>
        ResolveSafelyAsync(
            caseRecord,
            crumbs,
            SignatureStage.Final,
            associate: true,
            cancellationToken);

    private async Task<PatternContext> ResolveSafelyAsync(
        CaseRecord caseRecord,
        IReadOnlyList<Crumb> crumbs,
        SignatureStage stage,
        bool associate,
        CancellationToken cancellationToken)
    {
        try
        {
            var (signature, possible) = await BuildAsync(caseRecord, crumbs, stage, cancellationToken);
            return associate
                ? await AssociateAsync(caseRecord, signature, possible, cancellationToken)
                : Provisional(signature, possible);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Pattern resolution failed at {SignatureStage} using {AlgorithmVersion}",
                stage,
                SignatureGenerator.AlgorithmVersion);
            return Unavailable(stage, exception);
        }
    }

    private async Task<(CaseSignature Signature, IReadOnlyList<PossiblePatternMatch> Possible)> BuildAsync(
        CaseRecord caseRecord,
        IReadOnlyList<Crumb> crumbs,
        SignatureStage stage,
        CancellationToken cancellationToken)
    {
        var signature = generator.Generate(extractor.Extract(caseRecord, crumbs), stage);
        await repository.SaveSignatureAsync(caseRecord.Id, signature, cancellationToken);
        var candidates = await repository.FindCandidatesAsync(
            caseRecord.Team,
            signature,
            cancellationToken);
        var ranked = policy.RankPossible(signature, candidates);
        var possible = ranked
            .Select(value => new PossiblePatternMatch(
                value.Candidate.PatternKey,
                value.MatchType,
                value.Score,
                value.MatchedFeatures,
                value.Candidate.LastSeen))
            .ToArray();
        return (signature, possible);
    }

    private async Task<PatternContext> AssociateAsync(
        CaseRecord caseRecord,
        CaseSignature signature,
        IReadOnlyList<PossiblePatternMatch> possible,
        CancellationToken cancellationToken)
    {
        var match = await repository.MatchOrCreateAsync(caseRecord, signature, cancellationToken);
        return new PatternContext(
            "available",
            signature.AlgorithmVersion,
            signature.Stage,
            match.PatternKey,
            match.PatternId,
            match.LifecycleState,
            match.MatchType,
            match.MatchType == "new" ? null : match.Score,
            match.MatchedFeatures,
            match.OccurrenceCount,
            match.FirstSeen,
            match.LastSeen,
            match.RecentOccurrences,
            possible.Where(value => !string.Equals(value.PatternKey, match.PatternKey, StringComparison.Ordinal)).ToArray(),
            signature.Completeness);
    }

    private static PatternContext Provisional(
        CaseSignature signature,
        IReadOnlyList<PossiblePatternMatch> possible) => new(
        "provisional",
        signature.AlgorithmVersion,
        signature.Stage,
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
        signature.Completeness);

    private static PatternContext Unavailable(SignatureStage stage, Exception exception) => new(
        "unavailable",
        SignatureGenerator.AlgorithmVersion,
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
        exception.GetType().Name is { Length: <= 80 } name ? name : "Pattern resolution failure");
}

public static class PatternRegistration
{
    public static IServiceCollection AddPatternMatching(this IServiceCollection services)
    {
        services.AddSingleton<SignatureNormalizer>();
        services.AddSingleton<SignatureExtractor>();
        services.AddSingleton<SignatureGenerator>();
        services.AddSingleton<PatternPolicy>();
        services.AddSingleton<PatternRepository>();
        services.AddSingleton<IPatternRepository>(provider => provider.GetRequiredService<PatternRepository>());
        services.AddSingleton<IPatternCoordinator, PatternCoordinator>();
        return services;
    }
}
