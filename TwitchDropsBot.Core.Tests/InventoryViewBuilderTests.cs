using TwitchDropsBot.Core.Platform.Twitch.Models;

namespace TwitchDropsBot.Core.Tests;

public class InventoryViewBuilderTests
{
    [Fact]
    public void Build_NullInventory_ReturnsEmptyList()
    {
        var items = InventoryViewBuilder.Build(null);

        Assert.Empty(items);
    }

    [Fact]
    public void Build_InProgressCampaignWithDrop_MapsAllDisplayFields()
    {
        var endAt = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = new Game
        {
            DisplayName = "Game Display",
            Name = "Game Name",
            Slug = "game-slug",
            BoxArtUrl = "https://example.test/boxart.png"
        };
        var benefit = new DropBenefit
        {
            Name = "Benefit Reward",
            ImageAssetURL = "https://example.test/benefit.png",
            IsClaimed = false
        };
        var drop = new TimeBasedDrop
        {
            Id = "drop-1",
            Name = "Drop Name",
            RequiredMinutesWatched = 60,
            Self = new TimeBasedDropSelfEdge
            {
                CurrentMinutesWatched = 30,
                IsClaimed = false
            },
            BenefitEdges = new List<DropBenefitEdge>
            {
                new() { Benefit = benefit }
            }
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-1",
            Name = "Campaign One",
            Game = game,
            EndAt = endAt,
            TimeBasedDrops = new List<TimeBasedDrop> { drop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal(InventoryViewSection.InProgress, item.Section);
        Assert.Equal("drop-1", item.Id);
        Assert.Equal("Benefit Reward", item.Name);
        Assert.Equal("Game Display", item.GameName);
        Assert.Equal("game-slug", item.GameSlug);
        Assert.Equal("Campaign One", item.CampaignName);
        Assert.Equal("campaign-1", item.CampaignId);
        Assert.Equal(30, item.CurrentMinutes);
        Assert.Equal(60, item.RequiredMinutes);
        Assert.False(item.IsClaimed);
        Assert.False(item.IsCampaignLevel);
        Assert.Equal("30/60 min", item.StatusText);
        Assert.Equal("https://example.test/benefit.png", item.ImageUrl);
        Assert.Equal("https://example.test/boxart.png", item.GameImageUrl);
        Assert.Equal(endAt, item.EndsAt);
    }

    [Fact]
    public void Build_EmptyBenefitEdges_FallsBackToDropNameAndDoesNotThrow()
    {
        var game = new Game { DisplayName = "Game", BoxArtUrl = "https://example.test/boxart.png" };
        var drop = new TimeBasedDrop
        {
            Id = "drop-2",
            Name = "Fallback Drop",
            RequiredMinutesWatched = 10,
            Self = null,
            BenefitEdges = new List<DropBenefitEdge>()
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-2",
            Name = "Campaign",
            Game = game,
            TimeBasedDrops = new List<TimeBasedDrop> { drop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("Fallback Drop", item.Name);
        Assert.False(item.IsClaimed);
        Assert.Equal(0, item.CurrentMinutes);
        Assert.Equal("0/10 min", item.StatusText);
        Assert.Equal("https://example.test/boxart.png", item.ImageUrl);
    }

    [Fact]
    public void Build_NullSelfWithMixedBenefitClaims_IsNotClaimed()
    {
        var drop = new TimeBasedDrop
        {
            Id = "drop-mixed-claims",
            Name = "Mixed Claims Drop",
            RequiredMinutesWatched = 10,
            Self = null,
            BenefitEdges = new List<DropBenefitEdge>
            {
                new() { Benefit = new DropBenefit { Name = "Claimed Benefit", IsClaimed = true } },
                new() { Benefit = new DropBenefit { Name = "Unclaimed Benefit", IsClaimed = false } }
            }
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-mixed-claims",
            Name = "Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop> { drop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.False(item.IsClaimed);
        Assert.Equal("0/10 min", item.StatusText);
    }

    [Fact]
    public void Build_NullSelfWithAllBenefitsClaimed_IsClaimed()
    {
        var drop = new TimeBasedDrop
        {
            Id = "drop-all-claimed",
            Name = "All Claimed Drop",
            RequiredMinutesWatched = 10,
            Self = null,
            BenefitEdges = new List<DropBenefitEdge>
            {
                new() { Benefit = new DropBenefit { Name = "First Benefit", IsClaimed = true } },
                new() { Benefit = null },
                new() { Benefit = new DropBenefit { Name = "Second Benefit", IsClaimed = true } }
            }
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-all-claimed",
            Name = "Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop> { drop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.True(item.IsClaimed);
        Assert.Equal("Claimed", item.StatusText);
    }

    [Fact]
    public void Build_NullBenefitEdges_DoesNotThrowAndUsesDropFallback()
    {
        var drop = new TimeBasedDrop
        {
            Id = "drop-null-benefits",
            Name = "Null Benefits Drop",
            RequiredMinutesWatched = 10,
            Self = null,
            BenefitEdges = null!
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-null-benefits",
            Name = "Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop> { drop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("Null Benefits Drop", item.Name);
        Assert.False(item.IsClaimed);
        Assert.Equal("0/10 min", item.StatusText);
    }

    [Fact]
    public void Build_CampaignWithoutTimeBasedDrops_EmitsCampaignLevelItem()
    {
        var campaign = new DropCampaign
        {
            Id = "campaign-empty",
            Name = "Empty Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop>()
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.True(item.IsCampaignLevel);
        Assert.Equal(InventoryViewSection.InProgress, item.Section);
        Assert.Equal("Empty Campaign", item.Name);
        Assert.Equal("campaign-empty", item.Id);
        Assert.Equal("Campaign in progress", item.StatusText);
        Assert.Equal(0, item.RequiredMinutes);
    }

    [Fact]
    public void Build_TimeBasedDropsContainsOnlyNullElements_EmitsCampaignLevelItem()
    {
        var campaign = new DropCampaign
        {
            Id = "campaign-null-drops",
            Name = "Null Drops Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop> { null!, null! }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.True(item.IsCampaignLevel);
        Assert.Equal("Null Drops Campaign", item.Name);
        Assert.Equal("Campaign in progress", item.StatusText);
    }

    [Fact]
    public void Build_NullTimeBasedDrops_EmitsCampaignLevelItem()
    {
        var campaign = new DropCampaign
        {
            Id = "campaign-null-drop-list",
            Name = "Null Drop List Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = null!
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.True(item.IsCampaignLevel);
        Assert.Equal("Null Drop List Campaign", item.Name);
        Assert.Equal("Campaign in progress", item.StatusText);
    }

    [Fact]
    public void Build_NullEarnedDropRewards_DoesNotThrow()
    {
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign>(),
            EarnedDropRewards = null
        };

        var items = InventoryViewBuilder.Build(inventory);

        Assert.Empty(items);
    }

    [Fact]
    public void Build_NullDropCampaignsInProgress_DoesNotThrowAndReturnsEmpty()
    {
        var inventory = new Inventory
        {
            DropCampaignsInProgress = null!
        };

        var items = InventoryViewBuilder.Build(inventory);

        Assert.Empty(items);
    }

    [Fact]
    public void Build_NullEarnedDropRewardsEdges_DoesNotThrowAndReturnsEmpty()
    {
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection { Edges = null! }
        };

        var items = InventoryViewBuilder.Build(inventory);

        Assert.Empty(items);
    }

    [Fact]
    public void Build_NullGameEventDropsEdges_DoesNotThrowAndReturnsEmpty()
    {
        var inventory = new Inventory
        {
            GameEventDropsConnection = new UserDropRewardConnection { Edges = null! }
        };

        var items = InventoryViewBuilder.Build(inventory);

        Assert.Empty(items);
    }

    [Fact]
    public void Build_NullCollectionsAndNullElements_DoesNotThrow()
    {
        var campaignWithNullDrops = new DropCampaign
        {
            Id = "campaign-null",
            Name = "Null Drops",
            TimeBasedDrops = null!
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { null!, campaignWithNullDrops },
            GameEventDropsConnection = null!,
            EarnedDropRewards = new EarnedDropRewardConnection { Edges = null! }
        };

        var items = InventoryViewBuilder.Build(inventory);

        var item = Assert.Single(items);
        Assert.True(item.IsCampaignLevel);
        Assert.Equal("Null Drops", item.Name);
    }

    [Fact]
    public void Build_NullElementsInsideCollections_AreSkipped()
    {
        var validDrop = new TimeBasedDrop
        {
            Id = "drop-valid",
            Name = "Valid Drop",
            RequiredMinutesWatched = 10,
            BenefitEdges = new List<DropBenefitEdge>
            {
                null!,
                new() { Benefit = new DropBenefit { Name = "Valid Benefit" } }
            }
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-valid",
            Name = "Valid Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop> { null!, validDrop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { null!, campaign },
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge> { null!, new() { Node = null! } }
            },
            GameEventDropsConnection = new UserDropRewardConnection
            {
                Edges = new List<UserDropRewardEdge> { null!, new() { Node = null! } }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("Valid Benefit", item.Name);
        Assert.Equal("Valid Campaign", item.CampaignName);
        Assert.Equal("Game", item.GameName);
    }

    [Fact]
    public void Build_EarnedRewardEdge_MapsEarnedSectionNameAndClaimedStatus()
    {
        var endAt = new DateTime(2026, 11, 30, 10, 0, 0, DateTimeKind.Utc);
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge>
                {
                    new()
                    {
                        Node = new EarnedDropReward
                        {
                            Id = "earned-1",
                            Status = "claimed",
                            Item = new DropsReward
                            {
                                Id = "item-1",
                                Name = "Earned Reward",
                                ThumbnailURL = "https://example.test/earned.png"
                            },
                            Campaign = new DropsCampaign
                            {
                                Id = "earned-campaign",
                                Name = "Earned Campaign",
                                EndAt = endAt,
                                Game = new Game
                                {
                                    DisplayName = "Earned Game",
                                    Slug = "earned-game",
                                    BoxArtUrl = "https://example.test/earned-boxart.png"
                                }
                            }
                        }
                    }
                }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal(InventoryViewSection.Earned, item.Section);
        Assert.Equal("earned-1", item.Id);
        Assert.Equal("Earned Reward", item.Name);
        Assert.Equal("Earned Game", item.GameName);
        Assert.Equal("earned-game", item.GameSlug);
        Assert.Equal("Earned Campaign", item.CampaignName);
        Assert.Equal("earned-campaign", item.CampaignId);
        Assert.True(item.IsClaimed);
        Assert.Equal("Claimed", item.StatusText);
        Assert.Equal("https://example.test/earned.png", item.ImageUrl);
        Assert.Equal("https://example.test/earned-boxart.png", item.GameImageUrl);
        Assert.Equal(endAt, item.EndsAt);
        Assert.False(item.IsCampaignLevel);
    }

    [Fact]
    public void Build_EarnedRewardEdgeNotClaimed_StatusIsReadyToClaim()
    {
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge>
                {
                    new()
                    {
                        Node = new EarnedDropReward
                        {
                            Id = "earned-ready",
                            Status = "IN_PROGRESS",
                            Item = new DropsReward { Name = "Ready Reward" },
                            Campaign = new DropsCampaign { Name = "Campaign", Game = new Game() }
                        }
                    }
                }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.False(item.IsClaimed);
        Assert.Equal("Ready to claim", item.StatusText);
    }

    [Fact]
    public void Build_ProgressMeetsRequirement_StatusIsReadyToClaim()
    {
        var drop = new TimeBasedDrop
        {
            Id = "drop-complete",
            Name = "Completed Progress Drop",
            RequiredMinutesWatched = 60,
            Self = new TimeBasedDropSelfEdge
            {
                CurrentMinutesWatched = 75,
                IsClaimed = false
            },
            BenefitEdges = new List<DropBenefitEdge>
            {
                new() { Benefit = new DropBenefit { Name = "Reward" } }
            }
        };
        var campaign = new DropCampaign
        {
            Id = "campaign-complete",
            Name = "Campaign",
            Game = new Game { DisplayName = "Game" },
            TimeBasedDrops = new List<TimeBasedDrop> { drop }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.False(item.IsClaimed);
        Assert.Equal(75, item.CurrentMinutes);
        Assert.Equal(60, item.RequiredMinutes);
        Assert.Equal("Ready to claim", item.StatusText);
    }

    [Fact]
    public void Build_GameEventDrop_MapsEarnedSectionConnectedStatusAndCountSuffix()
    {
        var awardedAt = new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc);
        var inventory = new Inventory
        {
            GameEventDropsConnection = new UserDropRewardConnection
            {
                Edges = new List<UserDropRewardEdge>
                {
                    new()
                    {
                        Node = new UserDropReward
                        {
                            Id = "game-event-1",
                            Name = "Event Drop",
                            ImageURL = "https://example.test/event.png",
                            IsConnected = true,
                            TotalCount = 3,
                            LastAwardedAt = awardedAt
                        }
                    }
                }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal(InventoryViewSection.Earned, item.Section);
        Assert.Equal("game-event-1", item.Id);
        Assert.Equal("Event Drop", item.Name);
        Assert.Equal("Inventory", item.GameName);
        Assert.Equal("Game event drops", item.CampaignName);
        Assert.Equal("Connected x3", item.StatusText);
        Assert.Equal("https://example.test/event.png", item.ImageUrl);
        Assert.Equal(awardedAt, item.LastAwardedAt);
    }

    [Fact]
    public void Build_GameEventDropDisconnectedWithoutCount_SaysNotConnected()
    {
        var inventory = new Inventory
        {
            GameEventDropsConnection = new UserDropRewardConnection
            {
                Edges = new List<UserDropRewardEdge>
                {
                    new()
                    {
                        Node = new UserDropReward
                        {
                            Id = "game-event-2",
                            Name = "Event Drop",
                            IsConnected = false,
                            TotalCount = 1
                        }
                    }
                }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("Not connected", item.StatusText);
    }

    [Fact]
    public void Build_GameEventDropDefaultLastAwardedAt_MapsToNull()
    {
        var inventory = new Inventory
        {
            GameEventDropsConnection = new UserDropRewardConnection
            {
                Edges = new List<UserDropRewardEdge>
                {
                    new()
                    {
                        Node = new UserDropReward
                        {
                            Id = "game-event-default-date",
                            Name = "Never Awarded",
                            IsConnected = true,
                            TotalCount = 1,
                            LastAwardedAt = default
                        }
                    }
                }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Null(item.LastAwardedAt);
    }

    [Fact]
    public void Build_NullGame_FallsBackToUnknownGame()
    {
        var campaign = new DropCampaign
        {
            Id = "campaign-nogame",
            Name = "No Game Campaign",
            Game = null,
            TimeBasedDrops = new List<TimeBasedDrop>
            {
                new()
                {
                    Id = "drop-nogame",
                    Name = "Drop",
                    RequiredMinutesWatched = 5,
                    BenefitEdges = new List<DropBenefitEdge>()
                }
            }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("Unknown game", item.GameName);
        Assert.Null(item.GameSlug);
        Assert.Null(item.GameImageUrl);
    }

    [Fact]
    public void Build_GameWithoutDisplayName_UsesGameName()
    {
        var campaign = new DropCampaign
        {
            Id = "campaign-noname",
            Name = "Campaign",
            Game = new Game { DisplayName = null, Name = "Fallback Game Name" },
            TimeBasedDrops = new List<TimeBasedDrop>
            {
                new()
                {
                    Id = "drop-noname",
                    Name = "Drop",
                    RequiredMinutesWatched = 5,
                    BenefitEdges = new List<DropBenefitEdge>()
                }
            }
        };
        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign> { campaign }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("Fallback Game Name", item.GameName);
    }

    [Fact]
    public void Build_DuplicateEarnedAndGameEventId_KeepsFirstItemOnly()
    {
        var inventory = new Inventory
        {
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge>
                {
                    new()
                    {
                        Node = new EarnedDropReward
                        {
                            Id = "duplicate-id",
                            Status = "CLAIMED",
                            Item = new DropsReward { Name = "Earned First" },
                            Campaign = new DropsCampaign { Name = "Campaign", Game = new Game() }
                        }
                    }
                }
            },
            GameEventDropsConnection = new UserDropRewardConnection
            {
                Edges = new List<UserDropRewardEdge>
                {
                    new()
                    {
                        Node = new UserDropReward
                        {
                            Id = "duplicate-id",
                            Name = "Game Event Second",
                            IsConnected = true,
                            TotalCount = 1
                        }
                    }
                }
            }
        };

        var item = Assert.Single(InventoryViewBuilder.Build(inventory));

        Assert.Equal("duplicate-id", item.Id);
        Assert.Equal("Earned First", item.Name);
        Assert.Equal(InventoryViewSection.Earned, item.Section);
    }

    [Fact]
    public void Build_SortsInProgressBeforeEarnedAndOrdersByGameCampaignRequiredMinutesName()
    {
        static DropCampaign Campaign(string id, string name, string gameName, params TimeBasedDrop[] drops) => new()
        {
            Id = id,
            Name = name,
            Game = new Game { DisplayName = gameName },
            TimeBasedDrops = drops.ToList()
        };

        static TimeBasedDrop Drop(string id, string name, int required) => new()
        {
            Id = id,
            Name = name,
            RequiredMinutesWatched = required,
            BenefitEdges = new List<DropBenefitEdge>()
        };

        var inventory = new Inventory
        {
            DropCampaignsInProgress = new List<DropCampaign>
            {
                Campaign("c-zeta", "Zeta Campaign", "Zeta Game", Drop("d-zeta", "Zeta Drop", 10)),
                Campaign("c-alpha-2", "Beta Campaign", "Alpha Game", Drop("d-alpha-2", "Beta Drop", 10)),
                Campaign("c-alpha-1", "Alpha Campaign", "Alpha Game",
                    Drop("d-required-60", "Drop 60", 60),
                    Drop("d-required-30", "Drop 30", 30)),
                Campaign("c-earned-game", "Earned Campaign", "AAA Game",
                    Drop("d-earned-game", "AAA Drop", 10))
            },
            EarnedDropRewards = new EarnedDropRewardConnection
            {
                Edges = new List<EarnedDropRewardEdge>
                {
                    new()
                    {
                        Node = new EarnedDropReward
                        {
                            Id = "earned-z",
                            Status = "CLAIMED",
                            Item = new DropsReward { Name = "Earned Z" },
                            Campaign = new DropsCampaign { Name = "Z Campaign", Game = new Game { DisplayName = "Z Game" } }
                        }
                    },
                    new()
                    {
                        Node = new EarnedDropReward
                        {
                            Id = "earned-a",
                            Status = "CLAIMED",
                            Item = new DropsReward { Name = "Earned A" },
                            Campaign = new DropsCampaign { Name = "A Campaign", Game = new Game { DisplayName = "A Game" } }
                        }
                    }
                }
            }
        };

        var items = InventoryViewBuilder.Build(inventory);

        Assert.Equal(7, items.Count);
        Assert.All(items.Take(5), item => Assert.Equal(InventoryViewSection.InProgress, item.Section));
        Assert.Equal("d-earned-game", items[0].Id); // AAA Game sorts before Alpha Game
        Assert.Equal("d-required-30", items[1].Id); // Alpha Game / Alpha Campaign / 30 min
        Assert.Equal("d-required-60", items[2].Id);
        Assert.Equal("d-alpha-2", items[3].Id); // Alpha Game / Beta Campaign
        Assert.Equal("d-zeta", items[4].Id); // Zeta Game
        Assert.Equal(InventoryViewSection.Earned, items[5].Section);
        Assert.Equal("earned-a", items[5].Id);
        Assert.Equal("earned-z", items[6].Id);
    }
}
