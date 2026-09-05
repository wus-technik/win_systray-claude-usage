namespace ClaudeUsageTray.Core;

/// <summary>State of ~/.claude.json when the cache reader produced no snapshot. Unreadable means the
/// usage key is present but did not parse — the status is only consulted after TryRead returned null.</summary>
public enum ConfigStatus { Missing, NoUsageKey, Unreadable }

/// <summary>State of ~/.claude/.credentials.json. Unusable: the file exists but yields no valid token
/// (expired, near expiry, malformed).</summary>
public enum CredentialStatus { Missing, Unusable, Valid }
