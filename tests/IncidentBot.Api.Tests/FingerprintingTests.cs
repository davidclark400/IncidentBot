using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using Microsoft.Extensions.Options;

namespace IncidentBot.Api.Tests;

public sealed class FingerprintingTests
{
    private readonly FingerprintNormalizer _normalizer = new();

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
        Assert.True(normalized.Length <= FingerprintNormalizer.MaximumFeatureLength);
    }

    [Fact]
    public void HashesAreIndependentOfEvidenceOrderAndSuspectedCommit()
    {
        var extractor = new FingerprintExtractor(_normalizer);
        var generator = new FingerprintGenerator();
        var first = Finding("a", "exception", "Provider timeout", "aaaaaaa", "src/ProviderClient.cs");
        var second = Finding("b", "workload-failure", "checkout failed", "bbbbbbb", "src/Checkout.cs");
        var forward = generator.Generate(extractor.Extract(Incident(), [first, second]), FingerprintStage.Final);
        var reverse = generator.Generate(extractor.Extract(Incident(), [second, first]), FingerprintStage.Final);

        Assert.Equal(forward.FamilyHash, reverse.FamilyHash);
        Assert.Equal(forward.ExactHash, reverse.ExactHash);
        Assert.DoesNotContain("aaaaaaa", JsonSerializer.Serialize(forward.Features));
        Assert.DoesNotContain("bbbbbbb", JsonSerializer.Serialize(forward.Features));
    }

    [Fact]
    public void ChangingOnlyTheSuspectedCommitDoesNotChangeEitherHash()
    {
        var extractor = new FingerprintExtractor(_normalizer);
        var generator = new FingerprintGenerator();
        var first = generator.Generate(
            extractor.Extract(Incident(), [Finding("error", "exception", "Provider timeout", "aaaaaaa", "src/ProviderClient.cs")]),
            FingerprintStage.Final);
        var second = generator.Generate(
            extractor.Extract(Incident(), [Finding("error", "exception", "Provider timeout", "bbbbbbb", "src/ProviderClient.cs")]),
            FingerprintStage.Final);

        Assert.Equal(first.FamilyHash, second.FamilyHash);
        Assert.Equal(first.ExactHash, second.ExactHash);
    }

    [Fact]
    public void DifferentServicesAndKnownEnvironmentsDoNotMatch()
    {
        var matcher = Matcher();
        var fingerprint = Fingerprint(Features(service: "payments", scopes: ["production"]));
        var otherService = Candidate(Fingerprint(Features(service: "catalog", scopes: ["production"])));
        var otherEnvironment = Candidate(Fingerprint(Features(service: "payments", scopes: ["staging"])));

        Assert.Empty(matcher.Rank(fingerprint, [otherService, otherEnvironment]));
    }

    [Fact]
    public void ConflictingEnvironmentsRemainABoundaryEvenWhenRegionsMatch()
    {
        var current = Fingerprint(Features(scopes: ["environment:production", "region:eu-west"]));
        var historical = Fingerprint(Features(scopes: ["environment:staging", "region:eu-west"]));

        Assert.Empty(Matcher().Rank(current, [Candidate(historical)]));
    }

    [Fact]
    public void SuspectedChangeCodeLocationsDoNotEnterTheFingerprint()
    {
        var change = Finding("deploy", "deployment", "deployed suspected change", "abcdef123456", "src/SuspectedChange.cs");

        var features = new FingerprintExtractor(_normalizer).Extract(Incident(), [change]);

        Assert.Empty(features.CodeLocations);
        Assert.DoesNotContain("suspectedchange", JsonSerializer.Serialize(features), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedPipelineOutputDoesNotBecomeARecurringSymptomTemplate()
    {
        var pipeline = Finding(
            "pipeline-job", "pipeline-job-output", "Job integration-tests in pipeline 919 failed",
            "abcdef123456", "src/IntegrationTests.cs");

        var features = new FingerprintExtractor(_normalizer).Extract(Incident(), [pipeline]);

        Assert.DoesNotContain("pipeline", features.SymptomCategories);
        Assert.Empty(features.ErrorTemplates);
        Assert.Empty(features.CodeLocations);
    }

    [Fact]
    public void ActorIdentityIsRemovedAndStableMemberIdentityIsRetained()
    {
        var finding = new EvidenceFinding(
            "error", "logs", DateTimeOffset.UtcNow, null, "exception", "warning",
            "Alex Chen observed timeout in ProviderClient.SendAsync", null, null, 1, new JsonObject(), Actor: "Alex Chen");

        var features = new FingerprintExtractor(_normalizer).Extract(Incident(), [finding]);
        var serialized = JsonSerializer.Serialize(features);

        Assert.DoesNotContain("alex chen", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("member:providerclient.sendasync", features.CodeLocations);
    }

    [Fact]
    public void ErrorAndCodeMatchesProduceAnAutomaticExplainableScore()
    {
        var matcher = Matcher();
        var current = Fingerprint(Features(title: "provider timeout checkout", errors: ["provider timeout"], code: ["payments:providerclient.sendasync"]));
        var historical = Fingerprint(Features(title: "checkout provider timed out", errors: ["provider timeout"], code: ["payments:providerclient.sendasync"]));

        var match = matcher.Automatic(current, [Candidate(historical)]);

        Assert.NotNull(match);
        Assert.True(match.Score >= 80);
        Assert.Contains(match.MatchedFeatures, value => value.StartsWith("error:", StringComparison.Ordinal));
        Assert.Contains(match.MatchedFeatures, value => value.StartsWith("code location:", StringComparison.Ordinal));
    }

    [Fact]
    public void FamilyEqualityCannotOverrideConflictingExactSymptoms()
    {
        var matcher = Matcher();
        var current = Fingerprint(Features(errors: ["database refused connection"], code: ["payments:dbclient.open"]));
        var historical = Fingerprint(Features(errors: ["provider request timeout"], code: ["payments:providerclient.send"]));
        historical = historical with { FamilyHash = current.FamilyHash };

        var score = Assert.Single(matcher.Rank(current, [Candidate(historical)]));

        Assert.True(score.Score < 60);
        Assert.Null(matcher.Automatic(current, [Candidate(historical)]));
    }

    [Fact]
    public void AlgorithmVersionsNeverCompareAsExactMatches()
    {
        var current = Fingerprint(Features());
        var historical = current with { AlgorithmVersion = "v2" };

        Assert.Empty(Matcher().Rank(current, [Candidate(historical)]));
    }

    [Fact]
    public void ProvisionalPreviewCanExposeAnExactCandidateWithoutTreatingItAsAPossibleFinalMatch()
    {
        var fingerprint = Fingerprint(Features());
        var candidate = Candidate(fingerprint);
        var matcher = Matcher();

        Assert.Single(matcher.Preview(fingerprint, [candidate]));
        Assert.Empty(matcher.Possible(fingerprint, [candidate]));
    }

    [Fact]
    public void SecretsAreNotStoredAsFeaturesOrRenderedInSlack()
    {
        var incident = Incident() with { Title = "checkout password=hunter2 token=secret-token-value failed" };
        var features = new FingerprintExtractor(_normalizer).Extract(incident,
            [new EvidenceFinding("e", "logs", DateTimeOffset.UtcNow, null, "error", "warning",
                "authorization: Bearer abcdefghijklmnop timeout", null, null, 1, new JsonObject())]);
        var serialized = JsonSerializer.Serialize(features);
        Assert.DoesNotContain("hunter2", serialized);
        Assert.DoesNotContain("secret-token-value", serialized);
        Assert.DoesNotContain("abcdefghijklmnop", serialized);

        var problem = new ProblemContext("available", "v1", FingerprintStage.Final, "PAYMENTS-CHECKOUT-1234", Guid.NewGuid(),
            ProblemLifecycleState.Ongoing, "similarity", 90, features.ErrorTemplates, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], [], 1);
        var slack = SlackPublisher.BuildProblemText(problem)!;
        Assert.DoesNotContain("hunter2", slack);
        Assert.DoesNotContain("abcdefghijklmnop", slack);
    }

    [Fact]
    public void SlackRendersConciseRegressionContextAndExplanation()
    {
        var problem = new ProblemContext(
            "available", "v1", FingerprintStage.Final, "PAYMENTS-CHECKOUT-4F19", Guid.NewGuid(),
            ProblemLifecycleState.Regressed, "similarity", 90,
            ["error: provider timeout", "component: checkout", "code location: ProviderClient.SendAsync"],
            4, DateTimeOffset.Parse("2026-06-01T00:00:00Z"), DateTimeOffset.Parse("2026-06-14T00:00:00Z"), [], [], 0.9);

        var text = SlackPublisher.BuildProblemText(problem)!;

        Assert.Contains("Regressed problem PAYMENTS-CHECKOUT-4F19", text);
        Assert.Contains("90% similarity", text);
        Assert.Contains("4 occurrences", text);
        Assert.Contains("Matched on provider timeout, checkout, ProviderClient.SendAsync", text);
    }

    [Fact]
    public void OldReportsWithoutProblemContextStillDeserialize()
    {
        var report = JsonSerializer.Deserialize<InvestigationReport>("""
            {"id":"11111111-1111-1111-1111-111111111111","pagerDutyIncidentId":"PD1","serviceId":"payments",
             "profileId":"profile","profileRevision":"r1","title":"failure","urgency":"high","state":0,"status":"ready",
             "triggeredAt":"2026-07-11T10:00:00Z","updatedAt":"2026-07-11T10:01:00Z","version":1,
             "deterministicSummary":"summary","ai":{"status":"complete","possibleContributors":[],"unknowns":[],"recommendedChecks":[]},
             "timeline":[],"evidence":[],"sources":[],"links":[]}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(report);
        Assert.Null(report.Problem);
    }

    private static FingerprintMatcher Matcher() => new(Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions()));

    private static ProblemCandidate Candidate(IncidentFingerprint fingerprint) => new(
        Guid.NewGuid(), "PAYMENTS-CHECKOUT-1234", fingerprint, ProblemLifecycleState.Ongoing, 2,
        DateTimeOffset.Parse("2026-07-01T10:00:00Z"), DateTimeOffset.Parse("2026-07-10T10:00:00Z"));

    private static IncidentFingerprint Fingerprint(FingerprintFeatures features) =>
        new FingerprintGenerator().Generate(features, FingerprintStage.Final);

    private static FingerprintFeatures Features(
        string service = "payments", IReadOnlyList<string>? scopes = null, string title = "checkout failure",
        IReadOnlyList<string>? errors = null, IReadOnlyList<string>? code = null) => new(
            service, "profile", scopes ?? ["production"], title,
            title.Split(' ', StringSplitOptions.RemoveEmptyEntries), ["error"], errors ?? ["provider timeout"],
            ["checkout"], code ?? ["payments:providerclient.sendasync"]);

    private static IncidentRecord Incident() => new(
        Guid.NewGuid(), "PD-1", "payments", "profile", "Checkout failed 17 times", "high", IncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"), DateTimeOffset.Parse("2026-07-11T10:01:00Z"), 1,
        "queued", false, null, "#incidents", null,
        new Dictionary<string, string> { ["environment"] = "production", ["component"] = "checkout" });

    private static EvidenceFinding Finding(string id, string category, string summary, string commit, string path) => new(
        id, "logs", DateTimeOffset.Parse("2026-07-11T10:00:00Z"), null, category, "warning", summary,
        null, null, 0.9, new JsonObject(), CodeReferences:
        [new CodeReference(id + "-code", "platform/payments", commit, path, 10, 20, "https://gitlab/file", "secret diff")]);
}
