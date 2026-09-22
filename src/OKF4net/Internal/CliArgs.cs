// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// One command's arguments, scanned once so every later question agrees on
/// what each token is. Shared by both CLI binaries in this repo — <c>okf</c>
/// (multi-verb, one call per verb with that verb's own declared flag lists)
/// and <c>okf-render</c> (single command) — which used to carry two
/// independently hand-rolled copies of this exact scan until they drifted:
/// <c>okf</c> treated a lone <c>-</c> as a positional while <c>okf-render</c>
/// rejected it as <c>unknown option: -</c>, and a repeated valued flag was an
/// error on one side and first-wins on the other. One scanner now backs both.
///
/// Scanning left to right, a flag listed in <c>valuedFlags</c> (see
/// <see cref="Scan"/>) consumes the following token as its value; every
/// other <c>-</c>-prefixed token is a valueless flag; anything else is
/// positional. A <c>--</c> separator ends the scan: everything after it is
/// positional, never a flag (so a path beginning with <c>-</c> works).
///
/// Scanning once is the point. When presence, value and positional were three
/// independent scans of the raw array, they disagreed: a token consumed as a
/// value was still seen as a flag by the presence check, so
/// <c>okf audit b --type --stale</c> set the stale filter even though
/// <c>--stale</c> was <c>--type</c>'s value, and only the positional scan
/// honoured <c>--</c>.
/// </summary>
internal sealed class CliArgs
{
    /// <summary>
    /// Every flag given, mapped to the value it consumed: <c>null</c> both
    /// for a valueless flag and for a valued one left without a value. Key
    /// absent means the flag was not given — one dictionary rather than a
    /// presence set beside a value map, so presence and value cannot drift
    /// apart.
    /// </summary>
    private readonly Dictionary<string, string?> _flags = new(StringComparer.Ordinal);

    /// <summary>
    /// The positional tokens, in order. `--` ends option parsing without
    /// discarding what came before it, so a caller taking several
    /// positionals (<c>okf verify &lt;bundle&gt; &lt;id&gt;…</c>) keeps them
    /// all.
    /// </summary>
    private readonly List<string> _positionals = [];

    /// <summary>The flags this scan was told consume a value, kept so <see cref="Value"/> can tell a user's mistake from the caller's.</summary>
    private string[] _valuedFlags = [];

    /// <summary>The flags this scan was told stand alone (excluding the help flags, checked separately).</summary>
    private string[] _valuelessFlags = [];

    /// <summary>The flags treated as "asking for help" regardless of the caller's own valueless-flag list — see <see cref="WantsHelp"/>.</summary>
    private string[] _helpFlags = [];

    /// <summary>Whether the caller declared it takes more than one positional.</summary>
    private bool _variadic;

    private CliArgs()
    {
    }

    /// <summary>
    /// Scans <paramref name="args"/> against the caller's declared contract,
    /// rejecting anything it does not define.
    ///
    /// An option in neither <paramref name="valuedFlags"/> nor
    /// <paramref name="valuelessFlags"/> nor <paramref name="helpFlags"/> is
    /// <c>unknown option</c> rather than a silently kept valueless flag, and a
    /// second positional (when <paramref name="variadic"/> is <c>false</c>)
    /// is <c>unexpected argument</c> rather than silently dropped.
    ///
    /// That second rule applies after <c>--</c> too, which narrows what a
    /// naive separator might do: it does NOT let the first token after the
    /// separator override an earlier positional and swallow the rest — a
    /// side-effecting flag parked after the separator still never fires, and
    /// the leftover token after it is named instead of discarded. The
    /// separator's actual contract — nothing after it is ever a flag — holds
    /// regardless.
    /// </summary>
    /// <param name="args">The command-line arguments to scan (excluding the program name and, for <c>okf</c>, the verb).</param>
    /// <param name="valuedFlags">Flags that consume the following token as their value.</param>
    /// <param name="valuelessFlags">Flags that stand alone. A flag in neither this list nor <paramref name="valuedFlags"/> nor <paramref name="helpFlags"/> is rejected as unknown.</param>
    /// <param name="variadic">Whether more than one positional is accepted; when <c>false</c>, a second positional is rejected as an unexpected argument.</param>
    /// <param name="helpFlags">Flags accepted regardless of the caller's own lists, surfaced through <see cref="WantsHelp"/>.</param>
    internal static CliArgs Scan(string[] args, string[] valuedFlags, string[] valuelessFlags, bool variadic, string[] helpFlags)
    {
        var scanned = new CliArgs
        {
            _valuedFlags = valuedFlags,
            _valuelessFlags = valuelessFlags,
            _helpFlags = helpFlags,
            _variadic = variadic,
        };

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];

            if (token == "--")
            {
                // Nothing past the separator is a flag -- that is what it is
                // for (a path starting with `-`). They are positionals, and
                // so bound by the declared arity like any other.
                for (var j = i + 1; j < args.Length; j++)
                {
                    scanned.TakePositional(args[j]);
                }

                break;
            }

            if (Array.IndexOf(valuedFlags, token) >= 0)
            {
                i = scanned.TakeValuedFlag(args, i, token);
                continue;
            }

            // A lone "-" is POSIX's "read from standard input" — an
            // argument, not an option. Only a token with something after
            // the dash is a flag.
            if (token.Length > 1 && token.StartsWith('-'))
            {
                scanned.TakeOption(token);
                continue;
            }

            scanned.TakePositional(token);
        }

        return scanned;
    }

    /// <summary>
    /// Records a flag that consumes the following token as its value, and
    /// returns the index the scan continues from — one past the value when
    /// there was one, otherwise unchanged.
    /// </summary>
    /// <param name="args">The full argument array being scanned.</param>
    /// <param name="i">The index of <paramref name="token"/> itself.</param>
    /// <param name="token">The valued flag.</param>
    private int TakeValuedFlag(string[] args, int i, string token)
    {
        var hasValue = CliArgScanning.HasFollowingValue(args, i);

        // Refused, not first-wins: a script that appends an override flag
        // got the EARLIER value with no diagnostic, the exact "silently
        // different behaviour than asked for" this scanner exists to stop.
        if (_flags.ContainsKey(token))
        {
            throw new CliArgumentException($"option {token} given more than once");
        }

        _flags[token] = hasValue ? args[i + 1] : null;

        return hasValue ? i + 1 : i;
    }

    /// <summary>
    /// Records a valueless flag, rejecting one the caller does not declare.
    /// The allowlist is the caller's own valueless-flag list plus the help
    /// flags it passed to <see cref="Scan"/>, so a flag another caller
    /// defines (e.g. one of <c>okf</c>'s other verbs) is still unknown here.
    /// </summary>
    /// <param name="token">The <c>-</c>-prefixed token.</param>
    private void TakeOption(string token)
    {
        if (Array.IndexOf(_valuelessFlags, token) < 0 && Array.IndexOf(_helpFlags, token) < 0)
        {
            throw new CliArgumentException($"unknown option: {token}");
        }

        _flags[token] = null;
    }

    /// <summary>
    /// Appends a positional, enforcing the declared arity. A non-variadic
    /// caller still reports the surplus token rather than dropping it; a
    /// variadic one keeps them all in order.
    /// </summary>
    private void TakePositional(string token)
    {
        if (!_variadic && _positionals.Count > 0)
        {
            throw new CliArgumentException($"unexpected argument: {token}");
        }

        _positionals.Add(token);
    }

    /// <summary>Whether one of the <c>helpFlags</c> passed to <see cref="Scan"/> was given.</summary>
    internal bool WantsHelp => _helpFlags.Any(Has);

    /// <summary>True if <paramref name="flag"/> was given as a flag — not as another flag's value, and not after <c>--</c>.</summary>
    internal bool Has(string flag) => _flags.ContainsKey(flag);

    /// <summary>
    /// The value <paramref name="flag"/> consumed, or <c>null</c> when the
    /// flag is absent. Throws when the flag is present but unvalued.
    /// </summary>
    internal string? Value(string flag)
    {
        if (!_flags.TryGetValue(flag, out var value))
        {
            return null;
        }

        if (value is not null)
        {
            return value;
        }

        // Present with nothing attached. That is a user mistake only if the
        // flag was declared as taking a value; otherwise the caller asked a
        // question about a flag the scan was never told to value, and
        // reporting "requires a value" would blame the user for a bug here.
        if (Array.IndexOf(_valuedFlags, flag) < 0)
        {
            throw new InvalidOperationException(
                $"{flag} was not declared as a valued flag in this command's CliArgs.Scan call");
        }

        throw new CliArgumentException($"{flag} requires a value");
    }

    /// <summary>The first positional argument, or throws naming <paramref name="what"/>.</summary>
    internal string Positional(string what) =>
        _positionals.Count > 0 ? _positionals[0] : throw new CliArgumentException($"missing {what}");

    /// <summary>Every positional argument, in order — the first is what <see cref="Positional"/> returns.</summary>
    internal IReadOnlyList<string> Positionals => _positionals;
}
