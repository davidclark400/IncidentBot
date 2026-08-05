namespace Panko.Api.Security;

public static class TeamKey
{
    public const string Unmapped = "unmapped";

    public static bool IsCanonical(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && !string.Equals(value, Unmapped, StringComparison.Ordinal)
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character =>
            char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character == '-');
}
