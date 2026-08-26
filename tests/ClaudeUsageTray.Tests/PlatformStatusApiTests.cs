using System.IO;
using System.Net;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class PlatformStatusApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

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

    private const string AllOperational = """
        {
          "page": { "id": "tymt9n04zgry", "name": "Claude" },
          "status": { "indicator": "none", "description": "All Systems Operational" },
          "components": [ { "id": "rwppv331jlwc", "name": "claude.ai", "status": "operational" } ],
          "incidents": [],
          "scheduled_maintenances": []
        }
        """;

    private static (PlatformStatus? Status, FakeHandler Handler) Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void AllOperational_Parses_AndIsNotDegraded()
    {
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, AllOperational));
        Assert.NotNull(s);
        Assert.Equal(Now, s!.FetchedAt);
        Assert.Equal("none", s.Indicator);
        Assert.Equal("All Systems Operational", s.Description);
        Assert.Empty(s.Incidents);
        Assert.False(s.Degraded);
    }

    [Fact]
    public void Request_HasExactUrlAndUserAgent_NoAuthorization()
    {
        var (_, h) = Fetch(_ => Json(HttpStatusCode.OK, AllOperational));
        Assert.Equal("https://status.claude.com/api/v2/summary.json", h.LastRequest!.RequestUri!.ToString());
        Assert.StartsWith("ClaudeUsageTray/", h.LastRequest.Headers.UserAgent.ToString());
        Assert.Null(h.LastRequest.Headers.Authorization);
    }

    [Fact]
    public void TestOnlyEndpointOverride_UsesProvidedUrl()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, AllOperational));
        using var http = new HttpClient(handler);
        var result = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None,
            "http://localhost:8080/summary.json").GetAwaiter().GetResult();
        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080/summary.json", handler.LastRequest!.RequestUri!.ToString());
    }


    [Fact]
    public void Degraded_ParsesIncidents_WithAndWithoutComponents()
    {
        const string body = """
            {
              "status": { "indicator": "major", "description": "Major outage" },
              "incidents": [
                { "name": "Elevated error rates on claude.ai", "status": "investigating",
                  "impact": "major", "shortlink": "https://sta.us/abcd",
                  "updated_at": "2026-08-26T11:56:00Z",
                  "components": [ { "name": "claude.ai" } ] },
                { "name": "Slow API responses", "status": "monitoring" }
              ]
            }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.NotNull(s);
        Assert.True(s!.Degraded);
        Assert.Equal("Major outage", s.Description);

        var first = s.Incidents[0];
        Assert.Equal("Elevated error rates on claude.ai", first.Name);
        Assert.Equal("investigating", first.Status);
        Assert.Equal("major", first.Impact);
        Assert.Equal("https://sta.us/abcd", first.Shortlink);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 11, 56, 0, TimeSpan.Zero), first.UpdatedAt);
        Assert.Equal(new[] { "claude.ai" }, first.Components);

        var second = s.Incidents[1];
        Assert.Equal("Slow API responses", second.Name);
        Assert.Equal("monitoring", second.Status);
        Assert.Null(second.Impact);
        Assert.Null(second.Shortlink);
        Assert.Null(second.UpdatedAt);
        Assert.Empty(second.Components);
    }

    [Fact]
    public void Incident_MissingName_IsSkipped_OthersKept()
    {
        const string body = """
            { "status": { "indicator": "minor", "description": "Minor outage" },
              "incidents": [
                { "status": "monitoring", "impact": "minor" },
                { "name": "Slow responses", "status": "monitoring" } ] }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        var incident = Assert.Single(s!.Incidents);
        Assert.Equal("Slow responses", incident.Name);
    }

    [Fact]
    public void Incident_MissingStatus_FallsBackToUnknown()
    {
        const string body = """
            { "status": { "indicator": "minor", "description": "Minor outage" },
              "incidents": [ { "name": "Something is off" } ] }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.Equal("unknown", Assert.Single(s!.Incidents).Status);
    }

    [Fact]
    public void Incident_MalformedUpdatedAt_IsKeptWithNullTimestamp()
    {
        const string body = """
            { "status": { "indicator": "minor", "description": "Minor outage" },
              "incidents": [ { "name": "Something is off", "status": "monitoring",
                                "updated_at": "yesterday" } ] }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        var incident = Assert.Single(s!.Incidents);
        Assert.Equal("Something is off", incident.Name);
        Assert.Null(incident.UpdatedAt);
    }

    [Fact]
    public void MissingStatusObject_ReturnsNull()
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK, """{ "incidents": [] }""")).Status);

    [Fact]
    public void MissingIndicator_ReturnsNull()
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK,
            """{ "status": { "description": "All Systems Operational" } }""")).Status);

    [Theory]
    [InlineData("[1]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void NonObjectRoot_ReturnsNull(string body)
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK, body)).Status);

    [Fact]
    public void MalformedBody_ReturnsNull()
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK, "{ not json")).Status);

    [Fact]
    public void UnknownIndicator_IsDegraded()
    {
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK,
            """{ "status": { "indicator": "weird-new-value", "description": "Whatever" } }"""));
        Assert.NotNull(s);
        Assert.True(s!.Degraded);
        Assert.Equal("weird-new-value", s.Indicator);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void NonSuccess_ReturnsNull(HttpStatusCode status)
        => Assert.Null(Fetch(_ => Json(status, AllOperational)).Status);

    [Fact]
    public void Timeout_ReturnsNull_NeverThrows()
    {
        // HttpClient surfaces a timeout as a TaskCanceledException.
        var handler = new FakeHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler);
        var r = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(r);
    }

    [Fact]
    public void NetworkException_ReturnsNull_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));
        using var http = new HttpClient(handler);
        var r = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(r);
    }

    [Fact]
    public void IOException_ReturnsNull_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new IOException("stream died"));
        using var http = new HttpClient(handler);
        var r = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(r);
    }
}