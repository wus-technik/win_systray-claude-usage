using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Read-only client for the Codex usage endpoint. Never throws; never logs the token.
/// Returns the same <see cref="UsageFetchResult"/> the Claude client does, so TrayApp's outcome
/// handling — adopt, back off, arm — is one shape for both providers.</summary>
public static class CodexUsageApiClient
{
    public const string EndpointUrl = "https://chatgpt.com/backend-api/wham/usage";

    private static readonly string UserAgent =
        $"ClaudeUsageTray/{typeof(CodexUsageApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static async Task<UsageFetchResult> FetchAsync(
        HttpClient http, CodexAuth auth, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            // Omitted rather than sent empty when absent: the endpoint resolves the account from the
            // token, and a blank header is a worse request than none. Also omitted when the value
            // would not be a legal header: auth.json is third-party input, and
            // HttpHeaders.Add throws FormatException on a control character — a throw this method
            // promises never to make.
            if (HeaderSafe(auth.AccountId))
                request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
            request.Headers.Accept.ParseAdd("application/json");
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

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new UsageFetchResult(CodexUsageJson.Parse(body, now), false, false, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or JsonException or FormatException or ArgumentException
            or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // FormatException/ArgumentException: AuthenticationHeaderValue and Headers.Add reject
            // values with control characters, and the token comes from a file this app does not own.
            return new UsageFetchResult(null, false, false, null);
        }
    }

    /// <summary>Whether a value can legally be a header. Checked rather than caught where it is
    /// cheap to check: an unusable account id should mean "send the request without the header",
    /// not "fail the fetch".</summary>
    private static bool HeaderSafe(string? value)
        => !string.IsNullOrEmpty(value) && !value.Any(c => char.IsControl(c));
}
