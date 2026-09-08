// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>Which of the three §7 actor forms a value matches.</summary>
public enum ActorKind
{
    /// <summary><c>human:&lt;id&gt;</c> — a person (drives the human-reviewed trust tier, §5.3).</summary>
    Human,

    /// <summary><c>process:&lt;id&gt;</c> — an automated process.</summary>
    Process,

    /// <summary><c>&lt;producer&gt;/&lt;version&gt;</c> — an agent/tool, or any value not matching the two prefixes.</summary>
    Producer,
}

/// <summary>
/// A parsed §7 actor (<c>generated.by</c>, <c>verified[].by</c>, <c>sources[].author</c>).
/// Classification keys off the <c>human:</c>/<c>process:</c> prefixes; anything else is a
/// producer form. <see cref="IsWellFormed"/> is false when the value matches none of the three
/// exact forms — the validator warns on that (loading never rejects it).
/// </summary>
public readonly record struct Actor(string Raw, ActorKind Kind, string? Id, string? Producer, string? Version, bool IsWellFormed)
{
    /// <summary>True if this actor carries the <c>human:</c> prefix (regardless of well-formedness).</summary>
    public bool IsHuman => Kind == ActorKind.Human;

    /// <summary>Parses a raw actor string into its §7 form. Never throws.</summary>
    public static Actor Parse(string raw)
    {
        if (raw.StartsWith("human:", StringComparison.Ordinal))
        {
            var id = raw["human:".Length..];
            return new Actor(raw, ActorKind.Human, id.Length == 0 ? null : id, null, null, id.Length > 0);
        }

        if (raw.StartsWith("process:", StringComparison.Ordinal))
        {
            var id = raw["process:".Length..];
            return new Actor(raw, ActorKind.Process, id.Length == 0 ? null : id, null, null, id.Length > 0);
        }

        var slash = raw.IndexOf('/');
        if (slash > 0 && slash < raw.Length - 1)
        {
            return new Actor(raw, ActorKind.Producer, null, raw[..slash], raw[(slash + 1)..], true);
        }

        return new Actor(raw, ActorKind.Producer, null, null, null, false);
    }
}
