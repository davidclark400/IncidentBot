namespace Panko.Kafka.Tests;

public sealed class RepositorySkillTests
{
    [Fact]
    public void SkillHasOnlyEssentialValidatedMetadataAndWorkflowResources()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "skill");
        var skill = File.ReadAllText(Path.Combine(root, "SKILL.md"));
        var metadata = File.ReadAllText(Path.Combine(root, "agents", "openai.yaml"));
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["SKILL.md", "agents/openai.yaml", "references/onboarding-contract.md"],
            files);
        Assert.StartsWith("---\nname: onboard-kafka-app\ndescription:", skill.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Stop before patching", skill, StringComparison.Ordinal);
        Assert.Contains("Never connect to Kafka brokers", skill, StringComparison.Ordinal);
        Assert.Contains("display_name: \"Onboard Kafka Application\"", metadata, StringComparison.Ordinal);
        Assert.Contains("short_description: \"Discover Kafka usage and configure Panko\"", metadata, StringComparison.Ordinal);
        Assert.Contains("Use $onboard-kafka-app", metadata, StringComparison.Ordinal);
    }
}
