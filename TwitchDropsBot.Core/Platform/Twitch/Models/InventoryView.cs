namespace TwitchDropsBot.Core.Platform.Twitch.Models;

public enum InventoryViewSection
{
    InProgress,
    Earned
}

public sealed class InventoryViewItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = "Unknown reward";
    public string GameName { get; init; } = "Unknown game";
    public string? GameSlug { get; init; }
    public string CampaignName { get; init; } = "Unknown campaign";
    public string? CampaignId { get; init; }
    public int CurrentMinutes { get; init; }
    public int RequiredMinutes { get; init; }
    public bool IsClaimed { get; init; }
    public bool IsCampaignLevel { get; init; }
    public InventoryViewSection Section { get; init; }
    public DateTime? EndsAt { get; init; }
    public DateTime? LastAwardedAt { get; init; }
    public string? ImageUrl { get; init; }
    public string? GameImageUrl { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

public static class InventoryViewBuilder
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<InventoryViewItem> Build(
        Inventory? inventory,
        IReadOnlyDictionary<string, string>? campaignNames = null)
    {
        if (inventory is null)
        {
            return Array.Empty<InventoryViewItem>();
        }

        var items = new List<InventoryViewItem>();
        AddInProgressItems(inventory, items);
        AddEarnedItems(inventory, campaignNames, items);

        return items
            .OrderBy(item => item.Section)
            .ThenBy(item => item.GameName, NameComparer)
            .ThenBy(item => item.CampaignName, NameComparer)
            .ThenBy(item => item.RequiredMinutes)
            .ThenBy(item => item.Name, NameComparer)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddInProgressItems(Inventory inventory, List<InventoryViewItem> items)
    {
        var campaigns = inventory.DropCampaignsInProgress;
        if (campaigns is null)
        {
            return;
        }

        foreach (var campaign in campaigns)
        {
            if (campaign is null)
            {
                continue;
            }

            var game = campaign.Game;
            var gameName = FirstNonEmpty(game?.DisplayName, game?.Name) ?? "Unknown game";
            var gameImageUrl = FirstNonEmpty(game?.BoxArtUrl);
            var campaignName = FirstNonEmpty(campaign.Name) ?? "Unknown campaign";
            var campaignId = FirstNonEmpty(campaign.Id);
            var endsAt = campaign.EndAt ?? campaign.EndsAt;

            var drops = campaign.TimeBasedDrops;
            if (drops is null || drops.All(drop => drop is null))
            {
                items.Add(new InventoryViewItem
                {
                    Id = campaignId ?? string.Empty,
                    Name = campaignName,
                    GameName = gameName,
                    GameSlug = FirstNonEmpty(game?.Slug),
                    CampaignName = campaignName,
                    CampaignId = campaignId,
                    IsCampaignLevel = true,
                    Section = InventoryViewSection.InProgress,
                    EndsAt = endsAt,
                    ImageUrl = gameImageUrl,
                    GameImageUrl = gameImageUrl,
                    StatusText = "Campaign in progress"
                });
                continue;
            }

            foreach (var drop in drops)
            {
                if (drop is null)
                {
                    continue;
                }

                var benefit = FirstBenefit(drop.BenefitEdges);
                var currentMinutes = drop.Self?.CurrentMinutesWatched ?? 0;
                var requiredMinutes = drop.RequiredMinutesWatched;
                var isClaimed = drop.Self is not null
                    ? drop.Self.IsClaimed
                    : AllBenefitsClaimed(drop.BenefitEdges);

                items.Add(new InventoryViewItem
                {
                    Id = FirstNonEmpty(drop.Id) ?? string.Empty,
                    Name = FirstNonEmpty(benefit?.Name, drop.Name) ?? "Unknown reward",
                    GameName = gameName,
                    GameSlug = FirstNonEmpty(game?.Slug),
                    CampaignName = campaignName,
                    CampaignId = campaignId,
                    CurrentMinutes = currentMinutes,
                    RequiredMinutes = requiredMinutes,
                    IsClaimed = isClaimed,
                    IsCampaignLevel = false,
                    Section = InventoryViewSection.InProgress,
                    EndsAt = endsAt,
                    ImageUrl = FirstNonEmpty(benefit?.ImageAssetURL) ?? gameImageUrl,
                    GameImageUrl = gameImageUrl,
                    StatusText = BuildInProgressStatus(isClaimed, currentMinutes, requiredMinutes)
                });
            }
        }
    }

    private static void AddEarnedItems(
        Inventory inventory,
        IReadOnlyDictionary<string, string>? campaignNames,
        List<InventoryViewItem> items)
    {
        var earnedItems = new List<InventoryViewItem>();

        var earnedEdges = inventory.EarnedDropRewards?.Edges;
        if (earnedEdges is not null)
        {
            foreach (var edge in earnedEdges)
            {
                var node = edge?.Node;
                if (node is null)
                {
                    continue;
                }

                var campaign = node.Campaign;
                var game = campaign?.Game;
                var campaignId = FirstNonEmpty(campaign?.Id);
                var gameName = FirstNonEmpty(
                        game?.DisplayName,
                        game?.Name,
                        campaign?.BrandName,
                        GetDetailsHost(campaign))
                    ?? "Earned rewards";
                var campaignName = FirstNonEmpty(
                        campaign?.Name,
                        campaign?.BrandName,
                        LookupCampaignName(campaignNames, campaignId))
                    ?? "Other rewards";
                var gameImageUrl = FirstNonEmpty(game?.BoxArtUrl);
                var isClaimed = string.Equals(node.Status, "CLAIMED", StringComparison.OrdinalIgnoreCase);

                earnedItems.Add(new InventoryViewItem
                {
                    Id = FirstNonEmpty(node.Id) ?? string.Empty,
                    Name = FirstNonEmpty(node.Item?.Name) ?? "Unknown reward",
                    GameName = gameName,
                    GameSlug = FirstNonEmpty(game?.Slug),
                    CampaignName = campaignName,
                    CampaignId = campaignId,
                    IsClaimed = isClaimed,
                    IsCampaignLevel = false,
                    Section = InventoryViewSection.Earned,
                    EndsAt = campaign?.EndAt,
                    ImageUrl = FirstNonEmpty(node.Item?.ThumbnailURL) ?? gameImageUrl,
                    GameImageUrl = gameImageUrl,
                    StatusText = isClaimed ? "Claimed" : "Ready to claim"
                });
            }
        }

        var gameEventEdges = inventory.GameEventDropsConnection?.Edges;
        if (gameEventEdges is not null)
        {
            foreach (var edge in gameEventEdges)
            {
                var node = edge?.Node;
                if (node is null)
                {
                    continue;
                }

                var status = node.IsConnected ? "Connected" : "Not connected";
                if (node.TotalCount > 1)
                {
                    status += $" x{node.TotalCount}";
                }

                earnedItems.Add(new InventoryViewItem
                {
                    Id = FirstNonEmpty(node.Id) ?? string.Empty,
                    Name = FirstNonEmpty(node.Name) ?? "Unknown reward",
                    GameName = "Inventory",
                    CampaignName = "Game event drops",
                    IsCampaignLevel = false,
                    Section = InventoryViewSection.Earned,
                    LastAwardedAt = node.LastAwardedAt == default ? null : node.LastAwardedAt,
                    ImageUrl = FirstNonEmpty(node.ImageURL),
                    StatusText = status
                });
            }
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in earnedItems)
        {
            if (item.Id.Length > 0 && !seenIds.Add(item.Id))
            {
                continue;
            }

            items.Add(item);
        }
    }

    private static DropBenefit? FirstBenefit(List<DropBenefitEdge>? edges)
    {
        if (edges is null)
        {
            return null;
        }

        foreach (var edge in edges)
        {
            if (edge?.Benefit is not null)
            {
                return edge.Benefit;
            }
        }

        return null;
    }

    private static bool AllBenefitsClaimed(List<DropBenefitEdge>? edges)
    {
        if (edges is null)
        {
            return false;
        }

        var hasBenefit = false;
        foreach (var edge in edges)
        {
            var benefit = edge?.Benefit;
            if (benefit is null)
            {
                continue;
            }

            hasBenefit = true;
            if (!benefit.IsClaimed)
            {
                return false;
            }
        }

        return hasBenefit;
    }

    private static string BuildInProgressStatus(bool isClaimed, int currentMinutes, int requiredMinutes)
    {
        if (isClaimed)
        {
            return "Claimed";
        }

        if (requiredMinutes > 0)
        {
            return currentMinutes >= requiredMinutes
                ? "Ready to claim"
                : $"{currentMinutes}/{requiredMinutes} min";
        }

        return "In progress";
    }

    private static string? GetDetailsHost(DropsCampaign? campaign)
    {
        var detailsUrl = campaign?.DetailsURL;
        if (string.IsNullOrWhiteSpace(detailsUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(detailsUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        const string wwwPrefix = "www.";
        return uri.Host.StartsWith(wwwPrefix, StringComparison.OrdinalIgnoreCase)
            ? uri.Host[wwwPrefix.Length..]
            : uri.Host;
    }

    private static string? LookupCampaignName(IReadOnlyDictionary<string, string>? campaignNames, string? campaignId)
    {
        if (campaignNames is null || string.IsNullOrWhiteSpace(campaignId))
        {
            return null;
        }

        if (campaignNames.TryGetValue(campaignId, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        foreach (var pair in campaignNames)
        {
            if (string.Equals(pair.Key, campaignId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
