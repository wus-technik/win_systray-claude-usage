using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTraySetupStub;

/// <summary>Why no build was resolved. The two kinds must never be collapsed: *unavailable* has a
/// fallback (interactively), *no asset anywhere* has nothing to fall back to and installing the
/// other ring's content would be wrong.</summary>
public enum ResolveFailure { None, ApiUnavailable, NoAssetInAnyRelease }

public sealed record ResolveResult(ResolvedBuild? Build, ResolveFailure Failure, string Detail);

/// <summary>Finds the newest release for a ring. Stable needs no API call — GitHub's
/// `/releases/latest` redirect is the version-independence. Beta cannot avoid one, because betas are
/// GitHub pre-releases and `/releases/latest` skips them by definition.</summary>
public static class ReleaseResolver
{
    /// <summary>One page of 100 is the whole query: years of releases at this cadence.</summary>
    public const string ReleasesApiUrl = "https://api.github.com/repos/" + Rings.Repository + "/releases?per_page=100";

    public static async Task<ResolveResult> ResolveAsync(
        HttpClient http, Ring ring, string? token, bool silent, IReadOnlyList<TimeSpan> retryDelays, CancellationToken ct)
    {
        if (ring == Ring.Stable)
            return new ResolveResult(ResolvedBuild.LatestOnChannel(Ring.Stable), ResolveFailure.None, "stable: /releases/latest redirect, no API call");

        using var response = await HttpRetry.SendAsync(http, () => BuildRequest(token), retryDelays,
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode)
            return Unavailable(response is null ? "no response" : $"HTTP {(int)response.StatusCode}", silent);

        List<GitHubRelease>? releases;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            releases = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.ListGitHubRelease, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Unavailable("HTTP 200 with unreadable JSON", silent);
        }

        var build = ReleaseSelection.Select(releases ?? [], ring);
        return build is null
            ? new ResolveResult(null, ResolveFailure.NoAssetInAnyRelease,
                $"HTTP 200, {releases?.Count ?? 0} releases, none carries {Rings.SetupAssetName(Rings.Channel(ring))}")
            : new ResolveResult(build, ResolveFailure.None, $"HTTP 200; selected {build.Version}");
    }

    /// <summary>Interactively the beta ring degrades to the latest stable build on the win-beta
    /// channel — the wizard says so via Describe. Silently it fails closed: an operator who asked for
    /// beta must not get stable content behind their back.</summary>
    private static ResolveResult Unavailable(string detail, bool silent) => silent
        ? new ResolveResult(null, ResolveFailure.ApiUnavailable, $"GitHub API unavailable ({detail}); --silent does not fall back")
        : new ResolveResult(ResolvedBuild.LatestOnChannel(Ring.Beta), ResolveFailure.None,
            $"GitHub API unavailable ({detail}); falling back to the latest stable build on win-beta");

    private static HttpRequestMessage BuildRequest(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd(HttpRetry.UserAgent);
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
