using System.Diagnostics;

namespace NexusMods.Sdk;

/// <summary>
/// Afterburn: runs heavy background work (installing collections) at below-normal process priority on Windows, so
/// the game and the desktop stay smooth while it works. With nothing else competing for the CPU it runs just as fast.
/// </summary>
public static class BackgroundWorkPriority
{
    private static readonly object Gate = new();
    private static int _activeCount;

    /// <summary>Lowers the priority until the returned object is disposed. Calls can nest.</summary>
    public static IDisposable Enter()
    {
        lock (Gate)
        {
            if (_activeCount++ == 0) Set(ProcessPriorityClass.BelowNormal);
        }

        return new Scope();
    }

    /// <summary>
    /// Puts the priority back to normal. Call this before starting the game: on Windows a program started from a
    /// below-normal process inherits that priority.
    /// </summary>
    public static void RestoreNormal()
    {
        lock (Gate)
        {
            Set(ProcessPriorityClass.Normal);
        }
    }

    private static void Set(ProcessPriorityClass priority)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            if (process.PriorityClass != priority) process.PriorityClass = priority;
        }
        catch
        {
            // Not allowed or not supported: just keep the current priority.
        }
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                if (--_activeCount == 0) Set(ProcessPriorityClass.Normal);
            }
        }
    }
}
