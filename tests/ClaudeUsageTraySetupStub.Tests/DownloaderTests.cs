using System.Net;
using System.Security.Cryptography;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class DownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stub-tests-" + Guid.NewGuid().ToString("N"));
    public DownloaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] FakePe(int length)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static string Sha256Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- verification ----

    [Fact]
    public void ZeroLengthIsRejected()
        => Assert.Equal(VerifyOutcome.Empty, DownloadVerification.Verify(Write("a.exe", []), null));

    [Fact]
    public void MissingFileIsRejectedAsEmpty()
        => Assert.Equal(VerifyOutcome.Empty, DownloadVerification.Verify(Path.Combine(_dir, "nope.exe"), null));

    [Fact]
    public void NonPeIsRejected()
        => Assert.Equal(VerifyOutcome.NotExecutable, DownloadVerification.Verify(Write("a.exe", "<html>rate limited</html>"u8.ToArray()), null));

    [Fact]
    public void PeWithoutDigestPasses()
    {
        // The stable path has no digest to check (redirect, no API call); TLS is the trust anchor there.
        Assert.Equal(VerifyOutcome.Ok, DownloadVerification.Verify(Write("a.exe", FakePe(64)), null));
    }

    [Fact]
    public void MatchingDigestPasses()
    {
        var bytes = FakePe(4096);
        Assert.Equal(VerifyOutcome.Ok, DownloadVerification.Verify(Write("a.exe", bytes), Sha256Digest(bytes).ToUpperInvariant()));
    }

    [Fact]
    public void MismatchedDigestFails()
    {
        var bytes = FakePe(4096);
        Assert.Equal(VerifyOutcome.DigestMismatch, DownloadVerification.Verify(Write("a.exe", bytes), Sha256Digest(FakePe(4095))));
    }

    [Fact]
    public void UnknownDigestAlgorithmFailsClosed()
        => Assert.False(DownloadVerification.DigestMatches("md5:abc", "abc"));

    // ---- download ----

    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var responder = responders[Math.Min(Calls, responders.Length - 1)];
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private static HttpResponseMessage Bytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = bytes.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public void DownloadsToTheDestinationAndReportsProgress()
    {
        var bytes = FakePe(200_000);
        var handler = new FakeHandler(_ => Bytes(bytes));
        using var http = new HttpClient(handler);
        var reported = new List<double>();
        var destination = Path.Combine(_dir, "setup.exe");

        var ok = Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), destination, NoDelays,
            new Progress<double>(reported.Add), CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(ok);
        Assert.Equal(bytes, File.ReadAllBytes(destination));
        // Progress<T> posts to the thread pool; give it a moment, then only check it ended at 1.
        SpinWait.SpinUntil(() => reported.Contains(1.0), TimeSpan.FromSeconds(2));
        Assert.Contains(1.0, reported);
    }

    [Fact]
    public void TransientFailureIsRetried()
    {
        var bytes = FakePe(1024);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Bytes(bytes));
        using var http = new HttpClient(handler);

        var ok = Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), Path.Combine(_dir, "s.exe"), NoDelays, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(ok);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void NotFoundIsNotRetriedAndFails()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var ok = Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), Path.Combine(_dir, "s.exe"), NoDelays, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(ok);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void RequestCarriesAUserAgent()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(r => { seen = r; return Bytes(FakePe(16)); });
        using var http = new HttpClient(handler);
        Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), Path.Combine(_dir, "s.exe"), NoDelays, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.StartsWith("ClaudeUsageTraySetup/", seen!.Headers.UserAgent.ToString());
    }
}
