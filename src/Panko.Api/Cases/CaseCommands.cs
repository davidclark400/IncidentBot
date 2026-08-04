using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Api.Recipes;
using Panko.Api.Security;
using Microsoft.Extensions.Options;
using SubmittedCrumbKind = Panko.Contracts.SubmittedCrumbKind;

namespace Panko.Api.Cases;

public interface ICaseCommands
{
    Task<CreateCaseResult> CreateAsync(
        CreateCase command,
        CallerIdentity caller,
        CancellationToken cancellationToken);

    Task<AppendCrumbsResult> AppendCrumbsAsync(
        Guid caseId,
        AppendCrumbs command,
        CallerIdentity caller,
        CancellationToken cancellationToken);

    Task<RebuildCaseResult> QueueRebuildAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken);

    Task<RefreshCaseResult> QueueSourceRefreshAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken);

    Task CloseAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken);
}

public sealed class CaseCommands(
    ICaseInputStore repository,
    RecipeStore recipes,
    ICaseAuthorization authorization,
    CaseInputBoundary inputBoundary,
    CaseFileProjectionBuilder projections,
    ICaseUpdatePublisher updates,
    IOptions<CaseOptions> options,
    CaseTelemetry telemetry,
    TimeProvider timeProvider) : ICaseCommands
{
    public async Task<CreateCaseResult> CreateAsync(
        CreateCase command,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var principal = caller.PrincipalName;
        var recipeId = Required(command.RecipeId, "recipeId", 128);
        await authorization.AuthorizeRecipeAsync(
            caller.Principal, recipeId, CasePermission.Create, cancellationToken);

        Recipe recipe;
        try
        {
            recipe = recipes.ResolveById(recipeId);
        }
        catch (InvalidOperationException exception)
        {
            throw new CaseValidationException(exception.Message);
        }
        if (recipe.AgentCases is not { Enabled: true } policy)
        {
            throw new CaseAuthorizationException(
                $"Recipe '{recipeId}' does not allow agent-created Case work.");
        }

        var idempotencyKey = Required(command.IdempotencyKey, "idempotencyKey", 128);
        var title = Required(command.Title, "title", 300);
        var serviceId = Required(command.ServiceId, "serviceId", 128);
        var urgency = Required(command.Urgency, "urgency", 16).ToLowerInvariant();
        if (urgency is not ("high" or "low"))
        {
            throw new CaseValidationException("urgency must be 'high' or 'low'.");
        }
        var referenceTime = command.ReferenceTime.ToUniversalTime();
        if ((referenceTime - timeProvider.GetUtcNow()).Duration()
            > TimeSpan.FromHours(options.Value.MaximumTimestampDistanceHours))
        {
            throw new CaseValidationException(
                "referenceTime is outside the configured distance from the server time.");
        }

        if (command.Labels.Count > 50)
        {
            throw new CaseValidationException("At most 50 labels may be supplied.");
        }
        var boundedLabels = command.Labels.ToDictionary(
            pair => Required(pair.Key, "label key", 64),
            pair => Required(pair.Value, $"label '{pair.Key}' value", 256),
            StringComparer.Ordinal);
        var labels = recipes.FilterPersistedLabels(recipe, boundedLabels);
        var requestHash = Hash(new
        {
            recipeId,
            title,
            serviceId,
            urgency,
            referenceTime,
            labels = labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        });

        var caseId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var origin = new CaseOrigin(CaseOriginKind.Agent, null);
        var proposed = new CaseRecord(
            caseId,
            null,
            serviceId,
            recipeId,
            title,
            urgency,
            PagerDutyIncidentState.Triggered,
            referenceTime,
            now,
            0,
            CaseProgression.Open,
            false,
            null,
            policy.PublishToSlack ? recipe.SlackChannel : string.Empty,
            null,
            labels)
        {
            Team = recipe.Team,
            Origin = origin,
            CreatedBy = principal,
            InputVersion = 0,
            ProjectedInputVersion = 0,
            PublishToSlack = policy.PublishToSlack
        };
        var createdId = CaseInputBoundary.DeterministicCrumbId(
            caseId, principal, "case-created");
        var createdEvent = new CaseInput(
            createdId,
            caseId,
            0,
            0,
            principal,
            "case-created",
            SubmittedCrumbKind.Event,
            referenceTime,
            now,
            "case-created",
            urgency == "high" ? "critical" : "info",
            "Case created by agent",
            null,
            "agent",
            null,
            null,
            principal,
            "case",
            caseId.ToString(),
            [],
            "collected",
            Hash(new { caseId, principal, referenceTime }),
            null,
            null,
            null);
        var initial = projections.Build(
            proposed,
            recipe,
            recipes.Revision,
            0,
            [createdEvent],
            [],
            new AiSynthesis("pending", null, [], [], [], null),
            null) with
        {
            Status = CaseProgression.Open,
            UpdatedAt = now,
            DeterministicSummary = "Case created. No Crumbs have been submitted yet."
        };

        var result = await repository.CreateAsync(
            proposed,
            initial,
            createdEvent,
            principal,
            idempotencyKey,
            requestHash,
            cancellationToken);
        if (!result.Duplicate)
        {
            telemetry.CaseCreated("agent");
            await updates.PublishCaseFileAsync(
                result.Case.Id,
                result.Case.Version,
                result.Case.InputVersion,
                result.Case.ProjectedInputVersion,
                result.Case.Status,
                CaseFileTransitions.ChangedSections(CaseFileTransition.Initial),
                cancellationToken);
        }
        return result;
    }

    public async Task<AppendCrumbsResult> AppendCrumbsAsync(
        Guid caseId,
        AppendCrumbs command,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var caseRecord = await RequireCaseAsync(caseId, cancellationToken);
        await authorization.AuthorizeTeamAsync(
            caller.Principal, caseRecord.Team, CasePermission.Append, cancellationToken);
        RequireMutableCase(caseRecord);
        var recipe = recipes.ResolveById(caseRecord.RecipeId);
        RequireStableTeam(caseRecord, recipe);
        if (recipe.AgentCases is not { Enabled: true } policy)
        {
            throw new CaseAuthorizationException(
                $"Recipe '{caseRecord.RecipeId}' does not allow agent submissions.");
        }

        var batchId = Required(command.BatchId, "batchId", 128);
        IReadOnlyList<NormalizedCrumb> normalized;
        try
        {
            normalized = inputBoundary.Normalize(
                caseId,
                caller.PrincipalName,
                caseRecord.OpenedAt,
                policy.AllowedInputCategories,
                command.Crumbs);
        }
        catch (CaseValidationException)
        {
            telemetry.CrumbRejected("validation");
            throw;
        }
        var requestHash = Hash(normalized.Select(item => new
        {
            item.ClientCrumbId,
            item.PayloadHash,
            item.SupersedesClientCrumbId
        }));
        var result = await repository.AppendAsync(
            caseId,
            caller.PrincipalName,
            batchId,
            requestHash,
            normalized,
            options.Value.MaximumInputsPerCase,
            cancellationToken);
        telemetry.CrumbsAccepted(result.Accepted);
        telemetry.CrumbsDeduplicated(result.Duplicates);

        if (result.Accepted > 0)
        {
            var current = await RequireCaseAsync(caseId, cancellationToken);
            telemetry.ProjectionLag(current.InputVersion, current.ProjectedInputVersion);
            await updates.PublishCaseFileAsync(
                current.Id,
                current.Version,
                current.InputVersion,
                current.ProjectedInputVersion,
                current.Status,
                CaseFileTransitions.ChangedSections(
                    CaseFileTransition.InputsAccepted),
                cancellationToken);
        }
        return result;
    }

    public async Task<RebuildCaseResult> QueueRebuildAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var caseRecord = await RequireCaseAsync(caseId, cancellationToken);
        await authorization.AuthorizeTeamAsync(
            caller.Principal, caseRecord.Team, CasePermission.Rebuild, cancellationToken);
        RequireMutableCase(caseRecord);
        RequireStableTeam(caseRecord, recipes.ResolveById(caseRecord.RecipeId));
        var queued = await repository.QueueProjectionAsync(
            caseId, caseRecord.InputVersion, cancellationToken);
        var current = await RequireCaseAsync(caseId, cancellationToken);
        await updates.PublishCaseFileAsync(
            current.Id,
            current.Version,
            current.InputVersion,
            current.ProjectedInputVersion,
            current.Status,
            ["status"],
            cancellationToken);
        return new RebuildCaseResult(caseId, caseRecord.InputVersion, queued);
    }

    public async Task<RefreshCaseResult> QueueSourceRefreshAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var caseRecord = await RequireCaseAsync(caseId, cancellationToken);
        await authorization.AuthorizeTeamAsync(
            caller.Principal,
            caseRecord.Team,
            CasePermission.RefreshSources,
            cancellationToken);
        RequireMutableCase(caseRecord);
        var recipe = recipes.ResolveById(caseRecord.RecipeId);
        RequireStableTeam(caseRecord, recipe);
        if (recipe.AgentCases is not { Enabled: true, AllowSourceRefresh: true })
        {
            throw new CaseAuthorizationException(
                $"Recipe '{caseRecord.RecipeId}' does not allow agent-requested source refreshes.");
        }
        if (caseRecord.IsFrozen)
        {
            throw new CaseConflictException(
                $"Case '{caseId}' is closed.");
        }
        var queued = await repository.QueueRefreshAsync(
            caseId, caseRecord.InputVersion, cancellationToken);
        var current = await RequireCaseAsync(caseId, cancellationToken);
        await updates.PublishCaseFileAsync(
            current.Id,
            current.Version,
            current.InputVersion,
            current.ProjectedInputVersion,
            current.Status,
            CaseFileTransitions.ChangedSections(
                CaseFileTransition.SourceRefreshStarted),
            cancellationToken);
        return new RefreshCaseResult(caseId, caseRecord.InputVersion, queued);
    }

    public async Task CloseAsync(
        Guid caseId,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var caseRecord = await RequireCaseAsync(caseId, cancellationToken);
        await authorization.AuthorizeTeamAsync(
            caller.Principal, caseRecord.Team, CasePermission.Close, cancellationToken);
        RequireMutableCase(caseRecord);
        RequireStableTeam(caseRecord, recipes.ResolveById(caseRecord.RecipeId));
        await repository.CloseAsync(caseId, caller.PrincipalName, cancellationToken);
        var current = await RequireCaseAsync(caseId, cancellationToken);
        await updates.PublishCaseFileAsync(
            current.Id,
            current.Version,
            current.InputVersion,
            current.ProjectedInputVersion,
            current.Status,
            ["status", "state"],
            cancellationToken);
    }

    private async Task<CaseRecord> RequireCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        await repository.GetCaseAsync(caseId, cancellationToken)
        ?? throw new CaseNotFoundException(caseId);

    private static void RequireStableTeam(CaseRecord caseRecord, Recipe recipe)
    {
        if (!TeamKey.IsCanonical(caseRecord.Team)
            || !string.Equals(caseRecord.Team, recipe.Team, StringComparison.Ordinal))
        {
            throw new CaseConflictException(
                $"Case '{caseRecord.Id}' is owned by a different team than its current Recipe configuration.");
        }
    }

    private static void RequireMutableCase(CaseRecord caseRecord)
    {
        if (caseRecord.Origin.Kind == CaseOriginKind.PagerDuty)
        {
            throw new CaseConflictException(
                "PagerDuty Case lifecycle and rebuilds are owned by the PagerDuty adapter.");
        }
    }

    private static string Required(string? value, string field, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CaseValidationException($"{field} is required.");
        }
        var trimmed = value.Trim();
        if (trimmed.Length > maximumCharacters)
        {
            throw new CaseValidationException(
                $"{field} may contain at most {maximumCharacters} characters.");
        }
        return trimmed;
    }

    private static string Hash<T>(T value) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
