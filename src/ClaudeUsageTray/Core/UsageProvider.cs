namespace ClaudeUsageTray.Core;

/// <summary>Which usage account a surface belongs to. Needed before any provider-specific code
/// exists, because which icons a status page marks has no answer without it.</summary>
public enum UsageProvider { Claude, Codex }
