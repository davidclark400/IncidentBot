namespace Panko.Api.Domain;

public static class ServiceCollectionKey
{
    public const string Default = "uncategorized";

    public static bool IsCanonical(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character =>
            char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character == '-');
}
