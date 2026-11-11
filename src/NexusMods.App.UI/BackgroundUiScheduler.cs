using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Avalonia.Threading;

namespace NexusMods.App.UI;

/// <summary>
/// Afterburn: runs work on the UI thread at background priority, so the window keeps drawing and reacting to input
/// between pieces of work (used to fill big lists in small chunks instead of freezing the screen).
/// </summary>
public sealed class BackgroundUiScheduler : LocalScheduler
{
    public static readonly BackgroundUiScheduler Instance = new();

    private BackgroundUiScheduler() { }

    public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
    {
        var cancel = new BooleanDisposable();
        var inner = new SerialDisposable();

        void Run()
        {
            if (cancel.IsDisposed) return;
            inner.Disposable = action(this, state);
        }

        if (dueTime <= TimeSpan.Zero)
        {
            Dispatcher.UIThread.Post(Run, DispatcherPriority.Background);
        }
        else
        {
            _ = Task.Delay(dueTime).ContinueWith(_ => Dispatcher.UIThread.Post(Run, DispatcherPriority.Background), TaskScheduler.Default);
        }

        return new CompositeDisposable(cancel, inner);
    }
}
