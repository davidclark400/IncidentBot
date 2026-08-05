using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Panko.Api.Mcp;

public static class McpServerEndpoints
{
    public const string Route = "/api/mcp";

    public static IMcpServerBuilder AddCaseMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<McpToolRouter>();

        return services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "panko",
                    Version = typeof(McpServerEndpoints).Assembly.GetName().Version?.ToString()
                        ?? "1.0.0"
                };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithTools<CaseMcpTools>(McpServerJson.Options);
    }

    public static IEndpointConventionBuilder MapCaseMcp(
        this IEndpointRouteBuilder endpoints) => endpoints.MapMcp(Route);
}

internal static class McpServerJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
