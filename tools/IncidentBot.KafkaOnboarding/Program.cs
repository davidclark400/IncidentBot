using IncidentBot.Kafka;
using IncidentBot.Kafka.Onboarding;

return await KafkaOnboardingCommand.RunAsync(args, Console.Out, Console.Error);

public static class KafkaOnboardingCommand
{
    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error)
    {
        try
        {
            if (args.Count == 0 || args[0] is "-h" or "--help")
            {
                output.Write(Usage);
                return Task.FromResult(0);
            }

            var command = args[0];
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return Task.FromResult(command switch
            {
                "scan" => Scan(options, output),
                "generate-dashboard" => GenerateDashboard(options, output, error),
                "validate" => Validate(options, output, error),
                _ => throw new CommandException($"Unknown command '{command}'.\n{Usage}")
            });
        }
        catch (CommandException exception)
        {
            error.WriteLine(exception.Message);
            return Task.FromResult(2);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or System.Text.Json.JsonException)
        {
            error.WriteLine(exception.Message);
            return Task.FromResult(1);
        }
    }

    private static int Scan(CliOptions options, TextWriter output)
    {
        var appRoot = options.Required("app-root");
        var environment = options.Required("environment");
        var outputPath = options.Required("output");
        var inventory = new KafkaApplicationScanner().Scan(appRoot, environment);
        var json = KafkaInventoryJson.Serialize(inventory);
        return WriteOrCheck(outputPath, json, options.Check, "Kafka inventory", output);
    }

    private static int GenerateDashboard(CliOptions options, TextWriter output, TextWriter error)
    {
        var profilesPath = options.Required("profiles");
        var metricPacksPath = options.Required("metric-packs");
        var profileId = options.Required("profile-id");
        var outputPath = options.Required("output");
        var scope = KafkaProfileScopeLoader.Load(profilesPath, profileId);
        var catalog = KafkaMetricCatalog.Load(metricPacksPath);
        var generator = new KafkaDashboardGenerator();
        var json = generator.Generate(profileId, scope, catalog);
        if (options.Check && !generator.Check(outputPath, profileId, scope, catalog, out var diagnostic))
        {
            error.WriteLine(diagnostic);
            return 1;
        }
        if (options.Check)
        {
            output.WriteLine("Kafka dashboard is current.");
            return 0;
        }
        return WriteOrCheck(outputPath, json, check: false, "Kafka dashboard", output);
    }

    private static int Validate(CliOptions options, TextWriter output, TextWriter error)
    {
        var inventoryPath = options.Required("inventory");
        var profilesPath = options.Required("profiles");
        var metricPacksPath = options.Required("metric-packs");
        var dashboardPath = options.Required("dashboard");
        var profileId = options.Required("profile-id");
        var inventory = KafkaInventoryJson.Deserialize(File.ReadAllText(inventoryPath));
        var scope = KafkaProfileScopeLoader.Load(profilesPath, profileId);
        var catalog = KafkaMetricCatalog.Load(metricPacksPath);
        var mappingsPath = options.Optional("mappings");
        var mappings = mappingsPath is null ? null : KafkaResourceMappingLoader.Load(mappingsPath);
        var result = new KafkaOnboardingValidator(new KafkaDashboardGenerator()).Validate(
            inventory,
            profileId,
            scope,
            catalog,
            File.ReadAllText(dashboardPath),
            mappings);
        if (!result.IsValid)
        {
            foreach (var failure in result.Errors) error.WriteLine(failure);
            return 1;
        }
        output.WriteLine("Kafka onboarding coverage is valid.");
        return 0;
    }

    private static int WriteOrCheck(
        string path,
        string expected,
        bool check,
        string artifact,
        TextWriter output)
    {
        if (check)
        {
            if (!File.Exists(path)
                || !string.Equals(
                    File.ReadAllText(path).ReplaceLineEndings("\n"),
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{artifact} is missing or stale: {path}");
            }
            output.WriteLine($"{artifact} is current.");
            return 0;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, expected);
        output.WriteLine($"Wrote {artifact.ToLowerInvariant()} to {path}");
        return 0;
    }

    private const string Usage = """
        IncidentBot Kafka onboarding (offline; makes no live calls)

        scan --app-root <path> --environment <name> --output <inventory.json> [--check]
        generate-dashboard --profiles <yaml> --profile-id <id> --metric-packs <yaml> --output <dashboard.json> [--check]
        validate --inventory <json> --profiles <yaml> --profile-id <id> --metric-packs <yaml> --dashboard <json> [--mappings <yaml>]
        """;

    private sealed class CliOptions
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public bool Check { get; private set; }

        public static CliOptions Parse(IReadOnlyList<string> args)
        {
            var result = new CliOptions();
            for (var index = 0; index < args.Count; index++)
            {
                var argument = args[index];
                if (argument == "--check")
                {
                    result.Check = true;
                    continue;
                }
                if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                {
                    throw new CommandException($"Invalid command option '{argument}'.");
                }
                var name = argument[2..];
                var value = args[++index];
                if (value.StartsWith("--", StringComparison.Ordinal) || !result.values.TryAdd(name, value))
                {
                    throw new CommandException($"Option '--{name}' is missing a value or is duplicated.");
                }
            }
            return result;
        }

        public string Required(string name) => values.TryGetValue(name, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new CommandException($"Required option '--{name}' was not supplied.");

        public string? Optional(string name) => values.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class CommandException(string message) : Exception(message);
}
