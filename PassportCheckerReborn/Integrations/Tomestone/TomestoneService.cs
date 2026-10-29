using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Integrations.Tomestone;

public sealed class TomestoneService : IDisposable
{
    private const string ApiBaseUrl = "https://tomestone.gg/api";
    private const int MaxConcurrentRequests = 4;

    private readonly ApiHttpClient http = new(MaxConcurrentRequests);
    private readonly ExpiringCache<(string World, string Name, string Duty), TomestoneCharacterInfo> results = new(TimeSpan.FromMinutes(5));
    private readonly ConcurrentDictionary<(string World, string Name, string Duty), Task<TomestoneCharacterInfo>> inFlight = new();

    public TomestoneService()
    {
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose()
    {
        http.Dispose();
    }

    public async Task<TomestoneCharacterInfo?> GetCharacterInfoAsync(string playerName, string world, string? dutyName = null)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(world))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dutyName))
        {
            return new TomestoneCharacterInfo();
        }

        var key = (world, playerName, dutyName);
        if (results.TryGet(key, out var cached))
        {
            return cached;
        }

        var lookup = inFlight.GetOrAdd(key, _ => LookUpAsync(playerName, world, dutyName));
        try
        {
            return await lookup;
        }
        finally
        {
            inFlight.TryRemove(KeyValuePair.Create(key, lookup));
        }
    }

    private async Task<TomestoneCharacterInfo> LookUpAsync(string playerName, string world, string dutyName)
    {
        var (encounter, fallback) = TomestoneEncounters.Find(dutyName);

        var info = new TomestoneCharacterInfo();
        var server = Uri.EscapeDataString(world);
        var name = Uri.EscapeDataString(playerName);

        var outcome = await FetchAsync(info, $"{ApiBaseUrl}/character/profile/{server}/{name}", root =>
        {
            TomestoneParser.ParseProfileEncounters(info, root, dutyName, encounter);
            if (fallback != null && !HasEncounterData(info))
            {
                TomestoneParser.ParseProfileEncounters(info, root, dutyName, fallback);
            }
        });

        info.NotFound = outcome == FetchOutcome.NotFound;
        var complete = outcome != FetchOutcome.Failed;
        if (outcome == FetchOutcome.Answered && !info.NoLogs)
        {
            foreach (var part in new[] { encounter, fallback })
            {
                if (part != null && !info.ActivityHidden && !HasEncounterData(info))
                {
                    complete &= await FetchEncounterEndpointsAsync(info, server, name, part);
                }
            }
        }

        if (HasEncounterData(info) || info.BestPercent.HasValue)
        {
            info.NoLogs = false;
        }

        info.LookupFailed = !complete;
        if (complete)
        {
            results.Set((world, playerName, dutyName), info);
        }

        return info;
    }

    private static bool HasEncounterData(TomestoneCharacterInfo info)
    {
        return info.ProgPoint != null || info.TotalClears.HasValue;
    }

    private async Task<bool> FetchEncounterEndpointsAsync(TomestoneCharacterInfo info, string server, string name, TomestoneEncounterParams encounter)
    {
        var query = $"{server}/{name}"
            + $"?expansion={Uri.EscapeDataString(encounter.Expansion)}"
            + $"&zone={Uri.EscapeDataString(encounter.Zone)}"
            + $"&encounter={Uri.EscapeDataString(encounter.Encounter)}";

        var outcomes = await Task.WhenAll(
            FetchAsync(info, $"{ApiBaseUrl}/character/progression-graph/{query}", root => info.ProgPoint = TomestoneParser.ParseProgPointFromGraph(root)),
            FetchAsync(info, $"{ApiBaseUrl}/character/activity/{query}", root => TomestoneParser.ParseActivityResponse(info, root)));
        return Array.TrueForAll(outcomes, outcome => outcome != FetchOutcome.Failed);
    }

    private async Task<FetchOutcome> FetchAsync(TomestoneCharacterInfo info, string url, Action<JsonElement> parse)
    {
        try
        {
            using var response = await http.SendAsync(() => CreateRequest(url));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FetchOutcome.NotFound;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (error.RootElement.GetStringOrNull("error")?.Contains("hidden", StringComparison.OrdinalIgnoreCase) == true)
                {
                    info.ActivityHidden = true;
                    return FetchOutcome.Answered;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Debug($"[TomestoneService] {(int)response.StatusCode} from {url}");
                return FetchOutcome.Failed;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 0)
            {
                info.NoLogs = true;
            }
            else
            {
                parse(doc.RootElement);
            }

            return FetchOutcome.Answered;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[TomestoneService] Request failed: {url}");
            return FetchOutcome.Failed;
        }
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var apiKey = Plugin.Configuration.TomestoneApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    private enum FetchOutcome
    {
        Failed,
        NotFound,
        Answered,
    }
}
