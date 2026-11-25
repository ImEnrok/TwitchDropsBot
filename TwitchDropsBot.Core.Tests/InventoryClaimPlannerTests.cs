using TwitchDropsBot.Core.Platform.Twitch.Models;

namespace TwitchDropsBot.Core.Tests;

public class InventoryClaimPlannerTests
{
    [Fact]
    public void Create_NullInventory_ReturnsEmptyPlan()
    {
        var plan = InventoryClaimPlanner.Create(null);

        Assert.Empty(plan.Drops);
        Assert.Empty(plan.EarnedRewards);
        Assert.Equal(0, plan.SkippedDropCount);
    }

    [Fact]
    public void Create_ConnectedCampaignWithClaimableDrop_ReturnsDrop()
    {
        var drop = new TimeBasedDrop
        {
            Id = "drop-1",
            Self = new TimeBasedDropSelfEdge
            {
                IsClaimed = false,
                DropInstanceID = "instance-1"
            }
        };
        var campaign = CreateCampaign(drop);
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        var claimable = Assert.Single(plan.Drops);
        Assert.Same(campaign, claimable.Campaign);
        Assert.Same(drop, claimable.Drop);
        Assert.Equal(0, plan.SkippedDropCount);
        Assert.Empty(plan.EarnedRewards);
    }

    [Fact]
    public void Create_NullCampaignSelf_IsTreatedAsConnected()
    {
        var drop = new TimeBasedDrop
        {
            Self = new TimeBasedDropSelfEdge { IsClaimed = false, DropInstanceID = "instance-1" }
        };
        var campaign = CreateCampaign((bool?)null, drop);
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Single(plan.Drops);
    }

    [Fact]
    public void Create_DisconnectedCampaign_SkipsCampaign()
    {
        var drop = new TimeBasedDrop
        {
            Self = new TimeBasedDropSelfEdge { IsClaimed = false, DropInstanceID = "instance-1" }
        };
        var campaign = CreateCampaign(false, drop);
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.Drops);
        Assert.Equal(0, plan.SkippedDropCount);
    }

    [Fact]
    public void Create_DropWithNullSelf_CountsSkippedAndExcludesIt()
    {
        var nullSelfDrop = new TimeBasedDrop { Id = "drop-no-self", Self = null };
        var claimableDrop = new TimeBasedDrop
        {
            Id = "drop-claimable",
            Self = new TimeBasedDropSelfEdge { IsClaimed = false, DropInstanceID = "instance-1" }
        };
        var campaign = CreateCampaign(nullSelfDrop, claimableDrop);
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        var claimable = Assert.Single(plan.Drops);
        Assert.Same(claimableDrop, claimable.Drop);
        Assert.Equal(1, plan.SkippedDropCount);
    }

    [Fact]
    public void Create_ClaimedDrop_IsExcluded()
    {
        var drop = new TimeBasedDrop
        {
            Self = new TimeBasedDropSelfEdge { IsClaimed = true, DropInstanceID = "instance-1" }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { CreateCampaign(drop) }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.Drops);
        Assert.Equal(0, plan.SkippedDropCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_EmptyDropInstanceId_IsExcluded(string? dropInstanceId)
    {
        var drop = new TimeBasedDrop
        {
            Self = new TimeBasedDropSelfEdge { IsClaimed = false, DropInstanceID = dropInstanceId }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { CreateCampaign(drop) }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.Drops);
    }

    [Fact]
    public void Create_NullDropCampaignsInProgress_ReturnsEmptyPlan()
    {
        var inventory = new Inventory
        {
            DropCampaignsInProgress = null!
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.Drops);
        Assert.Empty(plan.EarnedRewards);
    }

    [Fact]
    public void Create_NullCampaignInList_IsSkipped()
    {
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { null! }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.Drops);
        Assert.Equal(0, plan.SkippedDropCount);
    }

    [Fact]
    public void Create_NullTimeBasedDrops_DoesNotThrow()
    {
        var campaign = CreateCampaign();
        campaign.TimeBasedDrops = null!;
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.Drops);
        Assert.Equal(0, plan.SkippedDropCount);
    }

    [Fact]
    public void Create_NullDropInList_IsSkipped()
    {
        var claimableDrop = new TimeBasedDrop
        {
            Self = new TimeBasedDropSelfEdge { IsClaimed = false, DropInstanceID = "instance-1" }
        };
        var campaign = CreateCampaign(new TimeBasedDrop[] { null!, claimableDrop });
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Single(plan.Drops);
        Assert.Equal(0, plan.SkippedDropCount);
    }

    [Fact]
    public void Create_NullEarnedDropRewards_ReturnsNoRewards()
    {
        var inventory = new Inventory
        {
            EarnedDropRewards = null!
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.EarnedRewards);
    }

    [Fact]
    public void Create_NullEarnedEdges_ReturnsNoRewards()
    {
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection { Edges = null! }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Empty(plan.EarnedRewards);
    }

    [Fact]
    public void Create_EarnedRewards_ExcludesClaimedAndIncludesPendingCaseInsensitively()
    {
        var claimed = new EarnedDropReward { Id = "claimed", Status = "claimed" };
        var pending = new EarnedDropReward { Id = "pending", Status = "PENDING" };
        var noStatus = new EarnedDropReward { Id = "no-status", Status = null! };
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge>
                {
                    new() { Node = claimed },
                    new() { Node = pending },
                    new() { Node = noStatus }
                }
            }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        Assert.Equal(2, plan.EarnedRewards.Count);
        Assert.Contains(plan.EarnedRewards, reward => ReferenceEquals(reward, pending));
        Assert.Contains(plan.EarnedRewards, reward => ReferenceEquals(reward, noStatus));
        Assert.DoesNotContain(plan.EarnedRewards, reward => ReferenceEquals(reward, claimed));
    }

    [Fact]
    public void Create_NullEarnedEdgeOrNode_IsSkipped()
    {
        var pending = new EarnedDropReward { Id = "pending", Status = "PENDING" };
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge>
                {
                    null!,
                    new() { Node = null! },
                    new() { Node = pending }
                }
            }
        };

        var plan = InventoryClaimPlanner.Create(inventory);

        var reward = Assert.Single(plan.EarnedRewards);
        Assert.Same(pending, reward);
    }

    private static DropCampaign CreateCampaign(params TimeBasedDrop[] drops)
    {
        return CreateCampaign(true, drops);
    }

    private static DropCampaign CreateCampaign(bool? connected, params TimeBasedDrop[] drops)
    {
        return new DropCampaign
        {
            Id = "campaign-1",
            Name = "Campaign One",
            Self = connected is null ? null : new DropCampaignSelfEdge { IsAccountConnected = connected.Value },
            TimeBasedDrops = drops.ToList()
        };
    }
}
