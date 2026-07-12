using IncidentBot.Api.Contracts;
using IncidentBot.Api.Demo;

namespace IncidentBot.Api.Tests;

public sealed class ContractMappingTests
{
    [Fact]
    public void PublicReportContractPreservesTheCompleteWireShape()
    {
        var store = new DemoIncidentStore(TimeProvider.System);
        var reset = store.Reset();
        var domainReport = Assert.IsType<Api.Domain.InvestigationReport>(store.Advance(reset.Generation, 6));

        var contract = domainReport.ToContract();

        Assert.Equal(domainReport.Id, contract.Id);
        Assert.Equal(IncidentBot.Contracts.IncidentState.Triggered, contract.State);
        Assert.Equal(domainReport.Timeline.Count, contract.Timeline.Count);
        Assert.Equal(domainReport.Evidence.Count, contract.Evidence.Count);
        Assert.Equal(domainReport.Sources.Count, contract.Sources.Count);
        Assert.Equal(domainReport.CausalEvents?.Count, contract.CausalEvents?.Count);
        Assert.NotEmpty(contract.Ai.Diagnoses!);
        Assert.NotEmpty(contract.Evidence.Single(item => item.Id == "demo-mr-merged").CodeReferences!);
        Assert.Equal("demo", contract.Evidence[0].Provenance["mode"]?.GetValue<string>());
        Assert.NotNull(contract.Problem);
    }
}
