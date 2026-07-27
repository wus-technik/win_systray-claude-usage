using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Outcome of one usage-API fetch: Snapshot set on success; Unauthorized on 401/403;
/// RateLimited on ANY 429 (so throttles are never mistaken for network errors);
/// RetryAfter from the 429's header — delta form, or HTTP-date computed against now.</summary>
public sealed record UsageFetchResult(UsageSnapshot? Snapshot, bool Unauthorized, bool RateLimited, TimeSpan? RetryAfter);

/// <summary>Read-only client for Anthropic's OAuth usage endpoint. Never throws; never logs the token.</summary>
public static class UsageApiClient
{
    public const string EndpointUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly string UserAgent =
        $"ClaudeUsageTray/{typeof(UsageApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static async Task<UsageFetchResult> FetchAsync(
        HttpClient http, string accessToken, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new UsageFetchResult(null, Unauthorized: true, RateLimited: false, RetryAfter: null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var header = response.Headers.RetryAfter;
                TimeSpan? retryAfter = header?.Delta ?? (header?.Date is { } date ? date - now : null);
                return new UsageFetchResult(null, false, RateLimited: true, retryAfter);
            }
            if (!response.IsSuccessStatusCode)
                return new UsageFetchResult(null, false, false, null);

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new UsageFetchResult(null, false, false, null);

            var five = UsageJson.ReadWindow(doc.RootElement, "five_hour");
            var seven = UsageJson.ReadWindow(doc.RootElement, "seven_day");
            var scoped = UsageJson.ReadScopedLimits(doc.RootElement);
            var credits = UsageJson.ReadCredits(doc.RootElement);
            return new UsageFetchResult(
                new UsageSnapshot(now, five, seven, scoped, credits), false, false, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or JsonException)
        {
            return new UsageFetchResult(null, false, false, null);
        }
    }
}
