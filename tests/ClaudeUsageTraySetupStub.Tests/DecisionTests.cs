using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class DecisionTests
{
    private static StubOptions Opts(Ring? ring, bool silent) => new(ring, silent, null, false, false);
    private static readonly InstallInfo Installed = new("0.7.2", "win-beta");

    [Fact]
    public void FreshInteractiveWithNoRingAsks_DefaultingToStable()
        => Assert.Equal(new Decision(Step.AskRing, Ring.Stable), Flow.Decide(Opts(null, false), null, null));

    [Fact]
    public void FreshSilentWithNoRingInstallsStable()
        // The documented default for a normal install.
        => Assert.Equal(new Decision(Step.Install, Ring.Stable), Flow.Decide(Opts(null, true), null, null));

    [Theory]
    [InlineData(Ring.Beta, false)]
    [InlineData(Ring.Beta, true)]
    [InlineData(Ring.Stable, true)]
    public void FreshWithARingInstallsIt(Ring ring, bool silent)
        => Assert.Equal(new Decision(Step.Install, ring), Flow.Decide(Opts(ring, silent), null, null));

    [Fact]
    public void InstalledSilentWithNoRingIsAmbiguous()
    {
        // Defaulting to stable here would silently drag a deliberate beta opt-in back down.
        Assert.Equal(new Decision(Step.Ambiguous, Ring.Beta), Flow.Decide(Opts(null, true), Installed, Ring.Beta));
    }

    [Fact]
    public void InstalledInteractiveWithNoRingAsks_PreselectingTheCurrentRing()
        => Assert.Equal(new Decision(Step.AskRing, Ring.Beta), Flow.Decide(Opts(null, false), Installed, Ring.Beta));

    [Fact]
    public void InstalledOnTheRequestedRingIsConverged()
    {
        // Idempotent: the same --ring twice is a success, not a no-op.
        Assert.Equal(new Decision(Step.Converged, Ring.Beta), Flow.Decide(Opts(Ring.Beta, true), Installed, Ring.Beta));
    }

    [Fact]
    public void InstalledOnTheOtherRingChangesIt()
        => Assert.Equal(new Decision(Step.ChangeRing, Ring.Stable), Flow.Decide(Opts(Ring.Stable, true), Installed, Ring.Beta));
}
