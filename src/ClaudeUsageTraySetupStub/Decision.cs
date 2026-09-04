namespace ClaudeUsageTraySetupStub;

public enum Step { AskRing, Install, ChangeRing, Converged, Ambiguous }

/// <summary>For <see cref="Step.AskRing"/>, <c>Ring</c> is the radio button to preselect; the wizard's
/// answer is fed back through <see cref="Flow.Decide"/> with the ring filled in, so the interactive
/// and silent paths share one rule set.</summary>
public sealed record Decision(Step Step, Ring Ring);

public static class Flow
{
    public static Decision Decide(StubOptions options, InstallInfo? installed, Ring? currentRing)
    {
        if (installed is null || currentRing is null)
        {
            // No install: --ring wins; silently the default is stable, matching a normal install.
            if (options.Ring is { } chosen) return new Decision(Step.Install, chosen);
            return options.Silent ? new Decision(Step.Install, Ring.Stable) : new Decision(Step.AskRing, Ring.Stable);
        }

        if (options.Ring is null)
        {
            // Silent + installed + no ring: the operator never said what the desired state is. A default
            // of stable would reverse a deliberate beta opt-in where nobody is watching.
            return options.Silent
                ? new Decision(Step.Ambiguous, currentRing.Value)
                : new Decision(Step.AskRing, currentRing.Value);
        }

        return options.Ring == currentRing
            ? new Decision(Step.Converged, currentRing.Value)
            : new Decision(Step.ChangeRing, options.Ring.Value);
    }
}
