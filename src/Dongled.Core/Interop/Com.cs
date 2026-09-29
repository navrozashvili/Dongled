using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Dongled.Core.Interop;

/// <summary>
/// Creates and releases the COM objects the audio layer uses, so that every wrapper is released
/// deterministically rather than left to the finalizer.
/// </summary>
/// <remarks>
/// <para>
/// Wrappers are always created with <see cref="CreateObjectFlags.UniqueInstance"/>, because
/// <see cref="Release"/> calls <see cref="ComObject.FinalRelease"/> and that releases the
/// reference the wrapper holds. On a cached wrapper — which is what the other flags produce —
/// that would release a reference other holders still expect to own, and there is no runtime
/// guard against it: <c>FinalRelease</c> on a non-unique wrapper does not throw.
/// </para>
/// <para>
/// A released wrapper is not merely stale: calling through it throws
/// <see cref="ObjectDisposedException"/>. That is deliberate here, since it turns a
/// use-after-release from a silent read of freed memory into a test failure.
/// </para>
/// </remarks>
internal static class Com
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static long _releaseCount;

    /// <summary>
    /// How many wrappers <see cref="Release"/> has actually released since the process started.
    /// </summary>
    /// <remarks>
    /// Exists so the release discipline can be asserted rather than assumed. Omitting
    /// <see cref="Release"/> entirely makes no measurable difference to private bytes or handle
    /// count even over thousands of enumerations, because the wrappers are finalizable — so a
    /// memory-based leak test would pass whether or not anything was released. Counting the
    /// releases is the only thing that actually distinguishes the two.
    /// </remarks>
    internal static long ReleaseCount => Interlocked.Read(ref _releaseCount);

    /// <summary><c>CLSID_MMDeviceEnumerator</c>.</summary>
    private static readonly Guid MMDeviceEnumeratorClsid = new("bcde0395-e52f-467c-8e3d-c4579291692e");

    /// <summary><c>CLSID_CPolicyConfigClient</c>. Undocumented, like the interface it serves.</summary>
    private static readonly Guid PolicyConfigClientClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    /// <summary>Create an endpoint enumerator. The caller releases it with <see cref="Release"/>.</summary>
    /// <exception cref="COMException">The object could not be created.</exception>
    internal static IMMDeviceEnumerator CreateDeviceEnumerator() =>
        Create<IMMDeviceEnumerator>(MMDeviceEnumeratorClsid);

    /// <summary>Create the policy configuration object. The caller releases it.</summary>
    /// <exception cref="COMException">The object could not be created.</exception>
    internal static IPolicyConfig CreatePolicyConfig() =>
        Create<IPolicyConfig>(PolicyConfigClientClsid);

    /// <summary>
    /// Release a wrapper now rather than whenever finalization happens. Safe to call with null,
    /// with something that is not a wrapper, and twice with the same wrapper, so it can be used
    /// unguarded in a <c>finally</c>.
    /// </summary>
    /// <param name="rcw">
    /// Typed as <see cref="object"/> deliberately. A source-generated interface reference cannot
    /// be cast to <see cref="ComObject"/> directly — the wrapper implements the interface through
    /// <see cref="IDynamicInterfaceCastable"/>, so <c>(ComObject)someDevice</c> is a compile
    /// error — but converting to <see cref="object"/> first works.
    /// </param>
    internal static void Release(object? rcw)
    {
        if (rcw is ComObject com)
        {
            com.FinalRelease();
            Interlocked.Increment(ref _releaseCount);
        }
    }

    /// <summary>
    /// Read and free a string the callee allocated with <c>CoTaskMemAlloc</c>, which is how
    /// <see cref="IMMDevice.GetId"/> returns an endpoint identifier.
    /// </summary>
    internal static string? ReadCoTaskMemString(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static T Create<T>(Guid clsid)
        where T : class
    {
        var hr = Ole32.CoCreateInstance(
            in clsid,
            IntPtr.Zero,
            Ole32.ClsCtxInprocServer,
            in Ole32.IidIUnknown,
            out var unknown);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            // The wrapper takes its own reference, so this method's reference is still ours to
            // drop. Measured: the count goes up by one here and back down by one in
            // FinalRelease, and the Release below is what balances CoCreateInstance.
            return (T)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
