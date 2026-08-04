using System.Text.RegularExpressions;

namespace Panko.Api.Signatures;

public sealed partial class SignatureNormalizer
{
    public const int MaximumFeatureLength = 320;

    public string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var value = input.Normalize().ToLowerInvariant();
        value = SecretAssignment().Replace(value, "$1=<redacted>");
        value = BearerToken().Replace(value, "bearer <redacted>");
        value = IsoTimestamp().Replace(value, "<timestamp>");
        value = CommonTimestamp().Replace(value, "<timestamp>");
        value = Uuid().Replace(value, "<id>");
        value = IpWithPort().Replace(value, match => $"{NormalizeIp(match.Groups[1].Value)}:<port>");
        value = Ipv4().Replace(value, "<ip>");
        value = Ipv6().Replace(value, "<ip>");
        value = Duration().Replace(value, "<duration>");
        value = NamedId().Replace(value, "$1 <id>");
        value = PrefixedId().Replace(value, "<id>");
        value = LongHex().Replace(value, "<id>");
        value = QueryString().Replace(value, "?<query>");
        value = DynamicPathSegment().Replace(value, "/<id>");
        value = HttpStatus().Replace(value, match => $"<{match.Groups[1].Value[0]}xx>");
        value = NumericTimestamp().Replace(value, "<timestamp>");
        value = Number().Replace(value, "<count>");
        value = Whitespace().Replace(value, " ").Trim();
        return value.Length <= MaximumFeatureLength ? value : value[..MaximumFeatureLength];
    }

    public string NormalizeCodeLocation(string projectId, string path, string? member = null)
    {
        var safeProject = SafeIdentifier(projectId);
        var safePath = SafePath(path);
        var safeMember = string.IsNullOrWhiteSpace(member) ? null : SafeIdentifier(member);
        return Truncate(string.Join(':', new[] { safeProject, safePath, safeMember }.Where(value => !string.IsNullOrEmpty(value))));
    }

    public static string SafeIdentifier(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "unknown";
        var value = input.Trim().ToLowerInvariant();
        value = UnsafeIdentifierCharacter().Replace(value, "-").Trim('-');
        return Truncate(string.IsNullOrEmpty(value) ? "unknown" : value);
    }

    private static string SafePath(string input)
    {
        var value = input.Replace('\\', '/').Trim().ToLowerInvariant();
        value = UnsafePathCharacter().Replace(value, "-");
        return Truncate(value);
    }

    private static string NormalizeIp(string value) => value.Contains(':', StringComparison.Ordinal) ? "<ip>" : "<ip>";
    private static string Truncate(string value) => value.Length <= MaximumFeatureLength ? value : value[..MaximumFeatureLength];

    [GeneratedRegex(@"(?i)\b(api[_-]?key|authorization|credential|password|passwd|secret|token|connection[_-]?string)\s*[:=]\s*[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();
    [GeneratedRegex(@"(?i)\bbearer\s+[a-z0-9._~+/-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();
    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}[t ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:z|[+-]\d{2}:?\d{2})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestamp();
    [GeneratedRegex(@"\b(?:\d{1,2}[/.-]){2}\d{2,4}[ t]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommonTimestamp();
    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();
    [GeneratedRegex(@"(?<![\w:])((?:\d{1,3}\.){3}\d{1,3}|\[[0-9a-f:]+\]|[0-9a-f]*:[0-9a-f:]+):(\d{2,5})\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpWithPort();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();
    [GeneratedRegex(@"(?<![\w:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6();
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:ns|us|µs|ms|milliseconds?|s|sec(?:onds?)?|m|min(?:utes?)?|h|hours?|d|days?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();
    [GeneratedRegex(@"\b(request|trace|span|allocation|alloc|job[- ]?instance|order|resource)[-_ ]?(?:id\s*[:=]?\s*)?[a-z0-9._:-]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NamedId();
    [GeneratedRegex(@"\b(?:ord|req|trace|span|alloc|job|res)[_-][a-z0-9_-]{4,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedId();
    [GeneratedRegex(@"\b[0-9a-f]{7,64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();
    [GeneratedRegex(@"\?[^\s#]*", RegexOptions.CultureInvariant)]
    private static partial Regex QueryString();
    [GeneratedRegex(@"/(?=[^\s/?#]+)(?:[a-z_-]*\d[a-z0-9_.-]*|\d{2,})(?=/|\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicPathSegment();
    [GeneratedRegex(@"\b([1-5])\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HttpStatus();
    [GeneratedRegex(@"\b(?:1[6-9]\d{8}|2\d{9,12})\b", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTimestamp();
    [GeneratedRegex(@"(?<![\w<>])\d+(?:\.\d+)?(?![\w>])", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"[^a-z0-9._/-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeIdentifierCharacter();
    [GeneratedRegex(@"[^a-z0-9._/@+-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafePathCharacter();
}
