using HidSharp;

namespace Dongled.Plugin.HyperXHid;

/// <summary>
/// The real dongle, through HidSharp.
/// </summary>
/// <remarks>
/// Deliberately thin: it translates and does not decide. The decisions live in
/// <see cref="HeadsetInterfaceSelector"/> and <see cref="HeadsetProtocol"/>, which are testable without
/// a device. No unit test covers this type - a mock of a vendor type would only prove the mock works -
/// so the hardware smoke test is its evidence.
/// </remarks>
internal sealed class HidSharpHeadsetTransport : IHeadsetTransport
{
    private const int VendorId = 0x03F0;
    private const int ProductId = 0x06BE;

    /// <summary>Shortest report buffer worth allocating.</summary>
    private const int MinimumReportLength = 64;

    /// <inheritdoc />
    public IHeadsetSession? TryOpen()
    {
        // Always scans, with no rate limit. A filtered enumeration of every HID device on a typical
        // machine costs well under a millisecond, and a limiter that returned null would be
        // indistinguishable from an absent dongle.
        var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
        var index = HeadsetInterfaceSelector.SelectLargestInputReport(
            [.. devices.Select(TryGetMaxInputReportLength)]);

        if (index is null)
        {
            return null;
        }

        var device = devices[index.Value];
        return device.TryOpen(out var stream) ? new HidSharpHeadsetSession(device, stream) : null;
    }

    private static int TryGetMaxInputReportLength(HidDevice device)
    {
        try
        {
            return device.GetMaxInputReportLength();
        }
        catch (Exception)
        {
            // An interface that will not answer the query cannot deliver an event either. Negative
            // means "unusable" to the selector.
            return -1;
        }
    }

    private sealed class HidSharpHeadsetSession : IHeadsetSession
    {
        private readonly HidStream _stream;

        public HidSharpHeadsetSession(HidDevice device, HidStream stream)
        {
            _stream = stream;
            InputReportLength = Math.Max(MinimumReportLength, TryGetLength(device.GetMaxInputReportLength));
            OutputReportLength = Math.Max(MinimumReportLength, TryGetLength(device.GetMaxOutputReportLength));
        }

        public int InputReportLength { get; }

        public int OutputReportLength { get; }

        public void Write(ReadOnlySpan<byte> report) => _stream.Write(report);

        public bool TryRead(Span<byte> buffer, TimeSpan timeout, out int count)
        {
            _stream.ReadTimeout = (int)timeout.TotalMilliseconds;

            try
            {
                count = _stream.Read(buffer);
                return true;
            }
            catch (TimeoutException)
            {
                // The ordinary case: the dongle only speaks when something changes.
                count = 0;
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                count = 0;
                return false;
            }
        }

        public void Dispose() => _stream.Dispose();

        private static int TryGetLength(Func<int> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
