using System.Net;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class ReleaseResolverTests
{
    /// <summary>Canned responses in order; the last one repeats. Records every request.</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var responder = responders[Math.Min(Requests.Count - 1, responders.Length - 1)];
            return Task.FromResult(responder(request));
        }
    }

    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string BetaAsset = "WusTechnik.ClaudeUsageTray-win-beta-Setup.exe";
    private const string StableAsset = "WusTechnik.ClaudeUsageTray-win-Setup.exe";

    private static string Releases(params (string Tag, string Asset)[] releases)
        => "[" + string.Join(",", releases.Select(r =>
            "{ \"tag_name\": \"" + r.Tag + "\", \"draft\": false, \"prerelease\": true, \"assets\": [" +
            "{ \"name\": \"" + r.Asset + "\", \"browser_download_url\": \"https://github.com/o/r/releases/download/" + r.Tag + "/" + r.Asset + "\", \"digest\": \"sha256:ab\" }] }")) + "]";

    private static async Task<(ResolveResult Result, FakeHandler Handler)> Resolve(Ring ring, bool silent, string? token, params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        var handler = new FakeHandler(responders.Length == 0 ? [_ => Json(HttpStatusCode.OK, "[]")] : responders);
        using var http = new HttpClient(handler);
        var result = await ReleaseResolver.ResolveAsync(http, ring, token, silent, NoDelays, CancellationToken.None);
        return (result, handler);
    }

    [Fact]
    public async Task StableNeverTouchesTheApi()
    {
        var (r, h) = await Resolve(Ring.Stable, silent: true, token: null);
        Assert.Empty(h.Requests);
        Assert.Equal(ResolveFailure.None, r.Failure);
        Assert.Equal(Rings.LatestAssetUrl("win"), r.Build!.Url);
        Assert.Equal(ResolvedVia.LatestRedirect, r.Build.Via);
    }

    [Fact]
    public async Task BetaSelectsFromTheApi()
    {
        var (r, h) = await Resolve(Ring.Beta, false, null, _ => Json(HttpStatusCode.OK, Releases(("v0.7.3-beta.1", BetaAsset))));
        Assert.Equal("0.7.3-beta.1", r.Build!.Version!.ToString());
        Assert.Equal(ResolvedVia.Api, r.Build.Via);
        Assert.Equal(ReleaseResolver.ReleasesApiUrl, h.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task RequestCarriesTheGitHubHeaders()
    {
        var (_, h) = await Resolve(Ring.Beta, false, "tok", _ => Json(HttpStatusCode.OK, "[]"));
        var req = h.Requests.Single();
        Assert.Equal("application/vnd.github+json", req.Headers.Accept.Single().MediaType);
        Assert.Equal("2022-11-28", req.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.StartsWith("ClaudeUsageTraySetup/", req.Headers.UserAgent.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("tok", req.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task NoTokenMeansNoAuthorizationHeader()
    {
        var (_, h) = await Resolve(Ring.Beta, false, null, _ => Json(HttpStatusCode.OK, "[]"));
        Assert.Null(h.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public async Task ApiFineButNoAssetIsAHardError()
    {
        // Nothing to fall back to; installing the other ring's content would be wrong.
        var (r, _) = await Resolve(Ring.Beta, silent: false, null, _ => Json(HttpStatusCode.OK, Releases(("v0.7.2", StableAsset))));
        Assert.Null(r.Build);
        Assert.Equal(ResolveFailure.NoAssetInAnyRelease, r.Failure);
    }

    [Fact]
    public async Task ApiUnavailableInteractiveFallsBackToTheLatestRedirect()
    {
        var (r, _) = await Resolve(Ring.Beta, silent: false, null, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(ResolveFailure.None, r.Failure);
        Assert.Equal(ResolvedVia.LatestRedirect, r.Build!.Via);
        Assert.Equal(Rings.LatestAssetUrl("win-beta"), r.Build.Url);
        Assert.Contains("503", r.Detail);
    }

    [Fact]
    public async Task ApiUnavailableSilentFailsClosed()
    {
        // An operator asked for beta; silently installing stable content behind their back is the one thing not to do.
        var (r, _) = await Resolve(Ring.Beta, silent: true, null, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(r.Build);
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public async Task RateLimitedCountsAsUnavailableNotAsNoAsset()
    {
        var (r, _) = await Resolve(Ring.Beta, silent: true, null, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public async Task TransientFailuresAreRetriedThreeTimes()
    {
        var (r, h) = await Resolve(Ring.Beta, false, null,
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => Json(HttpStatusCode.OK, Releases(("v0.7.3-beta.1", BetaAsset))));
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal("0.7.3-beta.1", r.Build!.Version!.ToString());
    }

    [Fact]
    public async Task GivesUpAfterTheConfiguredRetries()
    {
        var (_, h) = await Resolve(Ring.Beta, true, null, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Equal(1 + NoDelays.Length, h.Requests.Count);
    }

    [Fact]
    public async Task NetworkExceptionsCountAsUnavailable()
    {
        var (r, _) = await Resolve(Ring.Beta, true, null, _ => throw new HttpRequestException("dns"));
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public async Task MalformedJsonCountsAsUnavailable()
    {
        var (r, _) = await Resolve(Ring.Beta, true, null, _ => Json(HttpStatusCode.OK, "{ not json"));
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public async Task DetailNeverContainsTheToken()
    {
        var (r, _) = await Resolve(Ring.Beta, true, "sekrit", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.DoesNotContain("sekrit", r.Detail);
    }
}
