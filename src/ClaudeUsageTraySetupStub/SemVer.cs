using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeUsageTraySetupStub;

/// <summary>Just enough SemVer 2 to order release tags: the beta resolver picks the highest version
/// *by tag*, not by publish date, so an out-of-order hotfix cannot win. Tags that do not parse are
/// skipped by the caller — the permanent `setup-stub` release is one such tag.</summary>
public sealed partial class SemVer : IComparable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    private SemVer(int major, int minor, int patch, IReadOnlyList<string> prerelease)
        => (Major, Minor, Patch, Prerelease) = (major, minor, patch, prerelease);

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    public static SemVer? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return null;
        var prerelease = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        return new SemVer(major, minor, patch, prerelease);
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // No prerelease outranks any prerelease of the same core version.
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        var shared = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var i = 0; i < shared; i++)
        {
            c = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (c != 0) return c;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    /// <summary>SemVer §11: numeric identifiers compare as numbers and rank below alphanumeric ones;
    /// alphanumeric ones compare in ASCII order.</summary>
    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
        var bNumeric = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
        if (aNumeric && bNumeric) return an.CompareTo(bn);
        if (aNumeric) return -1;
        if (bNumeric) return 1;
        return string.CompareOrdinal(a, b);
    }

    public override string ToString()
        => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}" : $"{Major}.{Minor}.{Patch}";
}
