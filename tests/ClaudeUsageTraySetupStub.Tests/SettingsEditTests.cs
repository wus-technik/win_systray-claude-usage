using System.Text.Json.Nodes;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class SettingsEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stub-settings-" + Guid.NewGuid().ToString("N"));
    public SettingsEditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Typical = """
        {
          "displayMode": "both",
          "thresholds": { "orange": 50, "red": 85 },
          "runAtStartup": true,
          "useBetaReleases": false,
          "futureKeyThisStubHasNeverHeardOf": { "nested": [1, 2, 3] }
        }
        """;

    // ---- Read ----

    [Fact]
    public void ReadsAnExplicitValue() => Assert.Equal(new SettingsReadResult(SettingsStatus.Ok, false), SettingsEdit.Read(Typical));

    [Fact]
    public void ReadsNullAsNoChoice()
        => Assert.Equal(new SettingsReadResult(SettingsStatus.Ok, null), SettingsEdit.Read("""{ "useBetaReleases": null }"""));

    [Fact]
    public void ReadsAnAbsentKeyAsNoChoice()
        => Assert.Equal(new SettingsReadResult(SettingsStatus.Ok, null), SettingsEdit.Read("""{ "runAtStartup": true }"""));

    [Fact]
    public void ReadsAnyCasing()
        => Assert.Equal(true, SettingsEdit.Read("""{ "UseBetaReleases": true }""").UseBetaReleases);

    [Fact]
    public void MissingOrEmptyFileIsAbsent()
    {
        Assert.Equal(SettingsStatus.Absent, SettingsEdit.Read(null).Status);
        Assert.Equal(SettingsStatus.Absent, SettingsEdit.Read("  ").Status);
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("[1, 2]")]
    public void MalformedIsReported(string json) => Assert.Equal(SettingsStatus.Malformed, SettingsEdit.Read(json).Status);

    [Fact]
    public void WrongTypeIsReported()
        => Assert.Equal(SettingsStatus.WrongType, SettingsEdit.Read("""{ "useBetaReleases": "yes" }""").Status);

    // ---- Apply ----

    [Fact]
    public void PreservesEveryOtherKeyIncludingUnknownOnes()
    {
        // The stub is routinely older than the app; a round-trip through Settings would drop keys it predates.
        var result = SettingsEdit.Apply(Typical, useBetaReleases: true);
        Assert.Equal(SettingsStatus.Ok, result.Status);
        var node = JsonNode.Parse(result.Json!)!.AsObject();
        Assert.True((bool)node["useBetaReleases"]!);
        Assert.Equal(3, node["futureKeyThisStubHasNeverHeardOf"]!["nested"]!.AsArray().Count);
        Assert.Equal(85, (int)node["thresholds"]!["red"]!);
        Assert.Equal(5, node.Count);
    }

    [Fact]
    public void RewritesADifferentlyCasedKeyInPlaceWithoutAddingASecond()
    {
        // Settings.Load is case-insensitive; two keys would leave which one wins to chance.
        var result = SettingsEdit.Apply("""{ "UseBetaReleases": false, "runAtStartup": true }""", true);
        var node = JsonNode.Parse(result.Json!)!.AsObject();
        Assert.Equal(2, node.Count);
        Assert.True((bool)node["UseBetaReleases"]!);
        Assert.Equal("UseBetaReleases", node.First().Key);
    }

    [Fact]
    public void AddsTheKeyWhenAbsent()
    {
        var node = JsonNode.Parse(SettingsEdit.Apply("""{ "runAtStartup": true }""", false).Json!)!.AsObject();
        Assert.False((bool)node["useBetaReleases"]!);
    }

    [Fact]
    public void ReplacesANullValue()
    {
        var node = JsonNode.Parse(SettingsEdit.Apply("""{ "useBetaReleases": null }""", true).Json!)!.AsObject();
        Assert.True((bool)node["useBetaReleases"]!);
    }

    [Fact]
    public void MissingFileBecomesAnObjectWithJustThatKey()
    {
        var node = JsonNode.Parse(SettingsEdit.Apply(null, true).Json!)!.AsObject();
        Assert.Single(node);
        Assert.True((bool)node["useBetaReleases"]!);
    }

    [Fact]
    public void RefusesToOverwriteAMalformedFile()
    {
        var result = SettingsEdit.Apply("{ nope", true);
        Assert.Equal(SettingsStatus.Malformed, result.Status);
        Assert.Null(result.Json);
    }

    [Fact]
    public void RefusesAWrongTypedValue()
        => Assert.Equal(SettingsStatus.WrongType, SettingsEdit.Apply("""{ "useBetaReleases": 1 }""", true).Status);

    // ---- reconciliation: the stale settings file ----

    [Theory]
    [InlineData(false, Ring.Beta, true)]   // the "beta installer undoes itself" bug, re-entering via a leftover file
    [InlineData(true, Ring.Stable, true)]  // mirror case: stable install that would stage a beta at once
    [InlineData(null, Ring.Beta, false)]   // absent/null is handled by the app's adoption rule; writing it adds a second source of truth
    [InlineData(null, Ring.Stable, false)]
    [InlineData(true, Ring.Beta, false)]
    [InlineData(false, Ring.Stable, false)]
    public void ReconcileOnlyWhenAnExplicitValueContradictsTheChosenRing(bool? existing, Ring chosen, bool expected)
        => Assert.Equal(expected, SettingsEdit.NeedsReconcile(existing, chosen));

    // ---- SettingsFile ----

    [Fact]
    public void WriteCreatesTheDirectoryAndReadsBack()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        Assert.Equal(SettingsWriteStatus.Written, SettingsFile.Write(path, true));
        Assert.Equal(true, SettingsFile.Read(path).UseBetaReleases);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteKeepsExistingContent()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, Typical);
        Assert.Equal(SettingsWriteStatus.Written, SettingsFile.Write(path, true));
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True((bool)node["useBetaReleases"]!);
        Assert.NotNull(node["futureKeyThisStubHasNeverHeardOf"]);
    }

    [Fact]
    public void WriteRefusesAMalformedFileAndLeavesItUntouched()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ nope");
        Assert.Equal(SettingsWriteStatus.Malformed, SettingsFile.Write(path, true));
        Assert.Equal("{ nope", File.ReadAllText(path));
    }

    [Fact]
    public void ReadOfAMissingFileIsAbsent()
        => Assert.Equal(SettingsStatus.Absent, SettingsFile.Read(Path.Combine(_dir, "none.json")).Status);
}
