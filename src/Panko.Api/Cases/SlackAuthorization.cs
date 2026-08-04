using Panko.Api.Options;
using Panko.Api.Security;

namespace Panko.Api.Cases;

internal sealed record SlackPromptChannelAccess(
    string? Team,
    string? RecipeId,
    bool IsAuthorized);

internal static class SlackChannelAuthorization
{
    internal static SlackPromptChannelAccess ResolvePrompt(
        SlackOptions options,
        IRecipeOwnershipCatalog recipes,
        string channelId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var team = ResolveExact(options.ChannelTeams, channelId);
        var recipeId = ResolveExact(options.PromptChannelRecipes, channelId);
        if (team is null || recipeId is null ||
            !recipes.TryGet(recipeId, out var ownership))
        {
            return new SlackPromptChannelAccess(team, recipeId, IsAuthorized: false);
        }

        var scope = TeamAccessScope.Restricted([team]);
        return new SlackPromptChannelAccess(
            team,
            recipeId,
            IsAuthorized: scope.Allows(ownership.Team));
    }

    internal static string? ResolveTeam(SlackOptions options, string channelId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        return ResolveExact(options.ChannelTeams, channelId);
    }

    internal static string? ResolveExact(
        IReadOnlyDictionary<string, string> mappings,
        string channelId)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        foreach (var mapping in mappings)
        {
            // Do not inherit a dictionary's comparer: Slack IDs are case-sensitive.
            if (string.Equals(mapping.Key, channelId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(mapping.Value))
            {
                return mapping.Value;
            }
        }

        return null;
    }
}
