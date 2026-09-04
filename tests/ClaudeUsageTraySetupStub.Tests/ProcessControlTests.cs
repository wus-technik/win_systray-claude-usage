using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class ProcessControlTests
{
    [Theory]
    [InlineData(true, 1, true)]   // SYSTEM in an interactive session (psexec -s) is still useless
    [InlineData(false, 0, true)]  // session 0: Intune/SCCM default context
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    public void RefusesSystemOrSessionZero(bool isLocalSystem, int sessionId, bool expected)
        => Assert.Equal(expected, ProcessControl.MustRefuseContext(isLocalSystem, sessionId));

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\WUSTECHNIK.CLAUDEUSAGETRAY\Update.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\WusTechnik.ClaudeUsageTray.old\current\ClaudeUsageTray.exe", false)]
    [InlineData(@"D:\portable\ClaudeUsageTray.exe", false)]
    [InlineData(null, false)]
    public void IsInsideRootIsAPathPrefixCheck(string? path, bool expected)
        => Assert.Equal(expected, ProcessControl.IsInsideRoot(path, @"C:\Users\x\AppData\Local\WusTechnik.ClaudeUsageTray"));
}
