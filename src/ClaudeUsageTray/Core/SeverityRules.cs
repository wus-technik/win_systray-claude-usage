namespace ClaudeUsageTray.Core;

public enum Severity { Green, Orange, Red }

public static class SeverityRules
{
    /// <summary>< orangeAt → Green, orangeAt..redAbove → Orange, > redAbove → Red.</summary>
    public static Severity For(int percent, int orangeAt = 50, int redAbove = 85)
        => percent > redAbove ? Severity.Red
         : percent >= orangeAt ? Severity.Orange
         : Severity.Green;
}
