using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Every test class that builds WinForms controls joins this collection, which runs on its
/// own with nothing alongside it.
///
/// WinForms layout is not safe across concurrent UI threads. xUnit runs test classes in parallel,
/// each on its own thread, and while one thread creates or lays out controls, another thread's
/// PreferredSize can come back wrong: pages measured 61 to 272 px too wide or too tall, in roughly
/// one of twenty dialogs built under contention, never once in isolation. That is what made the
/// settings dialog's fit-to-largest-page tests fail at random. The app itself has a single UI
/// thread, so this concerns only the tests.
///
/// A class that constructs a Form, a Control, or one of the app's dialogs or popups belongs here.
/// GDI+-only painting to a bitmap (IconRenderer, UsageBar.Paint) does not need it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WinFormsCollection
{
    public const string Name = "WinForms";
}
