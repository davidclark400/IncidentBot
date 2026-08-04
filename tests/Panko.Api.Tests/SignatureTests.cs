using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

public sealed class SignatureTests
{
    private readonly SignatureNormalizer _normalizer = new();

    [Theory]
    [InlineData("Found 17 failed requests", "Found 92 failed requests")]
    [InlineData("allocation 75ca81 failed after 31.4 seconds", "allocation 91bd22 failed after 2 seconds")]
    [InlineData("POST /payments/ord_78431 returned 502", "POST /payments/ord_99271 returned 504")]
    [InlineData("timeout connecting to 10.23.4.18:5432", "timeout connecting to 10.20.9.3:6432")]
    public void DynamicValuesNormalizeToTheSameTemplate(string first, string second) =>
        Assert.Equal(_normalizer.Normalize(first), _normalizer.Normalize(second));

    [Fact]
    public void IdentifiersAddressesTimestampsAndDurationsAreBounded()
    {
        var normalized = _normalizer.Normalize(
            "request_id=550e8400-e29b-41d4-a716-446655440000 trace abcdef123456 at 2026-07-11T10:02:03Z from [2001:db8::1]:443 after 920ms");

        Assert.DoesNotContain("550e8400", normalized);
        Assert.DoesNotContain("abcdef123456", normalized);
        Assert.DoesNotContain("2026-07-11", normalized);
        Assert.DoesNotContain("2001:db8", normalized);
        Assert.DoesNotContain("920", normalized);
        Assert.True(normalized.Length <= SignatureNormalizer.MaximumFeatureLength);
    }

    [Fact]
    public void HashesAreIndependentOfCrumbOrderAndSuspectedCommit()
    {
        var extractor = new SignatureExtractor(_normalizer);
        var generator = new SignatureGenerator();
        var first = Crumb("a", "exception", "Provider timeout", "aaaaaaa", "src/ProviderClient.cs");
        var second = Crumb("b", "workload-failure", "checkout failed", "bbbbbbb", "src/Checkout.cs");
        var forward = generator.Generate(extractor.Extract(BuildCase(), [first, second]), SignatureStage.Final);
        var reverse = generator.Generate(extractor.Extract(BuildCase(), [second, first]), SignatureStage.Final);

        Assert.Equal(forward.FamilyHash, reverse.FamilyHash);
        Assert.Equal(forward.ExactHash, reverse.ExactHash);
        Assert.DoesNotContain("aaaaaaa", JsonSerializer.Serialize(forward.Features));
        Assert.DoesNotContain("bbbbbbb", JsonSerializer.Serialize(forward.Features));
    }

    [Fact]
    public void ChangingOnlyTheSuspectedCommitDoesNotChangeEitherHash()
    {
        var extractor = new SignatureExtractor(_normalizer);
        var generator = new SignatureGenerator();
        var first = generator.Generate(
            extractor.Extract(BuildCase(), [Crumb("error", "exception", "Provider timeout", "aaaaaaa", "src/ProviderClient.cs")]),
            SignatureStage.Final);
        var second = generator.Generate(
            extractor.Extract(BuildCase(), [Crumb("error", "exception", "Provider timeout", "bbbbbbb", "src/ProviderClient.cs")]),
            SignatureStage.Final);

        Assert.Equal(first.FamilyHash, second.FamilyHash);
        Assert.Equal(first.ExactHash, second.ExactHash);
    }

    [Fact]
    public void DifferentServicesAndKnownEnvironmentsDoNotMatch()
    {
        var policy = Policy();
        var signature = Signature(Features(service: "payments", scopes: ["production"]));
        var otherService = Candidate(Signature(Features(service: "catalog", scopes: ["production"])));
        var otherEnvironment = Candidate(Signature(Features(service: "payments", scopes: ["staging"])));

        Assert.Empty(policy.RankPossible(signature, [otherService, otherEnvironment]));
    }

    [Fact]
    public void ConflictingEnvironmentsRemainABoundaryEvenWhenRegionsMatch()
    {
        var current = Signature(Features(scopes: ["environment:production", "region:eu-west"]));
        var historical = Signature(Features(scopes: ["environment:staging", "region:eu-west"]));

        Assert.Empty(Policy().RankPossible(current, [Candidate(historical)]));
    }

    [Fact]
    public void SuspectedChangeCodeLocationsDoNotEnterTheSignature()
    {
        var change = Crumb("deploy", "deployment", "deployed suspected change", "abcdef123456", "src/SuspectedChange.cs");

        var features = new SignatureExtractor(_normalizer).Extract(BuildCase(), [change]);

        Assert.Empty(features.CodeLocations);
        Assert.DoesNotContain("suspectedchange", JsonSerializer.Serialize(features), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedPipelineOutputDoesNotBecomeARecurringSymptomTemplate()
    {
        var pipeline = Crumb(
            "pipeline-job", "pipeline-job-output", "Job integration-tests in pipeline 919 failed",
            "abcdef123456", "src/IntegrationTests.cs");

        var features = new SignatureExtractor(_normalizer).Extract(BuildCase(), [pipeline]);

        Assert.DoesNotContain("pipeline", features.SymptomCategories);
        Assert.Empty(features.ErrorTemplates);
        Assert.Empty(features.CodeLocations);
    }

    [Fact]
    public void ActorIdentityIsRemovedAndStableMemberIdentityIsRetained()
    {
        var crumb = new Crumb(
            "error", "logs", DateTimeOffset.UtcNow, null, "exception", "warning",
            "Alex Chen observed timeout in ProviderClient.SendAsync", null, null, 1, new JsonObject(), Actor: "Alex Chen");

        var features = new SignatureExtractor(_normalizer).Extract(BuildCase(), [crumb]);
        var serialized = JsonSerializer.Serialize(features);

        Assert.DoesNotContain("alex chen", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("member:providerclient.sendasync", features.CodeLocations);
    }

    [Fact]
    public void ErrorAndCodeMatchesProduceAnAutomaticExplainableScore()
    {
        var policy = Policy();
        var current = Signature(Features(title: "provider timeout checkout", errors: ["provider timeout"], code: ["payments:providerclient.sendasync"]));
        var historical = Signature(Features(title: "checkout provider timed out", errors: ["provider timeout"], code: ["payments:providerclient.sendasync"]));

        var match = policy.SelectAssociation(current, [Candidate(historical)], existingPatternId: null);

        Assert.NotNull(match);
        Assert.True(match.Score >= 80);
        Assert.Contains(match.MatchedFeatures, value => value.StartsWith("error:", StringComparison.Ordinal));
        Assert.Contains(match.MatchedFeatures, value => value.StartsWith("code location:", StringComparison.Ordinal));
    }

    [Fact]
    public void FamilyEqualityCannotOverrideConflictingExactSymptoms()
    {
        var policy = Policy();
        var current = Signature(Features(errors: ["database refused connection"], code: ["payments:dbclient.open"]));
        var historical = Signature(Features(errors: ["provider request timeout"], code: ["payments:providerclient.send"]));
        historical = historical with { FamilyHash = current.FamilyHash };
        var candidates = new[] { Candidate(historical) };

        Assert.Empty(policy.RankPossible(current with { Stage = SignatureStage.Provisional }, candidates));
        Assert.Null(policy.SelectAssociation(current, candidates, existingPatternId: null));
    }

    [Fact]
    public void AlgorithmVersionsNeverCompareAsExactMatches()
    {
        var current = Signature(Features());
        var historical = current with { AlgorithmVersion = "v2" };
        var policy = Policy();
        var candidates = new[] { Candidate(historical) };

        Assert.Empty(policy.RankPossible(current with { Stage = SignatureStage.Provisional }, candidates));
        Assert.Null(policy.SelectAssociation(current, candidates, existingPatternId: null));
    }

    [Fact]
    public void ProvisionalPreviewCanExposeAnExactCandidateWithoutTreatingItAsAPossibleFinalMatch()
    {
        var signature = Signature(Features());
        var candidate = Candidate(signature);
        var policy = Policy();

        Assert.Single(policy.RankPossible(signature with { Stage = SignatureStage.Provisional }, [candidate]));
        Assert.Empty(policy.RankPossible(signature, [candidate]));
    }

    [Fact]
    public void ProvisionalCandidateRankingPrioritizesExactMatchesOverRecency()
    {
        var signature = Signature(Features());
        var exact = Candidate(
            signature,
            "PAYMENTS-EXACT-1234",
            DateTimeOffset.Parse("2026-07-01T10:00:00Z"));
        var similar = Candidate(
            Signature(Features(
                title: "unrelated title",
                errors: ["provider timeout"],
                code: ["payments:otherclient.sendasync"])),
            "PAYMENTS-SIMILAR-5678",
            DateTimeOffset.Parse("2026-07-12T10:00:00Z"));

        var ranked = Policy().RankPossible(
            signature with { Stage = SignatureStage.Provisional },
            [similar, exact]);

        Assert.Equal([exact, similar], ranked.Select(value => value.Candidate));
        Assert.Equal("exact", ranked[0].MatchType);
        Assert.Equal(100, ranked[0].Score);
    }

    [Fact]
    public void FinalCandidateThresholdsSeparatePossibleFromAutomaticMatches()
    {
        var signature = Signature(Features());
        var possible = Candidate(Signature(Features(
            title: "unrelated title",
            errors: ["provider timeout"],
            code: ["payments:otherclient.sendasync"])));
        var exact = Candidate(signature);
        var policy = Policy();

        var possibleMatch = Assert.Single(policy.RankPossible(signature, [possible]));

        Assert.InRange(possibleMatch.Score, 60, 79);
        Assert.Null(policy.SelectAssociation(signature, [possible], existingPatternId: null));
        Assert.Empty(policy.RankPossible(signature, [exact]));
        Assert.Equal("exact", policy.SelectAssociation(signature, [exact], existingPatternId: null)?.MatchType);
    }

    [Fact]
    public void PatternKeysAreDerivedByTheRecurrencePolicy()
    {
        var signature = Signature(Features(service: "payments"));

        var patternKey = PatternPolicy.PatternKey(signature);

        Assert.Equal($"PAYMENTS-CHECKOUT-{signature.ExactHash[..20].ToUpperInvariant()}", patternKey);
    }

    [Fact]
    public void SameFamilyWithDifferentExactCrumbsGetsDistinctPatternKeys()
    {
        var first = Signature(Features(
            errors: ["provider timeout"],
            code: ["payments:providerclient.sendasync"]));
        var second = Signature(Features(
            errors: ["database corruption"],
            code: ["payments:databaseclient.readasync"]));

        Assert.Equal(first.FamilyHash, second.FamilyHash);
        Assert.NotEqual(first.ExactHash, second.ExactHash);
        Assert.NotEqual(
            PatternPolicy.PatternKey(first),
            PatternPolicy.PatternKey(second));
    }

    [Fact]
    public void LifecycleClassificationAppliesResolutionRegressionEscalationAndOccurrenceRulesInOrder()
    {
        var policy = Policy();

        Assert.Equal(PatternLifecycleState.Resolved,
            policy.ClassifyLifecycle(PatternLifecycleState.Escalating, PagerDutyIncidentState.Resolved, active: false, occurrenceCount: 4, recentCount: 4));
        Assert.Equal(PatternLifecycleState.Regressed,
            policy.ClassifyLifecycle(PatternLifecycleState.Resolved, PagerDutyIncidentState.Triggered, active: true, occurrenceCount: 4, recentCount: 4));
        Assert.Equal(PatternLifecycleState.Escalating,
            policy.ClassifyLifecycle(PatternLifecycleState.Ongoing, PagerDutyIncidentState.Triggered, active: true, occurrenceCount: 4, recentCount: 3));
        Assert.Equal(PatternLifecycleState.New,
            policy.ClassifyLifecycle(previous: null, PagerDutyIncidentState.Triggered, active: true, occurrenceCount: 1, recentCount: 1));
        Assert.Equal(PatternLifecycleState.Ongoing,
            policy.ClassifyLifecycle(PatternLifecycleState.New, PagerDutyIncidentState.Triggered, active: true, occurrenceCount: 2, recentCount: 2));
    }

    [Fact]
    public void RetentionLifecycleClassificationUsesTheCurrentOccurrenceProjection()
    {
        var policy = Policy();

        Assert.Equal(PatternLifecycleState.Resolved,
            policy.ClassifyAfterRetention(active: false, occurrenceCount: 2, recentCount: 2));
        Assert.Equal(PatternLifecycleState.Escalating,
            policy.ClassifyAfterRetention(active: true, occurrenceCount: 3, recentCount: 3));
        Assert.Equal(PatternLifecycleState.New,
            policy.ClassifyAfterRetention(active: true, occurrenceCount: 1, recentCount: 1));
        Assert.Equal(PatternLifecycleState.Ongoing,
            policy.ClassifyAfterRetention(active: true, occurrenceCount: 2, recentCount: 2));
    }

    [Fact]
    public void CandidateAndEscalationCutoffsComeFromTheRecurrencePolicy()
    {
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var policy = Policy(new PankoOptions
        {
            SignatureCandidateLookbackDays = 30,
            SignatureEscalationWindowDays = 7
        });

        Assert.Equal(now - TimeSpan.FromDays(30), policy.CandidateCutoff(now));
        Assert.Equal(now - TimeSpan.FromDays(7), policy.EscalationCutoff(now));
    }

    [Fact]
    public void SecretsAreNotStoredAsFeaturesOrRenderedInSlack()
    {
        var caseRecord = BuildCase() with { Title = "checkout password=hunter2 token=secret-token-value failed" };
        var features = new SignatureExtractor(_normalizer).Extract(caseRecord,
            [new Crumb("e", "logs", DateTimeOffset.UtcNow, null, "error", "warning",
                "authorization: Bearer abcdefghijklmnop timeout", null, null, 1, new JsonObject())]);
        var serialized = JsonSerializer.Serialize(features);
        Assert.DoesNotContain("hunter2", serialized);
        Assert.DoesNotContain("secret-token-value", serialized);
        Assert.DoesNotContain("abcdefghijklmnop", serialized);

        var pattern = new PatternContext("available", "v1", SignatureStage.Final, "PAYMENTS-CHECKOUT-1234", Guid.NewGuid(),
            PatternLifecycleState.Ongoing, "similarity", 90, features.ErrorTemplates, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], 1);
        var slack = SlackPublisher.BuildPatternText(pattern)!;
        Assert.DoesNotContain("hunter2", slack);
        Assert.DoesNotContain("abcdefghijklmnop", slack);
    }

    [Fact]
    public void SlackRendersConciseRegressionContextAndExplanation()
    {
        var pattern = new PatternContext(
            "available", "v1", SignatureStage.Final, "PAYMENTS-CHECKOUT-4F19", Guid.NewGuid(),
            PatternLifecycleState.Regressed, "similarity", 90,
            ["error: provider timeout", "component: checkout", "code location: ProviderClient.SendAsync"],
            4, DateTimeOffset.Parse("2026-06-01T00:00:00Z"), DateTimeOffset.Parse("2026-06-14T00:00:00Z"), [], [], 0.9);

        var text = SlackPublisher.BuildPatternText(pattern)!;

        Assert.Contains("Regressed Pattern PAYMENTS-CHECKOUT-4F19", text);
        Assert.Contains("90% similarity", text);
        Assert.Contains("4 occurrences", text);
        Assert.Contains("Matched on provider timeout, checkout, ProviderClient.SendAsync", text);
    }

    private static PatternPolicy Policy(PankoOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new PankoOptions()));

    private static PatternCandidate Candidate(
        CaseSignature signature,
        string patternKey = "PAYMENTS-CHECKOUT-1234",
        DateTimeOffset? lastSeen = null) => new(
        Guid.NewGuid(), patternKey, signature, PatternLifecycleState.Ongoing, 2,
        DateTimeOffset.Parse("2026-07-01T10:00:00Z"),
        lastSeen ?? DateTimeOffset.Parse("2026-07-10T10:00:00Z"));

    private static CaseSignature Signature(SignatureFeatures features) =>
        new SignatureGenerator().Generate(features, SignatureStage.Final);

    private static SignatureFeatures Features(
        string service = "payments", IReadOnlyList<string>? scopes = null, string title = "checkout failure",
        IReadOnlyList<string>? errors = null, IReadOnlyList<string>? code = null) => new(
            service, "recipe", scopes ?? ["production"], title,
            title.Split(' ', StringSplitOptions.RemoveEmptyEntries), ["error"], errors ?? ["provider timeout"],
            ["checkout"], code ?? ["payments:providerclient.sendasync"]);

    private static CaseRecord BuildCase() => new(
        Guid.NewGuid(), "PD-1", "payments", "recipe", "Checkout failed 17 times", "high", PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"), DateTimeOffset.Parse("2026-07-11T10:01:00Z"), 1,
        "queued", false, null, "#cases", null,
        new Dictionary<string, string> { ["environment"] = "production", ["component"] = "checkout" });

    private static Crumb Crumb(string id, string category, string summary, string commit, string path) => new(
        id, "logs", DateTimeOffset.Parse("2026-07-11T10:00:00Z"), null, category, "warning", summary,
        null, null, 0.9, new JsonObject(), CodeReferences:
        [new CodeReference(id + "-code", "platform/payments", commit, path, 10, 20, "https://gitlab/file", "secret diff")]);
}
