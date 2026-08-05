using Panko.Api.Contracts;
using Panko.Api.Demo;

namespace Panko.Api.Tests;

public sealed class ContractMappingTests
{
    [Fact]
    public void PublicCaseFileContractPreservesTheCompleteWireShape()
    {
        var store = new DemoCaseStore(TimeProvider.System);
        var reset = store.Reset();
        var transition = store.Advance(reset.Generation, 6);
        Assert.NotNull(transition);
        var domainCaseFile = Assert.IsType<Api.Domain.CaseFile>(transition!.CaseFile);

        var contract = domainCaseFile.ToContract();

        Assert.Equal(domainCaseFile.CaseId, contract.CaseId);
        Assert.Equal(Panko.Contracts.PagerDutyIncidentState.Triggered, contract.PagerDutyState);
        Assert.Equal(domainCaseFile.Trail.Count, contract.Trail.Count);
        Assert.Equal(domainCaseFile.Crumbs.Count, contract.Crumbs.Count);
        Assert.Equal(domainCaseFile.CrumbSources.Count, contract.CrumbSources.Count);
        Assert.All(contract.CrumbSources, source => Assert.Equal(
            Panko.Contracts.CrumbSourceRequestState.Received,
            source.RequestState));
        Assert.Equal(domainCaseFile.CausalMarkers?.Count, contract.CausalMarkers?.Count);
        Assert.NotEmpty(contract.Ai.Diagnoses!);
        Assert.NotEmpty(contract.Crumbs.Single(item => item.Id == "demo-mr-merged").CodeReferences!);
        Assert.Equal("demo", contract.Crumbs[0].Provenance["mode"]?.GetValue<string>());
        Assert.NotNull(contract.Pattern);
    }

    [Fact]
    public void ProgressContractCarriesOnlyBoundedStatusMetadata()
    {
        var now = DateTimeOffset.Parse("2026-07-11T10:05:00Z");
        var domain = new Api.Domain.CaseProgress(
            Guid.NewGuid(),
            Guid.NewGuid(),
            7,
            3,
            now.AddSeconds(-2),
            now,
            1_800,
            Api.Domain.CaseProgressPhase.Synthesizing,
            2,
            120,
            true,
            true,
            Api.Domain.AiSynthesisProgressState.Running,
            Enum.GetValues<Api.Domain.CrumbSourceProgressState>()
                .Select((state, index) => new Api.Domain.CaseSourceProgress(
                    $"source-{index}",
                    state,
                    state is Api.Domain.CrumbSourceProgressState.Pending or Api.Domain.CrumbSourceProgressState.Querying
                        ? Api.Domain.CrumbSourceHealth.Pending
                        : state == Api.Domain.CrumbSourceProgressState.Received
                            ? Api.Domain.CrumbSourceHealth.Complete
                            : state == Api.Domain.CrumbSourceProgressState.Excluded
                                ? Api.Domain.CrumbSourceHealth.Excluded
                            : Api.Domain.CrumbSourceHealth.Unavailable,
                    2,
                    120,
                    400 + index,
                    index,
                    null,
                    now.AddSeconds(-1),
                    now))
                .ToArray(),
            [new Api.Domain.CaseEarlyCrumb(
                "crumb-1", "victorialogs", now, "warning", "Checkout timeout", .95)]);

        var contract = domain.ToContract();

        Assert.Equal(domain.AttemptId, contract.AttemptId);
        Assert.Equal(Panko.Contracts.CaseProgressPhase.Synthesizing, contract.Phase);
        Assert.Equal(Panko.Contracts.AiSynthesisProgressState.Running, contract.AiSynthesisState);
        Assert.Equal(Enum.GetValues<Panko.Contracts.CrumbSourceProgressState>(),
            contract.CrumbSources.Select(source => source.RequestState));
        Assert.Equal("crumb-1", Assert.Single(contract.EarlyCrumbs).Id);
    }
}
