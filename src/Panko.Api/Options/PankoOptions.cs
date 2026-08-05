using System.ComponentModel.DataAnnotations;

namespace Panko.Api.Options;

public sealed class PankoOptions
{
    public const string SectionName = "Panko";

    [Required]
    public string RecipesPath { get; init; } = "config/recipes.yaml";
    [Required]
    public string KafkaMetricPacksPath { get; init; } = "config/kafka-metric-packs.yaml";
    [Required]
    public string ServiceMetricPacksPath { get; init; } = "config/service-metric-packs.yaml";
    [Required]
    public string PublicBaseUrl { get; init; } = "http://localhost:5173";
    [Range(1, 240)]
    public int CrumbWindowMinutes { get; init; } = 30;
    [Range(1, 1440)]
    public int CrumbMaximumWindowMinutes { get; init; } = 240;
    [Range(0, 1440)]
    public int CrumbPostResolutionWindowMinutes { get; init; } = 30;
    [Range(25, 1000)]
    public int CrumbMaximumItems { get; init; } = 250;
    [Range(65536, 4194304)]
    public int CrumbMaximumBytes { get; init; } = 1048576;
    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;
    [Range(1, 3650)]
    public int SignatureRetentionDays { get; init; } = 365;
    [Range(1, 100)]
    public int SignatureAutomaticThreshold { get; init; } = 80;
    [Range(1, 100)]
    public int SignaturePossibleThreshold { get; init; } = 60;
    [Range(1, 3650)]
    public int SignatureCandidateLookbackDays { get; init; } = 365;
    [Range(1, 500)]
    public int SignatureMaximumCandidates { get; init; } = 100;
    [Range(2, 100)]
    public int SignatureEscalationCount { get; init; } = 3;
    [Range(1, 90)]
    public int SignatureEscalationWindowDays { get; init; } = 7;
    [Range(0, 100)]
    public int SignatureErrorTemplateWeight { get; init; } = 35;
    [Range(0, 100)]
    public int SignatureCodeLocationWeight { get; init; } = 25;
    [Range(0, 100)]
    public int SignatureComponentWeight { get; init; } = 15;
    [Range(0, 100)]
    public int SignatureSymptomWeight { get; init; } = 15;
    [Range(0, 100)]
    public int SignatureTitleWeight { get; init; } = 10;
    public bool CrumbCollectionEnabled { get; init; } = true;
    public bool McpEnabled { get; init; } = true;
    public bool RequireSlackForReadiness { get; init; }

}

public sealed class PagerDutyOptions
{
    public const string SectionName = "PagerDuty";
    [Required]
    public string WebhookSecretEnv { get; init; } = "PAGERDUTY_WEBHOOK_SECRET";
    [Range(1024, 1048576)]
    public int MaximumWebhookPayloadBytes { get; init; } = 262144;
    [Range(1, 120)]
    public int PullTimeoutSeconds { get; init; } = 15;
    [Range(65536, 4194304)]
    public int MaximumApiResponseBytes { get; init; } = 1048576;
    [Range(1, 100)]
    public int MaximumRecentIncidents { get; init; } = 100;
    [Range(1, 90)]
    public int MaximumLookbackDays { get; init; } = 30;
    public bool RequireSignature { get; init; } = true;
}

public sealed class SlackOptions
{
    public const string SectionName = "Slack";
    public bool Enabled { get; init; }
    public bool PromptMentionsEnabled { get; init; }
    [Required, Url]
    public string ApiBaseUrl { get; init; } = "https://slack.com/api";
    [Required]
    public string BotTokenEnv { get; init; } = "SLACK_BOT_TOKEN";
    [Required]
    public string AppTokenEnv { get; init; } = "SLACK_APP_TOKEN";
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 20;
    [Range(1, 60)]
    public int ReconnectDelaySeconds { get; init; } = 5;
    [Range(1024, 1048576)]
    public int MaximumEnvelopeBytes { get; init; } = 262144;
    [Range(1, 4000)]
    public int MaximumPromptCharacters { get; init; } = 2000;
    [Range(1, 1000)]
    public int PromptQueueCapacity { get; init; } = 32;
    [Range(1, 4)]
    public int PromptWorkerCount { get; init; } = 1;
    [Range(1, 60)]
    public int PromptRequestsPerMinutePerUser { get; init; } = 6;
    [Range(1, 600)]
    public int PromptRequestsPerMinute { get; init; } = 30;
    [Range(5, 300)]
    public int PromptTimeoutSeconds { get; init; } = 90;
    public bool AllowExternalSharedChannels { get; init; }
    public Dictionary<string, string> ChannelTeams { get; init; } = [];
    public Dictionary<string, string> PromptChannelRecipes { get; init; } = [];

}

public sealed class LiteLlmOptions
{
    public const string SectionName = "LiteLlm";
    [Required, Url]
    public string BaseUrl { get; init; } = "http://litellm.internal:4000";
    [Required]
    public string Model { get; init; } = "panko-case-summary";
    [Required]
    public string QueryPlannerModel { get; init; } = "panko-case-query-planner";
    [Required]
    public string ApiKeyEnv { get; init; } = "LITELLM_API_KEY";
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 20;
    [Range(256, 32000)]
    public int InputCharacterBudget { get; init; } = 24000;
    [Range(64, 4096)]
    public int MaxOutputTokens { get; init; } = 1000;
}

public sealed class JwtIdentityOptions
{
    public const string SectionName = "JwtIdentity";
    public bool Required { get; init; } = true;
    [Required]
    public string Authority { get; init; } = "https://identity.example";
    [Required]
    public string Issuer { get; init; } = "https://identity.example";
    [Required]
    public string Audience { get; init; } = "panko";
    [Required]
    public string NameClaimType { get; init; } = "preferred_username";
    public bool RequireHttpsMetadata { get; init; } = true;
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 60;
}

public sealed class TeamAuthorizationOptions
{
    public const string SectionName = "TeamAuthorization";
    public List<string> TeamClaimTypes { get; init; } = ["panko:team"];
    public List<string> GroupClaimTypes { get; init; } = ["groups"];
    public Dictionary<string, string> GroupTeamMappings { get; init; } = [];
}

public sealed class TrustedProxyOptions
{
    public const string SectionName = "TrustedProxies";
    [Range(1, 10)]
    public int ForwardLimit { get; init; } = 1;
    public List<string> KnownProxies { get; init; } = [];
    public List<string> KnownNetworks { get; init; } = [];
}

public sealed class CaseOptions
{
    public const string SectionName = "Cases";

    [Range(1, 500)]
    public int MaximumInputsPerBatch { get; init; } = 100;
    [Range(1024, 4 * 1024 * 1024)]
    public int MaximumRequestBytes { get; init; } = 256 * 1024;
    [Range(32, 4000)]
    public int MaximumSummaryCharacters { get; init; } = 1000;
    [Range(0, 32000)]
    public int MaximumExcerptCharacters { get; init; } = 8000;
    [Range(128, 256 * 1024)]
    public int MaximumAttributesBytes { get; init; } = 16 * 1024;
    [Range(1, 1_000_000)]
    public int MaximumInputsPerCase { get; init; } = 10_000;
    [Range(1, 24 * 3650)]
    public int MaximumTimestampDistanceHours { get; init; } = 24 * 30;
    [Range(1, 16)]
    public int MaximumAttributesDepth { get; init; } = 6;
    [Range(1024, 1024 * 1024)]
    public int MaximumMcpResponseBytes { get; init; } = 64 * 1024;

}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";
    public bool Enabled { get; init; }
    [Range(1, 30)]
    public int StepDelaySeconds { get; init; } = 2;
}
