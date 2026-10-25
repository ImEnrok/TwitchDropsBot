using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchDropsBot.Core.Platform.Shared.Bots;
using TwitchDropsBot.Core.Platform.Shared.Exceptions;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Shared.Settings;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;
using TwitchDropsBot.Core.Platform.Twitch.Models.Extensions;
using TwitchDropsBot.Core.Platform.Twitch.Settings;
using TwitchDropsBot.Core.Platform.Twitch.Utils;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public class TwitchBot : BaseBot<TwitchUser>
{
    // Only way for now to get refresh settings for each run
    private TwitchSettings TwitchSettings
    {
        get
        {
            if (_botSettings is null)
            {
                throw new Exception("BotSettings is null");
            }

            return _botSettings.CurrentValue.TwitchSettings;
        }
    }

    private List<AbstractCampaign> finishedCampaigns;
    private IOptionsMonitor<BotSettings> _botSettings;
    private List<string> _gamesToCheck;
    private readonly Dictionary<string, DateTime> _failedRewardCodeModals = new();

    public TwitchBot(
        TwitchUser user,
        ILogger logger,
        NotificationService notificationService,
        IOptionsMonitor<BotSettings> botSettings
    ) : base(user, logger, notificationService, botSettings)
    {
        finishedCampaigns = new List<AbstractCampaign>();

        _botSettings = botSettings;

        _gamesToCheck = new List<string>();
    }

    public override List<String> GetUserFavoriteGames()
    {
        var user = BotSettings.CurrentValue.TwitchSettings.TwitchUsers.Find(user => user.Id == BotUser.Id);

        return user.FavouriteGames;
    }

    protected override async Task StartAsync()
    {
        // Refresh data
        var userFavoriteGames = GetUserFavoriteGames();

        _gamesToCheck = userFavoriteGames.Count > 0
            ? userFavoriteGames
            : _botSettings.CurrentValue.FavouriteGames;

        BotUser.OnlyFavouriteGames = TwitchSettings.OnlyFavouriteGames;
        BotUser.OnlyConnectedAccounts = TwitchSettings.OnlyConnectedAccounts;

        // Get campaigns
        var thingsToWatch = await BotUser.TwitchRepository.FetchDropsAsync();
        var inventory = await BotUser.TwitchRepository.FetchInventoryDropsAsync();
        DateTime now = DateTime.Now;

        // Fallback for accounts where Twitch returns an empty drops dashboard
        // (ViewerDropsDashboard can be empty while Inventory still lists in-progress campaigns).
        if (inventory?.DropCampaignsInProgress is { Count: > 0 })
        {
            var existingCampaignIds = new HashSet<string>(thingsToWatch.Select(c => c.Id));
            var favouriteGamesSet = BotUser.FavouriteGames.Select(game => game.ToLower()).ToHashSet();

            foreach (var inProgressCampaign in inventory.DropCampaignsInProgress)
            {
                if (inProgressCampaign.Status != "ACTIVE" || existingCampaignIds.Contains(inProgressCampaign.Id))
                {
                    continue;
                }

                if (inProgressCampaign.Game is not null)
                {
                    var gameName = (inProgressCampaign.Game.DisplayName ?? inProgressCampaign.Game.Name ?? string.Empty)
                        .ToLower();
                    if (favouriteGamesSet.Contains(gameName) ||
                        favouriteGamesSet.Contains((inProgressCampaign.Game.Slug ?? string.Empty).ToLower()))
                    {
                        inProgressCampaign.Game.IsFavorite = true;
                    }
                }

                thingsToWatch.Add(inProgressCampaign);
            }
        }

        finishedCampaigns.RemoveAll(campaign =>
            campaign.EndAt.HasValue && campaign.EndAt.Value.ToLocalTime().AddHours(1) < now);

        Logger.LogInformation($"Removing {finishedCampaigns.Count} finished campaigns...");
        thingsToWatch.RemoveAll(campaign => finishedCampaigns.Any(finished => finished.Id == campaign.Id));

        BotUser.Inventory = inventory;
        BotUser.Status = BotStatus.Seeking;

        await CheckForClaim(inventory);

        var seedAttempted = false;

        if (thingsToWatch.Count == 0)
        {
            seedAttempted = true;
            var seededCampaign = await TrySeedNewCampaignAsync();

            if (seededCampaign is not null)
            {
                thingsToWatch.Add(seededCampaign);
            }
            else
            {
                throw new NoBroadcasterOrNoCampaignLeft();
            }
        }

        if (BotUser.OnlyConnectedAccounts)
        {
            thingsToWatch.RemoveAll(x =>
                x is DropCampaign dropCampaign && !dropCampaign.Self.IsAccountConnected &&
                dropCampaign.AccountLinkURL != "https://twitch.tv/");
        }

        if (BotUser.OnlyFavouriteGames)
        {
            thingsToWatch.RemoveAll(x => !x.Game?.IsFavorite ?? false);
        }

        // Assuming you have a list of favorite game names
        var favoriteGameNames = _gamesToCheck;

        var favouriteCampaigns = thingsToWatch.Where(x => x.Game.IsFavorite).ToList();
        thingsToWatch.RemoveAll(x => favouriteCampaigns.Contains(x));

        // Re order favouriteCampaigns to match the config file order

        favouriteCampaigns = favouriteCampaigns
            .Where(x => x.Game is not null)
            .OrderBy(x =>
            {
                if (!x.Game!.IsFavorite)
                    return int.MaxValue;

                var idx = favoriteGameNames.IndexOf(x.Game.DisplayName);
                return idx == -1 ? int.MaxValue : idx;
            })
            .ThenBy(x => x.GetEndDate())
            .ToList();


        thingsToWatch = thingsToWatch.OrderBy(x => x.GetEndDate()).ToList();

        // Keep the favourite order from the config: favourite campaigns are already sorted
        // by the priority list, do not re-sort them by end date.

        thingsToWatch = favouriteCampaigns.Concat(thingsToWatch).ToList();

        Logger.LogDebug("Priority queue: {names}",
            string.Join("; ", thingsToWatch.Select(c => $"{c.Name} [{(c.Game?.DisplayName ?? c.Game?.Name)}]")));

        // Proactively look for new campaigns of favourite games that are not in the list yet
        await TryDiscoverNewCampaignsAsync(thingsToWatch);

        TimeBasedDrop? timeBasedDrop = null;
        DropCurrentSession? dropCurrentSession = null;
        DropsRewardGroup? dropCurrentRewardGroup = null;
        User? broadcaster = null;
        AbstractCampaign? campaign = null;
        var startedCampaigns = 0;

        do
        {
            if (thingsToWatch.Count == 0)
            {
                if (seedAttempted)
                {
                    throw new NoBroadcasterOrNoCampaignLeft();
                }

                seedAttempted = true;
                var seededCampaign = await TrySeedNewCampaignAsync();

                if (seededCampaign is not null)
                {
                    thingsToWatch.Add(seededCampaign);
                }
                else
                {
                    throw new NoBroadcasterOrNoCampaignLeft();
                }
            }

            (campaign, broadcaster) = await SelectBroadcasterAsync(thingsToWatch, inventory);

            if (campaign is null)
            {
                Logger.LogInformation("No campaign found.");
                if (thingsToWatch.Count == 1)
                {
                    throw new NoBroadcasterOrNoCampaignLeft();
                }

                continue;
            }

            if (broadcaster is null)
            {
                Logger.LogInformation("No broadcaster found for this campaign.");
                thingsToWatch.Remove(campaign);
                continue;
            }


            /*
                dropCurrentSession = await CheckDropCurrentSession(broadcaster, campaign);

                if (dropCurrentSession is null)
                {
                    thingsToWatch.Remove(campaign);
                    continue;
                }

                if (dropCurrentSession.CurrentMinutesWatched > dropCurrentSession.RequiredMinutesWatched)
                {
                    Logger.LogInformation("CurrentMinutesWatched > requiredMinutesWatched, skipping");
                    thingsToWatch.Remove(campaign);
                    continue;
                }

                if (string.IsNullOrEmpty(dropCurrentSession.DropId))
                {
                    Logger.LogInformation("DropId is null or empty, skipping");
                    thingsToWatch.Remove(campaign);
                    continue;
                }

                if (dropCurrentSession.Channel.Id != broadcaster.Id)
                {
                    Logger.LogInformation(
                        $"DropCurrentSession found but not the right channel ({dropCurrentSession.Channel.Name} instead of {broadcaster.Login}), changing...");
                    dropCurrentSession = await BotUser.TwitchRepository.FetchCurrentSessionContextAsync(broadcaster);
                    if (dropCurrentSession is null)
                    {
                        Logger.LogInformation("Can't fetch new current drop session");
                        thingsToWatch.Remove(campaign);
                        continue;
                    }
                }

                timeBasedDrop = campaign.FindTimeBasedDrop(dropCurrentSession.DropId);
            */

            dropCurrentRewardGroup = await CheckDropProgress(broadcaster, campaign);

            if (dropCurrentRewardGroup is null)
            {
                // No progress on this campaign yet: it was never started on this account.
                // Start it by watching the broadcaster for a couple of minutes, then re-check.
                if (campaign is DropCampaign dropCampaign &&
                    dropCampaign.Self?.IsAccountConnected != false &&
                    startedCampaigns < 2 &&
                    !(_campaignStartFailures.TryGetValue(campaign.Id, out var lastStartFailure) &&
                      (DateTime.UtcNow - lastStartFailure).TotalMinutes < 60))
                {
                    startedCampaigns++;
                    dropCurrentRewardGroup = await TryStartCampaignAsync(broadcaster, campaign);
                }
            }

            if (dropCurrentRewardGroup is null)
            {
                _campaignStartFailures[campaign.Id] = DateTime.UtcNow;
                thingsToWatch.Remove(campaign);
                continue;
            }

            if (dropCurrentRewardGroup.Self.CurrentMinutesWatched >
                dropCurrentRewardGroup.ProgressCriteria.Requirements.MinutesWatched)
            {
                Logger.LogInformation("CurrentMinutesWatched > requiredMinutesWatched, skipping");
                thingsToWatch.Remove(campaign);
                continue;
            }

            if (string.IsNullOrEmpty(dropCurrentRewardGroup.Id))
            {
                Logger.LogInformation("DropId is null or empty, skipping");
                thingsToWatch.Remove(campaign);
                continue;
            }

            timeBasedDrop = campaign.FindTimeBasedDrop(dropCurrentRewardGroup.Id);

            if (timeBasedDrop is null)
            {
                Logger.LogInformation("Time based drop not found, skipping");
                thingsToWatch.Remove(campaign);
                continue;
            }

            Logger.LogInformation($"Time based drops : {timeBasedDrop.Name}");
            // } while (timeBasedDrop is null || dropCurrentSession is null || broadcaster is null || campaign is null);
        } while (timeBasedDrop is null || dropCurrentRewardGroup is null || broadcaster is null || campaign is null);


        BotUser.CurrentTimeBasedDrop = timeBasedDrop;
        BotUser.CurrentCampaign = campaign;
        BotUser.CurrentBroadcaster = broadcaster;
        BotUser.CurrentDropCurrentSession = dropCurrentSession;

        BotUser.Status = BotStatus.Watching;
        Logger.LogInformation(
            $"Current drop campaign: {campaign.Name} ({campaign.Game?.DisplayName}), watching {broadcaster.Login} | {broadcaster.Id}");
        await WatchStreamAsync(broadcaster, dropCurrentRewardGroup, campaign);

        Logger.LogDebug("Loop ended");
    }

    // Seed a single campaign for the given favourite game: find a live stream with drops
    // enabled and watch it for a couple of minutes, so the campaign appears in the
    // Inventory and can be farmed normally afterwards.
    private async Task<AbstractCampaign?> TrySeedCampaignForGameAsync(string gameName)
    {
        try
        {
            var game = await BotUser.TwitchRepository.FetchGameByNameAsync(gameName);

            if (game is null || string.IsNullOrEmpty(game.Slug))
            {
                return null;
            }

            // Live streams with drops enabled for this game
            var directoryGame = await BotUser.TwitchRepository.FetchDirectoryPageGameAsync(game.Slug, true);

            var edges = directoryGame?.Streams?.Edges;

            if (edges is null || edges.Count == 0)
            {
                return null;
            }

            var topBroadcasters = edges
                .OrderByDescending(x => x.Node.ViewersCount)
                .Select(x => x.Node.Broadcaster)
                .Where(x => x is not null)
                .Take(3)
                .ToList();

            foreach (var broadcaster in topBroadcasters)
            {
                try
                {
                    // Only seed channels that actually offer a (non-reward) drop campaign
                    // to this account; the directory's DROPS_ENABLED filter is not per-viewer.
                    var availableDrops = await BotUser.TwitchRepository.FetchAvailableDropsAsync(broadcaster.Id);

                    if (availableDrops is null || availableDrops.Count == 0 ||
                        availableDrops.All(campaign => campaign.Summary?.IsRewardCampaign == true))
                    {
                        Logger.LogDebug(
                            "No seedable drop campaign on {broadcaster} ({gameName}), skipping channel",
                            broadcaster.Login, gameName);
                        continue;
                    }

                    Logger.LogInformation(
                        "Seeding a new campaign: watching {broadcaster} ({gameName})...",
                        broadcaster.Login, gameName);

                    for (var i = 0; i < 3; i++)
                    {
                        await BotUser.WatchManager.WatchStreamAsync(broadcaster, game);
                        await Task.Delay(TimeSpan.FromSeconds(35));
                    }

                    BotUser.WatchManager.Close();
                    await Task.Delay(TimeSpan.FromSeconds(10));

                    var inventory = await BotUser.TwitchRepository.FetchInventoryDropsAsync();

                    var seededCampaign = inventory?.DropCampaignsInProgress?.FirstOrDefault(campaign =>
                        campaign.Status == "ACTIVE" &&
                        campaign.Game is not null &&
                        (string.Equals(campaign.Game.Slug, game.Slug, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(campaign.Game.DisplayName ?? campaign.Game.Name, gameName,
                             StringComparison.OrdinalIgnoreCase)));

                    if (seededCampaign is not null)
                    {
                        seededCampaign.Game!.IsFavorite = true;
                        Logger.LogInformation("New campaign seeded: {name} ({gameName})", seededCampaign.Name, gameName);
                        return seededCampaign;
                    }

                    // Watched this channel, but Twitch hasn't registered the campaign yet РІР‚вЂќ
                    // it should show up in the Inventory within a few minutes.
                    _seedFailures[gameName.ToLower()] = DateTime.UtcNow;
                    return null;
                }
                catch (StreamOffline)
                {
                    BotUser.WatchManager.Close();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to seed a campaign for {gameName}", gameName);
        }

        return null;
    }

    // When there is nothing to watch, try to seed campaigns for favourite games.
    private async Task<AbstractCampaign?> TrySeedNewCampaignAsync()
    {
        var seedingStartedAt = DateTime.UtcNow;

        foreach (var gameName in _gamesToCheck)
        {
            if ((DateTime.UtcNow - seedingStartedAt).TotalMinutes > 6)
            {
                break;
            }

            var seededCampaign = await TrySeedCampaignForGameAsync(gameName);

            if (seededCampaign is not null)
            {
                return seededCampaign;
            }
        }

        return null;
    }

    private readonly Dictionary<string, DateTime> _discoveryGameChecks = new();
    private readonly Dictionary<string, DateTime> _seedFailures = new();
    private readonly Dictionary<string, DateTime> _campaignStartFailures = new();

    // Proactively look for new campaigns of favourite games that are not in the current
    // watch list yet (e.g. brand new campaigns never watched on this account). Checks at
    // most one game per 15 minutes and caps the time spent per call.
    private async Task TryDiscoverNewCampaignsAsync(List<AbstractCampaign> thingsToWatch)
    {
        var coveredGames = thingsToWatch
            .Where(campaign => campaign.Game is not null)
            .Select(campaign => (campaign.Game!.DisplayName ?? campaign.Game!.Name ?? string.Empty).ToLower())
            .ToHashSet();

        var missingGames = _gamesToCheck
            .Where(gameName => !coveredGames.Contains(gameName.ToLower()))
            .OrderBy(gameName => _discoveryGameChecks.TryGetValue(gameName.ToLower(), out var lastCheck)
                ? lastCheck
                : DateTime.MinValue)
            .ToList();

        var discoveryStartedAt = DateTime.UtcNow;

        foreach (var gameName in missingGames)
        {
            var gameKey = gameName.ToLower();

            if (_discoveryGameChecks.TryGetValue(gameKey, out var lastCheck) &&
                (DateTime.UtcNow - lastCheck).TotalMinutes < 15)
            {
                continue;
            }

            // A recent seeding attempt for this game didn't register a campaign РІР‚вЂќ
            // give it a rest before burning more watch time on it.
            if (_seedFailures.TryGetValue(gameKey, out var lastFailure) &&
                (DateTime.UtcNow - lastFailure).TotalMinutes < 60)
            {
                continue;
            }

            if ((DateTime.UtcNow - discoveryStartedAt).TotalMinutes > 4)
            {
                break;
            }

            _discoveryGameChecks[gameKey] = DateTime.UtcNow;

            var seededCampaign = await TrySeedCampaignForGameAsync(gameName);

            if (seededCampaign is not null && thingsToWatch.All(campaign => campaign.Id != seededCampaign.Id))
            {
                thingsToWatch.Add(seededCampaign);
            }
        }
    }

    private async Task<DropCurrentSession?> CheckDropCurrentSession(User broadcaster,
        AbstractCampaign campaign)
    {
        var dropCurrentSession = await BotUser.TwitchRepository.FetchCurrentSessionContextAsync(broadcaster);

        if (dropCurrentSession is null)
        {
            Logger.LogInformation("No drop current session found, skipping");
            return null;
        }

        if (string.IsNullOrEmpty(dropCurrentSession.DropId) ||
            dropCurrentSession.CurrentMinutesWatched == dropCurrentSession.RequiredMinutesWatched)
        {
            await BotUser.WatchManager.FakeWatchAsync(broadcaster, campaign.Game,
                BotSettings.CurrentValue.AttemptToWatch);
            dropCurrentSession = await BotUser.TwitchRepository.FetchCurrentSessionContextAsync(broadcaster);
        }

        return dropCurrentSession;
    }

    private async Task<DropsCampaign?> FetchCampaignProgress(User broadcaster, AbstractCampaign campaign)
    {
        try
        {
            var campaignsProgress = await BotUser.TwitchRepository.FetchDropCampaignsProgressAsync(broadcaster);
            return campaignsProgress.FirstOrDefault(x => x.Id == campaign.Id);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error fetching campaign progress");
            return null;
        }
    }

    private async Task<List<DropProgressInfo>> GetTwitchProgressListAsync(User broadcaster, AbstractCampaign campaign, DropsRewardGroup dropCurrentRewardGroup)
    {
        var list = new List<DropProgressInfo>();
        try
        {
            var campaignProgress = await FetchCampaignProgress(broadcaster, campaign);
            if (campaignProgress?.RewardGroups != null)
            {
                foreach (var group in campaignProgress.RewardGroups)
                {
                    list.Add(new DropProgressInfo
                    {
                        Name = group.Name,
                        CurrentProgress = group.Self?.CurrentMinutesWatched ?? 0,
                        RequiredProgress = group.ProgressCriteria?.Requirements?.MinutesWatched ?? 0,
                        IsClaimed = group.Self?.Status == "CLAIMED",
                        IsActive = group.Id == dropCurrentRewardGroup.Id,
                        ImageUrl = group.Rewards.FirstOrDefault()?.ThumbnailURL
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting Twitch progress list");
        }

        if (list.Count == 0)
        {
            list.Add(new DropProgressInfo
            {
                Name = dropCurrentRewardGroup.Name,
                CurrentProgress = dropCurrentRewardGroup.Self?.CurrentMinutesWatched ?? 0,
                RequiredProgress = dropCurrentRewardGroup.ProgressCriteria?.Requirements?.MinutesWatched ?? 0,
                IsClaimed = dropCurrentRewardGroup.Self?.Status == "CLAIMED",
                IsActive = true,
                ImageUrl = dropCurrentRewardGroup.Rewards.FirstOrDefault()?.ThumbnailURL
            });
        }
        return list;
    }

    private async Task<DropsRewardGroup?> CheckDropProgress(User broadcaster, AbstractCampaign campaign)
    {
        var campaignProgress = await FetchCampaignProgress(broadcaster, campaign);
        if (campaignProgress is null) return null;

        //Remove every drop where
        var filteredCampaignProgress = campaignProgress.RewardGroups
            .Where(x => x.Self != null && x.Self.CurrentMinutesWatched < x.ProgressCriteria.Requirements.MinutesWatched)
            .OrderBy(x => x.ProgressCriteria.Requirements.MinutesWatched).ToList();

        return filteredCampaignProgress.FirstOrDefault();
    }

    // Start a campaign that has no progress on this account yet: watch the broadcaster
    // briefly so Twitch registers the campaign, then re-check its progress.
    private async Task<DropsRewardGroup?> TryStartCampaignAsync(User broadcaster, AbstractCampaign campaign)
    {
        try
        {
            Logger.LogInformation(
                "Campaign {campaignName} has no progress yet - starting it by watching {broadcaster}...",
                campaign.Name, broadcaster.Login);

            for (var i = 0; i < 3; i++)
            {
                await BotUser.WatchManager.WatchStreamAsync(broadcaster, campaign.Game);
                await Task.Delay(TimeSpan.FromSeconds(35));
            }

            BotUser.WatchManager.Close();
            await Task.Delay(TimeSpan.FromSeconds(10));

            var rewardGroup = await CheckDropProgress(broadcaster, campaign);

            if (rewardGroup is null)
            {
                Logger.LogInformation(
                    "Campaign {campaignName} is still not registered on this account; will retry later",
                    campaign.Name);
            }

            return rewardGroup;
        }
        catch (System.Exception ex)
        {
            BotUser.WatchManager.Close();
            Logger.LogError(ex, "Failed to start campaign {campaignName}", campaign.Name);
            return null;
        }
    }


    private async Task WatchStreamAsync(User broadcaster, DropCurrentSession dropCurrentSession,
        AbstractCampaign campaign,
        int? minutes = null)
    {
        var stuckCounter = 0;
        var previousMinuteWatched = 0;
        var minuteWatched = dropCurrentSession.CurrentMinutesWatched;
        var requiredMinutesToWatch = dropCurrentSession.RequiredMinutesWatched;

        BotUser.CurrentMinutesWatched = minuteWatched;
        BotUser.RequiredMinutesWatched = requiredMinutesToWatch;

        while (minuteWatched <
               (minutes ?? requiredMinutesToWatch) ||
               dropCurrentSession.RequiredMinutesWatched == 0) // While all the drops are not claimed
        {
            try
            {
                await BotUser.WatchManager
                    .WatchStreamAsync(broadcaster, campaign.Game); // If not live, it will throw a 404 error    
            }
            catch (System.Exception ex)
            {
                Logger.LogError(ex, ex.Message);
                BotUser.WatchManager.Close();
                throw new StreamOffline();
            }

            try
            {
                var newDropCurrentSession =
                    await BotUser.TwitchRepository.FetchCurrentSessionContextAsync(broadcaster);

                if (newDropCurrentSession is null)
                {
                    Logger.LogInformation("Can't fetch new current drop session");
                }
                else
                {
                    dropCurrentSession = newDropCurrentSession;
                }

                BotUser.CurrentDropCurrentSession = dropCurrentSession;
                BotUser.CurrentMinutesWatched = dropCurrentSession.CurrentMinutesWatched;
                BotUser.RequiredMinutesWatched = dropCurrentSession.RequiredMinutesWatched;
            }
            catch (System.Exception ex)
            {
                Logger.LogError(ex, ex.Message);
            }

            minuteWatched = dropCurrentSession.CurrentMinutesWatched;

            if (previousMinuteWatched == minuteWatched)
            {
                stuckCounter++;
            }
            else
            {
                stuckCounter = 0;
            }

            if (stuckCounter >= 30)
            {
                BotUser.WatchManager.Close();
                await BotUser.WatchManager.WatchStreamAsync(broadcaster, campaign.Game);
                await Task.Delay(TimeSpan.FromSeconds(20), BotUser.CancellationTokenSource?.Token ?? System.Threading.CancellationToken.None);

                var newDropCurrentSession =
                    await BotUser.TwitchRepository.FetchCurrentSessionContextAsync(broadcaster);

                if (newDropCurrentSession is null)
                {
                    Logger.LogInformation("Can't fetch new current drop session after being stuck");
                }
                else
                {
                    dropCurrentSession = newDropCurrentSession;
                }
            }

            if (string.IsNullOrEmpty(dropCurrentSession.DropId))
            {
                if (requiredMinutesToWatch - previousMinuteWatched <= 2)
                {
                    break;
                }

                Logger.LogInformation("No drop current session found");
                //Check if the stream still alive

                var broadcasterData = BotUser.TwitchRepository.FetchStreamInformationAsync(broadcaster.Login);

                if (broadcasterData?.Result is null)
                {
                    throw new System.Exception("No broadcaster data found");
                }

                if (!broadcasterData.Result.IsLive())
                {
                    BotUser.WatchManager.Close();
                    throw new StreamOffline();
                }

                BotUser.WatchManager.Close();
                throw new CurrentDropSessionChanged();
            }

            previousMinuteWatched = minuteWatched;

            Logger.LogInformation(
                $"Waiting 20 seconds... {minuteWatched}/{requiredMinutesToWatch} minutes watched.");

            await Task.Delay(TimeSpan.FromSeconds(20), BotUser.CancellationTokenSource?.Token ?? System.Threading.CancellationToken.None);
        }

        BotUser.WatchManager.Close();
    }

    private async Task WatchStreamAsync(User broadcaster, DropsRewardGroup dropCurrentRewardGroup,
        AbstractCampaign campaign,
        int? minutes = null)
    {
        var stuckCounter = 0;
        var previousMinuteWatched = 0;
        var minuteWatched = dropCurrentRewardGroup.Self.CurrentMinutesWatched;
        var requiredMinutesToWatch = dropCurrentRewardGroup.ProgressCriteria.Requirements.MinutesWatched;

        BotUser.CurrentMinutesWatched = minuteWatched;
        BotUser.RequiredMinutesWatched = requiredMinutesToWatch;

        if (minuteWatched.HasValue && requiredMinutesToWatch.HasValue)
        {
            var uniqueKey = $"twitch-{BotUser.Login}-{campaign.Id}";
            var itemImage = dropCurrentRewardGroup.Rewards.FirstOrDefault()?.ThumbnailURL ?? campaign.Game?.BoxArtUrl ?? string.Empty;
            var dropsProgress = await GetTwitchProgressListAsync(broadcaster, campaign, dropCurrentRewardGroup);
            await NotificationService.SendOrUpdateProgressNotification(
                BotUser,
                campaign.Game?.DisplayName ?? campaign.Game?.Name ?? "Unknown Game",
                campaign.Name ?? "Unknown Campaign",
                itemImage,
                dropsProgress,
                uniqueKey
            );
        }

        while (minuteWatched <= requiredMinutesToWatch)
        {
            try
            {
                await BotUser.WatchManager
                    .WatchStreamAsync(broadcaster, campaign.Game); // If not live, it will throw a 404 error    
            }
            catch (System.Exception ex)
            {
                Logger.LogError(ex, ex.Message);
                BotUser.WatchManager.Close();
                throw new StreamOffline();
            }

            try
            {
                var newDropCurrentSession = await CheckDropProgress(broadcaster, campaign);

                if (newDropCurrentSession is null)
                {
                    Logger.LogInformation("No more campaign progress found");
                    break;
                }
                else
                {
                    dropCurrentRewardGroup = newDropCurrentSession;
                }

                // BotUser.CurrentDropCurrentSession = dropCurrentSession;
            }
            catch (System.Exception ex)
            {
                Logger.LogError(ex, ex.Message);
            }

            minuteWatched = dropCurrentRewardGroup.Self.CurrentMinutesWatched;
            BotUser.CurrentMinutesWatched = minuteWatched;
            BotUser.RequiredMinutesWatched = dropCurrentRewardGroup.ProgressCriteria.Requirements.MinutesWatched;

            if (previousMinuteWatched == minuteWatched)
            {
                stuckCounter++;
            }
            else
            {
                stuckCounter = 0;
            }

            if (stuckCounter >= 10)
            {
                BotUser.WatchManager.Close();
                await BotUser.WatchManager.WatchStreamAsync(broadcaster, campaign.Game);
                await Task.Delay(TimeSpan.FromSeconds(20), BotUser.CancellationTokenSource?.Token ?? System.Threading.CancellationToken.None);

                var newDropCurrentSession = await CheckDropProgress(broadcaster, campaign);

                if (newDropCurrentSession is null)
                {
                    Logger.LogInformation("No more campaign progress found after being stuck");
                }
                else
                {
                    dropCurrentRewardGroup = newDropCurrentSession;
                }
            }

            if (string.IsNullOrEmpty(dropCurrentRewardGroup.Id))
            {
                if (requiredMinutesToWatch - previousMinuteWatched <= 2)
                {
                    break;
                }

                Logger.LogInformation("No drop current session found");
                //Check if the stream still alive

                var broadcasterData = BotUser.TwitchRepository.FetchStreamInformationAsync(broadcaster.Login);

                if (broadcasterData?.Result is null)
                {
                    throw new System.Exception("No broadcaster data found");
                }

                if (!broadcasterData.Result.IsLive())
                {
                    BotUser.WatchManager.Close();
                    throw new StreamOffline();
                }

                BotUser.WatchManager.Close();
                throw new CurrentDropSessionChanged();
            }

            if (minuteWatched is null)
            {
                Logger.LogError("Minute watched is null");
                BotUser.WatchManager.Close();
                throw new System.Exception("Minute watched is null");
            }

            previousMinuteWatched = minuteWatched.Value;

            Logger.LogInformation(
                $"Waiting 60 seconds... {minuteWatched}/{requiredMinutesToWatch} minutes watched.");

            if (requiredMinutesToWatch.HasValue)
            {
                var uniqueKey = $"twitch-{BotUser.Login}-{campaign.Id}";
                var itemImage = dropCurrentRewardGroup.Rewards.FirstOrDefault()?.ThumbnailURL ?? campaign.Game?.BoxArtUrl ?? string.Empty;
                var dropsProgress = await GetTwitchProgressListAsync(broadcaster, campaign, dropCurrentRewardGroup);
                await NotificationService.SendOrUpdateProgressNotification(
                    BotUser,
                    campaign.Game?.DisplayName ?? campaign.Game?.Name ?? "Unknown Game",
                    campaign.Name ?? "Unknown Campaign",
                    itemImage,
                    dropsProgress,
                    uniqueKey
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(60), BotUser.CancellationTokenSource?.Token ?? System.Threading.CancellationToken.None);
        }

        BotUser.WatchManager.Close();
    }

    private async Task<(AbstractCampaign? campaign, User? broadcaster)> SelectBroadcasterAsync(
        List<AbstractCampaign> campaigns, Inventory inventory)
    {
        User? broadcaster = null;

        if (TwitchSettings.AvoidCampaign.Count > 0)
        {
            /*campaigns.RemoveAll(x =>
                TwitchSettings.AvoidCampaign.Contains(x.Game?.DisplayName ?? x.Game?.Name,
                    StringComparer.OrdinalIgnoreCase));
            */
            campaigns.RemoveAll(x =>
                TwitchSettings.AvoidCampaign.Contains(x.Name,
                    StringComparer.OrdinalIgnoreCase));
        }

        foreach (var campaign in campaigns.ToList())
        {
            if (campaign.Game is null)
            {
                Logger.LogInformation("Skipping campaign {campaign.Name} because game is null.", campaign.Name);
                continue;
            }

            Logger.LogInformation("Checking {campaignGameDisplayName} ({campaignName})...", campaign.Game.DisplayName,
                campaign.Name);

            if (finishedCampaigns.Contains(campaign))
            {
                Logger.LogInformation("Campaign {campaign.Name} already completed from local list, skipping",
                    campaign.Name);
                campaigns.Remove(campaign);
                continue;
            }

            var tempDropCampaign = await BotUser.TwitchRepository.FetchTimeBasedDropsAsync(campaign.Id);

            if (tempDropCampaign is not null)
            {
                campaign.TimeBasedDrops = tempDropCampaign.TimeBasedDrops;
                campaign.Game = tempDropCampaign.Game;
                campaign.Allow = tempDropCampaign.Allow;
            }

            if (campaign.TimeBasedDrops.Count == 0)
            {
                Logger.LogInformation("No time based drops available for this campaign ({campaign.Name}), skipping",
                    campaign.Name);
                campaigns.Remove(campaign);
                continue;
            }

            try
            {
                var isCompleted = await campaign.IsCompleted(inventory, BotUser.TwitchRepository);
                if (isCompleted)
                {
                    Logger.LogInformation("Campaign {campaign.Name} already completed, skipping", campaign.Name);
                    finishedCampaigns.Add(campaign);
                    campaigns.Remove(campaign);
                    continue;
                }
            }
            catch (System.Exception e)
            {
                Logger.LogError(e, e.Message);
            }

            // Todo: check if we got enough time
            var inProgressCampaign = inventory?.DropCampaignsInProgress?.FirstOrDefault(x => x.Id == campaign.Id);
            var activeDrop = inProgressCampaign?.TimeBasedDrops?.FirstOrDefault(x => (x.Self?.CurrentMinutesWatched ?? 0) < x.RequiredMinutesWatched) 
                             ?? campaign.TimeBasedDrops.FirstOrDefault();

            if (activeDrop != null && campaign.EndAt.HasValue)
            {
                var minutesLeft = (campaign.EndAt.Value - DateTime.UtcNow).TotalMinutes;
                var currentWatched = activeDrop.Self?.CurrentMinutesWatched ?? 0;
                
                if (minutesLeft < (activeDrop.RequiredMinutesWatched - currentWatched))
                {
                    Logger.LogInformation("Not enough time to watch this campaign ({campaign.Name}), skipping",
                        campaign.Name);
                    finishedCampaigns.Add(campaign);
                    campaigns.Remove(campaign);
                    continue;
                }
            }

            if (campaign is DropCampaign dropCampaign)
            {
                if (!dropCampaign.TimeBasedDrops.Any())
                {
                    Logger.LogInformation(
                        "No time based drops found for this campaign ({dropCampaign.Name}), skipping.",
                        dropCampaign.Name);
                    campaigns.Remove(campaign);
                    continue;
                }
            }

            var channels = campaign.Allow?.Channels;
            

            if (channels is not null && channels.Count < 250)
            {
                var channelGroups = channels.Select(x => x.Name).Chunk(10).ToList();

                foreach (var channelGroup in channelGroups)
                {
                    var tempBroadcasters = await BotUser.TwitchRepository.FetchStreamInformationAsync(channelGroup);

                    if (tempBroadcasters is null)
                    {
                        continue;
                    }

                    // from tempBroadcasters, select the first one that is live and that have the right game
                    var tempBroadcaster = tempBroadcasters.FirstOrDefault(tempBroadcaster =>
                        tempBroadcaster.IsLive() && tempBroadcaster.BroadcastSettings.Game?.Id != null &&
                        (campaign.Game.DisplayName == "Special Events" ||
                         tempBroadcaster.BroadcastSettings.Game.Id == campaign.Game.Id));

                    if (tempBroadcaster is null)
                    {
                        if (channelGroup == channelGroups.Last())
                        {
                            if (_botSettings.CurrentValue.TwitchSettings.ForceTryWithTags)
                            {
                                Logger.LogInformation(
                                    "No live broadcaster found in this group of channels. Forcing with stream tags");
                            }
                            else
                            {
                                Logger.LogInformation(
                                    "No live broadcaster found in this group of channels. ({currentGroup}/{channelGroups.Count})",
                                    (channelGroups.IndexOf(channelGroup) + 1), channelGroups.Count);
                                return (campaign, tempBroadcaster);
                            }
                        }
                        else
                        {
                            Logger.LogInformation(
                                "No live broadcaster found in this group of channels. trying next group... ({currentGroup}/{channelGroups.Count})",
                                (channelGroups.IndexOf(channelGroup) + 1), channelGroups.Count);
                            await Task.Delay(TimeSpan.FromSeconds(2));
                        }

                        continue;
                    }

                    broadcaster = tempBroadcaster;
                    return (campaign, broadcaster);
                }
            }

            // Search for channel that potentially have the drops
            var game = await BotUser.TwitchRepository.FetchDirectoryPageGameAsync(campaign.Game.Slug,
                campaign is DropCampaign);

            if (game is null)
            {
                Logger.LogInformation("No game found for slug {campaign.Game.Slug}.", campaign.Game.Slug);
                continue;
            }

            // Sort the channels by viewers count
            game.Streams.Edges = game.Streams.Edges.OrderByDescending(x => x.Node.ViewersCount).ToList();

            // The directory's DROPS_ENABLED filter is not campaign-specific, so make sure
            // the chosen streamer actually offers this campaign to the account.
            if (campaign is DropCampaign)
            {
                foreach (var candidate in game.Streams.Edges)
                {
                    var candidateBroadcaster = candidate.Node?.Broadcaster;

                    if (candidateBroadcaster is null)
                    {
                        continue;
                    }

                    var availableDrops =
                        await BotUser.TwitchRepository.FetchAvailableDropsAsync(candidateBroadcaster.Id);

                    if (availableDrops is null || availableDrops.Count == 0)
                    {
                        continue;
                    }

                    if (availableDrops.Any(drop => drop.Id == campaign.Id))
                    {
                        return (campaign, candidateBroadcaster);
                    }
                }

                Logger.LogInformation(
                    "No streamer currently offers campaign {campaignName}, skipping",
                    campaign.Name);

                return (campaign, null);
            }

            var edge = game.Streams.Edges.FirstOrDefault();
            if (edge != null)
            {
                broadcaster = edge.Node.Broadcaster;
            }

            return (campaign, broadcaster);
        }

        return (null, null);
    }

    private async Task CheckForClaim(Inventory? inventory)
    {
        if (inventory is null)
        {
            return;
        }

        // For every timebased drop, check if it is claimed
        foreach (var dropCampaignInProgress in inventory.DropCampaignsInProgress)
        {
            if (dropCampaignInProgress.Self is not null && !dropCampaignInProgress.Self.IsAccountConnected)
            {
                continue;
            }
            
            foreach (var timeBasedDrop in dropCampaignInProgress.TimeBasedDrops)
            {
                if (timeBasedDrop.Self is null)
                {
                    Logger.LogError("Self in TimeBasedDrop is null for {dropCampaignInProgress.Name}",
                        dropCampaignInProgress.Name);
                    return;
                }

                if (timeBasedDrop.Self.IsClaimed == false && timeBasedDrop.Self?.DropInstanceID != null)
                {
                    try
                    {
                        var claimed = await BotUser.TwitchRepository.ClaimDropAsync(timeBasedDrop.Self.DropInstanceID);
                        if (claimed)
                        {
                            timeBasedDrop.Self.IsClaimed = true;
                            var isLastDrop = dropCampaignInProgress.TimeBasedDrops.All(d => d.Self?.IsClaimed == true);
                            var gameName = dropCampaignInProgress.Game?.DisplayName ?? dropCampaignInProgress.Game?.Name ?? "Unknown Game";
                            var itemName = timeBasedDrop.BenefitEdges.FirstOrDefault()?.Benefit.Name ?? timeBasedDrop.Name;
                            var itemImage = timeBasedDrop.BenefitEdges.FirstOrDefault()?.Benefit.ImageAssetURL ?? dropCampaignInProgress.Game?.BoxArtUrl ?? string.Empty;
                            var uniqueKey = $"twitch-{BotUser.Login}-{dropCampaignInProgress.Id}";
                            await NotificationService.SendClaimNotification(BotUser, gameName, dropCampaignInProgress.Name, itemName, itemImage, uniqueKey, isLastDrop: isLastDrop);
                        }
                    }
                    catch (Exception e)
                    {
                        var itemName = timeBasedDrop.BenefitEdges.FirstOrDefault()?.Benefit.Name ?? timeBasedDrop.Name;
                        var itemImage = timeBasedDrop.BenefitEdges.FirstOrDefault()?.Benefit.ImageAssetURL ?? dropCampaignInProgress.Game?.BoxArtUrl ?? string.Empty;
                        Logger.LogError(e, $"Failed to claim drop {itemName} for campaign {dropCampaignInProgress.Name}.");
                        var message = $"Can't claim {itemName} for the campaign {dropCampaignInProgress.Name}. Twitch API error or account not linked.";
                        await NotifyError("CLAIM ERROR", message, itemImage);
                    }
                    finally
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20));
                    }
                }
            }
        }

        var earnedDropRewardToClaim = inventory.EarnedDropRewards.Edges.Where(x => x.Node.Status != "CLAIMED").ToList();
        
        foreach (var earnedDropRewardEdge in earnedDropRewardToClaim)
        {
            var rewardId = earnedDropRewardEdge.Node.Id;
            if (_failedRewardCodeModals.TryGetValue(rewardId, out var failedTime))
            {
                if (DateTime.UtcNow - failedTime < TimeSpan.FromHours(8))
                {
                    continue;
                }
                _failedRewardCodeModals.Remove(rewardId);
            }

            if (earnedDropRewardEdge.Node.Item.DistributionType != DistributionType.CODE)
            {
                continue;
            }
            
            try
            {
                var rewardCampaignCode = await BotUser.TwitchRepository.RewardCodeModal(earnedDropRewardEdge.Node.Campaign.Id, rewardId);
                Logger.LogInformation("Code {Code} rewarded for {ItemName}", rewardCampaignCode.Value, earnedDropRewardEdge.Node.Item.Name);

                var gameName = earnedDropRewardEdge.Node.Campaign?.Game?.DisplayName ?? earnedDropRewardEdge.Node.Campaign?.Game?.Name ?? "Unknown Game";
                var itemName = earnedDropRewardEdge.Node.Item?.Name ?? "Unknown Item";
                var itemImage = earnedDropRewardEdge.Node.Item?.ThumbnailURL ?? earnedDropRewardEdge.Node.Campaign?.Game?.BoxArtUrl ?? string.Empty;
                var uniqueKey = $"twitch-{BotUser.Login}-{earnedDropRewardEdge.Node.Campaign?.Id}";
                await NotificationService.SendClaimNotification(BotUser, gameName, earnedDropRewardEdge.Node.Campaign?.Name ?? "Unknown Campaign", itemName, itemImage, uniqueKey, rewardCampaignCode.Value);
            }
            catch (Exception e)
            {
                _failedRewardCodeModals[rewardId] = DateTime.UtcNow;
                var itemName = earnedDropRewardEdge.Node.Item?.Name ?? "Unknown Item";
                var itemImage = earnedDropRewardEdge.Node.Item?.ThumbnailURL ?? earnedDropRewardEdge.Node.Campaign?.Game?.BoxArtUrl ?? string.Empty;
                Logger.LogError(e, $"Failed to fetch reward code for {itemName}. Skipping for 8 hours.");
                var message = $"Can't fetch reward code for {itemName}. Twitch API error. Claim skipped for 8 hours.";
                await NotifyError("CLAIM ERROR", message, itemImage);
            }
            finally
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}
