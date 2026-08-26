using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The release notes shown before a restart. Pure formatting, so the dialog decides nothing
/// about what "no notes" means.</summary>
public class ReleaseNotesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void NothingToShow_IsNull(string? notes) => Assert.Null(ReleaseNotes.Format(notes));

    [Fact]
    public void SurroundingBlankLinesAreTrimmed()
        => Assert.Equal("- Fixed the thing", ReleaseNotes.Format("\n\n- Fixed the thing\n\n"));

    /// <summary>A read-only WinForms TextBox needs CRLF; a bare LF renders as one run-together line.</summary>
    [Fact]
    public void LineEndingsAreNormalizedToCrLf()
        => Assert.Equal("- One\r\n- Two", ReleaseNotes.Format("- One\n- Two"));

    [Fact]
    public void ExistingCrLfIsNotDoubled()
        => Assert.Equal("- One\r\n- Two", ReleaseNotes.Format("- One\r\n- Two"));

    [Fact]
    public void TheMarkdownIsOtherwiseLeftAlone()
        => Assert.Equal("## 0.7.1\r\n\r\n### Fixed\r\n\r\n- A **bold** claim",
            ReleaseNotes.Format("## 0.7.1\n\n### Fixed\n\n- A **bold** claim"));
}
