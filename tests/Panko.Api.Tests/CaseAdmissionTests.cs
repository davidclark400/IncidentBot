using Panko.Api.Crumbs;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Recipes;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;
using SubmittedCrumb = Panko.Contracts.SubmittedCrumb;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Panko.Api.Tests;

public sealed class CaseAdmissionTests
{
    private const string RecipeCatalog = """
        version: 3
        revision: test-v3
        fallbackSlackChannel: "#cases"
        recipes:
          - id: payments-blue
            pagerDutyServiceId: P123PAYMENTS
            team: payments
            slackChannel: "#payments-cases"
            selectors:
              - labels:
                  tenant: blue
        """;

    [Fact]
    public async Task AcceptsSourceNeutralEventWithExactRecipeApprovedLabelsAndReceipt()
    {
        using var fixture = new RecipeFixture(RecipeCatalog);
        var cases = new RecordingCaseStore(isDuplicate: true);
        ICaseAdmission admission = new CaseAdmission(fixture.Store, cases);
        var rawPayload = new byte[] { 0, 1, 2, 3, 255 };

        var originEvent = OriginEvent(new Dictionary<string, string>
        {
            ["service"] = "P123PAYMENTS",
            ["environment"] = "production",
            ["tenant"] = "blue",
            ["diagnostic_noise"] = "not needed after routing",
            ["auth_token"] = "must-not-persist"
        });
        var result = await admission.AcceptAsync(
            originEvent,
            Receipt(rawPayload),
            CancellationToken.None);

        Assert.Equal(cases.CaseId, result.CaseId);
        Assert.True(result.IsDuplicate);
        Assert.Equal("payments-blue", cases.AcceptedRecipe!.Id);
        Assert.Equal("production", cases.AcceptedOriginEvent!.Labels["environment"]);
        Assert.Equal("blue", cases.AcceptedOriginEvent.Labels["tenant"]);
        Assert.DoesNotContain("diagnostic_noise", cases.AcceptedOriginEvent.Labels.Keys);
        Assert.DoesNotContain("auth_token", cases.AcceptedOriginEvent.Labels.Keys);
        Assert.Equal("pagerduty-adapter", cases.AcceptedReceipt!.ProducerPrincipal);
        Assert.Equal(rawPayload, cases.AcceptedReceipt.RawPayload.ToArray());
    }

    [Fact]
    public async Task ExactRecipeSelectionFailureIsExplicitAndDoesNotReachPersistence()
    {
        using var fixture = new RecipeFixture(RecipeCatalog);
        var cases = new RecordingCaseStore();
        ICaseAdmission admission = new CaseAdmission(fixture.Store, cases);

        var exception = await Assert.ThrowsAsync<RecipeSelectionException>(() =>
            admission.AcceptAsync(
                OriginEvent(
                    new Dictionary<string, string> { ["service"] = "P123PAYMENTS" },
                    recipeId: "missing-recipe"),
                Receipt(new byte[] { 1 }),
                CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("was not found", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, cases.AcceptCalls);
    }

    [Fact]
    public async Task PersistenceInvalidOperationIsNotMisclassifiedAsRecipeSelectionFailure()
    {
        using var fixture = new RecipeFixture(RecipeCatalog);
        var repositoryFailure = new InvalidOperationException("Case upsert failed");
        var cases = new RecordingCaseStore(repositoryFailure);
        ICaseAdmission admission = new CaseAdmission(fixture.Store, cases);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admission.AcceptAsync(
                OriginEvent(new Dictionary<string, string>
                {
                    ["service"] = "P123PAYMENTS",
                    ["tenant"] = "blue"
                }),
                Receipt(new byte[] { 1 }),
                CancellationToken.None));

        Assert.Same(repositoryFailure, exception);
        Assert.IsNotType<RecipeSelectionException>(exception);
        Assert.Equal(1, cases.AcceptCalls);
    }

    private static AcceptCaseOriginEvent OriginEvent(
        IReadOnlyDictionary<string, string> labels,
        string recipeId = "payments-blue")
    {
        var referenceTime = DateTimeOffset.Parse("2026-07-14T08:00:00Z");
        var occurredAt = DateTimeOffset.Parse("2026-07-14T08:05:00Z");
        return new AcceptCaseOriginEvent(
            new CaseOrigin(CaseOriginKind.PagerDuty, "PINCIDENT"),
            recipeId,
            "P123PAYMENTS",
            "Checkout latency",
            "high",
            PagerDutyIncidentState.Triggered,
            referenceTime,
            occurredAt,
            labels,
            new SubmittedCrumb(
                "event-1",
                SubmittedCrumbKind.Event,
                occurredAt,
                "pagerduty-incident-triggered",
                "critical",
                "PagerDuty incident triggered",
                DeclaredSource: "pagerduty"));
    }

    private static CaseOriginEventReceipt Receipt(ReadOnlyMemory<byte> rawPayload) => new(
        "pagerduty-adapter",
        "event-1",
        "incident.triggered",
        rawPayload);

    private sealed class RecipeFixture : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            $"panko-admission-recipe-{Guid.NewGuid():N}.yaml");

        public RecipeFixture(string document)
        {
            File.WriteAllText(path, document);
            Store = new RecipeStore(
                Microsoft.Extensions.Options.Options.Create(new PankoOptions { RecipesPath = path }),
                new TestEnvironment(),
                new CrumbSourceRegistry(
                    Array.Empty<ICrumbSourceAdapter>(),
                    TestConfiguration.CrumbSources()));
        }

        public RecipeStore Store { get; }

        public void Dispose() => File.Delete(path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Panko.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingCaseStore(
        Exception? acceptFailure = null,
        bool isDuplicate = false) : ICaseStore
    {
        public Guid CaseId { get; } = Guid.NewGuid();
        public int AcceptCalls { get; private set; }
        public AcceptCaseOriginEvent? AcceptedOriginEvent { get; private set; }
        public Recipe? AcceptedRecipe { get; private set; }
        public CaseOriginEventReceipt? AcceptedReceipt { get; private set; }

        public Task<(Guid CaseId, bool IsDuplicate)> AcceptOriginEventAsync(
            AcceptCaseOriginEvent originEvent,
            Recipe recipe,
            CaseOriginEventReceipt receipt,
            CancellationToken cancellationToken)
        {
            AcceptCalls++;
            AcceptedOriginEvent = originEvent;
            AcceptedRecipe = recipe;
            AcceptedReceipt = receipt;
            return acceptFailure is null
                ? Task.FromResult((CaseId, isDuplicate))
                : Task.FromException<(Guid CaseId, bool IsDuplicate)>(acceptFailure);
        }

        public Task<CaseRecord?> GetCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CaseFile?> GetCaseFileAsync(Guid caseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CaseProgress?> GetProgressAsync(
            Guid caseId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long?> BeginProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long?> UpdateProgressAsync(
            CaseProgress progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> SaveCaseFileAsync(
            CaseRecord caseRecord,
            CaseFile caseFile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetStatusAsync(Guid caseId, string status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RebuildCaseAsync(
            Guid caseId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetSlackTimestampAsync(
            Guid caseId,
            string timestamp,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
