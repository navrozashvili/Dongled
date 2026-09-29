using System.Runtime.InteropServices;

namespace AudioSourceSwitcher.Plugin.CorsairIcue.Interop;

// Minimal interop types adapted from RGB.NET.Devices.Corsair (DarthAffe/RGB.NET).

[Flags]
internal enum CorsairDeviceType : uint
{
    Unknown = 0x0000,
    Keyboard = 0x0001,
    Mouse = 0x0002,
    Mousemat = 0x0004,
    Headset = 0x0008,
    HeadsetStand = 0x0010,
    FanLedController = 0x0020,
    LedController = 0x0040,
    MemoryModule = 0x0080,
    Cooler = 0x0100,
    Motherboard = 0x0200,
    GraphicsCard = 0x0400,
    Touchbar = 0x0800,
    GameController = 0x1000,
    All = 0xFFFFFFFF
}

internal enum CorsairError
{
    Success = 0,
    NotConnected = 1,
    NoControl = 2,
    IncompatibleProtocol = 3,
    InvalidArguments = 4,
    InvalidOperation = 5,
    DeviceNotFound = 6,
    NotAllowed = 7
}

internal enum CorsairSessionState
{
    Invalid = 0,
    Closed = 1,
    Connecting = 2,
    Timeout = 3,
    ConnectionRefused = 4,
    ConnectionLost = 5,
    Connected = 6
}

// iCUE-SDK: contains information about version that consists of three components
[StructLayout(LayoutKind.Sequential)]
internal sealed class CorsairVersion
{
    internal int major;
    internal int minor;
    internal int patch;
}

// iCUE-SDK: contains information about SDK and iCUE versions
[StructLayout(LayoutKind.Sequential)]
internal sealed class CorsairSessionDetails
{
    internal CorsairVersion clientVersion = new();
    internal CorsairVersion serverVersion = new();
    internal CorsairVersion serverHostVersion = new();
}

// iCUE-SDK: contains information about session state and client/server versions
[StructLayout(LayoutKind.Sequential)]
internal sealed class CorsairSessionStateChanged
{
    internal CorsairSessionState state;
    internal CorsairSessionDetails details = new();
}

// iCUE-SDK: contains device search filter
[StructLayout(LayoutKind.Sequential)]
internal sealed class CorsairDeviceFilter
{
    internal CorsairDeviceType deviceTypeMask;

    public CorsairDeviceFilter() { }
    public CorsairDeviceFilter(CorsairDeviceType filter) => deviceTypeMask = filter;
}

// iCUE-SDK: contains information about device
// NOTE: iCUE SDK uses fixed-size `char[]` fields for strings (NOT UTF-16). Using CharSet.Unicode here
// corrupts the strings (garbled IDs/models) and can misalign subsequent fields.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct CorsairDeviceInfo
{
    internal CorsairDeviceType type;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CueSdk.CORSAIR_STRING_SIZE_M)]
    internal string id;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CueSdk.CORSAIR_STRING_SIZE_M)]
    internal string serial;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CueSdk.CORSAIR_STRING_SIZE_M)]
    internal string model;

    internal int ledCount;
    internal int channelCount;
}


