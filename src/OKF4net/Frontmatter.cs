// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>
/// Typed, order-preserving access to a concept's YAML frontmatter.
///
/// OKF frontmatter is an open mapping: a few well-known keys (§4.1 of the
/// spec) plus arbitrary producer-defined extensions that consumers MUST
/// preserve when round-tripping. <see cref="Frontmatter"/> therefore stores
/// the full <see cref="YamlMapping"/> verbatim and layers typed accessors on
/// top, rather than deserializing into a fixed shape that would drop unknown
/// keys. Port of the Rust <c>Frontmatter</c> (src/frontmatter.rs).
/// </summary>
public sealed class Frontmatter
{
    /// <summary>
    /// Frontmatter keys the reference enrichment agent requires before a
    /// document is considered publishable. Note this is *stricter* than spec
    /// conformance (§9), which requires only <c>type</c>. Port of
    /// <c>REQUIRED_FRONTMATTER_KEYS</c> (frontmatter.rs:16).
    /// </summary>
    public static readonly string[] RequiredKeys = ["type", "title", "description", "timestamp"];

    /// <summary>Well-known OKF fields excluded from <see cref="ExtensionKeys"/>. Port of frontmatter.rs:107.</summary>
    private static readonly string[] KnownKeys =
        ["type", "title", "description", "resource", "tags", "timestamp"];

    private readonly YamlMapping _map;

    /// <summary>Creates an empty frontmatter block.</summary>
    public Frontmatter()
    {
        _map = new YamlMapping();
    }

    private Frontmatter(YamlMapping map)
    {
        _map = map;
    }

    /// <summary>Wraps an existing mapping.</summary>
    public static Frontmatter FromMapping(YamlMapping map) => new(map);

    /// <summary>The underlying ordered mapping, in full.</summary>
    public YamlMapping AsMapping() => _map;

    /// <summary><c>true</c> if there are no keys.</summary>
    public bool IsEmpty => _map.IsEmpty;

    /// <summary>Raw value for an arbitrary key (including producer extensions).</summary>
    public YamlValue? Get(string key) => _map.Get(key);

    /// <summary>Sets a raw value for a key, preserving position if it already exists.</summary>
    public void Set(string key, YamlValue value) => _map.Insert(key, value);

    /// <summary>The **required** <c>type</c> field (§4.1). <c>null</c> if absent or not a scalar.</summary>
    public string? Type => _map.Get("type")?.AsDisplayString();

    /// <summary>The optional <c>title</c> field.</summary>
    public string? Title => _map.Get("title")?.AsDisplayString();

    /// <summary>The optional one-line <c>description</c>.</summary>
    public string? Description => _map.Get("description")?.AsDisplayString();

    /// <summary>The optional <c>resource</c> URI for the underlying asset.</summary>
    public string? Resource => _map.Get("resource")?.AsDisplayString();

    /// <summary>The optional ISO-8601 <c>timestamp</c> of last meaningful change.</summary>
    public string? Timestamp => _map.Get("timestamp")?.AsDisplayString();

    /// <summary>
    /// The optional <c>tags</c> list. Non-scalar elements are dropped; a
    /// non-sequence <c>tags</c> value (including a bare scalar) yields an
    /// empty list. Port of <c>tags()</c> (frontmatter.rs:96-101).
    /// </summary>
    public IReadOnlyList<string> Tags =>
        _map.Get("tags") is YamlSequence seq
            ? seq.Items.Select(v => v.AsDisplayString()).Where(s => s is not null).Select(s => s!).ToList()
            : [];

    /// <summary>
    /// The keys present that are not well-known OKF fields — i.e. the
    /// producer-defined extension keys consumers must preserve (§4.1). Port
    /// of <c>extension_keys()</c> (frontmatter.rs:105-110).
    /// </summary>
    public IReadOnlyList<string> ExtensionKeys =>
        _map.Keys.Where(k => !KnownKeys.Contains(k, StringComparer.Ordinal)).ToList();
}
