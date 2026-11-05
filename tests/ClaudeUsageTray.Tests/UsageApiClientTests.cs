using System.Net;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Captures the outgoing request and returns a canned response.</summary>
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

    private const string BothWindows = """
        { "five_hour": { "utilization": 42, "resets_at": "2026-07-24T18:39:59Z" },
          "seven_day": { "utilization": 13, "resets_at": "2026-07-27T15:59:59Z" },
          "extra_usage": {} }
        """;

    private static async Task<(UsageFetchResult Result, FakeHandler Handler)> Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = await UsageApiClient.FetchAsync(http, "dummy-token", Now, CancellationToken.None);
        return (result, handler);
    }

    [Fact]
    public async Task Success_ParsesBothWindows_AndStampsNow()
    {
        var (r, _) = await Fetch(_ => Json(HttpStatusCode.OK, BothWindows));
        Assert.NotNull(r.Snapshot);
        Assert.Equal(Now, r.Snapshot!.FetchedAt);
        Assert.Equal(42, r.Snapshot.FiveHour!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 18, 39, 59, TimeSpan.Zero), r.Snapshot.FiveHour.ResetsAt);
        Assert.Equal(13, r.Snapshot.SevenDay!.Percent);
        Assert.False(r.Unauthorized);
        Assert.Null(r.RetryAfter);
    }

    [Fact]
    public async Task Request_HasExactUrlAndHeaders()
    {
        var (_, h) = await Fetch(_ => Json(HttpStatusCode.OK, BothWindows));
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", h.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("dummy-token", h.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(h.LastRequest.Headers.GetValues("anthropic-beta")));
        Assert.StartsWith("ClaudeUsageTray/", h.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Success_ParsesDecimalUtilization_RoundedToInt()
    {
        // The live endpoint returns utilization as a JSON decimal (e.g. 11.0 / 53.6), unlike the
        // .claude.json cache which stores integers. TryGetInt32 rejects fractional numbers, so this
        // regressed to null windows (200 OK but no data). Must parse and round instead.
        var body = """
            { "five_hour": { "utilization": 11.0, "resets_at": "2026-07-24T18:39:59Z" },
              "seven_day": { "utilization": 53.6, "resets_at": "2026-07-27T15:59:59Z" } }
            """;
        var (r, _) = await Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.Equal(11, r.Snapshot!.FiveHour!.Percent);
        Assert.Equal(54, r.Snapshot.SevenDay!.Percent);
    }

    [Fact]
    public async Task Success_MissingWindow_IsNull()
    {
        var (r, _) = await Fetch(_ => Json(HttpStatusCode.OK, """{ "five_hour": { "utilization": 7 } }"""));
        Assert.Equal(7, r.Snapshot!.FiveHour!.Percent);
        Assert.Null(r.Snapshot.FiveHour.ResetsAt);
        Assert.Null(r.Snapshot.SevenDay);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1]")]
    public async Task MalformedBody_ReturnsFailure(string body)
    {
        var (r, _) = await Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthFailure_SetsUnauthorized(HttpStatusCode status)
    {
        var (r, _) = await Fetch(_ => new HttpResponseMessage(status));
        Assert.Null(r.Snapshot);
        Assert.True(r.Unauthorized);
    }

    [Fact]
    public async Task TooManyRequests_CarriesRetryAfterDelta()
    {
        var (r, _) = await Fetch(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1200));
            return resp;
        });
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
        Assert.True(r.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(1200), r.RetryAfter);
    }

    [Fact]
    public async Task TooManyRequests_WithHttpDateRetryAfter_ComputesDeltaAgainstNow()
    {
        var (r, _) = await Fetch(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(30));
            return resp;
        });
        Assert.True(r.RateLimited);
        Assert.Equal(TimeSpan.FromMinutes(30), r.RetryAfter);
    }

    [Fact]
    public async Task TooManyRequests_WithoutHeader_IsStillRateLimited()
    {
        var (r, _) = await Fetch(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Assert.Null(r.Snapshot);
        Assert.True(r.RateLimited);
        Assert.Null(r.RetryAfter);
    }

    [Fact]
    public async Task ServerError_ReturnsFailure_NotRateLimited()
    {
        var (r, _) = await Fetch(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
        Assert.False(r.RateLimited);
    }

    [Fact]
    public async Task NetworkException_ReturnsFailure_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));
        using var http = new HttpClient(handler);
        var r = await UsageApiClient.FetchAsync(http, "dummy-token", Now, CancellationToken.None);
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
    }

    /// <summary>The API returns limits[] at the response root, where the cache nests it under
    /// "utilization" — this proves the shared reader works at both levels. Percent arrives as a
    /// decimal here, as the live API sends it.</summary>
    [Fact]
    public async Task ScopedLimits_AreReadFromTheResponseRoot()
    {
        const string body = """
            { "five_hour": { "utilization": 11.0, "resets_at": "2026-07-24T16:00:00Z" },
              "limits": [
                { "group": "weekly", "percent": 99.6, "is_active": true,
                  "resets_at": "2026-07-27T16:00:00Z",
                  "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
              ] }
            """;

        var (r, _) = await Fetch(_ => Json(HttpStatusCode.OK, body));

        var limit = Assert.Single(r.Snapshot!.ScopedLimits);
        Assert.Equal("Fable", limit.Label);
        Assert.Equal(100, limit.Percent);
        Assert.True(limit.IsActive);
    }

    [Fact]
    public async Task ScopedLimits_AbsentFromResponse_DegradeToEmpty()
        => Assert.Empty((await Fetch(_ => Json(HttpStatusCode.OK, BothWindows))).Result.Snapshot!.ScopedLimits);

    [Fact]
    public async Task Credits_AreReadFromTheResponseRoot()
    {
        const string body = """
            { "spend": {
                "used":  { "amount_minor": 4001, "currency": "EUR", "exponent": 2 },
                "limit": { "amount_minor": 4000, "currency": "EUR", "exponent": 2 },
                "percent": 100, "severity": "critical", "enabled": true
              } }
            """;

        var (r, _) = await Fetch(_ => Json(HttpStatusCode.OK, body));

        Assert.Equal(4001, r.Snapshot!.Credits!.Used!.AmountMinor);
        Assert.True(r.Snapshot.Credits.State.LimitReached);
    }

    [Fact]
    public async Task Credits_AbsentFromResponse_IsNull()
        => Assert.Null((await Fetch(_ => Json(HttpStatusCode.OK, BothWindows))).Result.Snapshot!.Credits);
}
