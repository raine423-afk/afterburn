using Avalonia.Threading;
using R3;

namespace NexusMods.App.UI;

/// <summary>
/// Afterburn: delivers values on the UI thread without queueing when we are already on it.
/// </summary>
/// <remarks>
/// The usual "observe on UI thread" operators always post to the dispatcher, even when the value is produced on the
/// UI thread. When a list of ~2,000 mods is built, every cell of every row then queues its first value separately
/// (tens of thousands of dispatcher posts that run ahead of input), and each row is drawn empty first and filled in
/// later. These operators hand the value over immediately when that is safe, and only post when the value comes from
/// another thread. Order is kept: once a value had to be posted, later values for the same subscription wait behind
/// it until it has been delivered.
/// </remarks>
public static class UiThreadDelivery
{
    /// <summary>R3 version.</summary>
    public static Observable<T> ObserveOnUIThreadIfNeeded<T>(this Observable<T> source)
    {
        return Observable.Create<T>(observer =>
        {
            var disposed = false;
            var gate = new Gate(() => disposed);
            var subscription = source.Subscribe(
                value => gate.Run(() => observer.OnNext(value)),
                error => gate.Run(() => observer.OnErrorResume(error)),
                result => gate.Run(() => observer.OnCompleted(result))
            );
            return Disposable.Create(() =>
            {
                disposed = true;
                subscription.Dispose();
            });
        });
    }

    /// <summary>System.Reactive version.</summary>
    public static IObservable<T> OnUIIfNeeded<T>(this IObservable<T> source)
    {
        return System.Reactive.Linq.Observable.Create<T>(observer =>
        {
            var disposed = false;
            var gate = new Gate(() => disposed);
            var subscription = source.Subscribe(
                value => gate.Run(() => observer.OnNext(value)),
                error => gate.Run(() => observer.OnError(error)),
                () => gate.Run(observer.OnCompleted)
            );
            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                disposed = true;
                subscription.Dispose();
            });
        });
    }

    private sealed class Gate
    {
        private readonly object _lock = new();
        private int _pending;
        private readonly Func<bool> _isDisposed;

        public Gate(Func<bool> isDisposed) => _isDisposed = isDisposed;

        public void Run(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                var runNow = false;
                lock (_lock)
                {
                    if (_pending == 0) runNow = true;
                    else _pending++;
                }

                if (runNow)
                {
                    action();
                    return;
                }
            }
            else
            {
                lock (_lock) _pending++;
            }

            Dispatcher.UIThread.Post(() =>
            {
                lock (_lock) _pending--;
                if (_isDisposed()) return;
                action();
            });
        }
    }
}
