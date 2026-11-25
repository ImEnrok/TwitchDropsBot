namespace TwitchDropsBot.Core.Platform.Twitch.Models;

public sealed record ClaimableDrop(DropCampaign Campaign, TimeBasedDrop Drop);

public sealed class InventoryClaimPlan
{
    public static InventoryClaimPlan Empty { get; } = new(
        Array.Empty<ClaimableDrop>(),
        Array.Empty<EarnedDropReward>(),
        0);

    public InventoryClaimPlan(
        IReadOnlyList<ClaimableDrop> drops,
        IReadOnlyList<EarnedDropReward> earnedRewards,
        int skippedDropCount)
    {
        Drops = drops;
        EarnedRewards = earnedRewards;
        SkippedDropCount = skippedDropCount;
    }

    public IReadOnlyList<ClaimableDrop> Drops { get; }
    public IReadOnlyList<EarnedDropReward> EarnedRewards { get; }
    public int SkippedDropCount { get; }
}

public static class InventoryClaimPlanner
{
    private const string ClaimedStatus = "CLAIMED";

    public static InventoryClaimPlan Create(Inventory? inventory)
    {
        if (inventory is null)
        {
            return InventoryClaimPlan.Empty;
        }

        var claimableDrops = new List<ClaimableDrop>();
        var skippedDropCount = 0;

        var campaigns = inventory.DropCampaignsInProgress;
        if (campaigns is not null)
        {
            foreach (var campaign in campaigns)
            {
                if (campaign is null)
                {
                    continue;
                }

                if (campaign.Self is not null && !campaign.Self.IsAccountConnected)
                {
                    continue;
                }

                var timeBasedDrops = campaign.TimeBasedDrops;
                if (timeBasedDrops is null)
                {
                    continue;
                }

                foreach (var drop in timeBasedDrops)
                {
                    if (drop is null)
                    {
                        continue;
                    }

                    var self = drop.Self;
                    if (self is null)
                    {
                        skippedDropCount++;
                        continue;
                    }

                    if (self.IsClaimed || string.IsNullOrEmpty(self.DropInstanceID))
                    {
                        continue;
                    }

                    claimableDrops.Add(new ClaimableDrop(campaign, drop));
                }
            }
        }

        var earnedRewards = new List<EarnedDropReward>();
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

                if (!string.Equals(node.Status, ClaimedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    earnedRewards.Add(node);
                }
            }
        }

        return new InventoryClaimPlan(claimableDrops, earnedRewards, skippedDropCount);
    }
}
