using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncidentBot.Kafka.Onboarding;

internal sealed class KafkaConfigurationIndex
{
    private static readonly Regex SpringPlaceholder = new(
        @"\$\{(?<value>[^{}]+)\}",
        RegexOptions.CultureInvariant);
    private static readonly Regex HelmPlaceholder = new(
        @"\{\{\s*\.Values\.(?<key>[A-Za-z0-9_.-]+)(?:\s*\|[^}]*)?\s*\}\}",
        RegexOptions.CultureInvariant);
    private static readonly Regex KubernetesPlaceholder = new(
        @"\$\((?<key>[A-Za-z0-9_.:-]+)\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentPlaceholder = new(
        @"^(?:\$(?<dollar>[A-Za-z_][A-Za-z0-9_]*)|%(?<percent>[A-Za-z_][A-Za-z0-9_]*)%)$",
        RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<KafkaConfigurationEntry> entries;

    private KafkaConfigurationIndex(IReadOnlyList<KafkaConfigurationEntry> entries)
    {
        this.entries = entries;
    }

    public static KafkaConfigurationIndex Create(
        IReadOnlyList<KafkaScanFile> files,
        string environment)
    {
        var entries = new List<KafkaConfigurationEntry>();
        foreach (var file in files.Where(file => file.IsConfiguration)
                     .Where(file => AppliesToEnvironment(file.RelativePath, environment)))
        {
            var detector = ConfigurationDetector(file);
            var priority = ConfigurationPriority(file.RelativePath, environment);
            var extension = Path.GetExtension(file.AbsolutePath);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                ParseJson(file, detector, priority, entries);
            }
            else if (extension.Equals(".properties", StringComparison.OrdinalIgnoreCase)
                     || extension.Equals(".conf", StringComparison.OrdinalIgnoreCase)
                     || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
                     || extension.Equals(".env", StringComparison.OrdinalIgnoreCase))
            {
                ParseProperties(file, detector, priority, entries);
            }
            else
            {
                ParseYaml(file, detector, priority, environment, entries);
            }
        }

        return new KafkaConfigurationIndex(entries
            .OrderBy(entry => CanonicalKey(entry.Key), StringComparer.Ordinal)
            .ThenByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.Evidence.File, StringComparer.Ordinal)
            .ThenBy(entry => entry.Evidence.Line)
            .ThenBy(entry => entry.Value, StringComparer.Ordinal)
            .ToArray());
    }

    public KafkaConfigurationResolution Resolve(string key) =>
        Resolve(key, new HashSet<string>(StringComparer.Ordinal));

    public KafkaConfigurationResolution ResolveTemplate(string value) =>
        ResolveTemplate(value, new HashSet<string>(StringComparer.Ordinal));

    public void RecordResources(KafkaInventoryBuilder inventory)
    {
        foreach (var entry in ActiveEntries())
        {
            var kind = ResourceKind(entry.Key);
            if (kind is null)
            {
                continue;
            }

            var usage = ConfigurationUsage(entry.Key, kind);
            var evidence = entry.Evidence with { Usage = usage };
            var resolution = ResolveTemplate(entry.Value);
            if (!resolution.Success)
            {
                inventory.AddUnresolved(kind, entry.Value, resolution.Error!, evidence);
                continue;
            }

            foreach (var resolved in ExpandConfiguredValues(kind, entry.Key, resolution.Values))
            {
                if (LooksLikeResourceValue(resolved))
                {
                    inventory.AddResource(kind, resolved, evidence);
                }
            }
        }
    }

    private KafkaConfigurationResolution Resolve(string key, HashSet<string> stack)
    {
        var canonical = CanonicalKey(key);
        if (canonical.Length == 0)
        {
            return KafkaConfigurationResolution.Failed("The configuration key is empty.");
        }
        if (!stack.Add(canonical))
        {
            return KafkaConfigurationResolution.Failed(
                $"Configuration reference '{key}' is cyclic.");
        }

        try
        {
            var candidates = entries.Where(entry => CanonicalKey(entry.Key) == canonical).ToArray();
            if (candidates.Length == 0)
            {
                candidates = entries.Where(entry =>
                        CanonicalKey(entry.Key).EndsWith('.' + canonical, StringComparison.Ordinal))
                    .ToArray();
            }
            if (candidates.Length == 0)
            {
                return KafkaConfigurationResolution.Failed(
                    $"Configuration reference '{key}' has no deterministic value.");
            }

            var maximumPriority = candidates.Max(candidate => candidate.Priority);
            candidates = candidates.Where(candidate => candidate.Priority == maximumPriority).ToArray();
            var values = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var resolved = ResolveTemplate(candidate.Value, stack);
                if (!resolved.Success)
                {
                    return KafkaConfigurationResolution.Failed(resolved.Error!);
                }
                values.UnionWith(resolved.Values);
            }

            return values.Count == 0
                ? KafkaConfigurationResolution.Failed(
                    $"Configuration reference '{key}' resolves to an empty value.")
                : KafkaConfigurationResolution.Resolved(values);
        }
        finally
        {
            stack.Remove(canonical);
        }
    }

    private KafkaConfigurationResolution ResolveTemplate(string raw, HashSet<string> stack)
    {
        var value = Unquote(raw.Trim()).Replace("\\${", "${", StringComparison.Ordinal);
        if (value.Length == 0)
        {
            return KafkaConfigurationResolution.Failed("The configured Kafka resource is empty.");
        }

        var wholeSpring = SpringPlaceholder.Match(value);
        if (wholeSpring.Success && wholeSpring.Index == 0 && wholeSpring.Length == value.Length)
        {
            return ResolveSpringReference(wholeSpring.Groups["value"].Value, stack);
        }
        var wholeHelm = HelmPlaceholder.Match(value);
        if (wholeHelm.Success && wholeHelm.Index == 0 && wholeHelm.Length == value.Length)
        {
            return Resolve(wholeHelm.Groups["key"].Value, stack);
        }
        var wholeKubernetes = KubernetesPlaceholder.Match(value);
        if (wholeKubernetes.Success && wholeKubernetes.Index == 0
            && wholeKubernetes.Length == value.Length)
        {
            return Resolve(wholeKubernetes.Groups["key"].Value, stack);
        }
        var wholeEnvironment = EnvironmentPlaceholder.Match(value);
        if (wholeEnvironment.Success)
        {
            var key = wholeEnvironment.Groups["dollar"].Success
                ? wholeEnvironment.Groups["dollar"].Value
                : wholeEnvironment.Groups["percent"].Value;
            return Resolve(key, stack);
        }

        var error = default(string);
        value = ReplaceReferences(value, SpringPlaceholder, match =>
        {
            var resolved = ResolveSpringReference(match.Groups["value"].Value, stack);
            return SingleReplacement(resolved, match.Value, ref error);
        });
        value = ReplaceReferences(value, HelmPlaceholder, match =>
        {
            var resolved = Resolve(match.Groups["key"].Value, stack);
            return SingleReplacement(resolved, match.Value, ref error);
        });
        value = ReplaceReferences(value, KubernetesPlaceholder, match =>
        {
            var resolved = Resolve(match.Groups["key"].Value, stack);
            return SingleReplacement(resolved, match.Value, ref error);
        });

        if (error is not null)
        {
            return KafkaConfigurationResolution.Failed(error);
        }
        if (value.Contains("${", StringComparison.Ordinal)
            || value.Contains("#{", StringComparison.Ordinal)
            || value.Contains("{{", StringComparison.Ordinal)
            || value.Contains("$(", StringComparison.Ordinal))
        {
            return KafkaConfigurationResolution.Failed(
                $"Dynamic Kafka resource '{Bound(value)}' cannot be resolved offline.");
        }
        return KafkaConfigurationResolution.Resolved([value]);
    }

    private KafkaConfigurationResolution ResolveSpringReference(
        string reference,
        HashSet<string> stack)
    {
        var separator = reference.IndexOf(':');
        var key = separator < 0 ? reference.Trim() : reference[..separator].Trim();
        var resolved = Resolve(key, stack);
        if (resolved.Success || separator < 0)
        {
            return resolved;
        }

        var fallback = reference[(separator + 1)..].Trim();
        return fallback.Length == 0
            ? resolved
            : ResolveTemplate(fallback, stack);
    }

    private IEnumerable<KafkaConfigurationEntry> ActiveEntries()
    {
        foreach (var group in entries.GroupBy(entry => CanonicalKey(entry.Key), StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var maximumPriority = group.Max(entry => entry.Priority);
            foreach (var entry in group.Where(entry => entry.Priority == maximumPriority)
                         .OrderBy(entry => entry.Evidence.File, StringComparer.Ordinal)
                         .ThenBy(entry => entry.Evidence.Line)
                         .ThenBy(entry => entry.Value, StringComparer.Ordinal))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<string> ExpandConfiguredValues(
        string kind,
        string key,
        IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            if (kind == KafkaInventoryResourceKinds.Topic
                && (CanonicalKey(key).Contains("destination", StringComparison.Ordinal)
                    || CanonicalKey(key).Contains("topics", StringComparison.Ordinal)))
            {
                foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return item;
                }
            }
            else
            {
                yield return value;
            }
        }
    }

    private static string? ResourceKind(string key)
    {
        var canonical = CanonicalKey(key);
        var segments = canonical.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var compact = canonical.Replace(".", "", StringComparison.Ordinal);
        var kafkaScoped = segments.Contains("kafka", StringComparer.Ordinal)
            || canonical.Contains("spring.cloud.stream", StringComparison.Ordinal);
        if (canonical.Contains("bootstrap.servers", StringComparison.Ordinal)
            || compact.EndsWith("bootstrapservers", StringComparison.Ordinal)
            || kafkaScoped && (canonical.EndsWith(".brokers", StringComparison.Ordinal)
                || canonical == "brokers")
            || (segments.Contains("cluster", StringComparer.Ordinal)
                && kafkaScoped))
        {
            return KafkaInventoryResourceKinds.Cluster;
        }
        if (kafkaScoped && (segments.Contains("topic", StringComparer.Ordinal)
                || segments.Contains("topics", StringComparer.Ordinal))
            || (segments.Contains("destination", StringComparer.Ordinal)
                && (canonical.Contains("stream", StringComparison.Ordinal)
                    || canonical.Contains("binding", StringComparison.Ordinal)
                    || canonical.Contains("kafka", StringComparison.Ordinal))))
        {
            return KafkaInventoryResourceKinds.Topic;
        }
        if (canonical.Contains("group.id", StringComparison.Ordinal)
            || kafkaScoped && compact.EndsWith("groupid", StringComparison.Ordinal)
            || kafkaScoped && (canonical.EndsWith(".group", StringComparison.Ordinal)
                || canonical == "group")
            || canonical.Contains("consumer.group", StringComparison.Ordinal)
            || canonical.Contains("application.id", StringComparison.Ordinal)
            || kafkaScoped && compact.EndsWith("applicationid", StringComparison.Ordinal))
        {
            return KafkaInventoryResourceKinds.ConsumerGroup;
        }
        return null;
    }

    private static string ConfigurationUsage(string key, string kind)
    {
        var canonical = CanonicalKey(key);
        var compact = canonical.Replace(".", "", StringComparison.Ordinal);
        if (canonical.Contains("bootstrap.servers", StringComparison.Ordinal)
            || compact.EndsWith("bootstrapservers", StringComparison.Ordinal))
        {
            return "bootstrap-servers";
        }
        if (canonical.Contains("spring.cloud.stream", StringComparison.Ordinal))
        {
            return kind == KafkaInventoryResourceKinds.ConsumerGroup
                ? "cloud-stream-group"
                : "cloud-stream-destination";
        }
        if (canonical.Contains("application.id", StringComparison.Ordinal)
            || compact.EndsWith("applicationid", StringComparison.Ordinal))
        {
            return "streams-application-id";
        }
        return kind + "-configuration";
    }

    private static bool LooksLikeResourceValue(string value)
    {
        var normalized = value.Trim();
        return normalized.Length > 0
            && !normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
            && normalized is not "[]" and not "{}";
    }

    private static void ParseProperties(
        KafkaScanFile file,
        string detector,
        int priority,
        ICollection<KafkaConfigurationEntry> output)
    {
        var lines = Lines(file.Text);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
            }
            var separator = PropertySeparator(line);
            if (separator <= 0)
            {
                continue;
            }
            AddEntry(
                line[..separator].Trim(),
                line[(separator + 1)..].Trim(),
                file.EvidenceForLine(index + 1, detector, "configuration"),
                priority,
                output);
        }
    }

    private static void ParseJson(
        KafkaScanFile file,
        string detector,
        int priority,
        ICollection<KafkaConfigurationEntry> output)
    {
        try
        {
            using var document = JsonDocument.Parse(file.Text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            FlattenJson(document.RootElement, "", file, detector, priority, output);
        }
        catch (JsonException)
        {
            // Template-bearing deployment JSON is handled by source/config text detectors only.
        }
    }

    private static void FlattenJson(
        JsonElement element,
        string path,
        KafkaScanFile file,
        string detector,
        int priority,
        ICollection<KafkaConfigurationEntry> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var child = path.Length == 0 ? property.Name : path + "." + property.Name;
                FlattenJson(property.Value, child, file, detector, priority, output);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                FlattenJson(item, path, file, detector, priority, output);
            }
            return;
        }
        if (path.Length == 0 || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : element.GetRawText();
        var leaf = path.Split('.').Last();
        var marker = "\"" + leaf.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var offset = file.Text.IndexOf(marker, StringComparison.Ordinal);
        AddEntry(
            path,
            value,
            file.Evidence(Math.Max(0, offset), detector, "configuration"),
            priority,
            output);
    }

    private static void ParseYaml(
        KafkaScanFile file,
        string detector,
        int priority,
        string environment,
        ICollection<KafkaConfigurationEntry> output)
    {
        var lines = Lines(file.Text);
        var activeDocuments = ActiveYamlDocuments(lines, environment);
        var document = 0;
        var stack = new List<YamlKey>();
        for (var index = 0; index < lines.Length; index++)
        {
            var withoutComment = StripYamlComment(lines[index]);
            if (string.IsNullOrWhiteSpace(withoutComment))
            {
                continue;
            }
            var indent = withoutComment.TakeWhile(char.IsWhiteSpace).Count();
            var content = withoutComment.Trim();
            if (content is "---" or "...")
            {
                stack.Clear();
                if (content == "---") document++;
                continue;
            }
            if (!activeDocuments.GetValueOrDefault(document, true))
            {
                continue;
            }

            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var listItem = content.StartsWith("- ", StringComparison.Ordinal);
            if (listItem)
            {
                content = content[2..].Trim();
            }
            var separator = YamlSeparator(content);
            if (separator < 0)
            {
                if (listItem && stack.Count > 0)
                {
                    AddEntry(
                        JoinPath(stack.Select(item => item.Key)),
                        content,
                        file.EvidenceForLine(index + 1, detector, "configuration"),
                        priority,
                        output);
                }
                continue;
            }

            var key = Unquote(content[..separator].Trim());
            var value = content[(separator + 1)..].Trim();
            var path = JoinPath(stack.Select(item => item.Key).Append(key));
            if (value.Length == 0)
            {
                stack.Add(new YamlKey(indent, key));
                continue;
            }

            foreach (var item in InlineYamlValues(value))
            {
                AddEntry(
                    path,
                    item,
                    file.EvidenceForLine(index + 1, detector, "configuration"),
                    priority,
                    output);
            }

            if (listItem)
            {
                stack.Add(new YamlKey(indent, key));
            }
        }

        ParseKubernetesEnvironment(file, detector, priority, output);
    }

    private static IReadOnlyDictionary<int, bool> ActiveYamlDocuments(
        IReadOnlyList<string> lines,
        string environment)
    {
        var documents = new Dictionary<int, List<string>> { [0] = [] };
        var document = 0;
        foreach (var line in lines)
        {
            var content = StripYamlComment(line).Trim();
            if (content == "---")
            {
                document++;
                documents.TryAdd(document, []);
                continue;
            }
            documents[document].Add(content);
        }

        return documents.ToDictionary(
            item => item.Key,
            item => DocumentMatchesEnvironment(item.Value, environment));
    }

    private static bool DocumentMatchesEnvironment(
        IEnumerable<string> lines,
        string environment)
    {
        var expressions = lines
            .Select(line => Regex.Match(
                line,
                @"^(?:on-profile|profiles)\s*:\s*(?<value>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Where(match => match.Success)
            .Select(match => Unquote(match.Groups["value"].Value.Trim()))
            .ToArray();
        if (expressions.Length == 0) return true;

        var tokens = expressions
            .SelectMany(expression => expression.Trim('[', ']')
                .Split([',', '|', '&'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        var positive = tokens.Where(token => !token.StartsWith('!')).ToArray();
        if (tokens.Any(token => token.StartsWith('!')
                && token[1..].Equals(environment, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return positive.Length == 0
            || positive.Contains(environment, StringComparer.OrdinalIgnoreCase);
    }

    private static void ParseKubernetesEnvironment(
        KafkaScanFile file,
        string detector,
        int priority,
        ICollection<KafkaConfigurationEntry> output)
    {
        var lines = Lines(file.Text);
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Regex.Match(
                StripYamlComment(lines[index]),
                @"^(?<indent>\s*)-?\s*name\s*:\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*$",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            var indent = match.Groups["indent"].Value.Length;
            string? value = null;
            var valueLine = index + 1;
            string? referencedKey = null;
            for (var cursor = index + 1; cursor < lines.Length; cursor++)
            {
                var candidate = StripYamlComment(lines[cursor]);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }
                var candidateIndent = candidate.TakeWhile(char.IsWhiteSpace).Count();
                if (candidateIndent <= indent && candidate.TrimStart().StartsWith("-", StringComparison.Ordinal))
                {
                    break;
                }
                var trimmed = candidate.Trim();
                if (trimmed.StartsWith("value:", StringComparison.Ordinal))
                {
                    value = trimmed["value:".Length..].Trim();
                    valueLine = cursor + 1;
                    break;
                }
                if (trimmed.StartsWith("key:", StringComparison.Ordinal))
                {
                    referencedKey = Unquote(trimmed["key:".Length..].Trim());
                    valueLine = cursor + 1;
                }
            }

            value ??= referencedKey is null ? null : "$(" + referencedKey + ")";
            if (value is not null)
            {
                AddEntry(
                    match.Groups["name"].Value,
                    value,
                    file.EvidenceForLine(valueLine, detector, "environment-variable"),
                    priority + 5,
                    output);
            }
        }
    }

    private static void AddEntry(
        string key,
        string value,
        KafkaInventoryEvidence evidence,
        int priority,
        ICollection<KafkaConfigurationEntry> output)
    {
        key = Unquote(key.Trim());
        value = Unquote(value.Trim());
        if (key.Length > 0 && value.Length > 0)
        {
            output.Add(new KafkaConfigurationEntry(key, value, evidence, priority));
        }
    }

    private static bool AppliesToEnvironment(string path, string environment)
    {
        var file = Path.GetFileName(path);
        foreach (var pattern in new[]
                 {
                     @"^appsettings\.(?<environment>[^.]+)\.json$",
                     @"^application-(?<environment>[^.]+)\.(?:ya?ml|properties)$",
                     @"^values-(?<environment>[^.]+)\.ya?ml$"
                 })
        {
            var match = Regex.Match(file, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups["environment"].Value.Equals(environment, StringComparison.OrdinalIgnoreCase);
            }
        }

        var normalized = path.Replace('\\', '/');
        var overlay = Regex.Match(
            normalized,
            @"(?:^|/)(?:overlays|environments)/(?<environment>[^/]+)(?:/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (overlay.Success)
        {
            return overlay.Groups["environment"].Value.Equals(
                environment,
                StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private static int ConfigurationPriority(string path, string environment)
    {
        var normalized = path.Replace('\\', '/');
        var environmentOverlay = Regex.IsMatch(
            normalized,
            $@"(?:^|/)(?:overlays|environments)/{Regex.Escape(environment)}(?:/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var file = Path.GetFileName(path);
        return environmentOverlay ? 40
            : file.Contains(environment, StringComparison.OrdinalIgnoreCase) ? 30
            : normalized.Contains("/templates/", StringComparison.OrdinalIgnoreCase) ? 20
            : 10;
    }

    private static string ConfigurationDetector(KafkaScanFile file)
    {
        if (file.RelativePath.Contains("helm", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.Contains("/templates/", StringComparison.OrdinalIgnoreCase)
            || file.Text.Contains("{{ .Values.", StringComparison.Ordinal))
        {
            return "helm";
        }
        if ((file.Text.Contains("apiVersion:", StringComparison.Ordinal)
             && file.Text.Contains("kind:", StringComparison.Ordinal))
            || file.RelativePath.Contains("k8s", StringComparison.OrdinalIgnoreCase)
            || file.RelativePath.Contains("kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            return "kubernetes";
        }
        return "application-config";
    }

    internal static string CanonicalKey(string key)
    {
        var canonical = key.Trim().ToLowerInvariant()
            .Replace("__", ".", StringComparison.Ordinal)
            .Replace(':', '.')
            .Replace('_', '.')
            .Replace('-', '.');
        return string.Join('.', canonical.Split('.', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ReplaceReferences(string value, Regex expression, MatchEvaluator evaluator) =>
        expression.Replace(value, evaluator);

    private static string SingleReplacement(
        KafkaConfigurationResolution resolution,
        string expression,
        ref string? error)
    {
        if (!resolution.Success)
        {
            error ??= resolution.Error;
            return expression;
        }
        if (resolution.Values.Count != 1)
        {
            error ??= $"Dynamic reference '{Bound(expression)}' resolves to multiple values.";
            return expression;
        }
        return resolution.Values[0];
    }

    private static string[] Lines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static int PropertySeparator(string value)
    {
        var equals = value.IndexOf('=');
        var colon = value.IndexOf(':');
        if (equals < 0) return colon;
        if (colon < 0) return equals;
        return Math.Min(equals, colon);
    }

    private static int YamlSeparator(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote != '\0')
            {
                if (current == quote && (index == 0 || value[index - 1] != '\\'))
                {
                    quote = '\0';
                }
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == ':')
            {
                return index;
            }
        }
        return -1;
    }

    private static string StripYamlComment(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote != '\0')
            {
                if (current == quote && (index == 0 || value[index - 1] != '\\'))
                {
                    quote = '\0';
                }
            }
            else if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == '#'
                     && (index == 0 || char.IsWhiteSpace(value[index - 1])))
            {
                return value[..index];
            }
        }
        return value;
    }

    private static IEnumerable<string> InlineYamlValues(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            foreach (var item in value[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return Unquote(item);
            }
            yield break;
        }
        yield return Unquote(value);
    }

    private static string JoinPath(IEnumerable<string> parts) => string.Join('.', parts.Where(part => part.Length > 0));

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\'", "'", StringComparison.Ordinal);
        }
        return value;
    }

    private static string Bound(string value) => value.Length <= 200 ? value : value[..197] + "...";

    private sealed record YamlKey(int Indent, string Key);
}

internal sealed record KafkaConfigurationEntry(
    string Key,
    string Value,
    KafkaInventoryEvidence Evidence,
    int Priority);

internal sealed record KafkaConfigurationResolution(
    IReadOnlyList<string> Values,
    string? Error)
{
    public bool Success => Error is null;

    public static KafkaConfigurationResolution Resolved(IEnumerable<string> values) =>
        new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), null);

    public static KafkaConfigurationResolution Failed(string error) => new([], error);
}
