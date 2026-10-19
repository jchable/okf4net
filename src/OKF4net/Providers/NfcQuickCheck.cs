// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Providers;

/// <summary>Result of the NFC quick check for one code point (UAX #15 §9).</summary>
public enum NfcStatus
{
    /// <summary>Never changes under NFC on its own.</summary>
    Yes,

    /// <summary>Never in NFC (a singleton or non-starter decomposition), or refused by the reference normalizer.</summary>
    No,

    /// <summary>A mark that recomposes with some preceding base; undecidable without composition tables.</summary>
    Maybe,
}

/// <summary>
/// UAX #15's NFC quick check, table-driven and independent of <see cref="string.Normalize(NormalizationForm)"/>,
/// which is a silent no-op under invariant globalization (the Native AOT <c>okf</c>).
/// <see cref="No"/> is <c>NFC_QC=No</c> plus the code points the reference normalizer refuses;
/// <see cref="Maybe"/> is the set of second elements of every direct canonical pair that
/// recomposes — tighter than UCD's <c>NFC_QC=Maybe</c>, and exact for the question "can this
/// mark compose with something before it". The canonical ordering of the marks is checked
/// separately by <see cref="CanonicalForm.IsCanonicallyOrdered"/>. Both tables are derived
/// from ICU by <c>tests/OKF4net.Tests/Providers/UnicodeTableTests.cs</c>, which fails with the
/// literal to paste whenever a host normalizer disagrees with them. Generated on
/// <c>Windows 11 25H2 (10.0.26200) / .NET 10.0.8 / ICU 72.1, Unicode 15.1</c>.
/// </summary>
internal static class NfcQuickCheck
{
    internal static readonly (int Start, int End)[] No =
    [
        (0x0340, 0x0341),
        (0x0343, 0x0344),
        (0x0374, 0x0374),
        (0x037E, 0x037E),
        (0x0387, 0x0387),
        (0x0958, 0x095F),
        (0x09DC, 0x09DD),
        (0x09DF, 0x09DF),
        (0x0A33, 0x0A33),
        (0x0A36, 0x0A36),
        (0x0A59, 0x0A5B),
        (0x0A5E, 0x0A5E),
        (0x0B5C, 0x0B5D),
        (0x0F43, 0x0F43),
        (0x0F4D, 0x0F4D),
        (0x0F52, 0x0F52),
        (0x0F57, 0x0F57),
        (0x0F5C, 0x0F5C),
        (0x0F69, 0x0F69),
        (0x0F73, 0x0F73),
        (0x0F75, 0x0F76),
        (0x0F78, 0x0F78),
        (0x0F81, 0x0F81),
        (0x0F93, 0x0F93),
        (0x0F9D, 0x0F9D),
        (0x0FA2, 0x0FA2),
        (0x0FA7, 0x0FA7),
        (0x0FAC, 0x0FAC),
        (0x0FB9, 0x0FB9),
        (0x1F71, 0x1F71),
        (0x1F73, 0x1F73),
        (0x1F75, 0x1F75),
        (0x1F77, 0x1F77),
        (0x1F79, 0x1F79),
        (0x1F7B, 0x1F7B),
        (0x1F7D, 0x1F7D),
        (0x1FBB, 0x1FBB),
        (0x1FBE, 0x1FBE),
        (0x1FC9, 0x1FC9),
        (0x1FCB, 0x1FCB),
        (0x1FD3, 0x1FD3),
        (0x1FDB, 0x1FDB),
        (0x1FE3, 0x1FE3),
        (0x1FEB, 0x1FEB),
        (0x1FEE, 0x1FEF),
        (0x1FF9, 0x1FF9),
        (0x1FFB, 0x1FFB),
        (0x1FFD, 0x1FFD),
        (0x2000, 0x2001),
        (0x2126, 0x2126),
        (0x212A, 0x212B),
        (0x2329, 0x232A),
        (0x2ADC, 0x2ADC),
        (0xF900, 0xFA0D),
        (0xFA10, 0xFA10),
        (0xFA12, 0xFA12),
        (0xFA15, 0xFA1E),
        (0xFA20, 0xFA20),
        (0xFA22, 0xFA22),
        (0xFA25, 0xFA26),
        (0xFA2A, 0xFA6D),
        (0xFA70, 0xFAD9),
        (0xFB1D, 0xFB1D),
        (0xFB1F, 0xFB1F),
        (0xFB2A, 0xFB36),
        (0xFB38, 0xFB3C),
        (0xFB3E, 0xFB3E),
        (0xFB40, 0xFB41),
        (0xFB43, 0xFB44),
        (0xFB46, 0xFB4E),
        (0xFFFE, 0xFFFE),
        (0x1D15E, 0x1D164),
        (0x1D1BB, 0x1D1C0),
        (0x2F800, 0x2FA1D),
    ];

    internal static readonly (int Start, int End)[] Maybe =
    [
        (0x0300, 0x0304),
        (0x0306, 0x030C),
        (0x030F, 0x030F),
        (0x0311, 0x0311),
        (0x0313, 0x0314),
        (0x031B, 0x031B),
        (0x0323, 0x0328),
        (0x032D, 0x032E),
        (0x0330, 0x0331),
        (0x0338, 0x0338),
        (0x0342, 0x0342),
        (0x0345, 0x0345),
        (0x0653, 0x0655),
        (0x093C, 0x093C),
        (0x09BE, 0x09BE),
        (0x09D7, 0x09D7),
        (0x0B3E, 0x0B3E),
        (0x0B56, 0x0B57),
        (0x0BBE, 0x0BBE),
        (0x0BD7, 0x0BD7),
        (0x0C56, 0x0C56),
        (0x0CC2, 0x0CC2),
        (0x0CD5, 0x0CD6),
        (0x0D3E, 0x0D3E),
        (0x0D57, 0x0D57),
        (0x0DCA, 0x0DCA),
        (0x0DCF, 0x0DCF),
        (0x0DDF, 0x0DDF),
        (0x102E, 0x102E),
        (0x1161, 0x1175),
        (0x11A8, 0x11C2),
        (0x1B35, 0x1B35),
        (0x3099, 0x309A),
        (0x110BA, 0x110BA),
        (0x11127, 0x11127),
        (0x1133E, 0x1133E),
        (0x11357, 0x11357),
        (0x114B0, 0x114B0),
        (0x114BA, 0x114BA),
        (0x114BD, 0x114BD),
        (0x115AF, 0x115AF),
        (0x11930, 0x11930),
    ];

    /// <summary>The quick-check status of one scalar value.</summary>
    internal static NfcStatus Of(Rune rune) =>
        Contains(No, rune.Value) ? NfcStatus.No
        : Contains(Maybe, rune.Value) ? NfcStatus.Maybe
        : NfcStatus.Yes;

    private static bool Contains((int Start, int End)[] ranges, int cp)
    {
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var (start, end) = ranges[mid];
            if (cp < start)
            {
                hi = mid - 1;
            }
            else if (cp > end)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
