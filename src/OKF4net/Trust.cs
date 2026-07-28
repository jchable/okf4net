// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>One <c>{ by, at }</c> generation or verification stamp (§5.2). <see cref="By"/> is null when the mapping omitted the (required) <c>by</c> key.</summary>
public readonly record struct Stamp(Actor? By, string? At);

/// <summary>The trust tier derived from a concept's <c>verified</c> list (§5.3), lowest to highest.</summary>
public enum TrustTier
{
    /// <summary>No <c>verified</c> entries.</summary>
    Unverified,

    /// <summary>Verified only by non-<c>human:</c> actors.</summary>
    MachineConfirmed,

    /// <summary>Verified by at least one <c>human:&lt;id&gt;</c> actor.</summary>
    HumanReviewed,
}

/// <summary>Parsing and tier derivation for the §5.2/§5.3 trust fields. All helpers are lenient and never throw.</summary>
public static class Trust
{
    /// <summary>Parses the <c>generated</c> mapping into a single <see cref="Stamp"/>; null if absent or not a mapping.</summary>
    public static Stamp? ParseGenerated(YamlValue? value)
        => value is YamlMapping m ? StampFrom(m) : null;

    /// <summary>Parses <c>verified</c>: a bare <c>{by,at}</c> mapping normalizes to a one-element list (§5.2); a sequence reads each mapping entry; anything else is empty.</summary>
    public static IReadOnlyList<Stamp> ParseVerified(YamlValue? value) => value switch
    {
        YamlMapping m => [StampFrom(m)],
        YamlSequence seq => seq.Items.OfType<YamlMapping>().Select(StampFrom).ToList(),
        _ => [],
    };

    /// <summary>Derives the trust tier (§5.3): human actor ⇒ human-reviewed; else any verifier ⇒ machine-confirmed; empty ⇒ unverified.</summary>
    public static TrustTier DeriveTier(IReadOnlyList<Stamp> verified)
    {
        if (verified.Count == 0)
        {
            return TrustTier.Unverified;
        }

        return verified.Any(s => s.By is { IsHuman: true }) ? TrustTier.HumanReviewed : TrustTier.MachineConfirmed;
    }

    private static Stamp StampFrom(YamlMapping m)
    {
        var by = m.Get("by")?.AsDisplayString();
        var at = m.Get("at")?.AsDisplayString();
        return new Stamp(by is null ? null : Actor.Parse(by), at);
    }
}
