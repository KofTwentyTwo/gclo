namespace gclo.UiTests;

/// <summary>
/// The single xunit collection every UI test belongs to (referenced via
/// <c>nameof(UiSessionTests)</c>). All UI tests share one launched gclo instance
/// (the <see cref="AppSession"/> collection fixture) and run sequentially
/// (<c>DisableParallelization</c>), so two tests never drive two overlapping app
/// instances or interleave input on the same one.
/// </summary>
[CollectionDefinition(nameof(UiSessionTests), DisableParallelization = true)]
public sealed class UiSessionTests : ICollectionFixture<AppSession>
{
}

/// <summary>
/// Base class for UI tests: holds the shared <see cref="AppSession"/> so test
/// bodies read as session-relative steps. Derived classes must carry
/// <c>[Collection(nameof(UiSessionTests))]</c> — xunit does not inherit it.
/// </summary>
public abstract class UiTestBase
{
    /// <summary>Captures the collection fixture xunit injects per test class.</summary>
    protected UiTestBase(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
    }

    /// <summary>The app instance shared by every test in the collection.</summary>
    protected AppSession Session { get; }
}
