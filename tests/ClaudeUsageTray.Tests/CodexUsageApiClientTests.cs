using System.Net;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Canned responses only. The endpoint's request budget is unmeasured and shared with the
/// user's real Codex sessions, so no test ever reaches the network.</summary>
public class CodexUsageApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    private static readonly CodexAuth Auth =
        new("dummy.token.value", "00000000-0000-0000-0000-000000000000", Now.AddDays(1));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string Ok = """
        { "rate_limit": { "primary_window":   { "used_percent": 42, "reset_at": 1791932468 },
                          "secondary_window": { "used_percent": 21, "reset_at": 1792474516 } } }
        """;

    private static (UsageFetchResult Result, FakeHandler Handler) Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond, CodexAuth? auth = null)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = CodexUsageApiClient.FetchAsync(http, auth ?? Auth, Now, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void Fetch_Success_ReturnsASnapshot()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.OK, Ok));

        Assert.Equal(42, result.Snapshot!.FiveHour!.Percent);
        Assert.Equal(UsageSource.Codex, result.Snapshot.Source);
        Assert.False(result.Unauthorized);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public void Fetch_SendsBearerTokenAndAccountHeader()
    {
        var (_, handler) = Fetch(_ => Json(HttpStatusCode.OK, Ok));
        var request = handler.LastRequest!;

        Assert.Equal(CodexUsageApiClient.EndpointUrl, request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Auth.AccessToken, request.Headers.Authorization.Parameter);
        Assert.Equal("00000000-0000-0000-0000-000000000000",
            Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
    }

    [Fact]
    public void Fetch_WithoutAnAccountId_OmitsTheHeader()
    {
        var (_, handler) = Fetch(_ => Json(HttpStatusCode.OK, Ok),
            auth: new CodexAuth("dummy.token.value", null, Now.AddDays(1)));

        Assert.False(handler.LastRequest!.Headers.Contains("ChatGPT-Account-Id"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Fetch_Rejected_IsUnauthorized(HttpStatusCode status)
    {
        var (result, _) = Fetch(_ => Json(status, "{}"));

        Assert.True(result.Unauthorized);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Fetch_TooManyRequests_IsRateLimitedWithRetryAfter()
    {
        var (result, _) = Fetch(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(30));
            return response;
        });

        Assert.True(result.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public void Fetch_ServerError_IsAPlainFailure()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.InternalServerError, "{}"));

        Assert.Null(result.Snapshot);
        Assert.False(result.Unauthorized);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public void Fetch_SuccessWithUnusableBody_IsAPlainFailure()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.OK, "{ not json"));

        Assert.Null(result.Snapshot);
        Assert.False(result.Unauthorized);
    }

    [Fact]
    public async Task Fetch_NetworkError_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("down"));
        using var http = new HttpClient(handler);

        var result = await CodexUsageApiClient.FetchAsync(http, Auth, Now, CancellationToken.None);

        Assert.Null(result.Snapshot);
    }

    /// <summary>auth.json is third-party input. A control character in the account id would make
    /// HttpHeaders.Add throw FormatException — the header is dropped instead, because an unusable
    /// account id should cost the header, not the whole fetch.</summary>
    [Fact]
    public void Fetch_WithAnIllegalAccountId_SendsTheRequestWithoutTheHeader()
    {
        var (result, handler) = Fetch(_ => Json(HttpStatusCode.OK, Ok),
            auth: new CodexAuth("dummy.token.value", "bad\nvalue", Now.AddDays(1)));

        Assert.False(handler.LastRequest!.Headers.Contains("ChatGPT-Account-Id"));
        Assert.NotNull(result.Snapshot);
    }

    /// <summary>And an unusable token fails the fetch rather than throwing out of it.</summary>
    [Fact]
    public void Fetch_WithAnIllegalToken_NeverThrows()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.OK, Ok),
            auth: new CodexAuth("bad\ntoken", null, Now.AddDays(1)));

        Assert.Null(result.Snapshot);
    }
    [Fact]
    public async Task Fetch_DisposedHttpClient_NeverThrows()
    {
        using var http = new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, Ok)));
        http.Dispose();
        var result = await CodexUsageApiClient.FetchAsync(http, Auth, Now, CancellationToken.None);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Fetch_IoFailure_NeverThrows()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new IOException("unreadable")));
        var result = await CodexUsageApiClient.FetchAsync(http, Auth, Now, CancellationToken.None);
        Assert.Null(result.Snapshot);
    }
}
