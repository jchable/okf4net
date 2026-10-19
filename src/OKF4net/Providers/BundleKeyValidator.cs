// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>A raw name the tree validation refused, with the reason (spec §2).</summary>
public sealed record BundleKeyRejection(string RawName, string Reason);

/// <summary>
/// The outcome of <see cref="BundleKeyValidator.ValidateTree"/>: accepted keys in input order,
/// every rejected raw name, and the colliding or conflicting keys themselves
/// (<see cref="RejectedPrefixes"/>), under which nothing may later be admitted.
/// </summary>
public sealed record BundleKeyTreeResult(IReadOnlyList<BundleKey> Accepted, IReadOnlyList<BundleKeyRejection> Rejected, IReadOnlyList<BundleKey> RejectedPrefixes);

/// <summary>
/// Applies spec §2's layers 2 and 3 to keys: the provider's native constraints,
/// the host's portability policy, and optional extra validators that can only
/// restrict. <see cref="ValidateTree"/> checks a whole listing as a tree —
/// per-level collisions and file/directory conflicts — not key by key.
/// NFC is decided by <see cref="NfcQuickCheck"/> and <see cref="CanonicalForm.IsCanonicallyOrdered"/>;
/// sibling names are compared through <see cref="FoldSegment"/>: canonical decomposition
/// (<see cref="CanonicalForm.Decompose"/>) and the per-code-point upper-case fold
/// <see cref="FoldForCollision"/>, never a linguistic case mapping, never <c>Normalize</c>.
/// </summary>
public sealed class BundleKeyValidator
{
    private static readonly string[] ReservedBases =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    ];

    private static readonly char[] WindowsIllegal = ['<', '>', ':', '"', '|', '?', '*'];

    private readonly IReadOnlyList<Func<BundleKey, string?>> _extra;

    /// <summary>Creates a validator for a provider declaring <paramref name="native"/>, under <paramref name="policy"/>.</summary>
    public BundleKeyValidator(BundleKeyConstraints native, BundleKeyPolicy policy, IReadOnlyList<Func<BundleKey, string?>>? extraValidators = null)
    {
        Effective = policy == BundleKeyPolicy.Strict ? BundleKeyConstraintSets.Strict | native : native;
        _extra = extraValidators ?? [];
    }

    /// <summary>The constraints actually enforced: never below the provider's native set.</summary>
    public BundleKeyConstraints Effective { get; }

    /// <summary>
    /// Folds one segment for collision detection: per code point,
    /// <see cref="Rune.ToUpperInvariant(Rune)"/>, no context. This is a <b>conservative
    /// over-approximation</b> of a filesystem upcase table, not an equivalence: it merges
    /// <c>Σ</c>, <c>σ</c> and <c>ς</c>, whereas NTFS keeps <c>σ</c> and <c>ς</c> distinct — so
    /// wherever <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/> applies
    /// (<see cref="BundleKeyPolicy.Strict"/>, or a provider that declares it) two such
    /// siblings are flagged although an NTFS volume would hold both (plan decision 13: the
    /// exact table cannot be shipped). No native set carries the flag, so under the default
    /// policy they load, with a portability warning. Works under invariant globalization
    /// (full simple case mapping since .NET 8).
    /// </summary>
    public static string FoldForCollision(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var sb = new StringBuilder(segment.Length);
        foreach (var rune in segment.EnumerateRunes())
        {
            sb.Append(Rune.ToUpperInvariant(rune).ToString());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Folds one segment the way the effective constraints say two sibling names are
    /// compared: canonical decomposition (<see cref="CanonicalForm.Decompose"/>) under
    /// <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, then <see cref="FoldForCollision"/>
    /// under <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/>; identity under
    /// neither. It runs on every structural name, refused ones included: a decomposed name
    /// that <see cref="BundleKeyConstraints.RequireNfc"/> refuses is still a file beside its
    /// precomposed twin (seventh review, decision 17).
    /// </summary>
    public string FoldSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var folded = Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions) ? CanonicalForm.Decompose(segment) : segment;
        return Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) ? FoldForCollision(folded) : folded;
    }

    /// <summary>
    /// The text under which a key is remembered as a rejected prefix (decision 11): every
    /// segment through <see cref="FoldSegment"/>. Two spellings that would collide fold to the
    /// same text, so a condemned prefix condemns every spelling of itself.
    /// </summary>
    public string FoldKeyText(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) && !Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions))
        {
            return key.ToString();
        }

        return string.Join('/', key.Segments.Select(FoldSegment));
    }

    /// <summary>Checks one key's segments against the effective constraints and the extra validators; <see langword="null"/> when accepted.</summary>
    public string? CheckKey(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var segment in key.Segments)
        {
            var reason = CheckSegment(segment);
            if (reason is not null)
            {
                return $"segment {DebugQuote.Quote(segment)} {reason}";
            }
        }

        foreach (var validator in _extra)
        {
            var reason = validator(key);
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a listing as a tree. Every raw name goes through the base rules and
    /// <see cref="CheckKey"/>; then, under <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/>
    /// or <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, siblings at any level that fold
    /// to the same name are all rejected with their subtrees; under
    /// <see cref="BundleKeyConstraints.FileDirectoryConflict"/>, an object that is also a prefix of
    /// other keys is rejected with everything under it. A raw name listed twice is rejected.
    /// The colliding and conflicting keys are returned as <see cref="BundleKeyTreeResult.RejectedPrefixes"/>.
    /// <para>
    /// Physical presence never depends on admission. A name in
    /// <paramref name="unadmittedNames"/> that parses as a key is an object that is there: it
    /// takes part in the sibling collisions and the file/directory conflict like any listed
    /// object — <c>A.sql</c> refused for a bound still condemns <c>a.sql</c> beside it under
    /// <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/> — but it is never a
    /// candidate: never accepted, never rejected on its own. The proper ancestors (<c>a</c>,
    /// <c>a/b</c>, …) of a name that does not parse, listed or unadmitted, are directories
    /// that are there: each one that parses takes part, as a prefix only, in the same checks —
    /// an object <c>x.md</c> listed beside an unparseable <c>x.md/…</c> is condemned — and is
    /// likewise never accepted and never rejected on its own.
    /// </para>
    /// </summary>
    /// <param name="rawNames">The listing to validate.</param>
    /// <param name="unadmittedNames">Names the caller refused for a reason of its own (a bound) before the validation, but that physically exist. They are not validated themselves: one that parses counts as an object, one that does not counts through its proper ancestors, as prefixes.</param>
    public BundleKeyTreeResult ValidateTree(IEnumerable<string> rawNames, IEnumerable<string>? unadmittedNames = null)
    {
        ArgumentNullException.ThrowIfNull(rawNames);
        var rejected = new List<BundleKeyRejection>();
        var candidates = new List<(string Raw, BundleKey Key)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        // Structural presence: every name that is a key at all, recorded BEFORE CheckKey and
        // before duplicate handling. Admission (policy, extra validators) decides what is
        // accepted and diagnosed; it never erases the fact that an object is THERE, which is
        // what the collision and conflict checks reason about (sixth review: a host validator
        // refusing `x.md` must not rehabilitate `x.md/ok.sql`).
        var structural = new HashSet<BundleKey>();
        // Directories implied by a name that is no key at all, listed or unadmitted, yet is
        // physically there: prefixes only, never objects. Without them, `x.md` beside an
        // unparseable `x.md/…` read as an ordinary file and the conflict went unnoticed
        // (external review).
        var prefixOnly = new HashSet<BundleKey>();

        foreach (var raw in rawNames)
        {
            if (!seen.Add(raw))
            {
                if (duplicates.Add(raw))
                {
                    rejected.Add(new BundleKeyRejection(raw, "listed twice by the provider"));
                }

                continue;
            }

            if (!BundleKey.TryParse(raw, out var key, out var reason))
            {
                rejected.Add(new BundleKeyRejection(raw, reason));
                AddProperAncestors(raw, prefixOnly);
                continue;
            }

            structural.Add(key);
            var keyReason = CheckKey(key);
            if (keyReason is not null)
            {
                rejected.Add(new BundleKeyRejection(raw, keyReason));
                continue;
            }

            candidates.Add((raw, key));
        }

        if (unadmittedNames is not null)
        {
            foreach (var raw in unadmittedNames)
            {
                // A name the caller refused for a bound but that parses is an OBJECT that is
                // there (external review, residual): `A.sql` over the bound still collides with
                // `a.sql`. It joins `structural` only, never `candidates`: it is neither
                // accepted nor diagnosed here.
                if (BundleKey.TryParse(raw, out var unadmitted, out _))
                {
                    structural.Add(unadmitted);
                }
                else
                {
                    AddProperAncestors(raw, prefixOnly);
                }
            }
        }

        // A duplicated name is rejected, but the object it names is still THERE: its first
        // occurrence entered `structural` above, so a duplicated `x` still conflicts with
        // `x/y.sql` (r3 dropped it first and let `x/y.sql` through).
        candidates.RemoveAll(c => duplicates.Contains(c.Raw));

        var doomed = new HashSet<BundleKey>();
        var rejectedPrefixes = new HashSet<BundleKey>();
        if (Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) || Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions))
        {
            var groups = new Dictionary<(string Parent, string Folded), HashSet<string>>();
            // Prefix-only entries too: `docs` implied by an over-long `docs/…` still collides
            // with a listed `Docs/a.md`.
            foreach (var key in structural.Concat(prefixOnly))
            {
                for (var depth = 1; depth <= key.Segments.Count; depth++)
                {
                    var parent = string.Join('/', key.Segments.Take(depth - 1));
                    var name = key.Segments[depth - 1];
                    // Grouped by the SAME fold Bundle later matches rejected prefixes with
                    // (FoldKeyText): canonical decomposition and/or case, per the effective
                    // constraints. r7 grouped by case alone and let `café` stand beside the
                    // refused `cafe` + U+0301 (seventh review).
                    var g = (parent, FoldSegment(name));
                    if (!groups.TryGetValue(g, out var names))
                    {
                        names = new HashSet<string>(StringComparer.Ordinal);
                        groups[g] = names;
                    }

                    names.Add(name);
                }
            }

            var colliding = groups.Where(kv => kv.Value.Count > 1)
                .SelectMany(kv => kv.Value.Select(name => kv.Key.Parent.Length == 0 ? name : kv.Key.Parent + "/" + name))
                .Select(BundleKey.Parse)
                .ToHashSet();

            // The condemned prefixes come from the STRUCTURAL collision, not from the candidates
            // it dooms: when every offender is a duplicate there is no candidate left to doom,
            // yet the prefix is condemned all the same (fourth review).
            rejectedPrefixes.UnionWith(colliding);

            foreach (var (raw, key) in candidates)
            {
                var hit = OutermostCondemned(key, colliding);
                if (hit is not null && doomed.Add(key))
                {
                    rejected.Add(new BundleKeyRejection(raw, $"'{hit}' collides with a sibling that differs only by case or Unicode form"));
                }
            }
        }

        if (Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict))
        {
            // On ALL structural keys, case-doomed ones included: a child condemned for a case
            // collision is still physically under its parent, so the parent is still an object
            // that is also a prefix (fifth review: `x.md`, `x.md/A.sql`, `x.md/a.sql`).
            var keys = structural;
            var prefixes = new HashSet<BundleKey>();
            foreach (var key in keys)
            {
                for (var parent = key.Parent; parent is not null; parent = parent.Parent)
                {
                    prefixes.Add(parent);
                }
            }

            // A prefix-only entry is a directory: it and its ancestors are prefixes. It is never
            // an object, so it only lands in `conflicts` when an object of the same key is listed.
            foreach (var key in prefixOnly)
            {
                for (var prefix = key; prefix is not null; prefix = prefix.Parent)
                {
                    prefixes.Add(prefix);
                }
            }

            var conflicts = keys.Where(prefixes.Contains).ToHashSet();
            rejectedPrefixes.UnionWith(conflicts); // likewise: structural, not candidate-driven
            foreach (var (raw, key) in candidates)
            {
                if (doomed.Contains(key))
                {
                    continue;
                }

                var conflict = OutermostCondemned(key, conflicts);
                if (conflict is not null)
                {
                    doomed.Add(key);
                    rejected.Add(new BundleKeyRejection(raw, $"'{conflict}' is both an object and a prefix of other keys"));
                }
            }
        }

        var accepted = candidates.Where(c => !doomed.Contains(c.Key)).Select(c => c.Key).ToList();
        return new BundleKeyTreeResult(accepted, rejected, rejectedPrefixes.OrderBy(k => k).ToList());
    }

    /// <summary>
    /// The outermost prefix of <paramref name="key"/> (the key itself included) that is in
    /// <paramref name="condemned"/>, or <see langword="null"/>: one set lookup per segment, so
    /// the whole pass is linear in the listing times its depth. r7 scanned the condemned list
    /// once per candidate — quadratic when that list grows with the listing: 26 s for 64 000
    /// names in colliding sibling pairs, 13 s in file/prefix conflicts (seventh review).
    /// </summary>
    private static BundleKey? OutermostCondemned(BundleKey key, HashSet<BundleKey> condemned)
    {
        for (var depth = 1; depth <= key.Segments.Count; depth++)
        {
            var prefix = depth == key.Segments.Count ? key : BundleKey.FromSegments(key.Segments.Take(depth).ToList());
            if (condemned.Contains(prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds to <paramref name="into"/> every proper ancestor of <paramref name="raw"/>
    /// (<c>a</c>, <c>a/b</c>, … — never the name itself) that parses as a key. Every base-rule
    /// failure carries over to the longer prefixes (a forbidden character, an empty, <c>.</c>
    /// or <c>..</c> segment, a leading <c>/</c>, the 1 024-byte bound), so the walk stops at
    /// the first ancestor that does not parse.
    /// </summary>
    private static void AddProperAncestors(string raw, HashSet<BundleKey> into)
    {
        for (var slash = raw.IndexOf('/'); slash >= 0; slash = raw.IndexOf('/', slash + 1))
        {
            if (!BundleKey.TryParse(raw[..slash], out var ancestor, out _))
            {
                return;
            }

            into.Add(ancestor);
        }
    }

    /// <summary>The first surrogate code unit of <paramref name="segment"/> that is not half of a pair, or <see langword="null"/>.</summary>
    private static char? UnpairedSurrogate(string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (char.IsHighSurrogate(c) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1]))
            {
                i++; // a well-formed pair
                continue;
            }

            if (char.IsSurrogate(c))
            {
                return c;
            }
        }

        return null;
    }

    private string? CheckSegment(string segment)
    {
        // First: the later checks enumerate runes, which would read a lone surrogate as U+FFFD.
        if (Effective.HasFlag(BundleKeyConstraints.WellFormedUnicode) && UnpairedSurrogate(segment) is { } lone)
        {
            return $"contains the unpaired surrogate U+{(int)lone:X4}, which a UTF-8 store would turn into U+FFFD";
        }

        if (Effective.HasFlag(BundleKeyConstraints.TrailingDotOrSpace) && (segment[^1] == '.' || segment[^1] == ' '))
        {
            return "ends with '.' or a space";
        }

        if (Effective.HasFlag(BundleKeyConstraints.WindowsIllegalCharacters) && segment.IndexOfAny(WindowsIllegal) >= 0)
        {
            return "contains a character Windows forbids (< > : \" | ? *)";
        }

        if (Effective.HasFlag(BundleKeyConstraints.WindowsReservedNames))
        {
            var dot = segment.IndexOf('.');
            var baseName = dot < 0 ? segment : segment[..dot];
            foreach (var reserved in ReservedBases)
            {
                if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return $"has the reserved Windows device name '{reserved}' as its base";
                }
            }
        }

        if (Effective.HasFlag(BundleKeyConstraints.RequireNfc))
        {
            foreach (var rune in segment.EnumerateRunes())
            {
                switch (NfcQuickCheck.Of(rune))
                {
                    case NfcStatus.No:
                        return $"contains U+{rune.Value:X4}, which is never in Unicode NFC";
                    case NfcStatus.Maybe:
                        return $"contains the combining character U+{rune.Value:X4}, whose NFC form cannot be decided without composition tables; use the precomposed form";
                }
            }

            // Every rune is a starter or a non-composing mark here; NFC would still reorder
            // marks of decreasing class (decision 17 closed this residual).
            if (!CanonicalForm.IsCanonicallyOrdered(segment))
            {
                return "has combining marks out of canonical order, so it is not in Unicode NFC";
            }
        }

        return null;
    }
}
