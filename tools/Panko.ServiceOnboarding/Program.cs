using System.Diagnostics;
using Panko.Observability;
using Panko.Observability.Onboarding;

return await ServiceOnboardingCommand.RunAsync(args, Console.Out, Console.Error);

public static class ServiceOnboardingCommand
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
            if (command is not (
                    "init-evidence" or "assess" or "generate-dashboard" or "validate" or "explain"))
            {
                throw new CommandException($"Unknown command '{command}'.\n{Usage}");
            }
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            options.ValidateFor(command);
            return Task.FromResult(command switch
            {
                "init-evidence" => InitEvidence(options, output),
                "assess" => Assess(options, output, error),
                "generate-dashboard" => GenerateDashboard(options, output, error),
                "validate" => Validate(options, output, error),
                "explain" => Explain(options, output),
                _ => throw new UnreachableException()
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

    private static int InitEvidence(CliOptions options, TextWriter output)
    {
        var evidence = ServiceTelemetryEvidenceTemplate.Create(
            options.Required("recipe-id"),
            options.Required("workload-kind"),
            options.Required("service"),
            options.Required("environment"));
        return WriteOrCheck(
            options.Required("output"),
            ServiceTelemetryEvidenceJson.Serialize(evidence),
            options.Check,
            "Service telemetry evidence template",
            output);
    }

    private static int Assess(CliOptions options, TextWriter output, TextWriter error)
    {
        var evidence = ServiceTelemetryEvidenceJson.Load(options.Required("evidence"));
        var catalog = ServiceMetricCatalog.Load(options.Required("metric-packs"));
        var assessment = new ServiceMetricPackAssessor().Assess(evidence, catalog);
        var result = WriteOrCheck(
            options.Required("output"),
            ServiceMetricPackAssessmentJson.Serialize(assessment),
            options.Check,
            "Service metric-pack assessment",
            output);
        output.WriteLine($"Assessment decision: {assessment.Decision}");
        if (assessment.Decision is ServiceMetricPackDecision.Blocked
            or ServiceMetricPackDecision.ContractDesignReview)
        {
            foreach (var blocker in assessment.Blockers) error.WriteLine(blocker);
            return 1;
        }
        return result;
    }

    private static int GenerateDashboard(
        CliOptions options,
        TextWriter output,
        TextWriter error)
    {
        var recipeId = options.Required("recipe-id");
        var outputPath = options.Required("output");
        var plan = ResolvePlan(options, recipeId);
        var generator = new ServiceDashboardGenerator();
        if (options.Check)
        {
            if (!generator.Check(outputPath, recipeId, plan, out var diagnostic))
            {
                error.WriteLine(diagnostic);
                return 1;
            }

            output.WriteLine("Service dashboard is current.");
            return 0;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, generator.Generate(recipeId, plan));
        output.WriteLine($"Wrote service dashboard to {outputPath}");
        return 0;
    }

    private static int Validate(
        CliOptions options,
        TextWriter output,
        TextWriter error)
    {
        var recipeId = options.Required("recipe-id");
        var dashboardPath = options.Required("dashboard");
        var recipesPath = options.Required("recipes");
        var packsPath = options.Required("metric-packs");
        var evidencePath = options.Required("evidence");
        var scope = ServiceRecipeScopeLoader.Load(recipesPath, recipeId);
        var catalog = ServiceMetricCatalog.Load(packsPath);
        var result = new ServiceOnboardingValidator(new ServiceDashboardGenerator()).Validate(
            recipeId,
            scope,
            catalog,
            File.ReadAllText(dashboardPath),
            ServiceTelemetryEvidenceJson.Load(evidencePath));
        if (!result.IsValid)
        {
            foreach (var failure in result.Errors) error.WriteLine(failure);
            return 1;
        }

        output.WriteLine("Service observability artifacts conform to the selected Recipe scope and metric pack.");
        return 0;
    }

    private static int Explain(CliOptions options, TextWriter output)
    {
        var recipeId = options.Required("recipe-id");
        output.Write(ServiceMetricPlanExplainFormatter.Format(
            recipeId,
            ResolvePlan(options, recipeId)));
        return 0;
    }

    private static ServiceMetricPlan ResolvePlan(CliOptions options, string recipeId)
    {
        var scope = ServiceRecipeScopeLoader.Load(
            options.Required("recipes"),
            recipeId);
        return ServiceMetricCatalog.Load(options.Required("metric-packs")).CompilePlan(scope);
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
        Panko observable-service onboarding (offline; makes no live calls)

        init-evidence --recipe-id <id> --workload-kind <request-driven|worker> --service <value> --environment <value> --output <evidence.json> [--check]
        assess --evidence <json> --metric-packs <yaml> --output <assessment.json> [--check]
        generate-dashboard --recipes <yaml> --recipe-id <id> --metric-packs <yaml> --output <dashboard.json> [--check]
        validate --recipes <yaml> --recipe-id <id> --metric-packs <yaml> --dashboard <dashboard.json> --evidence <json>
        explain --recipes <yaml> --recipe-id <id> --metric-packs <yaml>
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
                    if (result.Check)
                    {
                        throw new CommandException("Option '--check' is duplicated.");
                    }
                    result.Check = true;
                    continue;
                }
                if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                {
                    throw new CommandException($"Invalid command option '{argument}'.");
                }

                var name = argument[2..];
                var value = args[++index];
                if (value.StartsWith("--", StringComparison.Ordinal)
                    || !result.values.TryAdd(name, value))
                {
                    throw new CommandException(
                        $"Option '--{name}' is missing a value or is duplicated.");
                }
            }

            return result;
        }

        public void ValidateFor(string command)
        {
            var allowed = command switch
            {
                "init-evidence" => new HashSet<string>(
                    ["recipe-id", "workload-kind", "service", "environment", "output"],
                    StringComparer.Ordinal),
                "assess" => new HashSet<string>(
                    ["evidence", "metric-packs", "output"],
                    StringComparer.Ordinal),
                "generate-dashboard" => new HashSet<string>(
                    ["recipes", "recipe-id", "metric-packs", "output"],
                    StringComparer.Ordinal),
                "validate" => new HashSet<string>(
                    ["recipes", "recipe-id", "metric-packs", "dashboard", "evidence"],
                    StringComparer.Ordinal),
                "explain" => new HashSet<string>(
                    ["recipes", "recipe-id", "metric-packs"],
                    StringComparer.Ordinal),
                _ => throw new UnreachableException()
            };
            var unknown = values.Keys
                .Where(name => !allowed.Contains(name))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (unknown is not null)
            {
                throw new CommandException(
                    $"Option '--{unknown}' is not supported by '{command}'.");
            }
            if (Check && command is not ("init-evidence" or "assess" or "generate-dashboard"))
            {
                throw new CommandException(
                    $"Option '--check' is not supported by '{command}'.");
            }
        }

        public string Required(string name)
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            throw new CommandException($"Required option '--{name}' was not supplied.");
        }

    }

    private sealed class CommandException(string message) : Exception(message);
}
