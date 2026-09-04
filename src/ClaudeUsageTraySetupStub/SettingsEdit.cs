using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTraySetupStub;

public enum SettingsStatus { Ok, Absent, Malformed, WrongType }

public sealed record SettingsReadResult(SettingsStatus Status, bool? UseBetaReleases);

public sealed record SettingsEditResult(SettingsStatus Status, string? Json);

/// <summary>Reads and rewrites one key of settings.json as a JsonNode DOM. Never through the app's
/// Settings type: that would drop every key this stub's copy predates (the stub cannot auto-update,
/// so it is routinely older than the app) and would run NormalizeFields over values it was never
/// asked to touch. Pure — the file IO is in <see cref="SettingsFile"/>.</summary>
public static class SettingsEdit
{
    public const string Key = "useBetaReleases";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static SettingsReadResult Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(SettingsStatus.Absent, null);
        var root = ParseObject(json);
        if (root is null) return new(SettingsStatus.Malformed, null);
        var property = FindKey(root);
        if (property is null || property.Value.Value is null) return new(SettingsStatus.Ok, null);
        return property.Value.Value is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? new(SettingsStatus.Ok, flag)
            : new(SettingsStatus.WrongType, null);
    }

    /// <summary>The whole document with the key set. A malformed file or a non-bool value is refused,
    /// not replaced: overwriting would destroy the user's other settings. A missing file becomes an
    /// object with just this key.</summary>
    public static SettingsEditResult Apply(string? json, bool useBetaReleases)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(json)) root = new JsonObject();
        else
        {
            var parsed = ParseObject(json);
            if (parsed is null) return new(SettingsStatus.Malformed, null);
            root = parsed;
        }

        var existing = FindKey(root);
        if (existing is { Value: { } current } && !(current is JsonValue v && v.TryGetValue<bool>(out _)))
            return new(SettingsStatus.WrongType, null);

        // Settings.Load matches case-insensitively, so the existing spelling is kept and rewritten in
        // place; adding a second key would leave which one wins to chance.
        root[existing?.Key ?? Key] = JsonValue.Create(useBetaReleases);
        return new(SettingsStatus.Ok, root.ToJsonString(WriteOptions));
    }

    /// <summary>The stale-settings rule. Only an explicit value that contradicts the chosen ring is
    /// corrected; absent or null stays, because Program.cs resolves that from the installed channel
    /// and writing it would add a second source of truth.</summary>
    public static bool NeedsReconcile(bool? existing, Ring chosen)
        => existing is bool value && value != (chosen == Ring.Beta);

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static KeyValuePair<string, JsonNode?>? FindKey(JsonObject root)
    {
        foreach (var property in root)
            if (string.Equals(property.Key, Key, StringComparison.OrdinalIgnoreCase)) return property;
        return null;
    }
}

public enum SettingsWriteStatus { Written, Malformed, WrongType, IoError, ReadBackMismatch }

public static class SettingsFile
{
    /// <summary>Roaming AppData — the same path as Settings.DefaultPath. Roaming is why a stale file
    /// can follow the user to a machine that never had the app.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "settings.json");

    public static SettingsReadResult Read(string path)
    {
        try
        {
            return File.Exists(path) ? SettingsEdit.Read(File.ReadAllText(path)) : new(SettingsStatus.Absent, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(SettingsStatus.Malformed, null);
        }
    }

    /// <summary>Temp file plus atomic replace, matching Settings.Save, then a read-back: a write that
    /// did not persist is exit 3005, not a silent success.</summary>
    public static SettingsWriteStatus Write(string path, bool useBetaReleases)
    {
        string? existing;
        try { existing = File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SettingsWriteStatus.IoError; }

        var edit = SettingsEdit.Apply(existing, useBetaReleases);
        if (edit.Status == SettingsStatus.Malformed) return SettingsWriteStatus.Malformed;
        if (edit.Status == SettingsStatus.WrongType) return SettingsWriteStatus.WrongType;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, edit.Json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SettingsWriteStatus.IoError; }

        var back = Read(path);
        return back.Status == SettingsStatus.Ok && back.UseBetaReleases == useBetaReleases
            ? SettingsWriteStatus.Written
            : SettingsWriteStatus.ReadBackMismatch;
    }
}
