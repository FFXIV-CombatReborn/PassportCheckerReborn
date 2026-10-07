using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

// Caps the requests in flight to one API, and waits out a 429 once before giving up.
internal sealed class ApiHttpClient : IDisposable
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim gate;

    public ApiHttpClient(int maxConcurrentRequests)
    {
        gate = new SemaphoreSlim(maxConcurrentRequests);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");
    }

    public HttpRequestHeaders DefaultRequestHeaders => httpClient.DefaultRequestHeaders;

    public void Dispose()
    {
        httpClient.Dispose();
    }

    // A request message can only be sent once, so a retry builds a new one.
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest)
    {
        await gate.WaitAsync();
        try
        {
            var response = await SendOnceAsync(createRequest);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || GetRetryDelay(response) is not { } delay)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(delay);
            return await SendOnceAsync(createRequest);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> createRequest)
    {
        using var request = createRequest();
        return await httpClient.SendAsync(request);
    }

    // Null when the API asks for a longer wait than is worth holding a lookup for.
    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? DefaultRetryDelay;
        return delay > MaxRetryDelay ? null
            : delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay;
    }
}
