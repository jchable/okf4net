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
/// keys.
/// </summary>
public sealed class Frontmatter : IEquatable<Frontmatter>
{
    /// <summary>
    /// Frontmatter keys a producer's enrichment workflow requires before a
    /// document is considered publishable. Note this is *stricter* than spec
    /// conformance (§11), which requires only <c>type</c>.
    /// </summary>
    public static readonly string[] RequiredKeys = ["type", "title", "description"];

    /// <summary>Well-known OKF fields excluded from <see cref="ExtensionKeys"/>.</summary>
    private static readonly string[] KnownKeys =
    [
        "type", "title", "description", "resource", "tags",
        "timestamp",              // legacy §13.1, still recognized (not an extension)
        "generated", "verified",  // §5.2
        "sources", "usage_window",// §5.1
        "status", "stale_after",  // §5.4/§5.5
        "runtime", "parameters", "computation", "executor", "attester", // §10
    ];

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

    /// <summary>The §5.1 provenance sources (frontmatter field only; empty if the field is absent or malformed).</summary>
    public IReadOnlyList<Source> Sources => Provenance.ParseSources(_map.Get("sources"));

    /// <summary>The §5.1 <c>usage_window</c> sibling of <c>sources</c>, if present.</summary>
    public UsageWindow? UsageWindow => Provenance.ParseUsageWindow(_map.Get("usage_window"));

    /// <summary>The §5.2 <c>generated</c> stamp, if present.</summary>
    public Stamp? Generated => Trust.ParseGenerated(_map.Get("generated"));

    /// <summary>The §5.2 <c>verified</c> stamps (a bare mapping normalizes to one element).</summary>
    public IReadOnlyList<Stamp> Verified => Trust.ParseVerified(_map.Get("verified"));

    /// <summary>The §5.3 trust tier derived from <see cref="Verified"/>.</summary>
    public TrustTier TrustTier => Trust.DeriveTier(Verified);

    /// <summary>The §5.4/§5.5 lifecycle (<c>status</c>, <c>stale_after</c>).</summary>
    // The static factory is namespace-qualified because this property shares
    // its name with the Lifecycle type (the C# "Color Color" case) — qualifying
    // keeps the static call unambiguous.
    public Lifecycle Lifecycle => OKF4net.Lifecycle.From(_map.Get("status")?.AsDisplayString(), _map.Get("stale_after")?.AsDisplayString());

    /// <summary><c>true</c> if <see cref="Type"/> is exactly <c>"Attested Computation"</c> (§10, ordinal comparison).</summary>
    public bool IsAttestedComputation =>
        string.Equals(Type, "Attested Computation", StringComparison.Ordinal);

    /// <summary>The §10.2 Attested Computation contract (<c>runtime</c>, <c>parameters</c>, <c>computation</c>, <c>executor</c>, <c>attester</c>), projected regardless of <see cref="IsAttestedComputation"/>.</summary>
    public AttestedComputationContract ComputationContract => AttestedComputation.Project(_map);

    /// <summary>The §5.2 <c>generated.at</c> timestamp, if any.</summary>
    public string? GeneratedAt => Generated?.At;

    /// <summary>The canonical "last meaningful change" time: <see cref="GeneratedAt"/>, falling back to the legacy <see cref="Timestamp"/> (§13.1).</summary>
    public string? LastChangedAt => GeneratedAt ?? Timestamp;

    /// <summary>
    /// The optional <c>tags</c> list. Non-scalar elements are dropped; a
    /// non-sequence <c>tags</c> value (including a bare scalar) yields an
    /// empty list.
    /// </summary>
    public IReadOnlyList<string> Tags => YamlValue.AsStringList(_map.Get("tags"));

    /// <summary>
    /// The keys present that are not well-known OKF fields — i.e. the
    /// producer-defined extension keys consumers must preserve (§4.1).
    /// </summary>
    public IReadOnlyList<string> ExtensionKeys =>
        _map.Keys.Where(k => !KnownKeys.Contains(k, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// Structural equality: the underlying <see cref="YamlMapping"/>s are
    /// structurally equal (key/value-wise, order-preserving).
    /// </summary>
    public bool Equals(Frontmatter? other) => other is not null && (ReferenceEquals(this, other) || _map.Equals(other._map));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Frontmatter);

    /// <inheritdoc/>
    public override int GetHashCode() => _map.GetHashCode();
}
