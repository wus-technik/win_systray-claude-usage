using System.Security.Cryptography;

namespace ClaudeUsageTraySetupStub;

public static class Downloader
{
    /// <summary>Streams the asset to <paramref name="destinationPath"/>. False on any failure; the
    /// caller decides whether a partial file matters (it deletes the whole temp directory anyway).
    /// No resume: a 58 MB retry is cheaper than the state to track one.</summary>
    public static async Task<bool> DownloadAsync(
        HttpClient http, Uri url, string destinationPath, IReadOnlyList<TimeSpan> retryDelays,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await HttpRetry.SendAsync(http, () => BuildRequest(url), retryDelays,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode) return false;

        try
        {
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total is > 0) progress?.Report(Math.Min(1.0, (double)done / total.Value));
            }
            progress?.Report(1.0);
            return true;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HttpRequestMessage BuildRequest(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(HttpRetry.UserAgent);
        return request;
    }
}

public enum VerifyOutcome { Ok, Empty, NotExecutable, DigestMismatch }

/// <summary>The checks the stub *can* make on what it is about to execute. Nothing this project ships
/// is code-signed, so there is no Authenticode to verify; the trust anchor is TLS to github.com plus
/// the API's per-asset sha256 digest where one exists.</summary>
public static class DownloadVerification
{
    public static VerifyOutcome Verify(string path, string? expectedDigest)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return VerifyOutcome.Empty;

        using var stream = File.OpenRead(path);
        var header = new byte[2];
        // A rate-limit HTML page or an error body saved as .exe must never be executed.
        if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return VerifyOutcome.NotExecutable;
        if (expectedDigest is null) return VerifyOutcome.Ok;

        stream.Position = 0;
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return DigestMatches(expectedDigest, actual) ? VerifyOutcome.Ok : VerifyOutcome.DigestMismatch;
    }

    /// <summary>`sha256:&lt;hex&gt;` only. Any other algorithm fails closed: a digest the stub cannot
    /// check is not a digest it may ignore.</summary>
    public static bool DigestMatches(string expected, string actualSha256Hex)
    {
        const string prefix = "sha256:";
        return expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected[prefix.Length..].Trim(), actualSha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
