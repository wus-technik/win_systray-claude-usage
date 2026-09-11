namespace ClaudeUsageTray.Core;

/// <summary>Whether a link out of a status payload may be handed to the shell. The incident
/// shortlink is third-party input: <see cref="StatusDetail.Rows"/> takes it verbatim from the
/// page's JSON and the popup opens it with <c>UseShellExecute</c>, which would let a compromised
/// payload pick any scheme (<c>file:</c>, a registered app protocol, a UNC path) or any host.
/// Dropping a link costs only the "Details" line under that row — the source's own page link sits
/// beside it and is shipped, not fetched.</summary>
public static class StatusLink
{
    /// <summary>Statuspage serves incident shortlinks from its own shortener rather than from the
    /// status host — status.claude.com's payload carries <c>https://stspg.io/&lt;id&gt;</c>. So the
    /// allowlist is the source's own page host plus that shortener: a strict same-host rule would
    /// drop every real incident link.</summary>
    private const string Shortener = "stspg.io";

    /// <summary>The url unchanged when it is an https link on a host this source may link to, null
    /// otherwise — including for null, a relative url, and anything unparseable.</summary>
    public static string? Safe(string? url, StatusSource source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        // Statuspage uses neither, and both are how a look-alike host hides from whoever reads the
        // link: "https://stspg.io@evil.example/" has host evil.example.
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        return Allows(uri.Host, HostOf(source.PageUrl)) || Allows(uri.Host, Shortener) ? url : null;
    }

    private static string? HostOf(string pageUrl)
        => Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>That host or a subdomain of it. The dot is why "notstspg.io" fails to match
    /// "stspg.io".</summary>
    private static bool Allows(string host, string? allowed)
        => allowed is not null
            && (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
}
