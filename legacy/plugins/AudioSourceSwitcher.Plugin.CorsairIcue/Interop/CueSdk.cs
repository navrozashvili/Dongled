using System.Runtime.InteropServices;

namespace AudioSourceSwitcher.Plugin.CorsairIcue.Interop;

internal delegate void CorsairSessionStateChangedHandler(nint context, CorsairSessionStateChanged eventData);

/// <summary>
/// Minimal iCUE SDK loader + wrappers, adapted from RGB.NET's native integration approach:
/// - loads a native iCUE SDK DLL via <see cref="NativeLibrary.TryLoad"/>
/// - resolves function exports via <see cref="NativeLibrary.TryGetExport"/>
/// - uses unmanaged function pointers for calls
/// </summary>
internal static unsafe class CueSdk
{
    internal const int CORSAIR_STRING_SIZE_M = 128;
    internal const int CORSAIR_DEVICE_COUNT_MAX = 64;

    private static readonly CorsairSessionStateChangedHandler SESSION_STATE_CHANGED_CALLBACK;

    private static nint _handle;
    private static string? _loadedPath;

    internal static bool IsInitialized { get; private set; }
    internal static CorsairSessionState SessionState { get; private set; } = CorsairSessionState.Invalid;

    private static delegate* unmanaged[Cdecl]<CorsairSessionStateChangedHandler, nint, CorsairError> _corsairConnectPtr;
    private static delegate* unmanaged[Cdecl]<CorsairError> _corsairDisconnect;
    private static delegate* unmanaged[Cdecl]<CorsairDeviceFilter, int, nint, out int, CorsairError> _corsairGetDevices;

    static CueSdk()
    {
        SESSION_STATE_CHANGED_CALLBACK = CorsairSessionStateChangedCallback;
    }

    private static void CorsairSessionStateChangedCallback(nint context, CorsairSessionStateChanged eventData)
    {
        SessionState = eventData.state;
    }

    internal static bool TryInitialize(string pluginDirectory, out string error)
    {
        error = string.Empty;

        if (IsInitialized)
            return true;

        try
        {
            Reload(pluginDirectory);
        }
        catch (Exception ex)
        {
            error = $"Failed to load native SDK DLL. {ex.GetType().Name}: {ex.Message}";
            return false;
        }

        try
        {
            var rc = CorsairConnect();
            if (rc != CorsairError.Success)
            {
                error = $"CorsairConnect failed: {rc}. Ensure iCUE is running and SDK control is enabled.";
                TryDisconnectAndUnload();
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"CorsairConnect threw. {ex.GetType().Name}: {ex.Message}";
            TryDisconnectAndUnload();
            return false;
        }

        IsInitialized = true;
        return true;
    }

    internal static void TryDisconnectAndUnload()
    {
        try
        {
            if (_corsairDisconnect != null && SessionState == CorsairSessionState.Connected)
                _corsairDisconnect();
        }
        catch
        {
            // ignore
        }
        finally
        {
            IsInitialized = false;
            SessionState = CorsairSessionState.Invalid;
            Unload();
        }
    }

    internal static bool TryGetDevices(CorsairDeviceFilter filter, out CorsairDeviceInfo[] devices, out CorsairError? error)
    {
        devices = Array.Empty<CorsairDeviceInfo>();
        error = null;

        if (!IsInitialized)
        {
            error = CorsairError.NotConnected;
            return false;
        }

        var structSize = Marshal.SizeOf<CorsairDeviceInfo>();
        nint devicePtr = Marshal.AllocHGlobal(structSize * CORSAIR_DEVICE_COUNT_MAX);

        try
        {
            var rc = _corsairGetDevices(filter, CORSAIR_DEVICE_COUNT_MAX, devicePtr, out var size);
            error = rc == CorsairError.Success ? null : rc;

            var count = Math.Clamp(size, 0, CORSAIR_DEVICE_COUNT_MAX);
            if (count == 0)
            {
                devices = Array.Empty<CorsairDeviceInfo>();
                return rc == CorsairError.Success;
            }

            var arr = new CorsairDeviceInfo[count];
            for (var i = 0; i < count; i++)
            {
                var ptr = devicePtr + (i * structSize);
                arr[i] = Marshal.PtrToStructure<CorsairDeviceInfo>(ptr)!;
            }

            devices = arr;
            return rc == CorsairError.Success;
        }
        catch
        {
            error = CorsairError.NotConnected;
            devices = Array.Empty<CorsairDeviceInfo>();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(devicePtr);
        }
    }

    private static void Reload(string pluginDirectory)
    {
        Unload();
        Load(pluginDirectory);
    }

    private static void Load(string pluginDirectory)
    {
        if (_handle != 0) return;

        var candidatePaths = GetPossibleLibraryPaths(pluginDirectory).ToList();
        var dllPath = candidatePaths.FirstOrDefault(File.Exists);
        if (dllPath is null)
            throw new InvalidOperationException("Can't find iCUE SDK DLL. Looked in:\r\n" + string.Join("\r\n", candidatePaths.Select(Path.GetFullPath)));

        if (!NativeLibrary.TryLoad(dllPath, out _handle))
            throw new InvalidOperationException($"NativeLibrary.Load failed with error {Marshal.GetLastPInvokeError()} (Path={dllPath})");

        _loadedPath = dllPath;

        _corsairConnectPtr = (delegate* unmanaged[Cdecl]<CorsairSessionStateChangedHandler, nint, CorsairError>)LoadFunction("CorsairConnect");
        _corsairDisconnect = (delegate* unmanaged[Cdecl]<CorsairError>)LoadFunction("CorsairDisconnect");
        _corsairGetDevices = (delegate* unmanaged[Cdecl]<CorsairDeviceFilter, int, nint, out int, CorsairError>)LoadFunction("CorsairGetDevices");
    }

    private static nint LoadFunction(string function)
    {
        if (_handle == 0) throw new InvalidOperationException("SDK not loaded.");
        if (!NativeLibrary.TryGetExport(_handle, function, out var ptr))
            throw new EntryPointNotFoundException($"Failed to load Corsair function '{function}' from '{_loadedPath}'.");
        return ptr;
    }

    private static void Unload()
    {
        if (_handle == 0) return;

        _corsairConnectPtr = null;
        _corsairDisconnect = null;
        _corsairGetDevices = null;

        try { NativeLibrary.Free(_handle); } catch { /* ignore */ }
        _handle = 0;
        _loadedPath = null;
    }

    private static CorsairError CorsairConnect()
    {
        if (_corsairConnectPtr == null) throw new InvalidOperationException("SDK not loaded.");
        return _corsairConnectPtr(SESSION_STATE_CHANGED_CALLBACK, 0);
    }

    private static IEnumerable<string> GetPossibleLibraryPaths(string pluginDirectory)
    {
        // Most robust: user can point directly at a DLL.
        var envPath = Environment.GetEnvironmentVariable("AUDIO_SOURCE_SWITCHER_CORSAIR_SDK_DLL");
        if (!string.IsNullOrWhiteSpace(envPath))
            yield return Environment.ExpandEnvironmentVariables(envPath.Trim());

        // Common shipping layout: native DLLs placed next to the plugin.
        // Match RGB.NET's possible paths:
        // x86:  x86/iCUESDK.dll, x86/CUESDK_2019.dll
        // x64:  x64/iCUESDK.dll, x64/iCUESDK.x64_2019.dll, x64/CUESDK.dll, x64/CUESDK.x64_2019.dll
        var rel = Environment.Is64BitProcess
            ? new[]
            {
                Path.Combine("x64", "iCUESDK.dll"),
                Path.Combine("x64", "iCUESDK.x64_2019.dll"),
                Path.Combine("x64", "CUESDK.dll"),
                Path.Combine("x64", "CUESDK.x64_2019.dll"),
            }
            : new[]
            {
                Path.Combine("x86", "iCUESDK.dll"),
                Path.Combine("x86", "CUESDK_2019.dll"),
            };

        foreach (var r in rel)
            yield return Path.Combine(pluginDirectory, r);

        // Also allow dropping the DLL directly in the plugin folder.
        yield return Path.Combine(pluginDirectory, "iCUESDK.dll");
        yield return Path.Combine(pluginDirectory, "CUESDK.dll");
    }
}





