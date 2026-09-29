using System.Runtime.InteropServices;

namespace Dongled.Core.Interop;

/// <summary>Direction of an audio endpoint. Native <c>EDataFlow</c>.</summary>
internal enum EDataFlow
{
    /// <summary>Playback.</summary>
    Render = 0,

    /// <summary>Recording.</summary>
    Capture = 1,

    /// <summary>Both. Never used: the app does not switch capture devices.</summary>
    All = 2,
}

/// <summary>Which default an endpoint holds. Native <c>ERole</c>.</summary>
/// <remarks>
/// <see cref="Console"/> is deliberately never written. The app manages only
/// <see cref="Multimedia"/> and <see cref="Communications"/>; setting the console role as well
/// would change which device Windows hands to applications that ask for it.
/// </remarks>
internal enum ERole
{
    /// <summary>Games and system notifications. Read about, never assigned.</summary>
    Console = 0,

    /// <summary>General playback. The role the app calls "Media".</summary>
    Multimedia = 1,

    /// <summary>Voice chat. The role the app calls "Calls".</summary>
    Communications = 2,
}

/// <summary>Endpoint availability. Native <c>DEVICE_STATE_*</c>.</summary>
[Flags]
internal enum DeviceState : uint
{
    /// <summary>Present and usable.</summary>
    Active = 0x00000001,

    /// <summary>Disabled in Windows sound settings.</summary>
    Disabled = 0x00000002,

    /// <summary>The device is gone.</summary>
    NotPresent = 0x00000004,

    /// <summary>The jack is empty.</summary>
    Unplugged = 0x00000008,

    /// <summary>Every state, which is what enumeration asks for.</summary>
    All = Active | Disabled | NotPresent | Unplugged,
}

/// <summary>Property store access mode. Native <c>STGM_*</c>.</summary>
internal enum StorageAccessMode : uint
{
    /// <summary>Read only, which is all this app ever needs.</summary>
    Read = 0,
}

/// <summary>Native <c>PROPERTYKEY</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    /// <summary>Property set identifier.</summary>
    public Guid Fmtid;

    /// <summary>Property identifier within the set.</summary>
    public uint Pid;
}

/// <summary>The property keys this app reads.</summary>
internal static class PropertyKeys
{
    /// <summary><c>PKEY_Device_FriendlyName</c>: the name Windows sound settings shows.</summary>
    public static readonly PropertyKey DeviceFriendlyName = new()
    {
        Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        Pid = 14,
    };
}

/// <summary>
/// Enough of native <c>PROPVARIANT</c> to read a wide string, which is the only variant type
/// this app asks for.
/// </summary>
/// <remarks>
/// Explicit layout with the pointer at offset 8 matches the 64-bit native union: two bytes of
/// <c>vt</c>, six bytes of reserved fields, then the union. This app is x64 only.
/// </remarks>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    /// <summary><c>VT_LPWSTR</c>.</summary>
    private const ushort VtLpwstr = 31;

    /// <summary>Variant type tag.</summary>
    [FieldOffset(0)]
    public ushort Vt;

    /// <summary>The union, read only as a string pointer.</summary>
    [FieldOffset(8)]
    public IntPtr Pointer;

    /// <summary>The string this variant holds, or null if it holds something else.</summary>
    public readonly string? AsString() =>
        Vt == VtLpwstr && Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(Pointer) : null;
}
