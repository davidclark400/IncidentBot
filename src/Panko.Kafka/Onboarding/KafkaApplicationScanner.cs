namespace Panko.Kafka.Onboarding;

public sealed class KafkaApplicationScanner
{
    public KafkaApplicationInventory Scan(string appRoot, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var root = Path.GetFullPath(appRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Kafka application root was not found: {root}");
        }

        var files = KafkaScanFile.Enumerate(root);
        var configuration = KafkaConfigurationIndex.Create(files, environment.Trim());
        var inventory = new KafkaInventoryBuilder(environment.Trim());

        configuration.RecordResources(inventory);
        foreach (var file in files.Where(file => file.IsSource))
        {
            KafkaSourceDetector.Scan(file, configuration, inventory);
        }

        return inventory.Build();
    }
}

internal sealed record KafkaScanFile(
    string AbsolutePath,
    string RelativePath,
    string Text,
    bool IsSource,
    bool IsConfiguration)
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".gradle", ".idea", ".vs",
        "bin", "obj", "build", "builds", "bld", "target", "out", "dist",
        "node_modules", "packages", "vendor", "vendors", "coverage",
        "generated", "generated-code", "generated-sources", "generated_code"
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".java", ".kt", ".kts", ".cs"
    };

    private static readonly HashSet<string> ConfigurationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yaml", ".yml", ".properties", ".json", ".conf", ".config", ".env", ".tpl"
    };

    public static IReadOnlyList<KafkaScanFile> Enumerate(string root)
    {
        var paths = new List<string>();
        Visit(root, paths);
        paths.Sort(StringComparer.Ordinal);

        return paths.Select(path =>
        {
            var extension = Path.GetExtension(path);
            var source = SourceExtensions.Contains(extension);
            var configuration = IsConfigurationPath(root, path, extension);
            return new KafkaScanFile(
                path,
                NormalizeRelativePath(Path.GetRelativePath(root, path)),
                File.ReadAllText(path),
                source,
                configuration);
        }).ToArray();
    }

    public int LineAt(int characterIndex)
    {
        if (characterIndex <= 0)
        {
            return 1;
        }

        var line = 1;
        var limit = Math.Min(characterIndex, Text.Length);
        for (var index = 0; index < limit; index++)
        {
            if (Text[index] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    public KafkaInventoryEvidence Evidence(int characterIndex, string detector, string usage) =>
        EvidenceForLine(LineAt(characterIndex), detector, usage);

    public KafkaInventoryEvidence EvidenceForLine(int line, string detector, string usage)
    {
        var snippet = Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ElementAtOrDefault(Math.Max(0, line - 1))
            ?.Trim() ?? "";
        snippet = string.Concat(snippet.Where(character => !char.IsControl(character) || character == '\t'));
        if (snippet.Length > 240)
        {
            snippet = snippet[..237] + "...";
        }
        return new KafkaInventoryEvidence(RelativePath, Math.Max(1, line), detector, usage, snippet);
    }

    private static void Visit(string directory, ICollection<string> paths)
    {
        foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var info = new DirectoryInfo(child);
            if (IgnoredDirectories.Contains(info.Name)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            Visit(child, paths);
        }

        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(path);
            if (SourceExtensions.Contains(extension) || ConfigurationExtensions.Contains(extension))
            {
                paths.Add(path);
            }
        }
    }

    private static bool IsConfigurationPath(string root, string path, string extension)
    {
        if (!ConfigurationExtensions.Contains(extension))
        {
            return false;
        }

        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("config", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = NormalizeRelativePath(Path.GetRelativePath(root, path));
        return relative.Contains("/deploy/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("deploy/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/helm/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/k8s/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/kubernetes/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');
}

internal sealed class KafkaInventoryBuilder(string environment)
{
    private readonly Dictionary<(string Kind, string Name), HashSet<KafkaInventoryEvidence>> resources = [];
    private readonly Dictionary<UnresolvedKey, HashSet<KafkaInventoryEvidence>> unresolved = [];

    public void AddResource(string kind, string name, KafkaInventoryEvidence evidence)
    {
        var normalized = NormalizeValue(name);
        if (normalized.Length == 0 || !IsSupportedKind(kind))
        {
            return;
        }

        var key = (kind, normalized);
        if (!resources.TryGetValue(key, out var evidenceSet))
        {
            evidenceSet = [];
            resources.Add(key, evidenceSet);
        }
        evidenceSet.Add(evidence);
    }

    public void AddUnresolved(
        string kind,
        string expression,
        string reason,
        KafkaInventoryEvidence evidence,
        bool required = true)
    {
        if (!IsSupportedKind(kind))
        {
            return;
        }

        var key = new UnresolvedKey(
            kind,
            Bound(expression.Trim(), 300),
            Bound(reason.Trim(), 240),
            required);
        if (key.Expression.Length == 0)
        {
            return;
        }
        if (!unresolved.TryGetValue(key, out var evidenceSet))
        {
            evidenceSet = [];
            unresolved.Add(key, evidenceSet);
        }
        evidenceSet.Add(evidence);
    }

    public KafkaApplicationInventory Build()
    {
        var resolved = resources
            .OrderBy(pair => KindOrder(pair.Key.Kind))
            .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .Select(pair => new KafkaInventoryResource(
                pair.Key.Kind,
                pair.Key.Name,
                SortEvidence(pair.Value)))
            .ToArray();
        var dynamicReferences = unresolved
            .OrderBy(pair => KindOrder(pair.Key.Kind))
            .ThenBy(pair => pair.Key.Expression, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Reason, StringComparer.Ordinal)
            .ThenByDescending(pair => pair.Key.Required)
            .Select(pair => new KafkaUnresolvedReference(
                pair.Key.Kind,
                pair.Key.Expression,
                pair.Key.Reason,
                pair.Key.Required,
                SortEvidence(pair.Value)))
            .ToArray();
        return new KafkaApplicationInventory(1, environment, resolved, dynamicReferences);
    }

    private static KafkaInventoryEvidence[] SortEvidence(IEnumerable<KafkaInventoryEvidence> values) =>
        values.OrderBy(value => value.File, StringComparer.Ordinal)
            .ThenBy(value => value.Line)
            .ThenBy(value => value.Detector, StringComparer.Ordinal)
            .ThenBy(value => value.Usage, StringComparer.Ordinal)
            .ThenBy(value => value.Snippet, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length >= 2
            && ((normalized[0] == '"' && normalized[^1] == '"')
                || (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1].Trim();
        }
        return Bound(normalized, 512);
    }

    private static bool IsSupportedKind(string kind) => kind is
        KafkaInventoryResourceKinds.Cluster
        or KafkaInventoryResourceKinds.Topic
        or KafkaInventoryResourceKinds.ConsumerGroup;

    private static int KindOrder(string kind) => kind switch
    {
        KafkaInventoryResourceKinds.Cluster => 0,
        KafkaInventoryResourceKinds.Topic => 1,
        KafkaInventoryResourceKinds.ConsumerGroup => 2,
        _ => 3
    };

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 3)] + "...";

    private sealed record UnresolvedKey(string Kind, string Expression, string Reason, bool Required);
}
