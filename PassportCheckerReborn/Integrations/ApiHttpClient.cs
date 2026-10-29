using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Integrations;

internal sealed class ApiHttpClient : IDisposable
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    private readonly HttpClient httpClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly SemaphoreSlim gate;

    public ApiHttpClient(int maxConcurrentRequests)
    {
        gate = new SemaphoreSlim(maxConcurrentRequests);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{Plugin.Version}");
    }

    public HttpRequestHeaders DefaultRequestHeaders => httpClient.DefaultRequestHeaders;

    public void Dispose()
    {
        httpClient.Dispose();
    }

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

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? DefaultRetryDelay;
        return delay > MaxRetryDelay ? null
            : delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay;
    }
}
