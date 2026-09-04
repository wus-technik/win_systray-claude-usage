using System.Reflection;

namespace ClaudeUsageTraySetupStub;

/// <summary>The one place allowed to read the entry assembly's version via reflection — the global
/// constraint permits only `AssemblyInformationalVersionAttribute`, not `AssemblyVersion`:
/// `AssemblyVersion` (and thus `AssemblyName.Version`) drops any prerelease suffix (e.g. `-beta.1`)
/// and build metadata, while the informational version keeps both.</summary>
public static class StubVersion
{
    /// <summary>What `--version` prints (a later task). May carry `+&lt;sha&gt;` build metadata.</summary>
    public static readonly string Informational =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary><see cref="Informational"/> without anything from the first '+' onward — build
    /// metadata does not belong in a User-Agent header.</summary>
    public static readonly string Short = Informational.Split('+')[0];
}
