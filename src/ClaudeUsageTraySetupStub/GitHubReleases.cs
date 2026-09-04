using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("ClaudeUsageTraySetupStub.Tests")]

namespace ClaudeUsageTraySetupStub;

/// <summary>The fields of `GET /repos/{owner}/{repo}/releases` the resolver reads. Classes, not
/// records, so the source generator can populate them without a constructor contract.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    /// <summary>`sha256:&lt;hex&gt;`, reported by the API since 2025. Null on older assets.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>Reflection-based System.Text.Json is not AOT-safe; this is the whole JSON surface.</summary>
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

public static class ReleaseSelection
{
    /// <summary>The newest release, by parsed tag, that carries the ring's Setup.exe. Drafts and
    /// tags that are not SemVer are skipped. Null when no release carries the asset — which is a
    /// hard error for the caller, not a fallback case.</summary>
    public static ResolvedBuild? Select(IEnumerable<GitHubRelease> releases, Ring ring)
    {
        var channel = Rings.Channel(ring);
        var assetName = Rings.SetupAssetName(channel);
        ResolvedBuild? best = null;
        foreach (var release in releases)
        {
            if (release.Draft) continue;
            var version = SemVer.TryParse(release.TagName);
            if (version is null) continue;
            var asset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null
                || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url)) continue;
            if (best is null || version.CompareTo(best.Version) > 0)
                best = new ResolvedBuild(ring, channel, version, url, asset.Digest, ResolvedVia.Api);
        }
        return best;
    }
}
