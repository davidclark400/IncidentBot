using System.ComponentModel.DataAnnotations;

namespace IncidentBot.Api.Options;

public sealed class IncidentBotOptions
{
    public const string SectionName = "IncidentBot";

    [Required]
    public string ProfilesPath { get; init; } = "config/investigation-profiles.yaml";
    [Required]
    public string PublicBaseUrl { get; init; } = "http://localhost:5173";
    [Range(1, 240)]
    public int EvidenceWindowMinutes { get; init; } = 30;
    [Range(1, 1440)]
    public int EvidenceMaximumWindowMinutes { get; init; } = 240;
    [Range(25, 1000)]
    public int EvidenceMaximumItems { get; init; } = 250;
    [Range(65536, 4194304)]
    public int EvidenceMaximumBytes { get; init; } = 1048576;
    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;
    [Range(1, 3650)]
    public int FingerprintRetentionDays { get; init; } = 365;
    [Range(1, 100)]
    public int FingerprintAutomaticThreshold { get; init; } = 80;
    [Range(1, 100)]
    public int FingerprintPossibleThreshold { get; init; } = 60;
    [Range(1, 3650)]
    public int FingerprintCandidateLookbackDays { get; init; } = 365;
    [Range(1, 500)]
    public int FingerprintMaximumCandidates { get; init; } = 100;
    [Range(2, 100)]
    public int FingerprintEscalationCount { get; init; } = 3;
    [Range(1, 90)]
    public int FingerprintEscalationWindowDays { get; init; } = 7;
    [Range(0, 100)]
    public int FingerprintErrorTemplateWeight { get; init; } = 35;
    [Range(0, 100)]
    public int FingerprintCodeLocationWeight { get; init; } = 25;
    [Range(0, 100)]
    public int FingerprintComponentWeight { get; init; } = 15;
    [Range(0, 100)]
    public int FingerprintSymptomWeight { get; init; } = 15;
    [Range(0, 100)]
    public int FingerprintTitleWeight { get; init; } = 10;
    public bool CollectionEnabled { get; init; } = true;
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
    public Dictionary<string, string> PromptChannelProfiles { get; init; } = [];
}

public sealed class LiteLlmOptions
{
    public const string SectionName = "LiteLlm";
    [Required, Url]
    public string BaseUrl { get; init; } = "http://litellm.internal:4000";
    [Required]
    public string Model { get; init; } = "incident-summary";
    [Required]
    public string QueryPlannerModel { get; init; } = "incident-query-planner";
    [Required]
    public string ApiKeyEnv { get; init; } = "LITELLM_API_KEY";
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 20;
    [Range(256, 32000)]
    public int InputCharacterBudget { get; init; } = 24000;
    [Range(64, 4096)]
    public int MaxOutputTokens { get; init; } = 1000;
}

public sealed class IngressIdentityOptions
{
    public const string SectionName = "IngressIdentity";
    public bool Required { get; init; }
    [Required]
    public string HeaderName { get; init; } = "X-Forwarded-User";
}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";
    public bool Enabled { get; init; }
    [Range(1, 30)]
    public int StepDelaySeconds { get; init; } = 2;
}
