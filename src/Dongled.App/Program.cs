using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Dongled.App;

/// <summary>
/// The entry point, replacing the one the XAML compiler generates.
/// </summary>
/// <remarks>
/// <para>
/// It exists for exactly one reason: the single-instance check has to happen before
/// <see cref="Application.Start"/>, because a second copy must exit without creating an
/// <see cref="Application"/>, a window, or a switching engine. Everything else here reproduces the
/// generated entry point, which is still compiled as
/// <c>XamlGeneratedProgram.XamlGeneratedMain</c> and is the reference for this body.
/// </para>
/// <para>
/// There is no command-line handling. Arguments are ignored rather than parsed, so there is no
/// switch to misuse.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Declared before the try and disposed unconditionally in the finally, which is the shape
        // CA2000 asks for: the claim has to outlive the whole run and be released however it ends.
        SingleInstance? instance = null;

        try
        {
            instance = SingleInstance.TryAcquire();

            if (instance is null)
            {
                // Another copy owns the session. Ask it to show itself and stop; whether that
                // succeeds or not, this copy must not go on to open a second window.
                SingleInstance.SignalRunningInstance();
                return;
            }

            var claim = instance;

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(parameters =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);

                // Discarded rather than stored: the XAML runtime roots the Application it is handed,
                // and Application.Start does not return until the app exits.
                _ = new App(claim);
            });
        }
        finally
        {
            // Releases the mutex, so the next launch is not refused because this process ended
            // without tidying up.
            instance?.Dispose();
        }
    }
}
