using System.ComponentModel;
using System.Windows.Forms;
using TwitchDropsBot.Core.Platform.Shared.Bots;
using TwitchDropsBot.Core.Platform.Twitch.Bot;
using TwitchDropsBot.Core.Platform.Twitch.Models;

namespace TwitchDropsBot.WinForms
{
    public partial class TwitchUserTab : UserControl
    {
        private static readonly HttpClient ImageHttpClient = CreateImageHttpClient();

        private readonly TwitchUser twitchUser;
        public TabPage TabPage => currentTabPage;
        private readonly object imageListLock = new object();

        private System.Windows.Forms.Timer? inventoryRefreshTimer;
        private Panel? inventoryPanel;
        private Button? inventoryRefreshButton;
        private TreeView? inventoryTreeView;
        private ImageList? inventoryTreeImageList;
        private bool _initialized;
        private int _inventoryRefreshInProgress;
        private int _inventoryLoadGeneration;


        public TwitchUserTab(TwitchUser twitchUser)
        {
            InitializeComponent();

            try
            {
                InitializeListView();
            }
            catch (Exception ex)
            {
                // twitchUser.Logger.Error(ex);
                AppendLog(ex.Message);
            }

            this.twitchUser = twitchUser;
            twitchUser.PropertyChanged += TwitchUser_PropertyChanged;

            currentTabPage.Text = twitchUser.Login;

            if (twitchUser.UISink != null)
            {
                twitchUser.UISink.OnLogReceived += (message, level) =>
                {
                    var prefix = level.ToString().ToUpper();
                    AppendLog($"{prefix}: {message}");
                };
            }

            _initialized = true;
            inventoryRefreshTimer?.Start();
            currentTabPage.VisibleChanged += TwitchUserTab_VisibleChanged;

            if (inventoryTreeView != null)
            {
                inventoryTreeView.HandleCreated += InventoryTreeView_HandleCreated;
            }
        }

        private static HttpClient CreateImageHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TwitchDropsBot/1.0");
            return client;
        }

        private async void TwitchUser_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TwitchUser.Status))
            {
                await UpdateUI(twitchUser.Status);
            }

            if (e.PropertyName == nameof(TwitchUser.CurrentDropCurrentSession))
            {
                UpdateProgress();
            }

            if (e.PropertyName == nameof(TwitchUser.Inventory))
            {
                if (twitchUser?.Inventory != null)
                {
                    try
                    {
                        await LoadInventoryAsync();
                    }
                    catch (Exception ex)
                    {
                        // twitchUser.Logger.Error(ex);
                        TryAppendLog(ex.Message);
                    }

                }
            }
        }

        public async Task UpdateUI(BotStatus status)
        {

            switch (twitchUser.Status)
            {
                case BotStatus.Idle:
                case BotStatus.Seeking:
                    // reset every label
                    labelGame.Invoke((System.Windows.Forms.MethodInvoker)(() => labelGame.Text = $"Game : N/A"));
                    labelDrop.Invoke((System.Windows.Forms.MethodInvoker)(() => labelDrop.Text = $"Drop : N/A"));
                    labelDrop.Invoke((System.Windows.Forms.MethodInvoker)(() => labelPercentage.Text = $"-%"));
                    labelMinRemaining.Invoke((System.Windows.Forms.MethodInvoker)(() => labelMinRemaining.Text = $"Minutes remaining : -"));
                    progressBar.Invoke((System.Windows.Forms.MethodInvoker)(() => progressBar.Value = 0));

                    break;
                default:
                    labelGame.Invoke((System.Windows.Forms.MethodInvoker)(() => labelGame.Text = $"Game : {twitchUser.CurrentCampaign?.Game.DisplayName}"));
                    labelDrop.Invoke((System.Windows.Forms.MethodInvoker)(() => labelDrop.Text = $"Drop : {twitchUser.CurrentTimeBasedDrop?.Name}"));
                    break;
            }
        }

        private void AppendLog(string message)
        {
            if (twitchLoggerTextBox.InvokeRequired)
            {
                twitchLoggerTextBox.Invoke(new Action(() =>
                {
                    twitchLoggerTextBox.AppendText($"[{DateTime.Now}] {message}{Environment.NewLine}");
                }));
            }
            else
            {
                twitchLoggerTextBox.AppendText($"[{DateTime.Now}] {message}{Environment.NewLine}");
            }
        }

        private void UpdateProgress()
        {
            if (twitchUser.CurrentDropCurrentSession != null &&
                twitchUser.CurrentDropCurrentSession.RequiredMinutesWatched > 0)
            {
                var percentage = (int)((twitchUser.CurrentDropCurrentSession.CurrentMinutesWatched /
                                        (double)twitchUser.CurrentDropCurrentSession
                                            .RequiredMinutesWatched) * 100);

                if (percentage > 100) // for some reason it gave me 101 sometimes
                {
                    percentage = 100;
                }

                progressBar.Invoke((System.Windows.Forms.MethodInvoker)(() => progressBar.Value = percentage));
                labelPercentage.Invoke((System.Windows.Forms.MethodInvoker)(() => labelPercentage.Text = $"{percentage}%"));
                labelMinRemaining.Invoke((System.Windows.Forms.MethodInvoker)(() => labelMinRemaining.Text =
                                           $"Minutes remaining : {twitchUser.CurrentDropCurrentSession.RequiredMinutesWatched - twitchUser.CurrentDropCurrentSession.CurrentMinutesWatched}"));
            }
        }

        private void InitializeListView()
        {
            // Keep the legacy ListView in the Designer for compatibility, but the
            // inventory is rendered by the tree below.
            inventoryListView.Visible = false;

            inventoryTreeImageList = new ImageList(components)
            {
                ImageSize = new Size(24, 24),
                ColorDepth = ColorDepth.Depth32Bit,
                TransparentColor = Color.Transparent
            };

            inventoryTreeView = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                FullRowSelect = true,
                HideSelection = false,
                ShowNodeToolTips = true,
                ItemHeight = 28,
                ImageList = inventoryTreeImageList,
                RightToLeft = RightToLeft.No,
                TabIndex = 0
            };

            inventoryRefreshButton = new Button
            {
                Text = "Refresh",
                Dock = DockStyle.Top,
                Height = 28,
                UseVisualStyleBackColor = true,
                RightToLeft = RightToLeft.No,
                TabIndex = 1
            };
            inventoryRefreshButton.Click += InventoryRefreshButton_Click;

            inventoryPanel = new Panel
            {
                Dock = DockStyle.Fill,
                RightToLeft = RightToLeft.No
            };
            inventoryPanel.Controls.Add(inventoryTreeView);
            inventoryPanel.Controls.Add(inventoryRefreshButton);

            groupBox2.Controls.Add(inventoryPanel);
            inventoryPanel.BringToFront();

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                IsSplitterFixed = false
            };

            currentTabPage.Controls.Add(split);
            split.Panel1.Controls.Add(Bot);
            split.Panel2.Controls.Add(groupBox2);
            Bot.Dock = DockStyle.Fill;
            groupBox2.Dock = DockStyle.Fill;
            split.BringToFront();

            var splitInitialized = false;
            void InitializeSplitter()
            {
                if (splitInitialized || split.Width < 600) return;
                split.Panel1MinSize = 350;
                split.Panel2MinSize = 220;
                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width - 300);
                splitInitialized = true;
            }

            split.SizeChanged += (_, _) => InitializeSplitter();
            // The tab page already has a real size during construction, so the
            // SizeChanged event may not fire again before the user resizes.
            InitializeSplitter();

            inventoryRefreshTimer = new System.Windows.Forms.Timer(components)
            {
                Interval = 300_000
            };
            inventoryRefreshTimer.Tick += InventoryRefreshTimer_Tick;
        }

        private void InventoryTreeView_HandleCreated(object? sender, EventArgs e)
        {
            if (IsInventoryTabVisible())
            {
                _ = RefreshInventoryAsync();
            }
        }

        private async void InventoryRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsInventoryTabVisible())
            {
                return;
            }

            await RefreshInventoryAsync();
        }

        private void TwitchUserTab_VisibleChanged(object? sender, EventArgs e)
        {
            if (!IsInventoryTabVisible())
            {
                return;
            }

            _ = RefreshInventoryAsync();
        }

        private bool IsInventoryTabVisible()
        {
            return _initialized
                && !currentTabPage.IsDisposed
                && !currentTabPage.Disposing
                && currentTabPage.Visible;
        }

        private async void InventoryRefreshButton_Click(object? sender, EventArgs e)
        {
            await RefreshInventoryAsync();
        }

        private async Task RefreshInventoryAsync()
        {
            if (!_initialized)
            {
                return;
            }

            if (Interlocked.Exchange(ref _inventoryRefreshInProgress, 1) == 1)
            {
                return;
            }

            SetInventoryRefreshButtonEnabled(false);

            try
            {
                var inventoryBeforeRefresh = twitchUser.Inventory;
                await twitchUser.RefreshInventoryAsync();

                // The setter raises PropertyChanged when a new snapshot arrives, which
                // already triggers LoadInventoryAsync. Only refresh the tree here when
                // the snapshot did not change (for example, a failed fetch).
                if (ReferenceEquals(inventoryBeforeRefresh, twitchUser.Inventory))
                {
                    await LoadInventoryAsync();
                }
            }
            catch (Exception ex)
            {
                TryAppendLog($"Inventory refresh failed: {ex.Message}");
            }
            finally
            {
                SetInventoryRefreshButtonEnabled(true);
                Interlocked.Exchange(ref _inventoryRefreshInProgress, 0);
            }
        }

        private void SetInventoryRefreshButtonEnabled(bool enabled)
        {
            var button = inventoryRefreshButton;
            if (button == null || button.IsDisposed || button.Disposing)
            {
                return;
            }

            button.Enabled = enabled;
        }

        private async Task LoadInventoryAsync()
        {
            if (!_initialized)
            {
                return;
            }

            var generation = Interlocked.Increment(ref _inventoryLoadGeneration);

            try
            {
                var items = InventoryViewBuilder.Build(twitchUser.Inventory);
                if (items.Count == 0)
                {
                    if (IsCurrentInventoryLoad(generation))
                    {
                        ShowInventoryMessage("No inventory items yet");
                    }

                    return;
                }

                await LoadInventoryImagesAsync(items);
                if (IsCurrentInventoryLoad(generation))
                {
                    UpdateInventoryTree(items);
                }
            }
            catch (Exception ex)
            {
                TryAppendLog($"Inventory display failed: {ex.Message}");
                if (IsCurrentInventoryLoad(generation))
                {
                    ShowInventoryMessage("Inventory unavailable");
                }
            }
        }

        private bool IsCurrentInventoryLoad(int generation)
        {
            if (generation != Volatile.Read(ref _inventoryLoadGeneration) || IsDisposed)
            {
                return false;
            }

            if (currentTabPage.IsDisposed || currentTabPage.Disposing)
            {
                return false;
            }

            var tree = inventoryTreeView;
            return tree == null || (!tree.IsDisposed && !tree.Disposing);
        }

        private async Task LoadInventoryImagesAsync(IReadOnlyList<InventoryViewItem> items)
        {
            var urls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.GameImageUrl))
                {
                    urls.Add(item.GameImageUrl);
                }

                if (!string.IsNullOrWhiteSpace(item.ImageUrl))
                {
                    urls.Add(item.ImageUrl);
                }
            }

            if (urls.Count == 0)
            {
                return;
            }

            await Task.WhenAll(urls.Select(EnsureInventoryImageAsync));
        }

        private async Task<bool> EnsureInventoryImageAsync(string url)
        {
            var imageList = inventoryTreeImageList;
            if (imageList == null || string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            try
            {
                lock (imageListLock)
                {
                    if (imageList.Images.ContainsKey(url))
                    {
                        return true;
                    }
                }

                var bytes = await ImageHttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    return false;
                }

                using var stream = new MemoryStream(bytes);
                using var source = new Bitmap(stream);
                using var image = new Bitmap(source, new Size(24, 24));

                var added = TryRunOnUiThread(() =>
                {
                    lock (imageListLock)
                    {
                        if (!imageList.Images.ContainsKey(url))
                        {
                            if (imageList.Images.Count > 512)
                            {
                                imageList.Images.Clear();
                            }

                            imageList.Images.Add(url, new Bitmap(image));
                        }
                    }
                });

                return added && HasInventoryImage(url);
            }
            catch (Exception ex)
            {
                TryAppendLog($"Inventory image failed ({url}): {ex.Message}");
                return false;
            }
        }

        private bool HasInventoryImage(string url)
        {
            var imageList = inventoryTreeImageList;
            if (imageList == null)
            {
                return false;
            }

            lock (imageListLock)
            {
                return imageList.Images.ContainsKey(url);
            }
        }

        private string? GetInventoryImageKey(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return HasInventoryImage(url) ? url : null;
        }

        private void UpdateInventoryTree(IReadOnlyList<InventoryViewItem> items)
        {
            TryRunOnUiThread(() =>
            {
                if (inventoryTreeView == null)
                {
                    return;
                }

                inventoryTreeView.BeginUpdate();
                try
                {
                    inventoryTreeView.Nodes.Clear();

                    var inProgress = items
                        .Where(item => item.Section == InventoryViewSection.InProgress)
                        .ToList();
                    var earned = items
                        .Where(item => item.Section == InventoryViewSection.Earned)
                        .ToList();

                    inventoryTreeView.Nodes.Add(BuildSectionNode($"In progress ({inProgress.Count})", inProgress));
                    inventoryTreeView.Nodes.Add(BuildSectionNode($"Earned ({earned.Count})", earned));
                    inventoryTreeView.ExpandAll();
                }
                finally
                {
                    inventoryTreeView.EndUpdate();
                }
            });
        }

        private TreeNode BuildSectionNode(string title, IReadOnlyList<InventoryViewItem> items)
        {
            var sectionNode = new TreeNode(title);

            var gameGroups = items
                .GroupBy(
                    item => string.IsNullOrWhiteSpace(item.GameName) ? "Unknown game" : item.GameName,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var gameGroup in gameGroups)
            {
                var gameItems = gameGroup.ToList();
                var gameNode = new TreeNode(gameGroup.Key);
                var gameImageKey = GetInventoryImageKey(
                    gameItems.Select(item => item.GameImageUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)));
                ApplyImageKey(gameNode, gameImageKey);

                var campaignGroups = gameItems
                    .GroupBy(
                        item => string.IsNullOrWhiteSpace(item.CampaignId)
                            ? $"name:{item.CampaignName}"
                            : $"id:{item.CampaignId}",
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.First().CampaignName, StringComparer.OrdinalIgnoreCase);

                foreach (var campaignGroup in campaignGroups)
                {
                    var campaignItems = campaignGroup.ToList();
                    var campaignLevelItem = campaignItems.FirstOrDefault(item => item.IsCampaignLevel);
                    var campaignName = string.IsNullOrWhiteSpace(campaignItems[0].CampaignName)
                        ? "Unknown campaign"
                        : campaignItems[0].CampaignName;
                    var endsAt = campaignItems
                        .Select(item => item.EndsAt)
                        .FirstOrDefault(value => value.HasValue);

                    var campaignText = campaignName;
                    if (endsAt.HasValue)
                    {
                        campaignText += $"  (ends {FormatInventoryDate(endsAt.Value)})";
                    }

                    if (campaignLevelItem != null && !string.IsNullOrWhiteSpace(campaignLevelItem.StatusText))
                    {
                        campaignText += $"  {campaignLevelItem.StatusText}";
                    }

                    var campaignNode = new TreeNode(campaignText)
                    {
                        ToolTipText = BuildCampaignToolTip(campaignItems, campaignLevelItem, endsAt)
                    };
                    ApplyImageKey(campaignNode, GetInventoryImageKey(campaignLevelItem?.ImageUrl) ?? gameImageKey);

                    var dropItems = campaignItems
                        .Where(item => !item.IsCampaignLevel)
                        .OrderBy(item => item.RequiredMinutes)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

                    foreach (var drop in dropItems)
                    {
                        var dropText = string.IsNullOrWhiteSpace(drop.StatusText)
                            ? drop.Name
                            : $"{drop.Name}  {drop.StatusText}";
                        var dropNode = new TreeNode(dropText)
                        {
                            ToolTipText = BuildDropToolTip(drop)
                        };
                        ApplyImageKey(dropNode, GetInventoryImageKey(drop.ImageUrl));
                        campaignNode.Nodes.Add(dropNode);
                    }

                    gameNode.Nodes.Add(campaignNode);
                }

                sectionNode.Nodes.Add(gameNode);
            }

            return sectionNode;
        }

        private static string BuildCampaignToolTip(
            IReadOnlyList<InventoryViewItem> campaignItems,
            InventoryViewItem? campaignLevelItem,
            DateTime? endsAt)
        {
            var first = campaignItems[0];
            var lines = new List<string>
            {
                $"Game: {first.GameName}",
                $"Campaign: {first.CampaignName}"
            };

            if (endsAt.HasValue)
            {
                lines.Add($"Ends: {FormatInventoryDateTime(endsAt.Value)}");
            }

            if (campaignLevelItem != null)
            {
                lines.Add($"Progress: {BuildProgressText(campaignLevelItem)}");
                lines.Add($"Status: {campaignLevelItem.StatusText}");
            }
            else
            {
                lines.Add($"Drops: {campaignItems.Count(item => !item.IsCampaignLevel)}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildDropToolTip(InventoryViewItem item)
        {
            var lines = new List<string>
            {
                $"Game: {item.GameName}",
                $"Campaign: {item.CampaignName}"
            };

            if (item.EndsAt.HasValue)
            {
                lines.Add($"Ends: {FormatInventoryDateTime(item.EndsAt.Value)}");
            }

            lines.Add($"Progress: {BuildProgressText(item)}");
            lines.Add($"Status: {item.StatusText}");

            if (item.LastAwardedAt.HasValue)
            {
                lines.Add($"Last awarded: {FormatInventoryDateTime(item.LastAwardedAt.Value)}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildProgressText(InventoryViewItem item)
        {
            if (item.RequiredMinutes > 0)
            {
                return $"{item.CurrentMinutes}/{item.RequiredMinutes} min";
            }

            return item.CurrentMinutes > 0 ? $"{item.CurrentMinutes} min" : "n/a";
        }

        private static string FormatInventoryDate(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return "unknown";
            }

            return value.ToLocalTime().ToString("yyyy-MM-dd");
        }

        private static string FormatInventoryDateTime(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return "unknown";
            }

            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        private static void ApplyImageKey(TreeNode node, string? imageKey)
        {
            if (string.IsNullOrEmpty(imageKey))
            {
                return;
            }

            node.ImageKey = imageKey;
            node.SelectedImageKey = imageKey;
        }

        private void ShowInventoryMessage(string message)
        {
            try
            {
                TryRunOnUiThread(() =>
                {
                    if (inventoryTreeView == null)
                    {
                        return;
                    }

                    inventoryTreeView.BeginUpdate();
                    try
                    {
                        inventoryTreeView.Nodes.Clear();
                        inventoryTreeView.Nodes.Add(new TreeNode(message));
                    }
                    finally
                    {
                        inventoryTreeView.EndUpdate();
                    }
                });
            }
            catch
            {
                // The control may already be disposed.
            }
        }

        private bool TryRunOnUiThread(Action action)
        {
            var tree = inventoryTreeView;
            if (tree == null || tree.IsDisposed || tree.Disposing || !tree.IsHandleCreated)
            {
                return false;
            }

            try
            {
                if (tree.InvokeRequired)
                {
                    tree.Invoke(action);
                }
                else
                {
                    action();
                }

                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (InvalidAsynchronousStateException)
            {
                return false;
            }
        }

        private void TryAppendLog(string message)
        {
            try
            {
                AppendLog(message);
            }
            catch
            {
                // Logging must never break inventory refresh.
            }
        }

        private void ReloadButton_Click(object sender, EventArgs e)
        {
            if (twitchUser.CancellationTokenSource != null &&
                !twitchUser.CancellationTokenSource.IsCancellationRequested)
            {
                // twitchUser.Logger.Info("Reload requested");
                AppendLog("Reload requested");

                twitchUser.CancellationTokenSource?.Cancel();
            }
        }
    }
}