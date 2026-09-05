using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CredentialsReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-creds-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFixture(string json)
    {
        var path = Path.Combine(_dir, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CredsJson(string token, long expiresAtMs) => $$"""
        { "claudeAiOauth": { "accessToken": "{{token}}", "refreshToken": "dummy-refresh",
          "expiresAt": {{expiresAtMs}}, "scopes": ["user:inference"], "subscriptionType": "max" } }
        """;

    [Fact]
    public void ValidFutureToken_IsReturned()
        => Assert.Equal("dummy-token-abc", CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddHours(2).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void ExpiredToken_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddMinutes(-1).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void NearExpiryToken_ReturnsNull() // < 5 min margin
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddMinutes(4).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void MissingFile_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(Path.Combine(_dir, "nope.json"), Now));

    [Fact]
    public void MissingClaudeAiOauthKey_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture("""{ "mcpOAuth": {} }"""), Now));

    [Fact]
    public void EmptyToken_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("", Now.AddHours(2).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void MissingExpiresAt_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture("""{ "claudeAiOauth": { "accessToken": "dummy-token-abc" } }"""), Now));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("42")]
    public void MalformedOrNonObject_ReturnsNull(string json)
        => Assert.Null(CredentialsReader.TryReadAccessToken(WriteFixture(json), Now));

    [Fact]
    public void DefaultPath_IsClaudeCredentialsUnderUserProfile()
        => Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json"),
            CredentialsReader.DefaultPath);

    [Fact]
    public void Status_MissingFile_IsMissing()
        => Assert.Equal(CredentialStatus.Missing,
            CredentialsReader.Status(Path.Combine(_dir, "nope.json"), Now));

    [Fact]
    public void Status_ValidToken_IsValid()
        => Assert.Equal(CredentialStatus.Valid, CredentialsReader.Status(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddHours(2).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void Status_ExpiredToken_IsUnusable()
        => Assert.Equal(CredentialStatus.Unusable, CredentialsReader.Status(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddMinutes(-1).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void Status_MalformedFile_IsUnusable()
        => Assert.Equal(CredentialStatus.Unusable, CredentialsReader.Status(WriteFixture("{ nope"), Now));
}
