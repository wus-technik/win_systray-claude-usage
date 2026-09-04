using System.Net;

namespace ClaudeUsageTraySetupStub;

/// <summary>Three retries with exponential backoff on transient failures — 5xx, 429, 408, connection
/// errors, timeouts. Delays are a parameter so tests run with zeros. 4xx other than those are
/// returned at once: retrying a 401 or 404 only burns the rate limit.</summary>
public static class HttpRetry
{
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    /// <summary>GitHub rejects requests without a User-Agent. The version is <see
    /// cref="StubVersion.Short"/>, not `AssemblyName.Version`, which would silently drop a prerelease
    /// suffix such as `-beta.1`.</summary>
    public static readonly string UserAgent = $"ClaudeUsageTraySetup/{StubVersion.Short}";

    public static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    /// <summary>Null when every attempt threw; otherwise the last response (which may still be a
    /// transient failure if the retries ran out). The request is rebuilt per attempt because an
    /// HttpRequestMessage cannot be sent twice.</summary>
    public static async Task<HttpResponseMessage?> SendAsync(
        HttpClient http, Func<HttpRequestMessage> request, IReadOnlyList<TimeSpan> delays,
        HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(request(), completion, ct).ConfigureAwait(false);
                if (!IsTransient(response.StatusCode)) return response;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* HttpClient timeout */ }

            if (attempt >= delays.Count) return response;
            response?.Dispose();
            await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
        }
    }
}
