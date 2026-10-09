using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Twitch.Settings;
using Constant = TwitchDropsBot.Core.Platform.Twitch.Utils.Constant;

namespace TwitchDropsBot.Core.Platform.Twitch.Services;

public class TwitchAuthService
{
    // SMARTBOX (Android TV) client id: the only official client that still allows
    // external device-code login (Twitch disabled it for the Android app client id
    // in September 2026). Tokens issued for this client work with the bot's requests
    // (they are long-lived and accepted by GQL with the Android Client-Id header).
    private const string DeviceFlowClientId = "ue6666qo983tsx6so1t0vnawi233wa";

    public static async Task<JsonDocument> GetCodeAsync()
    {
        var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/device");
        var content = new MultipartFormDataContent();

        content.Add(new StringContent(DeviceFlowClientId), "client_id");
        content.Add(
            new StringContent("channel_read chat:read user_blocks_edit user_blocks_read user_follows_edit user_read"),
            "scopes");
        request.Content = content;

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(responseContent);
    }
    
    public static async Task<JsonDocument?> CodeConfirmationAsync(string deviceCode, ILogger logger, CancellationToken? token = default)
    {
        // Poll for up to 20 minutes (device codes are valid for 30 minutes).
        for (int i = 0; i < 240; i++)
        {
            if (token != null && token.Value.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
            await Task.Delay(TimeSpan.FromSeconds(5));

            var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token");
            var content = new MultipartFormDataContent();

            content.Add(new StringContent(DeviceFlowClientId), "client_id");
            content.Add(new StringContent(deviceCode), "device_code");
            content.Add(new StringContent("urn:ietf:params:oauth:grant-type:device_code"), "grant_type");
            request.Content = content;

            var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                logger.LogInformation("Waiting for user to authenticate...");
                continue;
            }

            var responseContent = await response.Content.ReadAsStringAsync();

            return JsonDocument.Parse(responseContent);
        }

        return null;
    }

    public static async Task<TwitchUserSettings> ClientSecretUserAsync(string secret)
    {
        var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Add("Authorization", "OAuth " + secret);
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        var jsonResponse = JsonDocument.Parse(responseContent);

        var UserConfig = new TwitchUserSettings();
        UserConfig.ClientSecret = secret;
        UserConfig.Id = jsonResponse.RootElement.GetProperty("user_id").GetString();
        UserConfig.Login = jsonResponse.RootElement.GetProperty("login").GetString();


        // Do request to TwitchClient URL to get the unique id
        request = new HttpRequestMessage(HttpMethod.Get, "https://m.twitch.tv");
        request.Headers.Add("Accept", "*/*");

        response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        // Get header set cookie and extract the unique id
        var setCookie = response.Headers.GetValues("Set-Cookie");

        foreach (var cookie in setCookie)
        {
            if (cookie.Contains("unique_id="))
            {
                // Extract the unique id
                UserConfig.UniqueId = cookie.Split(";").First().Split("=").Last();
                break;
            }
        }

        UserConfig.FavouriteGames = new List<string>();
        UserConfig.Enabled = true;

        return UserConfig;
    }
}