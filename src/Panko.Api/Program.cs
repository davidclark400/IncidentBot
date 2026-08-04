using Panko.Api.Crumbs;
using Panko.Contracts;
using Panko.Api.Demo;
using Panko.Api.Domain;
using Panko.Api.Hubs;
using Panko.Api.Patterns;
using Panko.Api.Signatures;
using Panko.Api.Cases;
using Panko.Api.Infrastructure;
using Panko.Api.Mcp;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Npgsql;
using OpenTelemetry.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var generatingOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

builder.Services.AddOptions<PankoOptions>()
    .Bind(builder.Configuration.GetSection(PankoOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => value.CrumbMaximumWindowMinutes >= value.CrumbWindowMinutes,
        "CrumbMaximumWindowMinutes must be at least CrumbWindowMinutes.")
    .Validate(value => value.SignaturePossibleThreshold < value.SignatureAutomaticThreshold,
        "SignaturePossibleThreshold must be lower than SignatureAutomaticThreshold.")
    .Validate(value => value.SignatureRetentionDays >= value.RetentionDays,
        "SignatureRetentionDays must be at least RetentionDays.")
    .Validate(value => value.SignatureErrorTemplateWeight + value.SignatureCodeLocationWeight
        + value.SignatureComponentWeight + value.SignatureSymptomWeight + value.SignatureTitleWeight > 0,
        "At least one Signature similarity weight must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<PagerDutyOptions>()
    .Bind(builder.Configuration.GetSection(PagerDutyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => CredentialVariableName.IsValid(value.WebhookSecretEnv),
        "PagerDuty:WebhookSecretEnv must be a valid environment-variable name.")
    .ValidateOnStart();
builder.Services.AddOptions<CrumbSourceOptions>()
    .Bind(builder.Configuration.GetSection(CrumbSourceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CrumbSourceOptions>, CrumbSourceOptionsValidator>();
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
    .Validate(value => !value.PromptMentionsEnabled || value.PromptChannelRecipes.Count > 0,
        "Slack prompt mentions require at least one channel-to-Recipe mapping.")
    .Validate(value => value.PromptChannelRecipes.All(mapping =>
            !string.IsNullOrWhiteSpace(mapping.Key) && !string.IsNullOrWhiteSpace(mapping.Value)),
        "Slack prompt channel and Recipe identifiers must not be blank.")
    .Validate(value => value.ChannelTeams.All(mapping =>
            !string.IsNullOrWhiteSpace(mapping.Key) && TeamKey.IsCanonical(mapping.Value)),
        "Slack channel-to-team mappings must use non-blank channel IDs and canonical team keys.")
    .Validate(value => !value.PromptMentionsEnabled
            || value.PromptChannelRecipes.Keys.All(channelId =>
                value.ChannelTeams.ContainsKey(channelId)),
        "Every Slack prompt channel must also have an explicit channel-to-team mapping.")
    .Validate(value => value.PromptRequestsPerMinutePerUser <= value.PromptRequestsPerMinute,
        "Slack per-user prompt rate must not exceed the global prompt rate.")
    .ValidateOnStart();
builder.Services.AddOptions<LiteLlmOptions>()
    .Bind(builder.Configuration.GetSection(LiteLlmOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => CredentialVariableName.IsValid(value.ApiKeyEnv),
        "LiteLlm:ApiKeyEnv must be a valid environment-variable name.")
    .ValidateOnStart();
builder.Services.AddOptions<JwtIdentityOptions>()
    .Bind(builder.Configuration.GetSection(JwtIdentityOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => Uri.TryCreate(value.Authority, UriKind.Absolute, out var authority)
            && authority.Scheme == Uri.UriSchemeHttps,
        "JwtIdentity:Authority must be an absolute HTTPS URL.")
    .Validate(value => Uri.TryCreate(value.Issuer, UriKind.Absolute, out var issuer)
            && issuer.Scheme == Uri.UriSchemeHttps,
        "JwtIdentity:Issuer must be an absolute HTTPS URL.")
    .Validate(value => !string.IsNullOrWhiteSpace(value.Audience)
            && !string.IsNullOrWhiteSpace(value.NameClaimType),
        "JWT audience and name claim type must not be blank.")
    .ValidateOnStart();
builder.Services.AddOptions<TeamAuthorizationOptions>()
    .Bind(builder.Configuration.GetSection(TeamAuthorizationOptions.SectionName))
    .Validate(value => value.TeamClaimTypes.Concat(value.GroupClaimTypes)
            .All(claimType => !string.IsNullOrWhiteSpace(claimType)),
        "Team and group claim types must not be blank.")
    .Validate(value => value.TeamClaimTypes.Count > 0
            || (value.GroupClaimTypes.Count > 0 && value.GroupTeamMappings.Count > 0),
        "Team authorization requires a direct team claim type or a group claim type with at least one mapping.")
    .Validate(value => value.GroupTeamMappings.All(mapping =>
            !string.IsNullOrWhiteSpace(mapping.Key)
            && TeamKey.IsCanonical(mapping.Value)),
        "Directory group mappings must use non-blank group IDs and canonical lowercase team keys.")
    .ValidateOnStart();
builder.Services.AddOptions<TrustedProxyOptions>()
    .Bind(builder.Configuration.GetSection(TrustedProxyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(TrustedProxyConfiguration.IsValid,
        "Trusted proxy entries must be explicit non-catch-all IP addresses or CIDR networks.")
    .ValidateOnStart();
builder.Services.AddOptions<CaseOptions>()
    .Bind(builder.Configuration.GetSection(CaseOptions.SectionName))
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

var jwtIdentity = builder.Configuration
    .GetSection(JwtIdentityOptions.SectionName)
    .Get<JwtIdentityOptions>() ?? new JwtIdentityOptions();
if (!jwtIdentity.Required && !generatingOpenApi && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "JWT authentication may only be disabled in the Development environment.");
}
if (!jwtIdentity.RequireHttpsMetadata && !generatingOpenApi && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "OIDC discovery metadata must use HTTPS outside the Development environment.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = jwtIdentity.Authority;
        options.Audience = jwtIdentity.Audience;
        options.RequireHttpsMetadata = jwtIdentity.RequireHttpsMetadata;
        options.MapInboundClaims = false;
        options.SaveToken = false;
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIdentity.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtIdentity.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(jwtIdentity.ClockSkewSeconds),
            NameClaimType = jwtIdentity.NameClaimType,
            RoleClaimType = "roles"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrWhiteSpace(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments(
                        "/hubs/cases", StringComparison.Ordinal))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Principal?.FindFirst("sub")?.Value))
                {
                    context.Fail("The access token must contain a non-empty subject claim.");
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CrumbSourceConfiguration>();
builder.Services.AddSingleton<ICredentialProvider, EnvironmentCredentialProvider>();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 16 * 1024)
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer(async (document, context, cancellationToken) =>
    {
        foreach (var contractType in new[] { typeof(CaseUpdated), typeof(CaseStatusChanged) })
        {
            var schema = await context.GetOrCreateSchemaAsync(contractType, null, cancellationToken);
            document.AddComponent(contractType.Name, schema);
        }
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<ICaseUpdatePublisher, SignalRCaseUpdatePublisher>();
builder.Services.AddSingleton<ICaseAuthorization, CaseAuthorization>();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(CaseTelemetry.MeterName)
        .AddOtlpExporter());

if (demoEnabled)
{
    builder.Services.AddSingleton<DemoCaseStore>();
    builder.Services.AddSingleton<DemoReplay>();
    builder.Services.AddSingleton<ICaseFileReader>(services => services.GetRequiredService<DemoCaseStore>());
    builder.Services.AddSingleton<ICaseProgressReader>(services => services.GetRequiredService<DemoCaseStore>());
    builder.Services.AddSingleton<IPagerDutyPullService, DemoPagerDutyPullService>();
    builder.Services.AddSingleton<IRecipeOwnershipCatalog, DemoRecipeOwnershipCatalog>();
    builder.Services.AddSingleton<ISecurityAuditTrail, LoggingSecurityAuditTrail>();
    if (!generatingOpenApi)
    {
        builder.Services.AddHostedService<DemoCaseWorker>();
    }
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("Panko")
        ?? throw new InvalidOperationException("ConnectionStrings:Panko is required.");
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<RecipeStore>();
    builder.Services.AddSingleton<IRecipeOwnershipCatalog>(services =>
        services.GetRequiredService<RecipeStore>());
    builder.Services.AddSingleton<KafkaMetricPlanStore>();
    builder.Services.AddSingleton<ServiceMetricPlanStore>();
    builder.Services.AddSingleton<DeploymentReadinessChecker>();
    builder.Services.AddSingleton<PagerDutySignatureValidator>();
    builder.Services.AddSingleton<PagerDutyIncidentClient>();
    builder.Services.AddSingleton<IPagerDutyPullService, PagerDutyPullService>();
    builder.Services.AddSingleton<SafeTemplateRenderer>();
    builder.Services.AddSingleton<McpCrumbSourceClient>();
    builder.Services.AddSingleton<IMcpCrumbSourceAdapter>(services => services.GetRequiredService<McpCrumbSourceClient>());
    builder.Services.AddCrumbSources();
    builder.Services.AddSingleton<ICaseStore, PostgresCaseStore>();
    builder.Services.AddSingleton<ICaseAdmission, CaseAdmission>();
    builder.Services.AddSingleton<PagerDutyCaseAdapter>();
    builder.Services.AddSingleton<DurableQueueRepository>();
    builder.Services.AddSingleton<IDurableQueue<WorkItem>>(services => services.GetRequiredService<DurableQueueRepository>());
    builder.Services.AddSingleton<IDurableQueue<OutboxItem>>(services => services.GetRequiredService<DurableQueueRepository>());
    builder.Services.AddSingleton<AdaptiveCrumbCollector>();
    builder.Services.AddSingleton<CaseFileComposer>();
    builder.Services.AddSingleton<CaseFileTransitions>();
    builder.Services.AddPatternMatching();
    builder.Services.AddSingleton<LiteLlmSynthesizer>();
    builder.Services.AddSingleton<ICaseFileSynthesizer>(services => services.GetRequiredService<LiteLlmSynthesizer>());
    builder.Services.AddSingleton<IRecipeProvider>(services => services.GetRequiredService<RecipeStore>());
    builder.Services.AddSingleton<ISlackQueryRecipeProvider>(services => services.GetRequiredService<RecipeStore>());
    builder.Services.AddSingleton<RepositoryCaseFileReader>();
    builder.Services.AddSingleton<ICaseFileReader>(services => services.GetRequiredService<RepositoryCaseFileReader>());
    builder.Services.AddSingleton<ICaseProgressReader>(services => services.GetRequiredService<RepositoryCaseFileReader>());
    builder.Services.AddSingleton<CaseFileBuilder>();
    builder.Services.AddSingleton<CaseRunRegistry>();
    builder.Services.AddSingleton<CaseRebuildService>();
    builder.Services.AddSingleton<SlackPublisher>();
    builder.Services.AddSingleton<SlackInteractiveHandler>();
    builder.Services.AddSingleton<SlackQueryPlanCompiler>();
    builder.Services.AddSingleton<ISlackQueryPlanner, LiteLlmSlackQueryPlanner>();
    builder.Services.AddSingleton<SlackCaseQueryRunner>();
    builder.Services.AddSingleton<ISlackReplyPublisher, SlackReplyPublisher>();
    builder.Services.AddSingleton<SlackMentionHandler>();
    builder.Services.AddSingleton<ISecurityAuditTrail, PostgresSecurityAuditTrail>();
    builder.Services.AddSingleton<ICaseInputStore, PostgresCaseInputStore>();
    builder.Services.AddSingleton<CaseInputBoundary>();
    builder.Services.AddSingleton<CaseFileProjectionBuilder>();
    builder.Services.AddSingleton<ICaseCommands, CaseCommands>();
    builder.Services.AddSingleton<ICaseQueries, CaseQueries>();
    builder.Services.AddSingleton<CaseWorkHandler>();
    builder.Services.AddSingleton<CaseTelemetry>();
    builder.Services.AddCaseMcp();

    builder.Services.AddHostedService<DatabaseInitializer>();
    builder.Services.AddHostedService<DataSourceConnectivityChecker>();
    builder.Services.AddHostedService<CaseWorker>();
    builder.Services.AddHostedService<OutboxWorker>();
    builder.Services.AddHostedService<SlackSocketModeWorker>();
    builder.Services.AddHostedService<RetentionWorker>();
}

builder.Services.AddSingleton<ITeamAuthorization, TeamAuthorization>();
builder.Services.AddSingleton<ICaseAccessAuthorizer, CaseAccessAuthorizer>();
builder.Services.AddSingleton<OperationsCatalogBrowser>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders(TrustedProxyConfiguration.Create(
    app.Services.GetRequiredService<IOptions<TrustedProxyOptions>>().Value));
app.UseAuthentication();
if (!jwtIdentity.Required && app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Development open access is enabled; requests receive a local identity with unrestricted team and case permissions");
    app.Use(async (context, next) =>
    {
        context.User = DevelopmentOpenAccessIdentity.CreatePrincipal();
        await next();
    });
}
app.UseAuthorization();
app.UseMiddleware<CaseRequestSizeMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
if (demoEnabled)
{
    app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", mode = "demo" }));
    app.MapDemoCaseApi();
}
else
{
    app.MapGet("/health/ready", async (
        NpgsqlDataSource dataSource,
        RecipeStore recipes,
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
                recipeRevision = recipes.Revision,
                productionPreflight = production
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new
        {
            status = "ready",
            recipeRevision = recipes.Revision,
            productionPreflight = production
        });
    });
    app.MapPagerDutyWebhook();
}

var protectedEndpoints = app.MapGroup(string.Empty);
protectedEndpoints.MapOperationsCatalogApi();
protectedEndpoints.MapCaseApi();
protectedEndpoints.MapPagerDutyPullApi();
if (!demoEnabled || generatingOpenApi)
{
    protectedEndpoints.MapCaseManagementApi();
}
protectedEndpoints.RequireAuthorization();

var caseHub = app.MapHub<CaseHub>(
    "/hubs/cases",
    options => options.CloseOnAuthenticationExpiration = true);
caseHub.RequireAuthorization();
if (!demoEnabled)
{
    var mcp = app.MapCaseMcp();
    mcp.RequireAuthorization();
}
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
