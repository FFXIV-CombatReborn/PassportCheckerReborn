using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Integrations.FFLogs;

internal sealed class FFLogsTokenProvider(ApiHttpClient http)
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";

    private readonly ApiHttpClient http = http;
    private readonly SemaphoreSlim tokenLock = new(1, 1);

    private string? cachedToken;
    private (string ClientId, string ClientSecret) tokenCredentials;
    private DateTime tokenExpiry;

    public async Task<bool> TestCredentialsAsync(string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return false;
        }

        await tokenLock.WaitAsync();
        try
        {
            return await FetchTokenAsync(clientId, clientSecret) is not null;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task<string?> GetTokenAsync()
    {
        var cfg = Plugin.Configuration;
        if (!cfg.HasFFLogsCredentials())
        {
            return null;
        }

        await tokenLock.WaitAsync();
        try
        {
            var credentials = (ClientId: cfg.FFLogsClientId, ClientSecret: cfg.FFLogsClientSecret);
            return cachedToken is not null && tokenCredentials == credentials && DateTime.UtcNow < tokenExpiry
                ? cachedToken
                : await FetchTokenAsync(credentials.ClientId, credentials.ClientSecret);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public void Invalidate(string token)
    {
        Interlocked.CompareExchange(ref cachedToken, null, token);
    }

    private async Task<string?> FetchTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", basic) },
                Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"),
            });

            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Warning($"[FFLogsTokenProvider] Token request failed: {(int)response.StatusCode}");
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (doc.RootElement.GetStringOrNull("access_token") is not { } token)
            {
                return null;
            }

            tokenExpiry = DateTime.UtcNow.AddSeconds((doc.RootElement.GetNumberOrNull("expires_in") ?? 3600) - 60);
            tokenCredentials = (clientId, clientSecret);
            return cachedToken = token;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[FFLogsTokenProvider] Exception during token fetch.");
            return null;
        }
    }
}
