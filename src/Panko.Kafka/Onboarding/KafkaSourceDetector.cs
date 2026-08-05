using System.Text.RegularExpressions;

namespace Panko.Kafka.Onboarding;

internal static class KafkaSourceDetector
{
    private static readonly Regex KafkaListener = Marker(@"@KafkaListener\s*\(");
    private static readonly Regex KafkaTopicPartition = Marker(@"@TopicPartition\s*\(");
    private static readonly Regex SendTo = Marker(@"@SendTo\s*\(");
    private static readonly Regex MethodCall = Marker(
        @"\.\s*(?<name>sendDefault|send|subscribe|ProduceAsync|Produce|Subscribe|stream|table|globalTable|to|through)\s*(?:<[^>{}()]*>)?\s*\(");
    private static readonly Regex KafkaTemplateSend = Marker(
        @"\b(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\?\s*)?\.\s*(?<name>sendDefault|send)\s*(?:<[^>{}()]*>)?\s*\(");
    private static readonly Regex ProducerRecord = Marker(
        @"\b(?:new\s+)?ProducerRecord\s*(?:<[^>{}()]*>)?\s*\(");
    private static readonly Regex TopicPartition = Marker(
        @"\b(?:new\s+)?TopicPartition\s*(?:<[^>{}()]*>)?\s*\(");
    private static readonly Regex PropertyPut = Marker(@"\.\s*put\s*\(");
    private static readonly Regex StreamBridgeSend = Marker(
        @"\b(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\?\s*)?\.\s*(?<name>send)\s*(?:<[^>{}()]*>)?\s*\(");
    private static readonly Regex JavaStreamBridgeVariable = Marker(
        @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*StreamBridge\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\b");
    private static readonly Regex KotlinStreamBridgeVariable = Marker(
        @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?:[A-Za-z_][A-Za-z0-9_]*\.)*StreamBridge\b");
    private static readonly Regex JavaKafkaTemplateVariable = Marker(
        @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*KafkaTemplate\s*(?:<[^>{}()]*>)?\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\b");
    private static readonly Regex KotlinKafkaTemplateVariable = Marker(
        @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?:[A-Za-z_][A-Za-z0-9_]*\.)*KafkaTemplate\s*(?:<[^>{}()]*>)?\??");
    private static readonly Regex Assignment = new(
        @"\b(?<name>BootstrapServers|GroupId|ApplicationId)\s*=\s*(?<value>[^,;}\r\n]+)",
        RegexOptions.CultureInvariant);

    public static void Scan(
        KafkaScanFile file,
        KafkaConfigurationIndex configuration,
        KafkaInventoryBuilder inventory)
    {
        var masked = MaskComments(file.Text);
        var constants = FindConstants(file.Text, masked);
        var context = new SourceContext(file, masked, configuration, inventory, constants);

        var springKafka = masked.Contains("@KafkaListener", StringComparison.Ordinal)
            || masked.Contains("KafkaTemplate", StringComparison.Ordinal)
            || masked.Contains("org.springframework.kafka", StringComparison.Ordinal);
        var cloudStream = masked.Contains("spring.cloud.stream", StringComparison.OrdinalIgnoreCase)
            || masked.Contains("StreamBridge", StringComparison.Ordinal)
            || masked.Contains("EnableBinding", StringComparison.Ordinal);
        var kafkaStreams = masked.Contains("org.apache.kafka.streams", StringComparison.Ordinal)
            || masked.Contains("StreamsBuilder", StringComparison.Ordinal)
            || Regex.IsMatch(masked, @"\bKStream\s*<", RegexOptions.CultureInvariant);
        var confluent = masked.Contains("Confluent.Kafka", StringComparison.Ordinal)
            || masked.Contains("ProducerBuilder<", StringComparison.Ordinal)
            || masked.Contains("ConsumerBuilder<", StringComparison.Ordinal)
            || masked.Contains("IProducer<", StringComparison.Ordinal)
            || masked.Contains("IConsumer<", StringComparison.Ordinal);
        var plainClient = masked.Contains("org.apache.kafka.clients", StringComparison.Ordinal)
            || masked.Contains("KafkaProducer", StringComparison.Ordinal)
            || masked.Contains("KafkaConsumer", StringComparison.Ordinal)
            || masked.Contains("ProducerRecord", StringComparison.Ordinal);

        if (springKafka)
        {
            DetectSpringKafka(context);
        }
        if (kafkaStreams)
        {
            DetectKafkaStreams(context);
        }
        if (plainClient)
        {
            DetectJvmClient(context);
        }
        if (confluent)
        {
            DetectConfluentClient(context);
        }
        if (cloudStream)
        {
            DetectCloudStream(context);
        }
        if (springKafka || kafkaStreams || plainClient || confluent || cloudStream)
        {
            DetectClientConfiguration(context, kafkaStreams, confluent);
        }
    }

    private static void DetectSpringKafka(SourceContext context)
    {
        foreach (var invocation in Invocations(context, KafkaListener))
        {
            var topics = NamedArgument(invocation.Arguments, "topics")
                ?? PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topics))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topics,
                    invocation.Start,
                    "spring-kafka",
                    "listener-topic");
            }

            var pattern = NamedArgument(invocation.Arguments, "topicPattern");
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                context.Inventory.AddUnresolved(
                    KafkaInventoryResourceKinds.Topic,
                    pattern,
                    "A Kafka listener topic pattern cannot be converted to an exact topic allowlist.",
                    context.File.Evidence(invocation.Start, "spring-kafka", "listener-topic-pattern"));
            }

            var group = NamedArgument(invocation.Arguments, "groupId");
            if (string.IsNullOrWhiteSpace(group))
            {
                group = NamedArgument(invocation.Arguments, "id");
            }
            if (!string.IsNullOrWhiteSpace(group))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.ConsumerGroup,
                    group,
                    invocation.Start,
                    "spring-kafka",
                    "listener-group");
            }
        }

        foreach (var invocation in Invocations(context, KafkaTopicPartition))
        {
            var topic = NamedArgument(invocation.Arguments, "topic")
                ?? PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "spring-kafka",
                    "listener-topic-partition");
            }
        }

        foreach (var invocation in Invocations(context, SendTo))
        {
            var topic = PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "spring-kafka",
                    "reply-topic");
            }
        }

        if (!context.Masked.Contains("KafkaTemplate", StringComparison.Ordinal))
        {
            return;
        }
        var receivers = KafkaTemplateVariables(context.Masked);
        foreach (var invocation in Invocations(context, KafkaTemplateSend)
                     .Where(item => receivers.Contains(item.Receiver)))
        {
            if (invocation.Name == "sendDefault")
            {
                var evidence = context.File.Evidence(
                    invocation.Start,
                    "spring-kafka",
                    "default-producer-topic");
                var defaultTopic = context.Configuration.Resolve("spring.kafka.template.default-topic");
                if (!defaultTopic.Success)
                {
                    context.Inventory.AddUnresolved(
                        KafkaInventoryResourceKinds.Topic,
                        "spring.kafka.template.default-topic",
                        $"KafkaTemplate.sendDefault has no resolved default-topic mapping: {defaultTopic.Error}",
                        evidence);
                }
                else
                {
                    foreach (var configuredTopic in defaultTopic.Values)
                    {
                        context.Inventory.AddResource(
                            KafkaInventoryResourceKinds.Topic,
                            configuredTopic,
                            evidence);
                    }
                }
                continue;
            }

            var topic = PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "spring-kafka",
                    "producer-topic");
            }
        }
    }

    private static IReadOnlySet<string> KafkaTemplateVariables(string source)
    {
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expression in new[] { JavaKafkaTemplateVariable, KotlinKafkaTemplateVariable })
        {
            foreach (Match match in expression.Matches(source))
            {
                variables.Add(match.Groups["variable"].Value);
            }
        }
        return variables;
    }

    private static void DetectJvmClient(SourceContext context)
    {
        foreach (var invocation in Invocations(context, ProducerRecord))
        {
            var topic = PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "kafka-client",
                    "producer-topic");
            }
        }
        foreach (var invocation in Invocations(context, MethodCall)
                     .Where(item => item.Name == "subscribe"))
        {
            var topic = PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "kafka-client",
                    "consumer-subscription");
            }
        }
        DetectTopicPartitions(context, "kafka-client");
    }

    private static void DetectKafkaStreams(SourceContext context)
    {
        foreach (var invocation in Invocations(context, MethodCall))
        {
            var usage = invocation.Name switch
            {
                "stream" or "table" or "globalTable" => "streams-input",
                "to" or "through" => "streams-output",
                _ => null
            };
            if (usage is null)
            {
                continue;
            }
            var topic = PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "kafka-streams",
                    usage);
            }
        }
    }

    private static void DetectConfluentClient(SourceContext context)
    {
        foreach (var invocation in Invocations(context, MethodCall))
        {
            var usage = invocation.Name switch
            {
                "Produce" or "ProduceAsync" => "producer-topic",
                "Subscribe" => "consumer-subscription",
                _ => null
            };
            if (usage is null)
            {
                continue;
            }
            var topic = PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    "confluent-kafka",
                    usage);
            }
        }
        DetectTopicPartitions(context, "confluent-kafka");
    }

    private static void DetectCloudStream(SourceContext context)
    {
        var receivers = StreamBridgeVariables(context.Masked);
        foreach (var invocation in Invocations(context, StreamBridgeSend)
                     .Where(item => receivers.Contains(item.Receiver)))
        {
            var bindingExpression = PositionalArgument(invocation.Arguments, 0);
            if (string.IsNullOrWhiteSpace(bindingExpression))
            {
                continue;
            }

            var binding = ResolveExpression(bindingExpression, context, []);
            foreach (var item in binding)
            {
                var evidence = context.File.Evidence(
                    invocation.Start,
                    "spring-cloud-stream",
                    "output-binding");
                if (!item.Success)
                {
                    context.Inventory.AddUnresolved(
                        KafkaInventoryResourceKinds.Topic,
                        item.Expression,
                        item.Error!,
                        evidence);
                    continue;
                }

                var destination = context.Configuration.Resolve(
                    $"spring.cloud.stream.bindings.{item.Value}.destination");
                if (!destination.Success)
                {
                    context.Inventory.AddUnresolved(
                        KafkaInventoryResourceKinds.Topic,
                        $"binding:{item.Value}",
                        $"Spring Cloud Stream binding '{item.Value}' has no resolved destination mapping.",
                        evidence);
                    continue;
                }
                foreach (var value in destination.Values
                             .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
                {
                    context.Inventory.AddResource(KafkaInventoryResourceKinds.Topic, value, evidence);
                }
            }
        }
    }

    private static IReadOnlySet<string> StreamBridgeVariables(string source)
    {
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expression in new[] { JavaStreamBridgeVariable, KotlinStreamBridgeVariable })
        {
            foreach (Match match in expression.Matches(source))
            {
                variables.Add(match.Groups["variable"].Value);
            }
        }
        return variables;
    }

    private static void DetectClientConfiguration(
        SourceContext context,
        bool kafkaStreams,
        bool confluent)
    {
        foreach (var invocation in Invocations(context, PropertyPut))
        {
            var arguments = SplitTopLevel(invocation.Arguments);
            if (arguments.Count < 2)
            {
                continue;
            }
            var key = arguments[0];
            string? kind = null;
            string usage;
            if (key.Contains("BOOTSTRAP_SERVERS_CONFIG", StringComparison.Ordinal)
                || key.Contains("bootstrap.servers", StringComparison.OrdinalIgnoreCase))
            {
                kind = KafkaInventoryResourceKinds.Cluster;
                usage = "bootstrap-servers";
            }
            else if (key.Contains("GROUP_ID_CONFIG", StringComparison.Ordinal)
                     || key.Contains("group.id", StringComparison.OrdinalIgnoreCase))
            {
                kind = KafkaInventoryResourceKinds.ConsumerGroup;
                usage = "consumer-group-configuration";
            }
            else if (key.Contains("APPLICATION_ID_CONFIG", StringComparison.Ordinal)
                     || key.Contains("application.id", StringComparison.OrdinalIgnoreCase))
            {
                kind = KafkaInventoryResourceKinds.ConsumerGroup;
                usage = "streams-application-id";
            }
            else
            {
                continue;
            }

            RecordExpression(
                context,
                kind,
                arguments[1],
                invocation.Start,
                kafkaStreams ? "kafka-streams" : "kafka-client",
                usage);
        }

        foreach (Match match in Assignment.Matches(context.Masked))
        {
            var name = match.Groups["name"].Value;
            var kind = name == "BootstrapServers"
                ? KafkaInventoryResourceKinds.Cluster
                : KafkaInventoryResourceKinds.ConsumerGroup;
            var usage = name switch
            {
                "BootstrapServers" => "bootstrap-servers",
                "ApplicationId" => "streams-application-id",
                _ => "consumer-group-configuration"
            };
            RecordExpression(
                context,
                kind,
                context.File.Text.Substring(
                    match.Groups["value"].Index,
                    match.Groups["value"].Length),
                match.Index,
                confluent ? "confluent-kafka" : kafkaStreams ? "kafka-streams" : "kafka-client",
                usage);
        }
    }

    private static void DetectTopicPartitions(SourceContext context, string detector)
    {
        foreach (var invocation in Invocations(context, TopicPartition))
        {
            var topic = NamedArgument(invocation.Arguments, "topic")
                ?? PositionalArgument(invocation.Arguments, 0);
            if (!string.IsNullOrWhiteSpace(topic))
            {
                RecordExpression(
                    context,
                    KafkaInventoryResourceKinds.Topic,
                    topic,
                    invocation.Start,
                    detector,
                    "topic-partition");
            }
        }
    }

    private static void RecordExpression(
        SourceContext context,
        string kind,
        string expression,
        int characterIndex,
        string detector,
        string usage)
    {
        var evidence = context.File.Evidence(characterIndex, detector, usage);
        foreach (var item in ResolveExpression(expression, context, []))
        {
            if (item.Success)
            {
                context.Inventory.AddResource(kind, item.Value!, evidence);
            }
            else
            {
                context.Inventory.AddUnresolved(kind, item.Expression, item.Error!, evidence);
            }
        }
    }

    private static IReadOnlyList<ExpressionResolution> ResolveExpression(
        string rawExpression,
        SourceContext context,
        HashSet<string> resolvingConstants)
    {
        var expression = TrimExpression(rawExpression);
        if (expression.Length == 0)
        {
            return [ExpressionResolution.Failed(rawExpression, "The Kafka resource expression is empty.")];
        }

        if (TryUnwrapCollection(expression, out var collection))
        {
            var values = new List<ExpressionResolution>();
            foreach (var item in SplitTopLevel(collection))
            {
                values.AddRange(ResolveExpression(item, context, resolvingConstants));
            }
            return values.Count == 0
                ? [ExpressionResolution.Failed(expression, "The Kafka resource collection is empty.")]
                : values;
        }

        var concatenated = SplitTopLevel(expression, '+');
        if (concatenated.Count > 1)
        {
            var parts = new List<string>();
            foreach (var part in concatenated)
            {
                var resolvedPart = ResolveExpression(part, context, resolvingConstants);
                if (resolvedPart.Count != 1 || !resolvedPart[0].Success)
                {
                    return [ExpressionResolution.Failed(
                        expression,
                        "A concatenated Kafka resource contains an unresolved dynamic value.")];
                }
                parts.Add(resolvedPart[0].Value!);
            }
            return [ExpressionResolution.Resolved(expression, string.Concat(parts))];
        }

        if (TryStringLiteral(expression, out var literal, out var interpolated))
        {
            if (IsKotlinSource(context.File.RelativePath)
                && ContainsKotlinStringInterpolation(expression))
            {
                return [ExpressionResolution.Failed(
                    expression,
                    "A Kotlin-interpolated Kafka resource cannot be resolved offline.")];
            }
            if (interpolated && (literal.Contains('{', StringComparison.Ordinal)
                                 || Regex.IsMatch(literal, @"(?<!\\)\$[A-Za-z_]", RegexOptions.CultureInvariant)))
            {
                return [ExpressionResolution.Failed(
                    expression,
                    "An interpolated Kafka resource cannot be resolved offline.")];
            }
            var resolved = context.Configuration.ResolveTemplate(literal);
            return resolved.Success
                ? resolved.Values.Select(value => ExpressionResolution.Resolved(expression, value)).ToArray()
                : [ExpressionResolution.Failed(expression, resolved.Error!)];
        }

        if (TryConfigurationLookup(expression, out var configurationKey))
        {
            var resolved = context.Configuration.Resolve(configurationKey);
            return resolved.Success
                ? resolved.Values.Select(value => ExpressionResolution.Resolved(expression, value)).ToArray()
                : [ExpressionResolution.Failed(expression, resolved.Error!)];
        }

        if (expression.Contains("${", StringComparison.Ordinal)
            || expression.Contains("{{", StringComparison.Ordinal)
            || expression.Contains("$(", StringComparison.Ordinal))
        {
            var resolved = context.Configuration.ResolveTemplate(expression);
            return resolved.Success
                ? resolved.Values.Select(value => ExpressionResolution.Resolved(expression, value)).ToArray()
                : [ExpressionResolution.Failed(expression, resolved.Error!)];
        }

        var identifier = expression.Split('.').Last().Trim();
        if (Regex.IsMatch(identifier, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)
            && context.Constants.TryGetValue(identifier, out var constantExpression))
        {
            if (!resolvingConstants.Add(identifier))
            {
                return [ExpressionResolution.Failed(
                    expression,
                    $"Kafka resource constant '{identifier}' is cyclic.")];
            }
            try
            {
                return ResolveExpression(constantExpression, context, resolvingConstants);
            }
            finally
            {
                resolvingConstants.Remove(identifier);
            }
        }

        return [ExpressionResolution.Failed(
            expression,
            "The Kafka resource is computed dynamically and cannot be resolved offline.")];
    }

    private static IReadOnlyDictionary<string, string> FindConstants(string original, string masked)
    {
        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var expressions = new[]
        {
            @"\b(?:static\s+final|final\s+static)\s+String\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^;\r\n]+)",
            @"\bconst\s+val\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*String)?\s*=\s*(?<value>[^;\r\n]+)",
            @"\b(?:const|static\s+readonly)\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^;\r\n]+)"
        };
        foreach (var expression in expressions)
        {
            foreach (Match match in Regex.Matches(masked, expression, RegexOptions.CultureInvariant))
            {
                AddConstantCandidate(candidates, original, match);
            }
        }

        var injected = new Regex(
            "@Value\\s*\\(\\s*(?<value>\\\"(?:\\\\.|[^\\\"])*\\\")\\s*\\)\\s*(?:private\\s+|protected\\s+|public\\s+|lateinit\\s+|final\\s+|var\\s+|val\\s+|String\\s+|:\\s*String\\s*)*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.CultureInvariant);
        foreach (Match match in injected.Matches(masked))
        {
            AddConstantCandidate(candidates, original, match);
        }
        return candidates
            .Where(item => item.Value.Count == 1)
            .ToDictionary(item => item.Key, item => item.Value[0], StringComparer.Ordinal);
    }

    private static void AddConstantCandidate(
        IDictionary<string, List<string>> candidates,
        string original,
        Match match)
    {
        var name = match.Groups["name"].Value;
        if (!candidates.TryGetValue(name, out var values))
        {
            values = [];
            candidates.Add(name, values);
        }
        values.Add(original.Substring(match.Groups["value"].Index, match.Groups["value"].Length));
    }

    private static bool IsKotlinSource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".kt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".kts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsKotlinStringInterpolation(string expression)
    {
        var candidate = TrimExpression(expression);
        var raw = candidate.Length >= 6
            && candidate.StartsWith("\"\"\"", StringComparison.Ordinal)
            && candidate.EndsWith("\"\"\"", StringComparison.Ordinal);
        string content;
        if (raw)
        {
            content = candidate[3..^3];
        }
        else if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            content = candidate[1..^1];
        }
        else
        {
            return false;
        }

        for (var index = 0; index + 1 < content.Length; index++)
        {
            if (content[index] != '$'
                || content[index + 1] != '{'
                    && !IsIdentifierStart(content[index + 1]))
            {
                continue;
            }
            if (raw || !IsEscaped(content, index))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEscaped(string value, int index)
    {
        var slashes = 0;
        while (--index >= 0 && value[index] == '\\')
        {
            slashes++;
        }
        return slashes % 2 == 1;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool TryConfigurationLookup(string expression, out string key)
    {
        var accessor = Regex.Match(
            expression,
            "(?:getProperty|getRequiredProperty|GetValue(?:<[^>]+>)?|GetSection)\\s*\\(\\s*\\\"(?<key>[^\\\"]+)\\\"",
            RegexOptions.CultureInvariant);
        if (!accessor.Success)
        {
            accessor = Regex.Match(
                expression,
                "\\[\\s*\\\"(?<key>[^\\\"]+)\\\"\\s*\\]",
                RegexOptions.CultureInvariant);
        }
        key = accessor.Success ? accessor.Groups["key"].Value : "";
        return accessor.Success;
    }

    private static bool TryStringLiteral(
        string expression,
        out string value,
        out bool interpolated)
    {
        var candidate = expression.Trim();
        interpolated = candidate.StartsWith('$');
        if (candidate.StartsWith("$@\"", StringComparison.Ordinal)
            || candidate.StartsWith("@$\"", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }
        else if (candidate.StartsWith("@\"", StringComparison.Ordinal)
                 || candidate.StartsWith("$\"", StringComparison.Ordinal))
        {
            candidate = candidate[1..];
        }
        if (candidate.Length >= 6
            && candidate.StartsWith("\"\"\"", StringComparison.Ordinal)
            && candidate.EndsWith("\"\"\"", StringComparison.Ordinal))
        {
            value = candidate[3..^3];
            return true;
        }
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            value = candidate[1..^1]
                .Replace("\"\"", "\"", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
                .Replace("\\$", "$", StringComparison.Ordinal);
            return true;
        }
        if (candidate.Length >= 2 && candidate[0] == '\'' && candidate[^1] == '\'')
        {
            value = candidate[1..^1];
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryUnwrapCollection(string expression, out string content)
    {
        var candidate = expression.Trim();
        if (candidate.StartsWith("new[]", StringComparison.Ordinal)
            || Regex.IsMatch(candidate, @"^new\s+[A-Za-z0-9_<>?,.]+\s*\[\s*\]", RegexOptions.CultureInvariant))
        {
            var brace = candidate.IndexOf('{');
            if (brace >= 0 && candidate.EndsWith('}'))
            {
                content = candidate[(brace + 1)..^1];
                return true;
            }
        }
        if ((candidate.StartsWith('{') && candidate.EndsWith('}'))
            || (candidate.StartsWith('[') && candidate.EndsWith(']')))
        {
            content = candidate[1..^1];
            return true;
        }

        foreach (var wrapper in new[]
                 {
                     "List.of", "java.util.List.of", "Set.of", "java.util.Set.of",
                     "Arrays.asList", "java.util.Arrays.asList", "Collections.singletonList",
                     "listOf", "setOf", "arrayOf"
                 })
        {
            var marker = wrapper + "(";
            var opening = candidate.IndexOf(marker, StringComparison.Ordinal);
            var qualifier = opening <= 0 ? "" : candidate[..opening];
            if (opening >= 0
                && candidate.EndsWith(')')
                && (opening == 0
                    || Regex.IsMatch(qualifier, @"^(?:[A-Za-z_][A-Za-z0-9_]*\.)+$", RegexOptions.CultureInvariant)))
            {
                content = candidate[(opening + marker.Length)..^1];
                return true;
            }
        }
        content = "";
        return false;
    }

    private static string TrimExpression(string expression)
    {
        var value = expression.Trim().TrimEnd(';').Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')'
               && FindClosingParenthesis(value, 0) == value.Length - 1)
        {
            value = value[1..^1].Trim();
        }
        return value;
    }

    private static IEnumerable<Invocation> Invocations(SourceContext context, Regex marker)
    {
        foreach (Match match in marker.Matches(context.Masked))
        {
            var open = context.Masked.IndexOf('(', match.Index, match.Length);
            if (open < 0)
            {
                continue;
            }
            var close = FindClosingParenthesis(context.Masked, open);
            if (close < 0)
            {
                continue;
            }
            yield return new Invocation(
                match.Index,
                match.Groups["name"].Success ? match.Groups["name"].Value : "",
                context.File.Text[(open + 1)..close],
                match.Groups["receiver"].Success ? match.Groups["receiver"].Value : "");
        }
    }

    private static int FindClosingParenthesis(string value, int open)
    {
        var depth = 0;
        var quote = '\0';
        var verbatim = false;
        for (var index = open; index < value.Length; index++)
        {
            var current = value[index];
            if (quote != '\0')
            {
                if (verbatim && current == '"' && index + 1 < value.Length && value[index + 1] == '"')
                {
                    index++;
                    continue;
                }
                if (current == quote && (verbatim || index == 0 || value[index - 1] != '\\'))
                {
                    quote = '\0';
                    verbatim = false;
                }
                continue;
            }
            if (current is '"' or '\'')
            {
                quote = current;
                verbatim = current == '"' && index > 0 && value[index - 1] == '@';
            }
            else if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static string? NamedArgument(string arguments, string name)
    {
        foreach (var argument in SplitTopLevel(arguments))
        {
            var separator = TopLevelSeparator(argument, '=');
            if (separator > 0
                && argument[..separator].Trim().Equals(name, StringComparison.Ordinal))
            {
                return argument[(separator + 1)..].Trim();
            }
        }
        return null;
    }

    private static string? PositionalArgument(string arguments, int index)
    {
        var values = SplitTopLevel(arguments);
        if (index >= values.Count)
        {
            return null;
        }
        var value = values[index];
        var separator = TopLevelSeparator(value, '=');
        return separator >= 0 ? value[(separator + 1)..].Trim() : value.Trim();
    }

    private static IReadOnlyList<string> SplitTopLevel(string value, char separator = ',')
    {
        var output = new List<string>();
        var start = 0;
        var round = 0;
        var square = 0;
        var curly = 0;
        var angle = 0;
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
            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }
            switch (current)
            {
                case '(': round++; break;
                case ')': round--; break;
                case '[': square++; break;
                case ']': square--; break;
                case '{': curly++; break;
                case '}': curly--; break;
                case '<': angle++; break;
                case '>': angle = Math.Max(0, angle - 1); break;
            }
            if (current == separator && round == 0 && square == 0 && curly == 0 && angle == 0)
            {
                output.Add(value[start..index].Trim());
                start = index + 1;
            }
        }
        output.Add(value[start..].Trim());
        return output.Where(item => item.Length > 0).ToArray();
    }

    private static int TopLevelSeparator(string value, char separator)
    {
        var round = 0;
        var square = 0;
        var curly = 0;
        var angle = 0;
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
            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }
            switch (current)
            {
                case '(': round++; break;
                case ')': round--; break;
                case '[': square++; break;
                case ']': square--; break;
                case '{': curly++; break;
                case '}': curly--; break;
                case '<': angle++; break;
                case '>': angle = Math.Max(0, angle - 1); break;
            }
            if (current == separator && round == 0 && square == 0 && curly == 0 && angle == 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static string MaskComments(string value)
    {
        var output = value.ToCharArray();
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            if (quote != '\0')
            {
                if (value[index] == quote && (index == 0 || value[index - 1] != '\\'))
                {
                    quote = '\0';
                }
                continue;
            }
            if (value[index] is '"' or '\'')
            {
                quote = value[index];
                continue;
            }
            if (value[index] == '/' && index + 1 < value.Length && value[index + 1] == '/')
            {
                for (; index < value.Length && value[index] != '\n'; index++)
                {
                    output[index] = ' ';
                }
                index--;
            }
            else if (value[index] == '/' && index + 1 < value.Length && value[index + 1] == '*')
            {
                output[index++] = ' ';
                output[index] = ' ';
                while (++index < value.Length)
                {
                    if (value[index] == '*' && index + 1 < value.Length && value[index + 1] == '/')
                    {
                        output[index] = ' ';
                        output[++index] = ' ';
                        break;
                    }
                    if (value[index] != '\n' && value[index] != '\r')
                    {
                        output[index] = ' ';
                    }
                }
            }
        }
        return new string(output);
    }

    private static Regex Marker(string expression) => new(
        expression,
        RegexOptions.CultureInvariant);

    private sealed record SourceContext(
        KafkaScanFile File,
        string Masked,
        KafkaConfigurationIndex Configuration,
        KafkaInventoryBuilder Inventory,
        IReadOnlyDictionary<string, string> Constants);

    private sealed record Invocation(int Start, string Name, string Arguments, string Receiver);

    private sealed record ExpressionResolution(string Expression, string? Value, string? Error)
    {
        public bool Success => Error is null;

        public static ExpressionResolution Resolved(string expression, string value) =>
            new(expression, value, null);

        public static ExpressionResolution Failed(string expression, string error) =>
            new(expression, null, error);
    }
}
