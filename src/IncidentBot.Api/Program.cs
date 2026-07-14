using IncidentBot.Api.Connectors;
using IncidentBot.Contracts;
using IncidentBot.Api.Demo;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Hubs;
using IncidentBot.Api.Fingerprinting;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Infrastructure;
using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using IncidentBot.Api.Security;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Npgsql;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var generatingOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

builder.Services.AddOptions<IncidentBotOptions>()
    .Bind(builder.Configuration.GetSection(IncidentBotOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => value.EvidenceMaximumWindowMinutes >= value.EvidenceWindowMinutes,
        "EvidenceMaximumWindowMinutes must be at least EvidenceWindowMinutes.")
    .Validate(value => value.FingerprintPossibleThreshold < value.FingerprintAutomaticThreshold,
        "FingerprintPossibleThreshold must be lower than FingerprintAutomaticThreshold.")
    .Validate(value => value.FingerprintRetentionDays >= value.RetentionDays,
        "FingerprintRetentionDays must be at least RetentionDays.")
    .Validate(value => value.FingerprintErrorTemplateWeight + value.FingerprintCodeLocationWeight
        + value.FingerprintComponentWeight + value.FingerprintSymptomWeight + value.FingerprintTitleWeight > 0,
        "At least one fingerprint similarity weight must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<PagerDutyOptions>()
    .Bind(builder.Configuration.GetSection(PagerDutyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => CredentialVariableName.IsValid(value.WebhookSecretEnv),
        "PagerDuty:WebhookSecretEnv must be a valid environment-variable name.")
    .ValidateOnStart();
builder.Services.AddOptions<EvidenceSourceOptions>()
    .Bind(builder.Configuration.GetSection(EvidenceSourceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<EvidenceSourceOptions>, EvidenceSourceOptionsValidator>();
builder.Services.AddOptions<SlackOptions>()
    .Bind(builder.Configuration.GetSection(SlackOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => CredentialVariableName.IsValid(value.BotTokenEnv)
        && CredentialVariableName.IsValid(value.AppTokenEnv),
        "Slack credential settings must be valid environment-variable names.")
    .Validate(value => !value.Enabled ||
            Uri.TryCreate(value.ApiBaseUrl, UriKind.Absolute, out var slackApi) &&
            slackApi.Scheme == Uri.UriSchemeHttps,
        "Slack:ApiBaseUrl must use HTTPS when Slack is enabled.")
    .Validate(value => !value.PromptMentionsEnabled || value.Enabled,
        "Slack prompt mentions require Slack to be enabled.")
    .Validate(value => !value.PromptMentionsEnabled || value.PromptChannelProfiles.Count > 0,
        "Slack prompt mentions require at least one channel-to-profile mapping.")
    .Validate(value => value.PromptChannelProfiles.All(mapping =>
            !string.IsNullOrWhiteSpace(mapping.Key) && !string.IsNullOrWhiteSpace(mapping.Value)),
        "Slack prompt channel and profile identifiers must not be blank.")
    .Validate(value => value.PromptRequestsPerMinutePerUser <= value.PromptRequestsPerMinute,
        "Slack per-user prompt rate must not exceed the global prompt rate.")
    .ValidateOnStart();
builder.Services.AddOptions<LiteLlmOptions>()
    .Bind(builder.Configuration.GetSection(LiteLlmOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => CredentialVariableName.IsValid(value.ApiKeyEnv),
        "LiteLlm:ApiKeyEnv must be a valid environment-variable name.")
    .ValidateOnStart();
builder.Services.AddOptions<IngressIdentityOptions>()
    .Bind(builder.Configuration.GetSection(IngressIdentityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DemoOptions>()
    .Bind(builder.Configuration.GetSection(DemoOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var demoEnabled = generatingOpenApi || builder.Configuration.GetValue<bool>($"{DemoOptions.SectionName}:Enabled");
if (demoEnabled && !generatingOpenApi && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Demo mode may only be enabled in the Development environment.");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EvidenceSourceConfiguration>();
builder.Services.AddSingleton<ICredentialProvider, EnvironmentCredentialProvider>();
builder.Services.AddHttpClient();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 16 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer(async (document, context, cancellationToken) =>
    {
        foreach (var contractType in new[] { typeof(IncidentUpdated), typeof(IncidentStatusChanged) })
        {
            var schema = await context.GetOrCreateSchemaAsync(contractType, null, cancellationToken);
            document.AddComponent(contractType.Name, schema);
        }
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IIncidentUpdatePublisher, SignalRIncidentUpdatePublisher>();

if (demoEnabled)
{
    builder.Services.AddSingleton<DemoIncidentStore>();
    builder.Services.AddSingleton<IIncidentReportReader>(services => services.GetRequiredService<DemoIncidentStore>());
    builder.Services.AddSingleton<IPagerDutyPullService, DemoPagerDutyPullService>();
    if (!generatingOpenApi)
    {
        builder.Services.AddHostedService<DemoIncidentWorker>();
    }
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("IncidentBot")
        ?? throw new InvalidOperationException("ConnectionStrings:IncidentBot is required.");
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<InvestigationProfileStore>();
    builder.Services.AddSingleton<KafkaMetricPackStore>();
    builder.Services.AddSingleton<DeploymentReadinessChecker>();
    builder.Services.AddSingleton<PagerDutySignatureValidator>();
    builder.Services.AddSingleton<PagerDutyIncidentClient>();
    builder.Services.AddSingleton<IPagerDutyPullService, PagerDutyPullService>();
    builder.Services.AddSingleton<SafeTemplateRenderer>();
    builder.Services.AddSingleton<McpStreamableHttpClient>();
    builder.Services.AddSingleton<IMcpEvidenceAdapter>(services => services.GetRequiredService<McpStreamableHttpClient>());
    builder.Services.AddIncidentEvidenceSources();
    builder.Services.AddSingleton<IIncidentStore, IncidentRepository>();
    builder.Services.AddSingleton<DurableQueueRepository>();
    builder.Services.AddSingleton<IDurableQueue<WorkItem>>(services => services.GetRequiredService<DurableQueueRepository>());
    builder.Services.AddSingleton<IDurableQueue<OutboxItem>>(services => services.GetRequiredService<DurableQueueRepository>());
    builder.Services.AddSingleton<AdaptiveEvidenceCollector>();
    builder.Services.AddSingleton<ReportComposer>();
    builder.Services.AddIncidentRecurrence();
    builder.Services.AddSingleton<LiteLlmSynthesizer>();
    builder.Services.AddSingleton<IInvestigationSynthesizer>(services => services.GetRequiredService<LiteLlmSynthesizer>());
    builder.Services.AddSingleton<IInvestigationProfileProvider>(services => services.GetRequiredService<InvestigationProfileStore>());
    builder.Services.AddSingleton<ISlackQueryProfileProvider>(services => services.GetRequiredService<InvestigationProfileStore>());
    builder.Services.AddSingleton<IIncidentReportReader, RepositoryIncidentReportReader>();
    builder.Services.AddSingleton<InvestigationRunner>();
    builder.Services.AddSingleton<InvestigationRunRegistry>();
    builder.Services.AddSingleton<InvestigationRestartService>();
    builder.Services.AddSingleton<SlackPublisher>();
    builder.Services.AddSingleton<SlackInteractiveHandler>();
    builder.Services.AddSingleton<SlackQueryPlanCompiler>();
    builder.Services.AddSingleton<ISlackQueryPlanner, LiteLlmSlackQueryPlanner>();
    builder.Services.AddSingleton<SlackPromptInvestigator>();
    builder.Services.AddSingleton<ISlackReplyPublisher, SlackReplyPublisher>();
    builder.Services.AddSingleton<SlackMentionHandler>();

    builder.Services.AddHostedService<DatabaseInitializer>();
    builder.Services.AddHostedService<DataSourceConnectivityChecker>();
    builder.Services.AddHostedService<InvestigationWorker>();
    builder.Services.AddHostedService<OutboxWorker>();
    builder.Services.AddHostedService<SlackSocketModeWorker>();
    builder.Services.AddHostedService<RetentionWorker>();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.UseMiddleware<IngressIdentityMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
if (demoEnabled)
{
    app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", mode = "demo" }));
    app.MapDemoIncidentApi();
}
else
{
    app.MapGet("/health/ready", async (
        NpgsqlDataSource dataSource,
        InvestigationProfileStore profiles,
        DeploymentReadinessChecker readiness,
        CancellationToken ct) =>
    {
        await using var command = dataSource.CreateCommand("select 1");
        await command.ExecuteScalarAsync(ct);
        var production = readiness.CheckProduction();
        if (!app.Environment.IsDevelopment() && !production.Ready)
        {
            return Results.Json(new
            {
                status = "not_ready",
                profileRevision = profiles.Revision,
                productionPreflight = production
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new
        {
            status = "ready",
            profileRevision = profiles.Revision,
            productionPreflight = production
        });
    });
    app.MapPagerDutyWebhook();
}
app.MapIncidentApi();
app.MapPagerDutyPullApi();
app.MapHub<IncidentHub>("/hubs/incidents");
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
