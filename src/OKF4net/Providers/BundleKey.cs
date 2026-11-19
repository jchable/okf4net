// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>Thrown when a string is not a valid <see cref="BundleKey"/> (base rules, spec §2 layer 1).</summary>
public sealed class BundleKeyException(string message) : OkfException(message)
{
}

/// <summary>
/// A validated, provider-independent object key inside a bundle: <c>/</c>-separated
/// segments, compared ordinally and case-sensitively, never percent-decoded.
/// This type carries only the base rules (spec §2, layer 1); the storage-specific
/// constraints and the portability policy live in <c>BundleKeyValidator</c>.
/// </summary>
public sealed class BundleKey : IEquatable<BundleKey>, IComparable<BundleKey>
{
    /// <summary>Upper bound on a key's UTF-8 length, inclusive (spec §2, §6).</summary>
    public const int MaxUtf8Bytes = 1024;

    private readonly string _text;

    private BundleKey(List<string> segments, int utf8ByteCount)
    {
        Segments = new ReadOnlyCollection<string>(segments);
        _text = string.Join('/', segments);
        Utf8ByteCount = utf8ByteCount;
    }

    /// <summary>The key's segments, in order; never empty.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The key's length in UTF-8 bytes (the unit of <see cref="MaxUtf8Bytes"/> and of a host's <c>BundleLimits.MaxKeyBytes</c>).</summary>
    public int Utf8ByteCount { get; }

    /// <summary>The last segment.</summary>
    public string Name => Segments[^1];

    /// <summary>The key without its last segment, or <see langword="null"/> for a top-level key.</summary>
    public BundleKey? Parent => Segments.Count <= 1 ? null : Parse(string.Join('/', Segments.Take(Segments.Count - 1)));

    /// <summary><see langword="true"/> when the last segment ends with <c>.md</c> (ordinal, like <c>MarkdownPaths</c>).</summary>
    public bool IsMarkdown => MarkdownPaths.HasMarkdownExtension(Name);

    /// <summary>Builds a key from segments; the joined text is checked by the base rules.</summary>
    public static BundleKey FromSegments(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Parse(string.Join('/', segments));
    }

    /// <summary>Parses a raw name, throwing <see cref="BundleKeyException"/> when it breaks a base rule.</summary>
    public static BundleKey Parse(string raw) =>
        TryParse(raw, out var key, out var reason) ? key : throw new BundleKeyException(reason);

    /// <summary>
    /// Parses a raw name against the base rules: non-empty, no leading or trailing
    /// <c>/</c>, no empty, <c>.</c> or <c>..</c> segment, no backslash, no control
    /// character (U+0000–U+001F, U+007F–U+009F — exactly those; U+2028 and friends are
    /// separators and stay), at most <see cref="MaxUtf8Bytes"/> bytes in UTF-8. The text is
    /// taken literally: <c>%2e%2e</c> is a name, not <c>..</c>.
    /// </summary>
    public static bool TryParse(string raw, [NotNullWhen(true)] out BundleKey? key, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(raw);
        key = null;

        if (raw.Length == 0)
        {
            reason = "key is empty";
            return false;
        }

        if (raw[0] == '/')
        {
            reason = "key has a leading '/'";
            return false;
        }

        if (raw[^1] == '/')
        {
            reason = "key has a trailing '/'";
            return false;
        }

        if (raw.Contains('\\'))
        {
            reason = "key contains a backslash";
            return false;
        }

        foreach (var c in raw)
        {
            if (c <= '\u001f' || (c >= '\u007f' && c <= '\u009f'))
            {
                reason = $"key contains the control character U+{(int)c:X4}";
                return false;
            }
        }

        var bytes = Encoding.UTF8.GetByteCount(raw);
        if (bytes > MaxUtf8Bytes)
        {
            reason = $"key is longer than {MaxUtf8Bytes} UTF-8 bytes";
            return false;
        }

        var segments = raw.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                reason = "key has an empty segment";
                return false;
            }

            if (segment == ".")
            {
                reason = "key has a '.' segment";
                return false;
            }

            if (segment == "..")
            {
                reason = "key has a '..' segment";
                return false;
            }
        }

        key = new BundleKey(segments.ToList(), bytes);
        reason = null;
        return true;
    }

    /// <summary>Returns this key with one more segment; the segment must pass the base rules and contain no <c>/</c>.</summary>
    public BundleKey Append(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Contains('/'))
        {
            throw new BundleKeyException("a segment must not contain '/'");
        }

        return Parse(_text + "/" + segment);
    }

    /// <summary><see langword="true"/> when this key equals <paramref name="prefix"/> or is under it at a segment boundary (<c>ab/x</c> is not under <c>a</c>).</summary>
    public bool StartsWith(BundleKey prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Segments.Count > Segments.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Segments.Count; i++)
        {
            if (!string.Equals(Segments[i], prefix.Segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Equals(BundleKey? other) => other is not null && string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as BundleKey);

    /// <inheritdoc/>
    public override int GetHashCode() => string.GetHashCode(_text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public int CompareTo(BundleKey? other) => other is null ? 1 : string.CompareOrdinal(_text, other._text);

    /// <inheritdoc/>
    public override string ToString() => _text;
}
