namespace Panko.Observability.Tests;

public sealed class ServiceOnboardingCommandTests
{
    [Fact]
    public async Task ValidateRequiresTheEvidenceGate()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ServiceOnboardingCommand.RunAsync(
        [
            "validate",
            "--recipes", "recipes.yaml",
            "--recipe-id", "payments-production",
            "--metric-packs", "packs.yaml",
            "--dashboard", "dashboard.json"
        ], output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("--evidence", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssessReportsNullPackSchemaWithoutLeakingAStackTrace()
    {
        var directory = Directory.CreateTempSubdirectory("panko-service-command-");
        try
        {
            var evidencePath = Path.Combine(directory.FullName, "evidence.json");
            var packsPath = Path.Combine(directory.FullName, "packs.yaml");
            var assessmentPath = Path.Combine(directory.FullName, "assessment.json");
            File.WriteAllText(
                evidencePath,
                ServiceTelemetryEvidenceJson.Serialize(ServiceTelemetryEvidenceTemplate.Create(
                    "payments-production",
                    ServiceTelemetryWorkloadKind.RequestDriven,
                    "payments-api",
                    "production")));
            File.WriteAllText(packsPath, "version: 1\npacks: [null]\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await ServiceOnboardingCommand.RunAsync(
            [
                "assess",
                "--evidence", evidencePath,
                "--metric-packs", packsPath,
                "--output", assessmentPath
            ], output, error);

            Assert.Equal(1, exitCode);
            Assert.Empty(output.ToString());
            Assert.Contains("null pack", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(assessmentPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
