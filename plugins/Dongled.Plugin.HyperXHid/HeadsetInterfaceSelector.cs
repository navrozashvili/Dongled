namespace Dongled.Plugin.HyperXHid;

/// <summary>
/// Which of the dongle's HID interfaces to talk to. Separated from the vendor library so the rule is
/// testable: the decision is about report lengths, not about a vendor device type.
/// </summary>
internal static class HeadsetInterfaceSelector
{
    /// <summary>
    /// Index of the interface with the largest usable input report, or null if none is usable. Ties take
    /// the earliest, so repeated scans of an unchanged dongle choose the same interface.
    /// </summary>
    /// <param name="inputReportLengths">
    /// Max input report length per interface, in enumeration order. Zero or negative means the length
    /// could not be read or the interface has no input report; either way it cannot deliver an event.
    /// </param>
    public static int? SelectLargestInputReport(IReadOnlyList<int> inputReportLengths)
    {
        ArgumentNullException.ThrowIfNull(inputReportLengths);

        int? best = null;
        for (var i = 0; i < inputReportLengths.Count; i++)
        {
            if (inputReportLengths[i] <= 0)
            {
                continue;
            }

            if (best is null || inputReportLengths[i] > inputReportLengths[best.Value])
            {
                best = i;
            }
        }

        return best;
    }
}
