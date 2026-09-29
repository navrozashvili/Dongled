using System.Threading;

namespace Dongled.App;

/// <summary>
/// Makes sure only one copy of the app runs, and lets a second copy bring the first
/// one's window to the front instead of starting.
/// </summary>
/// <remarks>
/// <para>
/// The reason is not tidiness. Two copies would both drive the default audio device from their own
/// rules and both write <c>state.json</c>, so the captured previous device would depend on which
/// process got there first.
/// </para>
/// <para>
/// Both names are in the <c>Local\</c> namespace, so the scope is one logon session rather than the
/// whole machine. That is the correct scope: two different users signed in at once each have their
/// own default audio device and their own configuration, so neither is fighting the other.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// Set on a process this app starts to replace itself, telling it to wait for the outgoing copy
    /// to let go rather than treating the held mutex as "another copy is already running".
    /// </summary>
    /// <remarks>
    /// An environment variable rather than a command-line switch, deliberately: <see cref="Program"/>
    /// ignores arguments so that there is no switch to misuse, and a switch here — even a harmless
    /// one — would add that surface. A variable is inherited only by a process this one
    /// starts, so nothing a user can type reaches it.
    /// </remarks>
    public const string AwaitReleaseVariable = "DONGLED_AWAIT_INSTANCE";

    private const string MutexName = @"Local\Dongled.SingleInstance";
    private const string ActivationEventName = @"Local\Dongled.Activate";

    /// <summary>
    /// How long a replacement process waits for the outgoing one to exit. Generous because the
    /// outgoing copy stops every provider before it goes, and a provider gets ten seconds.
    /// </summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether this process was started by an outgoing copy to replace it: after a plugin change
    /// or an update, both of which the user asked for from the window.
    /// </summary>
    /// <remarks>Set by <see cref="TryAcquire"/>.</remarks>
    public static bool StartedAsReplacement { get; private set; }

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly CancellationTokenSource _stopping = new();

    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>
    /// Claim the right to be the running copy, or return null if another copy already holds it.
    /// </summary>
    /// <remarks>
    /// Called before the XAML runtime is started, so a second copy exits without creating a window
    /// or touching configuration.
    /// </remarks>
    public static SingleInstance? TryAcquire()
    {
        StartedAsReplacement = string.Equals(
            Environment.GetEnvironmentVariable(AwaitReleaseVariable), "1", StringComparison.Ordinal);

        // Read once and then removed, so it is not inherited by anything this process starts. A
        // copy launched later from such a process would otherwise wait for the mutex like a
        // replacement instead of handing over to the running copy.
        Environment.SetEnvironmentVariable(AwaitReleaseVariable, null);

        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (!createdNew && !WaitForRelease(mutex))
        {
            mutex.Dispose();
            return null;
        }

        // Created by whichever copy wins, so a second copy always finds it. ManualReset would stay
        // signalled and activate the window repeatedly; auto-reset delivers exactly one activation
        // per request.
        var activation = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName);

        return new SingleInstance(mutex, activation);
    }

    /// <summary>
    /// Whether this process was started to replace a copy that is on its way out, and if so, wait
    /// for it to release the claim.
    /// </summary>
    /// <returns>
    /// True if the claim is now held by this process. False if this is an ordinary second copy, which
    /// must exit rather than wait.
    /// </returns>
    private static bool WaitForRelease(Mutex mutex)
    {
        if (!StartedAsReplacement)
        {
            // An ordinary second launch. Waiting here would leave a user staring at nothing for
            // twenty seconds after double-clicking the icon.
            return false;
        }

        try
        {
            return mutex.WaitOne(ReleaseTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The outgoing copy ended without releasing it, which is exactly the case this wait
            // exists for. An abandoned mutex is still acquired by the waiter.
            return true;
        }
    }

    /// <summary>
    /// Ask the copy that is already running to show its window. Returns false if signalling failed,
    /// in which case the second copy should still exit rather than open a competing window.
    /// </summary>
    public static bool SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivationEventName, out var activation))
            {
                // The other copy released the mutex between our attempt and this call. Rare, and
                // starting a window now would race whatever it is doing as it exits.
                return false;
            }

            using (activation)
            {
                return activation.Set();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Run <paramref name="onActivationRequested"/> whenever another copy asks for the window.
    /// </summary>
    /// <param name="onActivationRequested">
    /// Invoked on a background thread, so it must marshal to the UI thread itself.
    /// </param>
    public void ListenForActivation(Action onActivationRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        // A dedicated background thread rather than a task: it spends its whole life blocked in a
        // wait, which is exactly what the thread pool must not be used for.
        _listener = new Thread(() => Listen(onActivationRequested))
        {
            IsBackground = true,
            Name = "Dongled activation listener",
        };

        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopping.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));

        // Released before the handle is closed, so a copy started immediately afterwards sees the
        // name as free rather than abandoned.
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned, which happens only if something else already released it. Nothing to undo.
        }

        _mutex.Dispose();
        _activation.Dispose();
        _stopping.Dispose();
    }

    private void Listen(Action onActivationRequested)
    {
        var handles = new WaitHandle[] { _activation, _stopping.Token.WaitHandle };

        while (true)
        {
            int signalled;
            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                // Shutdown closed the handles while this thread was inside the wait.
                return;
            }

            // Index 1 is the shutdown signal.
            if (signalled != 0)
            {
                return;
            }

            onActivationRequested();
        }
    }
}
