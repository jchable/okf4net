// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// The estimate behind <see cref="Bundle.EstimatedMemoryBytes"/> and the memory ceiling (#151). The managed
/// size of an image is not cheaply measurable in .NET, so this is a linear model over what the load reads:
/// each concept object costs <see cref="PerObjectBytes"/> plus <see cref="PerUtf8Byte"/> times its size in
/// UTF-8 bytes (its text held as UTF-16 strings, the frontmatter tree), and each link its body holds
/// <see cref="PerLinkBytes"/> (the scanned link, its resolved form, its backlink entry: a link-heavy bundle
/// costs several times its text). The constants are calibrated by <c>SnapshotMemoryCalibrationTests</c>, which
/// measures retained bytes over reference bundles and pins the estimate within its stated tolerance. The byte
/// terms are known before a concept is parsed and the link term after, so the byte terms alone are a lower bound
/// of the whole: the refusal before any read (from the sizes the listing declares) and the stop of the parallel
/// reads use them, and never refuse what the load would have accepted (beyond a byte-order mark, which a
/// declared size counts and the decoded text does not).
/// </summary>
internal static class SnapshotMemory
{
    /// <summary>The fixed cost of one concept object: the <c>Concept</c>, its document, frontmatter mapping, index entries.</summary>
    internal const long PerObjectBytes = 2_300;

    /// <summary>The cost per UTF-8 byte of the object's content (UTF-16 strings: about two).</summary>
    internal const long PerUtf8Byte = 2;

    /// <summary>The cost of one link in a concept's body.</summary>
    internal const long PerLinkBytes = 350;

    /// <summary>The estimate for the links of one concept.</summary>
    internal static long OfLinks(int count) => PerLinkBytes * count;

    /// <summary>The estimate for one concept object of <paramref name="utf8Bytes"/> bytes.</summary>
    internal static long OfObject(long utf8Bytes) => PerObjectBytes + (PerUtf8Byte * Math.Max(0, utf8Bytes));

    /// <summary>The estimate for one concept object whose decoded text is <paramref name="text"/>.</summary>
    internal static long OfText(string text) => OfObject(System.Text.Encoding.UTF8.GetByteCount(text));
}
