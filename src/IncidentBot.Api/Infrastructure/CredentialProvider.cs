namespace IncidentBot.Api.Infrastructure;

public interface ICredentialProvider
{
    string? Get(string environmentVariableName);
}

public sealed class EnvironmentCredentialProvider : ICredentialProvider
{
    public string? Get(string environmentVariableName) =>
        string.IsNullOrWhiteSpace(environmentVariableName)
            ? null
            : Environment.GetEnvironmentVariable(environmentVariableName);
}
