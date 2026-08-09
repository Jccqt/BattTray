using System.Diagnostics;

namespace BattTray;

/// <summary>
/// The claim that makes this the only BattTray running, and the one message a launch that
/// arrives second has for the copy that got there first.
/// </summary>
/// <remarks>
/// A second tray icon for the same devices would only be confusing, so the second process
/// still exits at once — but exiting in silence is the very puzzle the startup dialog was
/// added to solve: double-clicking the exe appears to do nothing whatsoever, and there is
/// no way to tell a refused launch from a failed one. So it signals a named event on its
/// way out and the running copy answers with the dialog, which is the same answer the user
/// would have got had nothing been running. Both objects are per-session (<c>Local\</c>),
/// so two people signed in at once get one instance each rather than fighting over one.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    const string ClaimName = @"Local\BattTray.SingleInstance";
    const string SignalName = @"Local\BattTray.ShowSettings";

    readonly Mutex _claim;
    readonly EventWaitHandle _signal;

    /// <summary>The pool-thread wait on <see cref="_signal"/>; null until listening starts.</summary>
    RegisteredWaitHandle? _registration;

    /// <summary>Where <see cref="_onRequest"/> has to run; captured in StartListening.</summary>
    SynchronizationContext? _uiThread;

    Action? _onRequest;

    SingleInstance(Mutex claim, EventWaitHandle signal)
    {
        _claim = claim;
        _signal = signal;
    }

    /// <summary>The claim, or null when another instance already holds it.</summary>
    public static SingleInstance? TryAcquire()
    {
        var claim = new Mutex(initiallyOwned: true, ClaimName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            claim.Dispose();
            return null;
        }

        // Created here rather than alongside the wait, so the window in which a second
        // launch finds the claim taken but no event to signal closes before any of the
        // startup work rather than after it. Auto-reset, so a signal that lands before the
        // wait is registered is still delivered — once — as soon as it is.
        var signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SignalName);
        return new SingleInstance(claim, signal);
    }

    /// <summary>Asks the instance already running to show its settings dialog.</summary>
    public static void RequestSettings()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(SignalName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance has the claim but has not published the event yet, so it
            // is a few milliseconds into starting up — and a launch that new was itself
            // about to show the dialog. There is nothing left to ask for.
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Worth a line in the debugger, but not worth a message box: the user asked to
            // see the app, and the app is running, which is most of what they wanted.
            Debug.WriteLine($"Settings request failed: {ex}");
        }
    }

    /// <summary>
    /// Runs <paramref name="onRequest"/> whenever another launch asks for the dialog.
    /// </summary>
    /// <remarks>
    /// Call from the UI thread, after the first control exists: the wait is served by a pool
    /// thread, so the handler has to be marshalled back, and the synchronization context
    /// that does the marshalling is installed by the first control the thread creates.
    /// Calling before that would capture nothing and drop every request.
    /// </remarks>
    public void StartListening(Action onRequest)
    {
        _onRequest = onRequest;
        _uiThread = SynchronizationContext.Current;

        Debug.Assert(_uiThread is not null, "No synchronization context: requests would be dropped.");

        // executeOnlyOnce: false — the wait re-arms itself, since the user may well launch
        // the exe again later, and an auto-reset event is clear again by the time it fires.
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal, (_, _) => Deliver(), state: null, Timeout.Infinite, executeOnlyOnce: false);
    }

    void Deliver()
    {
        try
        {
            _uiThread?.Post(_ => _onRequest?.Invoke(), null);
        }
        catch (Exception ex)
        {
            // Runs on a pool thread, where an escaping exception takes the whole process
            // down with it — a far worse answer to a second launch than no answer at all.
            // The one failure expected here is a post that lost a race with shutdown.
            Debug.WriteLine($"Settings request dropped: {ex}");
        }
    }

    public void Dispose()
    {
        // Unregistered first: a wait left armed against a handle that is about to close is
        // how a shutdown turns into a callback on a disposed event. Null rather than a
        // handle to wait on, since a callback already in flight only posts, and a post that
        // arrives too late is caught in Deliver.
        _registration?.Unregister(waitObject: null);
        _registration = null;

        _signal.Dispose();
        _claim.Dispose();
    }
}
