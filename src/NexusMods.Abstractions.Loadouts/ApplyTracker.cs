namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Afterburn: counts applies that are in progress, so the app can let one finish before it exits. Closing in the middle
/// of an apply loses track of the files already written, and the next apply then has to re-check all of them.
/// </summary>
public static class ApplyTracker
{
    private static int _active;

    /// <summary>True while at least one apply is running.</summary>
    public static bool IsApplying => Volatile.Read(ref _active) > 0;

    /// <summary>Marks an apply as running until the returned object is disposed.</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _active);
        }
    }
}
