using System.Text;
using System.Text.Json;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Read-only extraction of Codex's stored token. No live calls, no writes, and no token
/// material in any assertion message.</summary>
public class CodexCredentialsReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A three-segment JWT whose payload carries the given exp. Only the payload is read,
    /// so the header and signature are filler.</summary>
    private static string Jwt(DateTimeOffset? exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = exp is { } e ? $"{{\"exp\":{e.ToUnixTimeSeconds()}}}" : "{}";
        return $"{B64("{\"alg\":\"RS256\"}")}.{B64(payload)}.sig";
    }

    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string AuthJson(string token, string? accountId = "00000000-0000-0000-0000-000000000000")
        => JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new { access_token = token, account_id = accountId },
        });

    [Fact]
    public void TryRead_ReturnsTheTokenAndAccount()
    {
        var path = Write(AuthJson(Jwt(Now.AddDays(2))));
        try
        {
            var auth = CodexCredentialsReader.TryRead(path, Now);
            Assert.NotNull(auth);
            Assert.Equal("00000000-0000-0000-0000-000000000000", auth!.AccountId);
            Assert.Equal(Now.AddDays(2).ToUnixTimeSeconds(), auth.ExpiresAt!.Value.ToUnixTimeSeconds());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryRead_MissingFile_IsNull()
        => Assert.Null(CodexCredentialsReader.TryRead(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"), Now));

    [Fact]
    public void TryRead_MalformedJson_IsNull()
    {
        var path = Write("{ not json at all ");
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>Review Focus 1, first half: an expired token is a known no-data reason, so the
    /// fetch is never attempted and the 401 never happens.</summary>
    [Fact]
    public void TryRead_ExpiredToken_IsNull()
    {
        var path = Write(AuthJson(Jwt(Now.AddMinutes(-1))));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>Within the expiry margin counts as expired: Codex is about to refresh it.</summary>
    [Fact]
    public void TryRead_TokenInsideTheExpiryMargin_IsNull()
    {
        var path = Write(AuthJson(Jwt(Now.AddMinutes(2))));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>Review Focus 1, second half: a token that is not a three-segment JWT must not throw.
    /// An unreadable expiry is treated as unusable rather than as "valid forever".</summary>
    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("two.segments")]
    [InlineData("three.!!!notbase64!!!.sig")]
    public void TryRead_UndecodableToken_IsNull(string token)
    {
        var path = Write(AuthJson(token));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryRead_TokenWithNoExpClaim_IsNull()
    {
        var path = Write(AuthJson(Jwt(null)));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>A missing account id is not fatal: the header is omitted and the endpoint resolves
    /// the account from the token.</summary>
    [Fact]
    public void TryRead_WithoutAccountId_StillReturnsTheToken()
    {
        var path = Write(AuthJson(Jwt(Now.AddDays(2)), accountId: null));
        try
        {
            var auth = CodexCredentialsReader.TryRead(path, Now);
            Assert.NotNull(auth);
            Assert.Null(auth!.AccountId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Status_DistinguishesMissingFromUnusable()
    {
        Assert.Equal(CredentialStatus.Missing, CodexCredentialsReader.Status(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"), Now));

        var path = Write(AuthJson(Jwt(Now.AddMinutes(-1))));
        try { Assert.Equal(CredentialStatus.Unusable, CodexCredentialsReader.Status(path, Now)); }
        finally { File.Delete(path); }
    }
}
