# Bundle provider, lot A: contract, keys and the filesystem provider (read side) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the `IBundleProvider` seam under `Bundle` and load a bundle through it, with the filesystem as the first — read-only, in this lot — provider, so that every later lot (writer, handles/observations, catalog) builds on a contract that already carries a bundle end to end with the golden suite untouched.

**Architecture:** New types live under `src/OKF4net/Providers/` (keys and their three-layer validation, the contract and its value types, the filesystem provider, the §6.2 resolver on keys). `Bundle.Load(IBundleProvider, BundleLoadOptions?)` becomes the real loader; `Bundle.Load(string)` is a thin overload over `FileSystemBundleProvider`. Every existing public member is **kept** in this lot (`Concept.Path`, `Bundle.Root`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)`): new members are added beside them (`Concept.Key`, `Bundle.Provider`/`Location`/`Version`/`ParseProblems`, `TryResolveResourceKey`, `ReadResourceText(BundleKey)`), and the historical ones are computed from the new ones by the display rule, so the CLI's bytes do not move. The breaking removals are lot D.

**Tech Stack:** C# 14 / .NET 10, BCL only in `src/OKF4net` (zero `PackageReference`), xunit + `Xunit.SkippableFact` in `tests/OKF4net.Tests`.

**Spec:** `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md` (revision 6, commit `39ea06c`). Sections implemented here: §1 (contract, read side), §2 (keys, three layers, tree validation, §6.2 resolution, enumeration scope), §3 (the provider-level guard only), §6 (bounds), §7 (no URL dereference; errors translated at the provider boundary), §8 (filesystem provider, display rule, confinement boundary, weak `GetVersion`), §9 rows `Bundle`, `ConceptId`, `BundleValidator`, §10 behaviour changes that this lot introduces, §11 items that this lot can verify.

## Decomposition of the spec into lots

The spec is one design but four sequential deliveries; each later lot assumes the previous one is merged into `dev`. Only lot A is planned here.

| Lot | Scope | Spec sections | Status |
|---|---|---|---|
| **A** (this plan) | `BundleKey` + three-layer validation; contract types; read-only `FileSystemBundleProvider`; `Bundle.Load(provider)`; §6.2 on keys; bounds; validator through the provider; display rule + Windows test | §1, §2, §3 (guard), §6, §7 (partial), §8, §9 (`Bundle`, `ConceptId`, `BundleValidator`), §10/§11 (its part) | planned |
| **B** | Write side: provider `Write`/`Delete` (`CanWrite` flips to true), SHA-256 object versions on write, `File.Replace`/`File.Move` atomic commit **starting from issue #157's recorded pitfalls** (`ReplaceFile` 1176, temp permissions before content, Unix `File.Move` race, durability statement, orphaned temporaries), `BundleConceptWriter`/`IndexGenerator` on the provider, optional expected version, retries, `log.md` through the writer, `BundleWriteReport`, tree check before creation, empty directories (D2) | §3 (rest), §4, §9 (`BundleConceptWriter`, `IndexGenerator`) | next |
| **C** | `BundleHandle`, refresh policies, reload obligation, observations registry and purposes (D1, D5), `okf_run_computation` capture and pinning, executable-resource survey states, D6, `OkfBundleTools`/`OkfContextProvider` on handles | §5, §9 (agents, attestation) | after B |
| **D** | Catalog (snapshot owns handles, provider factories, `MemoryDeleteResult` replaced), Viewer, `okf-mcp` discovery bound, `BundleRoot` obsolete, breaking removals (`Concept.Path`, `Bundle.Root`, `Diagnostic.Path`, `AuditFinding.Path`, …), spec-conformance entry for D6, CHANGELOG consolidation, README/CLAUDE.md (#143) | §9 (catalog, viewer, CLI/MCP), §10 (breaking list) | after C |

Lot A is additive on the public API and changes behaviour only where §10 of the spec already announces it: finite default bounds, `Strict` key policy (tree included), exact-case matching, `\` in a §6.2 path → `Unsafe`. It does **not** touch the write path: `BundleConceptWriter` and `IndexGenerator` keep their direct `File.*` code until lot B.

## Revision history

- **r1 (2026-10-12)** — first version, written from spec revision 6.

## Global Constraints

- **Branch and worktree.** Execute in a dedicated worktree on branch `feat/bundle-provider-a`, created from `origin/dev` **after** the spec + plan docs PR (`docs/bundle-provider-spec`) is merged into `dev`, or rebased on it. Never work in `E:\Sources\okf` (shared checkout; `dev` is checked out there). To start from `dev`: `git worktree add .claude/worktrees/bundle-provider-a -b feat/bundle-provider-a origin/dev`. Never `git checkout dev`.
- **Zero third-party runtime dependencies in `src/OKF4net`** — BCL only. No P/Invoke. No new `PackageReference` anywhere in `src/`.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc on every public member (the build treats missing docs as errors), nullable on, `TreatWarningsAsErrors`, LangVersion 14.
- **Source files are CRLF in the working tree.** `Write` produces LF; after every `Write`/`Edit` of a `.cs` file, normalise with `unix2dos` or `sed -i 's/$/\r/'` on a **fresh LF file only** — never on a file that already has CR (it would double them). Before each commit: `tr -cd '\r' < f | wc -c` equals `wc -l < f` for every touched `.cs` file. Markdown files (`CHANGELOG.md`, plans, specs) are LF.
- **`tests/fixtures/` is never edited and no snapshot is regenerated.** If a golden comparison turns red, STOP and report: lot A promises byte-identical CLI output.
- **Path-safety guards use the STRICT predicates** (`ReparsePoints.IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor`); enumeration walks use the lenient pair, and this lot adds exactly **one** lenient site (the provider's walk), which replaces `Bundle.CollectMarkdown` — update the count in `CLAUDE.md` in Task 8.
- **Keys are never percent-decoded** and a URL in a §6.2 field is **never** dereferenced.
- **Bounds, policy and constraints are enforced in the core** (`Bundle.Load`), reading provider streams with byte counting; the provider enforces the same bounds on its own walks (`GetVersion`, `Enumerate`).
- **NFC under invariant globalization fails closed.** `okf` and `okf-render` are published with `InvariantGlobalization`, under which `string.Normalize(NormalizationForm.FormC)` is a silent no-op (`"e\u0301".Normalize(FormC).Length == 2`, `IsNormalized` returns `true` — measured 2026-10-12 on .NET 10). The validator therefore detects whether normalization works once (`BundleKeyValidator.NormalizationAvailable`) and, when it does not, **rejects every non-ASCII segment under a constraint set that requires NFC**, with a reason that says why. It never pretends to have normalised. (Author's decision while writing this plan — flagged for the maintainer; see "Decisions taken by this plan".)
- `Bundle.Load` stays permissive (§11): a per-resource or per-concept problem goes to `ParseProblems`; only the global failures of §6 (entry bound, depth, provider I/O error) throw `BundleLoadException`.
- Each task adds its own `CHANGELOG.md` entry under `## [Unreleased]` (`### Added` for new API, `### Changed` for behaviour changes, prefixed **Breaking (0.x)** only when something public is removed or changes shape — nothing in this lot is), describing behaviour, not commits.
- Commit messages say only what the code actually does. End each with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Never `git add -A`. Do not push.
- Run tests with `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` unless a task says otherwise; `ContainerIntegration` tests need Docker and are out of CI by decision.

## Decisions taken by this plan (not in the spec — for the maintainer to confirm)

1. **Read-time version mismatch has its own exception.** The spec names `BundleWriteConflictException` for a failed write condition; a `Read(key, expected)` whose `expected` no longer matches is not a write. This plan adds `BundleVersionMismatchException : OkfException` for reads and keeps `BundleWriteConflictException` for writes (lot B). Both derive from a common `BundleProviderException : OkfException`.
2. **NFC fails closed under invariant globalization** (see Global Constraints).
3. **The filesystem provider is read-only in this lot** (`CanWrite = false`, `Write`/`Delete` throw `BundleReadOnlyException`). This is a declared capability per §3, not a placeholder: lot B flips it with the atomic commit protocol from #157.
4. **`Concept.Path` is computed by the display rule** (`Location` + OS separator + key segments) rather than kept from the walk. For the filesystem provider the two are byte-identical (`Path.Combine(root, …)` is exactly that rule); Task 7's Windows test pins it.
5. **`ParseErrors` stays `IReadOnlyList<(string Path, string Error)>`** and is projected from the new `ParseProblems` (`IReadOnlyList<BundleParseError>`); a rejected raw name (no key) projects its `Path` as `Location` + separator + the raw name passed through `DebugQuote.Quote`, since no existing output can contain one (no fixture has an invalid name).
6. **Layer-1 control-character range** is U+0000–U+001F and U+007F–U+009F, checked per UTF-16 code unit (no surrogate in that range, so this is exact).
7. **`BundleLimits.MaxDepth` counts key segments** (a key `a/b/c.md` has depth 3); the directory depth of the spec (64) is the same number for the filesystem.

## Review Focus

Inputs the spec implies but no task's tests exercise directly, each pinned to the owning task:

1. **A `.md` whose stem is a reserved name only once lower-cased** (`Con.md`, `lPt¹.md`): Windows reserves them case-insensitively — Task 2 tests `Con.md` and `lpt¹.md`.
2. **A directory named `index.md` or `log.md`** (reserved filename used as a directory): today `CollectMarkdown` descends into it; through keys it is a prefix, and a file under it is a concept whose id contains `index.md` as a segment, which `ConceptId.ValidateSegment` accepts — Task 6 pins that the loader classifies by **last segment of a file key only** and that `tables/index.md/x.md` becomes a concept `tables/index.md/x`, as today.
3. **A key exactly at 1 024 UTF-8 bytes vs 1 025** — Task 1 tests both edges.
4. **A root that is itself a symlink** (the bundle root is a reparse point): today `Bundle.Load` follows it (it only checks `Directory.Exists`). The provider must keep that — Task 4 pins that a symlinked root loads, while a symlinked **entry** is skipped, and a boundary that is given makes the root's own reparse status STRICT.
5. **A provider that enumerates the same raw name twice** (a buggy backend): the tree validator must not crash and must report it — Task 2 tests duplicate raw names.

---

### Task 1: `BundleKey` — layer-1 (base) validation

**Files:**
- Create: `src/OKF4net/Providers/BundleKey.cs`
- Test: `tests/OKF4net.Tests/Providers/BundleKeyTests.cs`

**Interfaces:**
- Consumes: `OKF4net.Internal.LineSafeText.ContainsControlCharacter(string)` (exists; checks U+0000–U+001F and U+007F–U+009F) and `OKF4net.Internal.MarkdownPaths.HasMarkdownExtension(string)`.
- Produces: `public sealed class BundleKey : IEquatable<BundleKey>, IComparable<BundleKey>` with `IReadOnlyList<string> Segments`, `string Name`, `BundleKey? Parent`, `bool IsMarkdown`, `string ToString()` (segments joined by `/`), `static bool TryParse(string raw, out BundleKey? key, out string? reason)`, `static BundleKey Parse(string raw)` (throws `BundleKeyException`), `BundleKey Append(string segment)`, `bool StartsWith(BundleKey prefix)` (segment-bounded), `static BundleKey FromSegments(IReadOnlyList<string>)`, `const int MaxUtf8Bytes = 1024`. Also `public sealed class BundleKeyException : OkfException`. Ordinal, case-sensitive equality and ordering.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleKeyTests
{
    [Theory]
    [InlineData("a.md", new[] { "a.md" })]
    [InlineData("tables/orders.md", new[] { "tables", "orders.md" })]
    [InlineData("a/b/c", new[] { "a", "b", "c" })]
    [InlineData("%2e%2e/x.md", new[] { "%2e%2e", "x.md" })] // never percent-decoded
    [InlineData("tables/index.md/x.md", new[] { "tables", "index.md", "x.md" })]
    public void Parses_valid_keys_into_segments(string raw, string[] segments)
    {
        Assert.True(BundleKey.TryParse(raw, out var key, out var reason), reason);
        Assert.Equal(segments, key!.Segments);
        Assert.Equal(raw, key.ToString());
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/a.md", "leading")]
    [InlineData("a.md/", "trailing")]
    [InlineData("a//b.md", "empty segment")]
    [InlineData("./a.md", "'.' segment")]
    [InlineData("a/../b.md", "'..' segment")]
    [InlineData("a\\b.md", "backslash")]
    [InlineData("a\0.md", "control")]
    [InlineData("a\u001f.md", "control")]
    [InlineData("a\u007f.md", "control")]
    [InlineData("a\u0085.md", "control")]
    public void Rejects_base_rule_violations(string raw, string reasonFragment)
    {
        Assert.False(BundleKey.TryParse(raw, out var key, out var reason));
        Assert.Null(key);
        Assert.Contains(reasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Length_bound_is_1024_utf8_bytes_inclusive()
    {
        var ascii1024 = new string('a', 1021) + ".md"; // 1024 bytes
        Assert.True(BundleKey.TryParse(ascii1024, out _, out _));
        var ascii1025 = new string('a', 1022) + ".md";
        Assert.False(BundleKey.TryParse(ascii1025, out _, out var reason));
        Assert.Contains("1024", reason);
        // 'é' is 2 bytes in UTF-8: 512 of them = 1024 bytes, 513 = 1026.
        Assert.True(BundleKey.TryParse(new string('é', 512), out _, out _));
        Assert.False(BundleKey.TryParse(new string('é', 513), out _, out _));
    }

    [Fact]
    public void Equality_and_ordering_are_ordinal_and_case_sensitive()
    {
        var a = BundleKey.Parse("A/x.md");
        var b = BundleKey.Parse("a/x.md");
        Assert.NotEqual(a, b);
        Assert.True(a.CompareTo(b) < 0); // 'A' (0x41) < 'a' (0x61)
        Assert.Equal(BundleKey.Parse("a/x.md"), b);
        Assert.Equal(BundleKey.Parse("a/x.md").GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Name_parent_markdown_and_prefix_are_segment_bounded()
    {
        var key = BundleKey.Parse("tables/orders.md");
        Assert.Equal("orders.md", key.Name);
        Assert.Equal(BundleKey.Parse("tables"), key.Parent);
        Assert.Null(BundleKey.Parse("a.md").Parent);
        Assert.True(key.IsMarkdown);
        Assert.False(BundleKey.Parse("tables/orders.MD").IsMarkdown); // ordinal, like MarkdownPaths
        Assert.True(key.StartsWith(BundleKey.Parse("tables")));
        Assert.False(BundleKey.Parse("tablesx/orders.md").StartsWith(BundleKey.Parse("tables")));
        Assert.Equal(BundleKey.Parse("tables/orders.md/extra"), key.Append("extra"));
        Assert.Throws<BundleKeyException>(() => key.Append("a/b"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleKeyTests"`
Expected: build error — `OKF4net.Providers.BundleKey` does not exist.

- [ ] **Step 3: Implement `BundleKey`**

```csharp
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
/// constraints and the portability policy live in <see cref="BundleKeyValidator"/>.
/// </summary>
public sealed class BundleKey : IEquatable<BundleKey>, IComparable<BundleKey>
{
    /// <summary>Upper bound on a key's UTF-8 length, inclusive (spec §2, §6).</summary>
    public const int MaxUtf8Bytes = 1024;

    private readonly string _text;

    private BundleKey(List<string> segments)
    {
        Segments = new ReadOnlyCollection<string>(segments);
        _text = string.Join('/', segments);
    }

    /// <summary>The key's segments, in order; never empty.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The last segment.</summary>
    public string Name => Segments[^1];

    /// <summary>The key without its last segment, or <see langword="null"/> for a top-level key.</summary>
    public BundleKey? Parent => Segments.Count <= 1 ? null : new BundleKey(Segments.Take(Segments.Count - 1).ToList());

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
    /// character (U+0000–U+001F, U+007F–U+009F), at most <see cref="MaxUtf8Bytes"/> bytes
    /// in UTF-8. The text is taken literally: <c>%2e%2e</c> is a name, not <c>..</c>.
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

        if (LineSafeText.ContainsControlCharacter(raw))
        {
            reason = "key contains a control character";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(raw) > MaxUtf8Bytes)
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

        key = new BundleKey(segments.ToList());
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
```

`LineSafeText.ContainsControlCharacter` is `internal` in `OKF4net.Internal`: same assembly, no change needed. Create `src/OKF4net/Providers/` and `tests/OKF4net.Tests/Providers/`; convert both new files to CRLF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleKeyTests"`
Expected: 5 test methods, 20 cases, all green.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/BundleKey.cs tests/OKF4net.Tests/Providers/BundleKeyTests.cs
git commit -F - <<'MSG'
feat(providers): BundleKey with the base (layer-1) key rules

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 2: `BundleKeyConstraints`, `BundleKeyPolicy` and `BundleKeyValidator` — layers 2 and 3, tree validation

**Files:**
- Create: `src/OKF4net/Providers/BundleKeyConstraints.cs`
- Create: `src/OKF4net/Providers/BundleKeyValidator.cs`
- Test: `tests/OKF4net.Tests/Providers/BundleKeyValidatorTests.cs`

**Interfaces:**
- Consumes: `BundleKey` (Task 1); `OKF4net.Internal.UnicodeCaseFold.ToLowercase(string)` (exists; simple case folding without culture).
- Produces:
  - `[Flags] public enum BundleKeyConstraints` with `None`, `TrailingDotOrSpace`, `WindowsIllegalCharacters`, `WindowsReservedNames`, `CaseInsensitiveCollisions`, `RequireNfc`, `UnicodeFormCollisions`, `FileDirectoryConflict`; static helpers on `BundleKeyConstraintSets`: `Windows`, `MacOS`, `Linux`, `Strict` (= union of all), `ForCurrentOperatingSystem()`.
  - `public enum BundleKeyPolicy { Strict, Native }`.
  - `public sealed record BundleKeyRejection(string RawName, string Reason)`.
  - `public sealed class BundleKeyValidator` with ctor `(BundleKeyConstraints native, BundleKeyPolicy policy, IReadOnlyList<Func<BundleKey, string?>>? extraValidators = null)`, property `BundleKeyConstraints Effective` (`Strict` → `BundleKeyConstraintSets.Strict | native`; `Native` → `native`), `static bool NormalizationAvailable`, `string? CheckKey(BundleKey key)` (segment rules of the effective constraints; `null` = accepted), and `BundleKeyTreeResult ValidateTree(IEnumerable<string> rawNames)`.
  - `public sealed record BundleKeyTreeResult(IReadOnlyList<BundleKey> Accepted, IReadOnlyList<BundleKeyRejection> Rejected)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleKeyValidatorTests
{
    private static BundleKeyValidator Strict() => new(BundleKeyConstraints.None, BundleKeyPolicy.Strict);
    private static BundleKeyValidator NativeLinux() => new(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native);

    [Fact]
    public void Strict_is_the_union_of_every_known_native_set()
    {
        var v = Strict();
        Assert.Equal(BundleKeyConstraintSets.Strict, v.Effective);
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.WindowsReservedNames));
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.RequireNfc));
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict));
    }

    [Fact]
    public void Native_never_drops_below_the_providers_own_constraints()
    {
        var v = new BundleKeyValidator(BundleKeyConstraintSets.Windows, BundleKeyPolicy.Native);
        Assert.Equal(BundleKeyConstraintSets.Windows, v.Effective);
        Assert.NotNull(v.CheckKey(BundleKey.Parse("con.md")));
    }

    [Theory]
    [InlineData("a./b.md")]      // trailing dot in a directory segment
    [InlineData("a /b.md")]      // trailing space
    [InlineData("con.md")]
    [InlineData("Con.md")]       // reserved names are case-insensitive
    [InlineData("COM1.md")]
    [InlineData("com¹.md")] // COM¹
    [InlineData("lpt³/x.md")]
    [InlineData("nul.txt.md")]   // reserved base before the first '.'
    [InlineData("a:b.md")]
    [InlineData("a|b.md")]
    [InlineData("a\"b.md")]
    public void Strict_rejects_windows_aliases_and_reserved_names(string raw)
    {
        var key = BundleKey.Parse(raw);
        Assert.NotNull(Strict().CheckKey(key));
        Assert.Null(NativeLinux().CheckKey(key)); // legal on a POSIX filesystem
    }

    [Theory]
    [InlineData("tables/orders.md")]
    [InlineData("a.b.c.md")]
    [InlineData("console.md")]   // 'console' is not 'con'
    [InlineData("foo..md")]      // a trailing dot in the STEM is not an alias; only a whole segment ending with '.' is (a./b.md above)
    [InlineData("com10.md")]     // only COM1..COM9 and the superscripts
    [InlineData("_con.md")]
    public void Strict_accepts_ordinary_names(string raw)
    {
        Assert.Null(Strict().CheckKey(BundleKey.Parse(raw)));
    }

    [Fact]
    public void Nfc_is_required_under_strict_and_fails_closed_without_normalization()
    {
        var nfd = BundleKey.Parse("café.md"); // e + combining acute
        var nfc = BundleKey.Parse("café.md");
        var v = Strict();
        if (BundleKeyValidator.NormalizationAvailable)
        {
            Assert.NotNull(v.CheckKey(nfd));
            Assert.Null(v.CheckKey(nfc));
        }
        else
        {
            // Invariant globalization: Normalize is a silent no-op, so any non-ASCII
            // segment is refused rather than silently accepted as "NFC".
            Assert.Contains("normalization", v.CheckKey(nfd), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("normalization", v.CheckKey(nfc), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(NativeLinux().CheckKey(nfd));
    }

    [Fact]
    public void Tree_rejects_per_level_case_collisions_with_their_subtrees()
    {
        var r = Strict().ValidateTree(["A/x.md", "a/y.md", "b/z.md"]);
        Assert.Equal([BundleKey.Parse("b/z.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("collides", rej.Reason));
        Assert.Contains(r.Rejected, rej => rej.RawName == "A/x.md");
        Assert.Contains(r.Rejected, rej => rej.RawName == "a/y.md");
    }

    [Fact]
    public void Tree_rejects_file_directory_conflicts_in_both_directions()
    {
        var r = Strict().ValidateTree(["a.md", "a.md/b.md", "ok.md"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("both an object and a prefix", rej.Reason));
    }

    [Fact]
    public void Tree_keeps_distinct_case_on_a_native_case_sensitive_set()
    {
        var r = NativeLinux().ValidateTree(["A/x.md", "a/y.md"]);
        Assert.Equal(2, r.Accepted.Count);
        Assert.Empty(r.Rejected);
    }

    [Fact]
    public void Tree_reports_base_rule_failures_and_duplicates_with_the_raw_name()
    {
        var r = Strict().ValidateTree(["bad\\name.md", "dup.md", "dup.md", "fine.md"]);
        Assert.Equal([BundleKey.Parse("fine.md")], r.Accepted);
        Assert.Contains(r.Rejected, rej => rej.RawName == "bad\\name.md" && rej.Reason.Contains("backslash"));
        Assert.Contains(r.Rejected, rej => rej.RawName == "dup.md" && rej.Reason.Contains("twice"));
    }

    [Fact]
    public void Extra_validators_only_restrict()
    {
        var v = new BundleKeyValidator(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native,
            [k => k.Segments.Any(s => s.Any(char.IsUpper)) ? "upper-case segment" : null]);
        Assert.NotNull(v.CheckKey(BundleKey.Parse("Tables/x.md")));
        Assert.Null(v.CheckKey(BundleKey.Parse("tables/x.md")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleKeyValidatorTests"`
Expected: build error — `BundleKeyValidator`, `BundleKeyConstraints` do not exist.

- [ ] **Step 3: Implement the constraints**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// Storage-specific aliasing rules a provider declares (spec §2, layer 2). Each flag
/// names a way two distinct keys could designate the same object on that storage.
/// </summary>
[Flags]
public enum BundleKeyConstraints
{
    /// <summary>No constraint beyond the base rules.</summary>
    None = 0,

    /// <summary>A segment must not end with <c>.</c> or a space (Windows strips them).</summary>
    TrailingDotOrSpace = 1 << 0,

    /// <summary>A segment must not contain <c>&lt; &gt; : " | ? *</c> (Windows; <c>:</c> also opens an NTFS stream).</summary>
    WindowsIllegalCharacters = 1 << 1,

    /// <summary>A segment's base (before the first <c>.</c>) must not be a reserved Windows device name.</summary>
    WindowsReservedNames = 1 << 2,

    /// <summary>Two siblings must not be equal once case-folded.</summary>
    CaseInsensitiveCollisions = 1 << 3,

    /// <summary>Every segment must already be in Unicode NFC.</summary>
    RequireNfc = 1 << 4,

    /// <summary>Two siblings must not be equal once normalised to NFC (macOS folds forms).</summary>
    UnicodeFormCollisions = 1 << 5,

    /// <summary>An object key must not also be a prefix of other keys (a file cannot be a directory).</summary>
    FileDirectoryConflict = 1 << 6,
}

/// <summary>The constraint sets of the storages this library knows (spec §2, table of native constraints).</summary>
public static class BundleKeyConstraintSets
{
    /// <summary>NTFS/FAT as Windows exposes them.</summary>
    public static BundleKeyConstraints Windows =>
        BundleKeyConstraints.TrailingDotOrSpace
        | BundleKeyConstraints.WindowsIllegalCharacters
        | BundleKeyConstraints.WindowsReservedNames
        | BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>APFS/HFS+ as macOS exposes them by default.</summary>
    public static BundleKeyConstraints MacOS =>
        BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.RequireNfc
        | BundleKeyConstraints.UnicodeFormCollisions
        | BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>A POSIX filesystem: only the file/directory conflict beyond the base rules.</summary>
    public static BundleKeyConstraints Linux => BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>The union of every known set: a key valid here is valid on every provider (<see cref="BundleKeyPolicy.Strict"/>).</summary>
    public static BundleKeyConstraints Strict => Windows | MacOS | Linux;

    /// <summary>The set for the operating system this process runs on. The real boundary is the volume, not the OS (spec §2); hosts on mixed volumes keep <see cref="BundleKeyPolicy.Strict"/>.</summary>
    public static BundleKeyConstraints ForCurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsMacOS() ? MacOS
        : Linux;
}

/// <summary>Which constraints apply on top of a provider's own (spec §2, layer 3). Chosen by the host, never by content.</summary>
public enum BundleKeyPolicy
{
    /// <summary>Every known native set (the default): a bundle valid on one provider is valid on all.</summary>
    Strict,

    /// <summary>Only the current provider's native set.</summary>
    Native,
}
```

- [ ] **Step 4: Implement the validator**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>A raw name the tree validation refused, with the reason (spec §2).</summary>
public sealed record BundleKeyRejection(string RawName, string Reason);

/// <summary>The outcome of <see cref="BundleKeyValidator.ValidateTree"/>: accepted keys in input order, and every rejected raw name.</summary>
public sealed record BundleKeyTreeResult(IReadOnlyList<BundleKey> Accepted, IReadOnlyList<BundleKeyRejection> Rejected);

/// <summary>
/// Applies spec §2's layers 2 and 3 to keys: the provider's native constraints,
/// the host's portability policy, and optional extra validators that can only
/// restrict. <see cref="ValidateTree"/> checks a whole listing as a tree —
/// per-level collisions and file/directory conflicts — not key by key.
/// </summary>
public sealed class BundleKeyValidator
{
    private static readonly string[] ReservedBases =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    private const string WindowsIllegal = "<>:\"|?*";

    private readonly IReadOnlyList<Func<BundleKey, string?>> _extra;

    /// <summary>
    /// Whether <see cref="string.Normalize(NormalizationForm)"/> actually normalises in this
    /// process. Under <c>InvariantGlobalization</c> it is a silent no-op (measured on .NET 10:
    /// <c>"é".Normalize(FormC)</c> keeps length 2 and <c>IsNormalized</c> answers
    /// <see langword="true"/>), so the check cannot be trusted and
    /// <see cref="BundleKeyConstraints.RequireNfc"/> fails closed instead.
    /// </summary>
    public static bool NormalizationAvailable { get; } = ProbeNormalization();

    /// <summary>Creates a validator for a provider declaring <paramref name="native"/>, under <paramref name="policy"/>.</summary>
    public BundleKeyValidator(BundleKeyConstraints native, BundleKeyPolicy policy, IReadOnlyList<Func<BundleKey, string?>>? extraValidators = null)
    {
        Effective = policy == BundleKeyPolicy.Strict ? BundleKeyConstraintSets.Strict | native : native;
        _extra = extraValidators ?? [];
    }

    /// <summary>The constraints actually enforced: never below the provider's native set.</summary>
    public BundleKeyConstraints Effective { get; }

    /// <summary>Checks one key's segments against the effective constraints and the extra validators; <see langword="null"/> when accepted.</summary>
    public string? CheckKey(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var segment in key.Segments)
        {
            var reason = CheckSegment(segment);
            if (reason is not null)
            {
                return $"segment '{DebugQuote.Quote(segment)}' {reason}";
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
    /// or <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, siblings that fold to the same
    /// name are all rejected with their subtrees; under <see cref="BundleKeyConstraints.FileDirectoryConflict"/>,
    /// an object that is also a prefix of other keys is rejected with everything under it.
    /// A raw name listed twice is rejected (a provider must not do that).
    /// </summary>
    public BundleKeyTreeResult ValidateTree(IEnumerable<string> rawNames)
    {
        ArgumentNullException.ThrowIfNull(rawNames);
        var rejected = new List<BundleKeyRejection>();
        var candidates = new List<(string Raw, BundleKey Key)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rawNames)
        {
            if (!seen.Add(raw))
            {
                rejected.Add(new BundleKeyRejection(raw, "listed twice by the provider"));
                continue;
            }

            if (!BundleKey.TryParse(raw, out var key, out var reason))
            {
                rejected.Add(new BundleKeyRejection(raw, reason));
                continue;
            }

            var keyReason = CheckKey(key);
            if (keyReason is not null)
            {
                rejected.Add(new BundleKeyRejection(raw, keyReason));
                continue;
            }

            candidates.Add((raw, key));
        }

        // Remove a raw name that was listed twice from the candidates as well.
        var duplicates = rejected.Where(r => r.Reason.Contains("twice", StringComparison.Ordinal)).Select(r => r.RawName).ToHashSet(StringComparer.Ordinal);
        candidates.RemoveAll(c => duplicates.Contains(c.Raw));

        var doomed = new HashSet<BundleKey>();
        var fold = Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions) || Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions);

        if (fold)
        {
            // Group siblings by (parent, folded name) over every prefix of every key.
            var byParent = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
            foreach (var (_, key) in candidates)
            {
                for (var depth = 1; depth <= key.Segments.Count; depth++)
                {
                    var parent = string.Join('/', key.Segments.Take(depth - 1));
                    var name = key.Segments[depth - 1];
                    if (!byParent.TryGetValue(parent, out var siblings))
                    {
                        siblings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                        byParent[parent] = siblings;
                    }

                    var folded = Fold(name);
                    if (!siblings.TryGetValue(folded, out var names))
                    {
                        names = [];
                        siblings[folded] = names;
                    }

                    if (!names.Contains(name, StringComparer.Ordinal))
                    {
                        names.Add(name);
                    }
                }
            }

            foreach (var (parent, siblings) in byParent)
            {
                foreach (var names in siblings.Values.Where(n => n.Count > 1))
                {
                    foreach (var (raw, key) in candidates)
                    {
                        if (doomed.Contains(key))
                        {
                            continue;
                        }

                        var prefixDepth = parent.Length == 0 ? 0 : parent.Count(c => c == '/') + 1;
                        if (key.Segments.Count > prefixDepth
                            && string.Join('/', key.Segments.Take(prefixDepth)) == parent
                            && names.Contains(key.Segments[prefixDepth], StringComparer.Ordinal))
                        {
                            doomed.Add(key);
                            rejected.Add(new BundleKeyRejection(raw, $"segment '{DebugQuote.Quote(key.Segments[prefixDepth])}' collides with a sibling that differs only by case or Unicode form"));
                        }
                    }
                }
            }
        }

        if (Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict))
        {
            var keys = candidates.Select(c => c.Key).Where(k => !doomed.Contains(k)).ToHashSet();
            var prefixes = new HashSet<BundleKey>();
            foreach (var key in keys)
            {
                var parent = key.Parent;
                while (parent is not null)
                {
                    prefixes.Add(parent);
                    parent = parent.Parent;
                }
            }

            var conflicts = keys.Where(prefixes.Contains).ToList();
            foreach (var (raw, key) in candidates)
            {
                if (doomed.Contains(key))
                {
                    continue;
                }

                var conflict = conflicts.FirstOrDefault(c => key.Equals(c) || key.StartsWith(c));
                if (conflict is not null)
                {
                    doomed.Add(key);
                    rejected.Add(new BundleKeyRejection(raw, $"'{conflict}' is both an object and a prefix of other keys"));
                }
            }
        }

        var accepted = candidates.Where(c => !doomed.Contains(c.Key)).Select(c => c.Key).ToList();
        return new BundleKeyTreeResult(accepted, rejected);
    }

    private string? CheckSegment(string segment)
    {
        if (Effective.HasFlag(BundleKeyConstraints.TrailingDotOrSpace) && (segment[^1] == '.' || segment[^1] == ' '))
        {
            return "ends with '.' or a space";
        }

        if (Effective.HasFlag(BundleKeyConstraints.WindowsIllegalCharacters) && segment.IndexOfAny(WindowsIllegal.ToCharArray()) >= 0)
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
            if (!NormalizationAvailable)
            {
                if (segment.Any(c => c > 0x7f))
                {
                    return "is not ASCII and Unicode normalization is unavailable in this process (invariant globalization), so NFC cannot be checked";
                }
            }
            else if (!string.Equals(segment, segment.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
            {
                return "is not in Unicode NFC";
            }
        }

        return null;
    }

    private string Fold(string name)
    {
        var s = name;
        if (Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions) && NormalizationAvailable)
        {
            s = s.Normalize(NormalizationForm.FormC);
        }

        if (Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions))
        {
            s = UnicodeCaseFold.ToLowercase(s);
        }

        return s;
    }

    private static bool ProbeNormalization()
    {
        try
        {
            return "é".Normalize(NormalizationForm.FormC).Length == 1;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
```

Convert both new source files and the test file to CRLF.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleKeyValidatorTests"`
Expected: all green. `Nfc_is_required_under_strict_and_fails_closed_without_normalization` takes the `NormalizationAvailable` branch in the test host (ICU present) — the invariant branch is exercised by Task 8's AOT probe.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net/Providers/BundleKeyConstraints.cs src/OKF4net/Providers/BundleKeyValidator.cs tests/OKF4net.Tests/Providers/BundleKeyValidatorTests.cs
git commit -F - <<'MSG'
feat(providers): native constraints, Strict/Native policy and tree validation for keys

Per-level case and Unicode-form collisions, file/directory conflicts and the
Windows reserved names (superscripts included) are checked on the whole
listing; NFC fails closed when string.Normalize is a no-op (invariant
globalization).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 3: The contract — `IBundleProvider`, its value types, exceptions, and the read-only decorator

**Files:**
- Create: `src/OKF4net/Providers/IBundleProvider.cs`
- Create: `src/OKF4net/Providers/BundleObject.cs`
- Create: `src/OKF4net/Providers/BundleProviderExceptions.cs`
- Create: `src/OKF4net/Providers/BundleParseError.cs`
- Create: `src/OKF4net/Providers/ReadOnlyBundleProvider.cs`
- Test: `tests/OKF4net.Tests/Providers/ReadOnlyBundleProviderTests.cs`

**Interfaces:**
- Consumes: `BundleKey` (Task 1), `BundleKeyConstraints` (Task 2).
- Produces (exact shapes below): `IBundleProvider`, `BundleProviderCapabilities`, `BundleLimits`, `BundleListing`, `BundleObject`, `BundleObjectVersion`, `BundleVersion`, `BundleWriteCondition`/`BundleWriteConditionKind`, `BundleProviderException` and its five subclasses, `BundleObjectTooLargeException : IOException`, `BundleParseError`, `ReadOnlyBundleProvider`, `BundleProviderExtensions.AsReadOnly`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class ReadOnlyBundleProviderTests
{
    /// <summary>A writable in-memory provider that records every write call; the decorator must never reach it.</summary>
    private sealed class RecordingProvider : IBundleProvider
    {
        public List<string> Calls { get; } = [];
        public string Id => "recording";
        public string Location => "memory://recording";
        public BundleProviderCapabilities Capabilities => new(CanWrite: true);
        public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.FileDirectoryConflict;
        public BundleVersion GetVersion(BundleLimits limits) => new("v1");
        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => [new BundleListing("a.md", 3, null)];
        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) =>
            new(new MemoryStream(Encoding.UTF8.GetBytes("abc")), new BundleObjectVersion("sha"), 3);
        public bool Exists(BundleKey key) => key.ToString() == "a.md";
        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
        {
            Calls.Add($"write {key}");
            return new BundleObjectVersion("sha2");
        }
        public void Delete(BundleKey key, BundleWriteCondition condition) => Calls.Add($"delete {key}");
    }

    [Fact]
    public void Decorator_declares_no_write_and_refuses_before_touching_the_inner_provider()
    {
        var inner = new RecordingProvider();
        var ro = inner.AsReadOnly();
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(BundleKey.Parse("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(BundleKey.Parse("a.md"), BundleWriteCondition.None));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void Decorator_delegates_every_read_member()
    {
        var inner = new RecordingProvider();
        var ro = inner.AsReadOnly();
        Assert.Equal("recording", ro.Id);
        Assert.Equal("memory://recording", ro.Location);
        Assert.Equal(BundleKeyConstraints.FileDirectoryConflict, ro.NativeConstraints);
        Assert.Equal(new BundleVersion("v1"), ro.GetVersion(BundleLimits.Default));
        Assert.Single(ro.Enumerate(null, BundleLimits.Default));
        Assert.True(ro.Exists(BundleKey.Parse("a.md")));
        using var obj = ro.Read(BundleKey.Parse("a.md"));
        Assert.Equal(new BundleObjectVersion("sha"), obj.Version);
    }

    [Fact]
    public void AsReadOnly_is_idempotent()
    {
        var ro = new RecordingProvider().AsReadOnly();
        Assert.Same(ro, ro.AsReadOnly());
    }

    [Fact]
    public void Default_limits_match_the_spec()
    {
        var l = BundleLimits.Default;
        Assert.Equal(16L * 1024 * 1024, l.MaxObjectBytes);
        Assert.Equal(1_000_000, l.MaxEntries);
        Assert.Equal(64, l.MaxDepth);
        Assert.Equal(1024, l.MaxKeyBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), l.RequestTimeout);
        Assert.Equal(16, l.MaxConcurrentRequests);
    }

    [Fact]
    public void Write_conditions_and_versions_are_value_types_compared_ordinally()
    {
        var v = new BundleObjectVersion("abc");
        Assert.Equal(BundleWriteConditionKind.IfMatch, BundleWriteCondition.IfMatch(v).Kind);
        Assert.Equal(v, BundleWriteCondition.IfMatch(v).Expected);
        Assert.Null(BundleWriteCondition.None.Expected);
        Assert.Equal(BundleWriteConditionKind.MustNotExist, BundleWriteCondition.MustNotExist.Kind);
        Assert.NotEqual(new BundleObjectVersion("ABC"), v);
    }

    [Fact]
    public void Too_large_is_an_IOException_so_existing_handlers_treat_it_as_unreadable()
    {
        var e = new BundleObjectTooLargeException(BundleKey.Parse("a.md"), 17, 16);
        Assert.IsAssignableFrom<IOException>(e);
        Assert.Contains("a.md", e.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReadOnlyBundleProviderTests"`
Expected: build error — none of the contract types exist.

- [ ] **Step 3: Implement the value types**

`src/OKF4net/Providers/BundleObject.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>An opaque version token for one object; only the provider knows how it is built. Compared ordinally.</summary>
public readonly record struct BundleObjectVersion(string Token);

/// <summary>An opaque version token for a whole bundle, cheap to obtain; drives refresh decisions. Compared ordinally.</summary>
public readonly record struct BundleVersion(string Token);

/// <summary>One entry of a provider listing: the raw name (relative to the root, <c>/</c>-separated, not yet validated), its length and version when the backend gives them for free.</summary>
public sealed record BundleListing(string RawName, long? Length, BundleObjectVersion? Version);

/// <summary>An object read from a provider: its content stream (owned by this instance) and its version. The core reads the stream counting bytes (spec §6).</summary>
public sealed class BundleObject : IDisposable
{
    /// <summary>Creates an object over <paramref name="content"/>; disposing the object disposes the stream.</summary>
    public BundleObject(Stream content, BundleObjectVersion version, long? length)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Version = version;
        Length = length;
    }

    /// <summary>The content, positioned at its start.</summary>
    public Stream Content { get; }

    /// <summary>The object's version at the time of the read.</summary>
    public BundleObjectVersion Version { get; }

    /// <summary>The length in bytes when the provider knows it before reading.</summary>
    public long? Length { get; }

    /// <inheritdoc/>
    public void Dispose() => Content.Dispose();
}

/// <summary>How a write or delete is conditioned (spec §4).</summary>
public enum BundleWriteConditionKind
{
    /// <summary>Unconditional.</summary>
    None,

    /// <summary>Succeeds only if no object exists under the key (a create).</summary>
    MustNotExist,

    /// <summary>Succeeds only if the current version equals <see cref="BundleWriteCondition.Expected"/>.</summary>
    IfMatch,
}

/// <summary>The condition a provider checks atomically with a write or delete (spec §4).</summary>
public readonly record struct BundleWriteCondition(BundleWriteConditionKind Kind, BundleObjectVersion? Expected)
{
    /// <summary>Unconditional.</summary>
    public static BundleWriteCondition None => new(BundleWriteConditionKind.None, null);

    /// <summary>Create only.</summary>
    public static BundleWriteCondition MustNotExist => new(BundleWriteConditionKind.MustNotExist, null);

    /// <summary>Replace only if the current version is <paramref name="expected"/>.</summary>
    public static BundleWriteCondition IfMatch(BundleObjectVersion expected) => new(BundleWriteConditionKind.IfMatch, expected);
}

/// <summary>What a provider can do beyond reading (spec §3): reading is never optional.</summary>
public sealed record BundleProviderCapabilities(bool CanWrite);

/// <summary>The bounds of spec §6. The core enforces sizes, counts and depth on what it receives; a provider enforces the same on its own walks, and the network bounds on its requests.</summary>
public sealed record BundleLimits
{
    /// <summary>The spec's defaults.</summary>
    public static BundleLimits Default { get; } = new();

    /// <summary>Largest object the core reads, in bytes (16 MiB).</summary>
    public long MaxObjectBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Largest listing, every entry counted whatever its name (1 000 000).</summary>
    public int MaxEntries { get; init; } = 1_000_000;

    /// <summary>Deepest key, in segments (64).</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Longest key, in UTF-8 bytes (1 024; also <see cref="BundleKey.MaxUtf8Bytes"/>).</summary>
    public int MaxKeyBytes { get; init; } = BundleKey.MaxUtf8Bytes;

    /// <summary>Per-request timeout for network providers (30 s); unused by the filesystem.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Concurrent requests for network providers (16); unused by the filesystem.</summary>
    public int MaxConcurrentRequests { get; init; } = 16;
}
```

`src/OKF4net/Providers/IBundleProvider.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// A storage behind a bundle (spec §1): a key space and the bytes under each key.
/// A provider knows nothing of concepts, links or frontmatter; it lists raw names,
/// reads and writes bytes under keys the core has already validated, and declares
/// what it can do. There are no directories: a prefix exists only while an object
/// lives under it. Reading is mandatory; writing is a declared capability.
/// </summary>
public interface IBundleProvider
{
    /// <summary>A stable, non-secret identity: diagnostics, catalog de-duplication, lock key for non-filesystem providers.</summary>
    string Id { get; }

    /// <summary>A displayable, non-secret location (the root as given, for the filesystem).</summary>
    string Location { get; }

    /// <summary>What this provider can do beyond reading.</summary>
    BundleProviderCapabilities Capabilities { get; }

    /// <summary>The aliasing rules of this storage (spec §2, layer 2); never disabled by any policy.</summary>
    BundleKeyConstraints NativeConstraints { get; }

    /// <summary>A cheap token for the whole bundle; its internal walk respects <paramref name="limits"/>.</summary>
    BundleVersion GetVersion(BundleLimits limits);

    /// <summary>Lists raw names under the root, or under <paramref name="prefix"/> at a segment boundary (<c>a</c> covers <c>a/…</c>, never <c>ab/…</c>). The core validates them; the provider does not.</summary>
    IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits);

    /// <summary>Reads one object. With <paramref name="expected"/>, succeeds only if the current version matches, else throws <see cref="BundleVersionMismatchException"/>.</summary>
    /// <exception cref="BundleObjectNotFoundException">No object under the key.</exception>
    BundleObject Read(BundleKey key, BundleObjectVersion? expected = null);

    /// <summary>Whether an object exists under the key, matching it exactly (case and Unicode form).</summary>
    bool Exists(BundleKey key);

    /// <summary>Writes an object atomically if <paramref name="condition"/> holds; returns the new version.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    /// <summary>Deletes an object if <paramref name="condition"/> holds.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    void Delete(BundleKey key, BundleWriteCondition condition);
}
```

`src/OKF4net/Providers/BundleProviderExceptions.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Base of the provider contract's exceptions (spec §1). Messages are library-authored and name keys, never host paths or credentials.</summary>
public abstract class BundleProviderException(string message) : OkfException(message)
{
}

/// <summary>A write or delete was attempted on a provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/> (spec §3). Nothing was attempted.</summary>
public sealed class BundleReadOnlyException(string providerId)
    : BundleProviderException($"provider '{providerId}' is read-only; the write was refused before being attempted")
{
}

/// <summary>A write or delete condition did not hold (spec §4).</summary>
public sealed class BundleWriteConflictException(BundleKey key)
    : BundleProviderException($"'{key}' changed since it was read; the write was refused")
{
    /// <summary>The key whose condition failed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>A read with an expected version found a different one (pinning, spec §5.4).</summary>
public sealed class BundleVersionMismatchException(BundleKey key)
    : BundleProviderException($"'{key}' is no longer the version that was observed")
{
    /// <summary>The key whose version changed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>No object under the key.</summary>
public sealed class BundleObjectNotFoundException(BundleKey key)
    : BundleProviderException($"no object under '{key}'")
{
    /// <summary>The missing key.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>
/// An object exceeds <see cref="BundleLimits.MaxObjectBytes"/> (spec §6). Derives from
/// <see cref="IOException"/> so every existing handler that treats an I/O failure as
/// "unreadable" classifies it correctly without change (spec, point to validate 6).
/// </summary>
public sealed class BundleObjectTooLargeException(BundleKey key, long observedBytes, long maxBytes)
    : IOException($"'{key}' is larger than the {maxBytes}-byte bound ({observedBytes} bytes read before stopping)")
{
    /// <summary>The offending key.</summary>
    public BundleKey Key { get; } = key;
}
```

`src/OKF4net/Providers/BundleParseError.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// One problem met while loading a bundle (spec §2): the raw name as the provider
/// listed it, the validated key when the name passed validation (<see langword="null"/>
/// when it is precisely the name that was rejected), and the reason.
/// </summary>
public sealed record BundleParseError(string RawName, BundleKey? Key, string Error);
```

`src/OKF4net/Providers/ReadOnlyBundleProvider.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Downgrades a provider to read-only (spec §3): every read delegates, every write throws <see cref="BundleReadOnlyException"/> before reaching the inner provider.</summary>
public sealed class ReadOnlyBundleProvider : IBundleProvider
{
    private readonly IBundleProvider _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public ReadOnlyBundleProvider(IBundleProvider inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public string Id => _inner.Id;

    /// <inheritdoc/>
    public string Location => _inner.Location;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints => _inner.NativeConstraints;

    /// <inheritdoc/>
    public BundleVersion GetVersion(BundleLimits limits) => _inner.GetVersion(limits);

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => _inner.Enumerate(prefix, limits);

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => _inner.Read(key, expected);

    /// <inheritdoc/>
    public bool Exists(BundleKey key) => _inner.Exists(key);

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);
}

/// <summary>Extension methods on <see cref="IBundleProvider"/>.</summary>
public static class BundleProviderExtensions
{
    /// <summary>Returns a read-only view of <paramref name="provider"/>; a provider that already is one is returned as is. A host can downgrade, never upgrade.</summary>
    public static IBundleProvider AsReadOnly(this IBundleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider is ReadOnlyBundleProvider ? provider : new ReadOnlyBundleProvider(provider);
    }
}
```

Convert the five new source files and the test file to CRLF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReadOnlyBundleProviderTests"`
Expected: 6 tests green. Then `dotnet build OKF4net.sln`: no warning (every public member documented).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/IBundleProvider.cs src/OKF4net/Providers/BundleObject.cs src/OKF4net/Providers/BundleProviderExceptions.cs src/OKF4net/Providers/BundleParseError.cs src/OKF4net/Providers/ReadOnlyBundleProvider.cs tests/OKF4net.Tests/Providers/ReadOnlyBundleProviderTests.cs
git commit -F - <<'MSG'
feat(providers): the IBundleProvider contract, its value types and the read-only decorator

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 4: `FileSystemBundleProvider` — the read side, with its guards

**Files:**
- Create: `src/OKF4net/Providers/FileSystemBundleProvider.cs`
- Create: `src/OKF4net/Providers/BundleDisplayPath.cs`
- Modify: `src/OKF4net/Providers/BundleProviderExceptions.cs` (two more exceptions)
- Test: `tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs`

**Interfaces:**
- Consumes: the contract (Task 3); `OKF4net.Internal.ReparsePoints` (`CanonicalizeRoot`, `IsWithin(root, path, comparison)`, `IsReparsePoint`, `IsReparsePointOrUninspectable`, `HasReparsePointOrUninspectableAncestor(root, path, comparison)`); `TempDir` test helper (`Path`, `Write`, `TryCreateFileSymlink`, `TryCreateDirectorySymlink`, `TryCreateJunctionToExternalDir`).
- Produces:
  - `public sealed class FileSystemBundleProvider : IBundleProvider` with ctor `(string root, string? confinementBoundary = null, BundleKeyConstraints? nativeConstraints = null, BundleLimits? limits = null)`, properties `string Root` (as given), `string? ConfinementBoundary` (canonical), plus the interface. `Capabilities.CanWrite` is **false** in this lot.
  - `public static class BundleDisplayPath` with `string Compose(string location, BundleKey key)` = `Path.Combine([location, ..key.Segments])`.
  - Exceptions added: `BundleAccessRefusedException : BundleProviderException` (a STRICT guard or the confinement boundary refused the access) and `BundleLimitExceededException : BundleProviderException` with `string Bound` (`"entries"` or `"depth"`).
- Decisions recorded here: the provider matches keys **exactly** (segment by segment, ordinal) only when its `NativeConstraints` include `CaseInsensitiveCollisions` or `UnicodeFormCollisions` (Windows, macOS by default) — on a POSIX native set the OS already compares bytes, so a path lookup is exact; the provider takes `BundleLimits` at construction for `Read` (the interface gives `Read` no limits parameter, and the SHA-256 version requires reading the whole object, which must be bounded before it is hashed).

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class FileSystemBundleProviderTests
{
    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static BundleKey K(string s) => BundleKey.Parse(s);

    [Fact]
    public void Enumerate_lists_every_file_relative_with_slashes_in_ordinal_order_per_directory()
    {
        using var tmp = new TempDir();
        tmp.Write("b.md", "b");
        tmp.Write("a/z.txt", "z");
        tmp.Write("a/B.md", "B");
        tmp.Write("a/a.md", "a");
        var p = new FileSystemBundleProvider(tmp.Path);
        var names = p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName).ToList();
        Assert.Equal(["a/B.md", "a/a.md", "a/z.txt", "b.md"], names); // 'B' < 'a' ordinally; non-.md files are listed too
        Assert.All(p.Enumerate(null, BundleLimits.Default), l => Assert.Null(l.Version)); // the filesystem gives no cheap version
        Assert.Equal(1, p.Enumerate(null, BundleLimits.Default).Single(l => l.RawName == "b.md").Length);
    }

    [Fact]
    public void Enumerate_prefix_is_segment_bounded()
    {
        using var tmp = new TempDir();
        tmp.Write("a/x.md", "x");
        tmp.Write("ab/y.md", "y");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["a/x.md"], p.Enumerate(K("a"), BundleLimits.Default).Select(l => l.RawName));
        Assert.Empty(p.Enumerate(K("nope"), BundleLimits.Default));
    }

    [Fact]
    public void Enumerate_counts_directories_and_files_against_the_entry_bound_and_enforces_depth()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "x");
        tmp.Write("y.md", "y");
        var p = new FileSystemBundleProvider(tmp.Path);
        var ex = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }).ToList()); // d, d/x.md, y.md = 3
        Assert.Equal("entries", ex.Bound);
        Assert.Equal(3, p.Enumerate(null, new BundleLimits { MaxEntries = 3 }).Count() + 1); // 2 files + 1 directory counted
        tmp.Write("l1/l2/l3/deep.md", "d");
        var depth = Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxDepth = 3 }).ToList()); // l1/l2/l3/deep.md has 4 segments
        Assert.Equal("depth", depth.Bound);
    }

    [SkippableFact]
    public void Enumerate_skips_a_symlinked_entry_but_read_refuses_it_strictly()
    {
        using var tmp = new TempDir();
        tmp.Write("real.md", "r");
        Skip.IfNot(tmp.TryCreateFileSymlink("link.md", "real.md"), "symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(["real.md"], p.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("link.md")));
        Assert.False(p.Exists(K("link.md")));
    }

    [SkippableFact]
    public void Read_refuses_a_file_under_a_junction_ancestor()
    {
        using var external = new TempDir();
        external.Write("secret.md", "s");
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("j/secret.md")));
    }

    [Fact]
    public void Read_returns_the_bytes_and_a_sha256_version_and_honours_expected()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "hello");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var obj = p.Read(K("a.md"));
        Assert.Equal(new BundleObjectVersion(Sha256Hex("hello")), obj.Version);
        Assert.Equal(5, obj.Length);
        using var reader = new StreamReader(obj.Content, Encoding.UTF8);
        Assert.Equal("hello", reader.ReadToEnd());
        Assert.Throws<BundleVersionMismatchException>(() => p.Read(K("a.md"), new BundleObjectVersion("other")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("missing.md")));
        using var pinned = p.Read(K("a.md"), new BundleObjectVersion(Sha256Hex("hello")));
        Assert.Equal(5, pinned.Length);
    }

    [Fact]
    public void Read_stops_at_the_object_bound_before_hashing()
    {
        using var tmp = new TempDir();
        tmp.Write("big.md", new string('x', 11));
        var p = new FileSystemBundleProvider(tmp.Path, limits: new BundleLimits { MaxObjectBytes = 10 });
        var ex = Assert.Throws<BundleObjectTooLargeException>(() => p.Read(K("big.md")));
        Assert.IsAssignableFrom<IOException>(ex);
        tmp.Write("ok.md", new string('x', 10));
        using var ok = p.Read(K("ok.md"));
        Assert.Equal(10, ok.Length);
    }

    [SkippableFact]
    public void Keys_match_exactly_in_case_on_a_case_insensitive_volume()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "needs a case-insensitive default volume");
        using var tmp = new TempDir();
        tmp.Write("References/A.sql", "select 1");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.True(p.Exists(K("References/A.sql")));
        Assert.False(p.Exists(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("references/a.sql")));
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("References/a.sql")));
        using var exact = p.Read(K("References/A.sql"));
        Assert.Equal(8, exact.Length);
        Assert.Empty(p.Enumerate(K("references"), BundleLimits.Default));
    }

    [Fact]
    public void GetVersion_changes_with_content_and_is_stable_otherwise()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("a.md", "one");
        var p = new FileSystemBundleProvider(tmp.Path);
        var v1 = p.GetVersion(BundleLimits.Default);
        Assert.Equal(v1, p.GetVersion(BundleLimits.Default));
        File.WriteAllText(file, "two!"); // different length
        Assert.NotEqual(v1, p.GetVersion(BundleLimits.Default));
        File.WriteAllText(file, "one");
        File.SetLastWriteTimeUtc(file, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(v1, p.GetVersion(BundleLimits.Default)); // same bytes, different mtime: the token is a listing digest, not a content hash
        Assert.Throws<BundleLimitExceededException>(() => p.GetVersion(new BundleLimits { MaxEntries = 0 }));
    }

    [Fact]
    public void Writes_are_refused_in_this_lot_and_the_capability_says_so()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.False(p.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => p.Write(K("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => p.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "a.md")));
    }

    [Fact]
    public void Id_is_the_canonical_root_and_location_is_the_root_as_given()
    {
        using var tmp = new TempDir();
        var withSlash = tmp.Path + Path.DirectorySeparatorChar;
        var a = new FileSystemBundleProvider(tmp.Path);
        var b = new FileSystemBundleProvider(withSlash);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(withSlash, b.Location);
        Assert.Equal(Path.Combine(tmp.Path, "tables", "x.md"), BundleDisplayPath.Compose(tmp.Path, K("tables/x.md")));
    }

    [Fact]
    public void A_boundary_outside_the_root_refuses_every_access()
    {
        using var tmp = new TempDir();
        tmp.Write("inside/a.md", "a");
        tmp.Write("elsewhere/b.md", "b");
        var p = new FileSystemBundleProvider(Path.Combine(tmp.Path, "elsewhere"), confinementBoundary: Path.Combine(tmp.Path, "inside"));
        Assert.Throws<BundleAccessRefusedException>(() => p.Enumerate(null, BundleLimits.Default).ToList());
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("b.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("b.md")));
    }

    [SkippableFact]
    public void A_root_turned_into_a_junction_after_construction_is_refused_on_the_next_access_when_a_boundary_is_given()
    {
        using var external = new TempDir();
        external.Write("a.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("src/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);
        using (var before = p.Read(K("a.md")))
        {
            Assert.Equal(6, before.Length);
        }

        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("a.md")));
        Assert.Throws<BundleAccessRefusedException>(() => p.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => p.Exists(K("a.md")));
    }

    [SkippableFact]
    public void A_symlinked_root_loads_without_a_boundary_but_not_with_one()
    {
        using var tmp = new TempDir();
        tmp.Write("target/a.md", "a");
        Skip.IfNot(tmp.TryCreateDirectorySymlink("link", "target"), "directory symlinks unavailable");
        var link = Path.Combine(tmp.Path, "link");
        var free = new FileSystemBundleProvider(link);
        Assert.Equal(["a.md"], free.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));
        using var obj = free.Read(K("a.md"));
        Assert.Equal(1, obj.Length);
        var bounded = new FileSystemBundleProvider(link, confinementBoundary: tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => bounded.Read(K("a.md")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderTests"`
Expected: build error — `FileSystemBundleProvider` does not exist.

- [ ] **Step 3: Add the two exceptions** to `src/OKF4net/Providers/BundleProviderExceptions.cs`

```csharp
/// <summary>A STRICT path guard or the confinement boundary refused an access (spec §8): the key resolves through a reparse point, an entry that cannot be inspected, or outside the boundary.</summary>
public sealed class BundleAccessRefusedException(BundleKey? key, string detail)
    : BundleProviderException(key is null ? $"access refused: {detail}" : $"access to '{key}' refused: {detail}")
{
    /// <summary>The key, when the refusal concerned one.</summary>
    public BundleKey? Key { get; } = key;
}

/// <summary>A provider walk exceeded one of its <see cref="BundleLimits"/> (spec §6): <see cref="Bound"/> is <c>"entries"</c> or <c>"depth"</c>.</summary>
public sealed class BundleLimitExceededException(string bound, long limit)
    : BundleProviderException($"the bundle exceeds the {bound} bound ({limit})")
{
    /// <summary>Which bound was exceeded.</summary>
    public string Bound { get; } = bound;
}
```

- [ ] **Step 4: Implement `BundleDisplayPath`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// The display rule of spec §8: a provider's <see cref="IBundleProvider.Location"/>
/// followed by the key's segments joined with the OS separator — exactly what
/// <c>Path.Combine(root, …)</c> produced before the seam, so the CLI's bytes do not move.
/// </summary>
public static class BundleDisplayPath
{
    /// <summary>Composes the displayable path of <paramref name="key"/> under <paramref name="location"/>.</summary>
    public static string Compose(string location, BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(key);
        var parts = new string[key.Segments.Count + 1];
        parts[0] = location;
        for (var i = 0; i < key.Segments.Count; i++)
        {
            parts[i + 1] = key.Segments[i];
        }

        return Path.Combine(parts);
    }
}
```

- [ ] **Step 5: Implement the provider**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>
/// The default provider (spec §8): a bundle as a directory tree. Read-only in this
/// lot (<see cref="Capabilities"/> says so; lot B adds the atomic write). Keeps every
/// guard the loader had: the lenient reparse-point predicate on the walk, the STRICT
/// pair before any read, and — when a confinement boundary is given — a STRICT
/// re-check of the chain from the boundary to the root, inclusive, before <b>every</b>
/// access. Versions are SHA-256 of content; the bundle token is a digest of the
/// listing (name, length, mtime), deliberately weak and never used for pinning.
/// </summary>
public sealed class FileSystemBundleProvider : IBundleProvider
{
    private static readonly StringComparison PathComparison = StringComparison.Ordinal;

    private readonly string _fullRoot;
    private readonly BundleLimits _limits;
    private readonly bool _exactCase;

    /// <summary>
    /// Creates a provider over <paramref name="root"/>. <paramref name="confinementBoundary"/>
    /// (a catalog root) makes the root itself subject to the STRICT guards on every access;
    /// without it the root is trusted host configuration, as today.
    /// <paramref name="nativeConstraints"/> defaults to the current OS's set;
    /// <paramref name="limits"/> bounds <see cref="Read"/> (the interface gives it no per-call limits).
    /// </summary>
    public FileSystemBundleProvider(string root, string? confinementBoundary = null, BundleKeyConstraints? nativeConstraints = null, BundleLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
        _fullRoot = ReparsePoints.CanonicalizeRoot(root);
        ConfinementBoundary = confinementBoundary is null ? null : ReparsePoints.CanonicalizeRoot(confinementBoundary);
        NativeConstraints = nativeConstraints ?? BundleKeyConstraintSets.ForCurrentOperatingSystem();
        _limits = limits ?? BundleLimits.Default;
        _exactCase = NativeConstraints.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions)
            || NativeConstraints.HasFlag(BundleKeyConstraints.UnicodeFormCollisions);
    }

    /// <summary>The root as given (also <see cref="Location"/>).</summary>
    public string Root { get; }

    /// <summary>The canonical boundary every access is re-checked against, or <see langword="null"/>.</summary>
    public string? ConfinementBoundary { get; }

    /// <inheritdoc/>
    public string Id => _fullRoot;

    /// <inheritdoc/>
    public string Location => Root;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints { get; }

    /// <inheritdoc/>
    public BundleVersion GetVersion(BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        CheckBoundary();
        var sb = new StringBuilder();
        foreach (var entry in Walk(_fullRoot, limits))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            long length;
            long mtime;
            try
            {
                var info = new FileInfo(entry.FullPath);
                length = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                length = -1;
                mtime = -1;
            }

            sb.Append(entry.RawName).Append('\0').Append(length).Append('\0').Append(mtime).Append('\n');
        }

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        CheckBoundary();
        var start = _fullRoot;
        if (prefix is not null)
        {
            var resolved = ResolveExact(prefix);
            if (resolved is null || !Directory.Exists(resolved))
            {
                return [];
            }

            start = resolved;
        }

        return EnumerateFrom(start, prefix, limits);
    }

    private IEnumerable<BundleListing> EnumerateFrom(string start, BundleKey? prefix, BundleLimits limits)
    {
        foreach (var entry in Walk(start, limits, prefix?.Segments.Count ?? 0))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            long? length;
            try
            {
                length = new FileInfo(entry.FullPath).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                length = null;
            }

            var raw = prefix is null ? entry.RawName : prefix + "/" + entry.RawName;
            yield return new BundleListing(raw, length, null);
        }
    }

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
        GuardStrict(key, path);
        if (!File.Exists(path))
        {
            throw new BundleObjectNotFoundException(key);
        }

        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > _limits.MaxObjectBytes)
            {
                throw new BundleObjectTooLargeException(key, fs.Length, _limits.MaxObjectBytes);
            }

            bytes = ReadBounded(fs, _limits.MaxObjectBytes, key);
        }
        catch (FileNotFoundException)
        {
            throw new BundleObjectNotFoundException(key);
        }
        catch (DirectoryNotFoundException)
        {
            throw new BundleObjectNotFoundException(key);
        }

        var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        if (expected is { } e && e != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
    }

    /// <inheritdoc/>
    public bool Exists(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var path = ResolveExact(key);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        return !ReparsePoints.IsReparsePointOrUninspectable(path)
            && !ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison);
    }

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <summary>
    /// With a boundary: the root must be under it, and neither the root nor any entry
    /// between the boundary and the root may be a reparse point or uninspectable — STRICT,
    /// on every access, because the loader's own ancestor walk stops <i>before</i> the root.
    /// </summary>
    private void CheckBoundary()
    {
        if (ConfinementBoundary is null)
        {
            return;
        }

        if (!ReparsePoints.IsWithin(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root is outside its confinement boundary");
        }

        if (ReparsePoints.IsReparsePointOrUninspectable(_fullRoot)
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root, or a directory between the boundary and it, is a reparse point or cannot be inspected");
        }
    }

    private void GuardStrict(BundleKey key, string path)
    {
        if (!ReparsePoints.IsWithin(_fullRoot, path, PathComparison)
            || ReparsePoints.IsReparsePointOrUninspectable(path)
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison))
        {
            throw new BundleAccessRefusedException(key, "it resolves outside the bundle root, or through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>
    /// Maps a key to its on-disk path. On a native set that folds case or Unicode form,
    /// each segment must match an existing entry's name <b>exactly</b> (ordinal) — spec §2's
    /// correspondence rule — else <see langword="null"/>. On a POSIX set the OS compares bytes
    /// already, so the joined path is returned as is.
    /// </summary>
    private string? ResolveExact(BundleKey key)
    {
        var parts = new string[key.Segments.Count + 1];
        parts[0] = _fullRoot;
        for (var i = 0; i < key.Segments.Count; i++)
        {
            parts[i + 1] = key.Segments[i];
        }

        var joined = Path.Combine(parts);
        if (!_exactCase)
        {
            return joined;
        }

        var current = _fullRoot;
        foreach (var segment in key.Segments)
        {
            string? match = null;
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos(segment, SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                    {
                        match = entry.FullName;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    private readonly record struct WalkEntry(string FullPath, string RawName, bool IsDirectory);

    /// <summary>
    /// Depth-first walk, entries sorted ordinally per directory (as <c>Bundle.CollectMarkdown</c> did),
    /// skipping reparse points with the <b>lenient</b> predicate (a walk must not abort on an
    /// unreadable entry; the read gate is STRICT). Every entry, file or directory, counts
    /// against <see cref="BundleLimits.MaxEntries"/>; a file deeper than <see cref="BundleLimits.MaxDepth"/>
    /// segments (counting from the bundle root, <paramref name="baseDepth"/> segments above <paramref name="start"/>) aborts.
    /// </summary>
    private static IEnumerable<WalkEntry> Walk(string start, BundleLimits limits, int baseDepth = 0)
    {
        var count = 0;
        var stack = new Stack<(string Dir, string Rel, int Depth)>();
        stack.Push((start, string.Empty, baseDepth));
        while (stack.Count > 0)
        {
            var (dir, rel, depth) = stack.Pop();
            string[] names;
            try
            {
                names = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToArray();
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }

            Array.Sort(names, StringComparer.Ordinal);
            // Push in reverse so the stack yields ordinal order.
            for (var i = names.Length - 1; i >= 0; i--)
            {
                var name = names[i];
                var full = Path.Combine(dir, name);
                var raw = rel.Length == 0 ? name : rel + "/" + name;
                if (ReparsePoints.IsReparsePoint(full))
                {
                    continue;
                }

                if (++count > limits.MaxEntries)
                {
                    throw new BundleLimitExceededException("entries", limits.MaxEntries);
                }

                if (depth + 1 > limits.MaxDepth)
                {
                    throw new BundleLimitExceededException("depth", limits.MaxDepth);
                }

                if (Directory.Exists(full))
                {
                    stack.Push((full, raw, depth + 1));
                    yield return new WalkEntry(full, raw, true);
                }
                else if (File.Exists(full))
                {
                    yield return new WalkEntry(full, raw, false);
                }
            }
        }
    }

    private static byte[] ReadBounded(Stream stream, long max, BundleKey key)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw new BundleObjectTooLargeException(key, total, max);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
```

Two notes for the implementer:
- `Walk` yields directories **before** descending (the stack pop order makes the yield happen at the parent's turn); the ordinal order test checks files only, but keep directory entries in the stream so `Enumerate` and `GetVersion` count them. The ordering guarantee is per directory, which is all `Bundle.Load` needs since it sorts keys itself.
- `ReparsePoints.IsWithin`, `CanonicalizeRoot` etc. are `internal` in the same assembly; nothing to expose.

Convert every new/modified source file and the test file to CRLF.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileSystemBundleProviderTests"`
Expected: green; the `SkippableFact`s run on Windows with symlink privilege and skip elsewhere — on the Windows dev box, confirm **0 skipped** at least once (run as an account that can create symlinks, or with Developer Mode on).

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net/Providers/FileSystemBundleProvider.cs src/OKF4net/Providers/BundleDisplayPath.cs src/OKF4net/Providers/BundleProviderExceptions.cs tests/OKF4net.Tests/Providers/FileSystemBundleProviderTests.cs
git commit -F - <<'MSG'
feat(providers): read-only filesystem provider with STRICT guards and an optional confinement boundary

Lenient walk, STRICT read gate, exact-case key matching on folding volumes,
SHA-256 object versions, a listing-digest bundle token, entry/depth bounds,
and the boundary re-checked before every access. Writes are refused and
declared so; lot B adds them.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 5: `ResourceKeyResolver` — §6.2 path-valued fields resolved lexically on keys

**Files:**
- Create: `src/OKF4net/Providers/ResourceKeyResolver.cs`
- Test: `tests/OKF4net.Tests/Providers/ResourceKeyResolverTests.cs`

**Interfaces:**
- Consumes: `BundleKey` (Task 1); `OKF4net.FrontmatterResourceClassifier.KindOf(string)` (`internal`, same assembly; returns `Url` / `BundleRelative` / `ConceptRelative`).
- Produces: `public enum ResourceKeyOutcome { Url, Unsafe, Candidate }` and `public static class ResourceKeyResolver` with `ResourceKeyOutcome Resolve(BundleKey conceptKey, string rawPath, out BundleKey? key)`. A `Candidate` is a syntactically valid key under the root whose existence has **not** been checked; Task 6 runs it through the validator and the provider.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class ResourceKeyResolverTests
{
    private static readonly BundleKey Concept = BundleKey.Parse("computations/revenue.md");
    private static readonly BundleKey TopLevel = BundleKey.Parse("revenue.md");

    [Theory]
    [InlineData("https://example.test/q.sql")]
    [InlineData("s3://bucket/q.sql")]
    public void A_url_is_never_dereferenced(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Url, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData("/references/a.sql", "references/a.sql")]   // bundle-relative
    [InlineData("references/a.sql", "references/a.sql")]    // bare: from the root (S6.2-1)
    [InlineData("./a.sql", "computations/a.sql")]           // concept-relative
    [InlineData("../references/a.sql", "references/a.sql")]
    [InlineData("a//./b.sql", "a/b.sql")]                   // empty and '.' segments ignored
    [InlineData("%2e%2e/x.sql", "%2e%2e/x.sql")]            // never percent-decoded
    [InlineData("/./a.sql", "a.sql")]
    public void Resolves_the_spec_table_rows_to_keys(string raw, string expected)
    {
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse(expected), key);
    }

    [Fact]
    public void Concept_relative_from_a_top_level_concept_resolves_from_the_root()
    {
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(TopLevel, "./a.sql", out var key));
        Assert.Equal(BundleKey.Parse("a.sql"), key);
    }

    [Theory]
    [InlineData("../../q.sql")]       // climbs above the root
    [InlineData("/../q.sql")]
    [InlineData("../")]               // resolves to the root itself
    [InlineData("/")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("C:\\x.sql")]         // drive forms
    [InlineData("e:q.sql")]
    [InlineData("/e:q.sql")]
    [InlineData("C:/x.sql")]
    [InlineData("references\\a.sql")] // backslash → Unsafe (point to validate 7)
    [InlineData("a\0b.sql")]          // control character
    public void Unsafe_rows(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void A_resolved_key_longer_than_the_bound_is_unsafe()
    {
        var raw = "/" + new string('a', 1030);
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out _));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ResourceKeyResolverTests"`
Expected: build error — `ResourceKeyResolver` does not exist.

- [ ] **Step 3: Implement the resolver**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What <see cref="ResourceKeyResolver.Resolve"/> made of a §6.2 value.</summary>
public enum ResourceKeyOutcome
{
    /// <summary>An absolute URL; never dereferenced (spec §7).</summary>
    Url,

    /// <summary>The value climbs above the root, resolves to the root itself, uses a drive form or a backslash, or breaks a base key rule.</summary>
    Unsafe,

    /// <summary>A syntactically valid key under the root; existence and policy are checked by the caller.</summary>
    Candidate,
}

/// <summary>
/// Resolves a §6.2 path-valued field lexically on keys (spec §2): no OS path semantics,
/// no percent-decoding, no filesystem access. <c>/x</c> and a bare <c>x</c> resolve from
/// the root (this implementation's reading of §6.2, docs/spec-conformance S6.2-1);
/// <c>./x</c> and <c>../x</c> from the concept's own prefix.
/// </summary>
public static class ResourceKeyResolver
{
    /// <summary>Resolves <paramref name="rawPath"/> relative to <paramref name="conceptKey"/>'s prefix or the root.</summary>
    public static ResourceKeyOutcome Resolve(BundleKey conceptKey, string rawPath, out BundleKey? key)
    {
        ArgumentNullException.ThrowIfNull(conceptKey);
        ArgumentNullException.ThrowIfNull(rawPath);
        key = null;

        var kind = FrontmatterResourceClassifier.KindOf(rawPath);
        if (kind == FrontmatterResourceKind.Url)
        {
            return ResourceKeyOutcome.Url;
        }

        if (rawPath.Contains('\\'))
        {
            return ResourceKeyOutcome.Unsafe;
        }

        var relative = rawPath.TrimStart('/');
        if (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
        {
            // A drive-relative or drive-absolute form (e:q.sql, C:/x): never a bundle path.
            return ResourceKeyOutcome.Unsafe;
        }

        var segments = new List<string>();
        if (kind == FrontmatterResourceKind.ConceptRelative && conceptKey.Parent is { } parent)
        {
            segments.AddRange(parent.Segments);
        }

        foreach (var segment in relative.Split('/'))
        {
            switch (segment)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                    {
                        return ResourceKeyOutcome.Unsafe;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        if (segments.Count == 0)
        {
            return ResourceKeyOutcome.Unsafe;
        }

        if (!BundleKey.TryParse(string.Join('/', segments), out var parsed, out _))
        {
            return ResourceKeyOutcome.Unsafe;
        }

        key = parsed;
        return ResourceKeyOutcome.Candidate;
    }
}
```

Convert both files to CRLF.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ResourceKeyResolverTests"`
Expected: green (every table row).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Providers/ResourceKeyResolver.cs tests/OKF4net.Tests/Providers/ResourceKeyResolverTests.cs
git commit -F - <<'MSG'
feat(providers): resolve §6.2 path-valued fields lexically on keys

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 6: `Bundle.Load(IBundleProvider, BundleLoadOptions?)` — the loader on the seam, and the validator through it

**Files:**
- Create: `src/OKF4net/Providers/BundleLoadOptions.cs`
- Modify: `src/OKF4net/ConceptId.cs` (add `ToKey()` / `FromKey(BundleKey)` next to `ToPath`/`FromPath`, which stay)
- Modify: `src/OKF4net/Bundle.cs` (`Concept` gains an optional `Key`; new `Load(provider)`; `Load(string)` delegates; new members; `CollectMarkdown` removed — the provider's walk replaces it)
- Modify: `src/OKF4net/Validate.cs` (`ValidateReserved` reads through the provider; `ConceptRelativeHint` and the resource existence check use `TryResolveResourceKey`)
- Create: `tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs` (test helper, read-only, OS-independent)
- Test: `tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5; `OKF4net.Internal.PathOrdering.CompareComponentWise` (splits on `/` and `\`, ordinal per segment, shorter first — applied to key texts it yields today's concept order); `OKF4net.Internal.OkfEncodings.Strict`; `DebugQuote.Quote` (wraps in `"…"`).
- Produces:
  - `public sealed record BundleLoadOptions { BundleLimits Limits; BundleKeyPolicy KeyPolicy = Strict; IReadOnlyList<Func<BundleKey,string?>>? ExtraKeyValidators; static Default }`.
  - `ConceptId.ToKey()` → `BundleKey` (`parents…/Name.md`), `static ConceptId FromKey(BundleKey)` (last segment must end with `.md`; throws `ConceptIdException` otherwise, same as `FromPath` does).
  - `Concept(ConceptId Id, string Path, OkfDocument Document, BundleKey? Key = null)` — `Key` is always set by `Bundle.Load`.
  - On `Bundle`: `static Bundle Load(IBundleProvider provider, BundleLoadOptions? options = null)`; `IBundleProvider Provider`; `string Location`; `BundleVersion Version`; `BundleLoadOptions Options`; `IReadOnlyList<BundleParseError> ParseProblems`; `IReadOnlyList<BundleKey> IndexKeys`, `LogKeys`; `IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions`; `bool TryResolveResourceKey(Concept concept, string rawPath, out BundleKey? key, out ResourceResolutionStatus status)`; `string ReadResourceText(BundleKey key)`. `Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`, `ReadResourceText(string)` are kept with their current behaviour.

- [ ] **Step 1: Write the in-memory provider test helper**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A read-only provider over a dictionary, so loader tests can use names the
/// host OS could not create (reserved names, case-colliding siblings) and never
/// touch the disk. Native constraints default to <see cref="BundleKeyConstraints.None"/>.
/// </summary>
public sealed class InMemoryBundleProvider : IBundleProvider
{
    private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public InMemoryBundleProvider(BundleKeyConstraints native = BundleKeyConstraints.None, string id = "memory")
    {
        NativeConstraints = native;
        Id = id;
    }

    public InMemoryBundleProvider Add(string rawName, string text)
    {
        _objects[rawName] = Encoding.UTF8.GetBytes(text);
        return this;
    }

    public InMemoryBundleProvider AddBytes(string rawName, byte[] bytes)
    {
        _objects[rawName] = bytes;
        return this;
    }

    public string Id { get; }
    public string Location => "memory://" + Id;
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);
    public BundleKeyConstraints NativeConstraints { get; }

    public BundleVersion GetVersion(BundleLimits limits)
    {
        var sb = new StringBuilder();
        foreach (var (name, bytes) in _objects)
        {
            sb.Append(name).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
        }

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        var count = 0;
        foreach (var (name, bytes) in _objects)
        {
            if (prefix is not null && !(name == prefix.ToString() || name.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException("entries", limits.MaxEntries);
            }

            yield return new BundleListing(name, bytes.Length, null);
        }
    }

    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        if (!_objects.TryGetValue(key.ToString(), out var bytes))
        {
            throw new BundleObjectNotFoundException(key);
        }

        var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        if (expected is { } e && e != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
    }

    public bool Exists(BundleKey key) => _objects.ContainsKey(key.ToString());

    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => throw new BundleReadOnlyException(Id);

    public void Delete(BundleKey key, BundleWriteCondition condition) => throw new BundleReadOnlyException(Id);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleProviderLoadTests
{
    private const string Orders = "---\ntype: Table\ntitle: Orders\ndescription: One row per order.\n---\n\nSee [customers](/tables/customers.md).\n";
    private const string Customers = "---\ntype: Table\ntitle: Customers\ndescription: One row per customer.\n---\n\nBody.\n";

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Loading_through_the_filesystem_provider_equals_loading_by_path()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n");
        tmp.Write("tables/orders.md", Orders);
        tmp.Write("tables/customers.md", Customers);
        tmp.Write("tables/log.md", "# Log\n\n## 2026-01-01\n\n- x\n");
        tmp.Write("tables/broken.md", "---\ntype: Note\nk: *a\n---\nbody\n"); // a YAML alias: the subset rejects it, so it lands in ParseErrors
        var byPath = Bundle.Load(tmp.Path);
        var provider = new FileSystemBundleProvider(tmp.Path);
        var byProvider = Bundle.Load(provider);

        Assert.Equal(byPath.Concepts.Select(c => c.Id), byProvider.Concepts.Select(c => c.Id));
        Assert.Equal(byPath.Concepts.Select(c => c.Path), byProvider.Concepts.Select(c => c.Path));
        Assert.Equal(byPath.Concepts.Select(c => c.Document.Body), byProvider.Concepts.Select(c => c.Document.Body));
        Assert.Equal(byPath.IndexFiles, byProvider.IndexFiles);
        Assert.Equal(byPath.LogFiles, byProvider.LogFiles);
        Assert.Equal(byPath.ParseErrors, byProvider.ParseErrors);
        Assert.Equal(byPath.OkfVersion, byProvider.OkfVersion);
        Assert.Equal(byPath.Root, byProvider.Root);
        var orders = ConceptId.Parse("tables/orders");
        Assert.Equal(byPath.LinksFrom(orders), byProvider.LinksFrom(orders));
        Assert.Equal(byPath.Backlinks(ConceptId.Parse("tables/customers")), byProvider.Backlinks(ConceptId.Parse("tables/customers")));

        Assert.Same(provider, byProvider.Provider);
        Assert.Equal(tmp.Path, byProvider.Location);
        Assert.Equal(provider.GetVersion(BundleLimits.Default), byProvider.Version);
        Assert.Equal(BundleKey.Parse("tables/orders.md"), byProvider.Get(orders)!.Key);
        Assert.Equal(new BundleObjectVersion(Sha(Orders)), byProvider.ObjectVersions[BundleKey.Parse("tables/orders.md")]);
        Assert.Equal([BundleKey.Parse("index.md")], byProvider.IndexKeys);
        Assert.Equal([BundleKey.Parse("tables/log.md")], byProvider.LogKeys);
        // Load(string) sets the new members too.
        Assert.NotNull(byPath.Provider);
        Assert.Equal(BundleKey.Parse("tables/orders.md"), byPath.Get(orders)!.Key);
    }

    [Fact]
    public void Strict_rejections_land_in_ParseProblems_with_the_raw_name_and_no_key()
    {
        var p = new InMemoryBundleProvider()
            .Add("con.md", Customers)
            .Add("ok.md", Customers)
            .Add("a:b.txt", "not a concept, silently skipped");
        var bundle = Bundle.Load(p);
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("con.md", problem.RawName);
        Assert.Null(problem.Key);
        Assert.Contains("reserved", problem.Error);
        var projected = Assert.Single(bundle.ParseErrors);
        Assert.Contains("con.md", projected.Path);
        Assert.Equal(problem.Error, projected.Error);
    }

    [Fact]
    public void Native_policy_keeps_what_strict_rejects()
    {
        var p = new InMemoryBundleProvider().Add("A/x.md", Customers).Add("a/y.md", Customers);
        var strict = Bundle.Load(p);
        Assert.Empty(strict.Concepts);
        Assert.Equal(2, strict.ParseProblems.Count);
        var native = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal(2, native.Concepts.Count);
        Assert.Empty(native.ParseProblems);
    }

    [Fact]
    public void A_concept_over_the_object_bound_becomes_a_parse_problem_and_the_load_goes_on()
    {
        var big = "---\ntype: T\n---\n" + new string('x', 100);
        var p = new InMemoryBundleProvider().Add("big.md", big).Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 64 } });
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(BundleKey.Parse("big.md"), problem.Key);
        Assert.Contains("64", problem.Error);
    }

    [Fact]
    public void A_root_index_over_the_bound_gives_no_declared_version()
    {
        var p = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n" + new string('#', 200))
            .Add("ok.md", Customers);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 64 } });
        Assert.Null(bundle.OkfVersion);
        Assert.Single(bundle.IndexKeys);
    }

    [Fact]
    public void The_entry_bound_aborts_the_load()
    {
        var p = new InMemoryBundleProvider().Add("a.md", Customers).Add("b.md", Customers);
        var ex = Assert.Throws<BundleLoadException>(() => Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxEntries = 1 } }));
        Assert.Contains("entries", ex.Message);
    }

    [Fact]
    public void Reserved_filenames_are_classified_by_the_last_segment_only()
    {
        var p = new InMemoryBundleProvider()
            .Add("tables/index.md/x.md", Customers)
            .Add("tables/index.md", "# Tables\n")
            .Add("log.md", "# Log\n");
        var bundle = Bundle.Load(p, new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Native });
        Assert.Equal([ConceptId.Parse("tables/index.md/x")], bundle.Concepts.Select(c => c.Id));
        Assert.Equal([BundleKey.Parse("tables/index.md")], bundle.IndexKeys);
        Assert.Equal([BundleKey.Parse("log.md")], bundle.LogKeys);
    }

    [Fact]
    public void Resource_keys_resolve_through_the_provider_and_the_policy()
    {
        var p = new InMemoryBundleProvider()
            .Add("computations/rev.md", "---\ntype: Attested Computation\n---\n")
            .Add("references/q.sql", "select 1")
            .Add("computations/sibling.sql", "select 2");
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse("computations/rev"))!;

        Assert.True(bundle.TryResolveResourceKey(concept, "/references/q.sql", out var key, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(BundleKey.Parse("references/q.sql"), key);

        bundle.TryResolveResourceKey(concept, "./sibling.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal(BundleKey.Parse("computations/sibling.sql"), key);

        bundle.TryResolveResourceKey(concept, "references/missing.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Missing, status);
        Assert.Equal(BundleKey.Parse("references/missing.sql"), key);

        bundle.TryResolveResourceKey(concept, "../../q.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(key);

        bundle.TryResolveResourceKey(concept, "references\\q.sql", out _, out status);
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);

        bundle.TryResolveResourceKey(concept, "/con.sql", out _, out status); // rejected by Strict
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);

        bundle.TryResolveResourceKey(concept, "https://x/q.sql", out key, out status);
        Assert.Equal(ResourceResolutionStatus.Url, status);
        Assert.Null(key);

        Assert.Equal("select 1", bundle.ReadResourceText(BundleKey.Parse("references/q.sql")));
        Assert.Throws<FileNotFoundException>(() => bundle.ReadResourceText(BundleKey.Parse("references/missing.sql")));
    }

    [Fact]
    public void ConceptId_round_trips_through_keys()
    {
        var id = ConceptId.Parse("tables/orders");
        Assert.Equal(BundleKey.Parse("tables/orders.md"), id.ToKey());
        Assert.Equal(id, ConceptId.FromKey(BundleKey.Parse("tables/orders.md")));
        Assert.Throws<ConceptIdException>(() => ConceptId.FromKey(BundleKey.Parse("tables/orders.txt")));
    }

    [Fact]
    public void The_validator_reads_reserved_files_and_resources_through_the_provider()
    {
        var p = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n\n- [Orders](tables/orders.md) — One row per order.\n")
            .Add("tables/index.md", "---\nstray: frontmatter\n---\n# Tables\n")
            .Add("tables/orders.md", "---\ntype: Table\ntitle: Orders\ndescription: d\nresource: q.sql\n---\n")
            .Add("tables/q.sql", "select 1")
            .Add("log.md", "# Log\n\n## not-a-date\n\n- x\n");
        var bundle = Bundle.Load(p);
        var report = BundleValidator.Validate(bundle);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.IndexHasFrontmatter);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.LogDateInvalid);
        var missing = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing);
        Assert.Contains("a file exists at tables/q.sql", missing.Message); // the concept-relative hint, now through the provider
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleProviderLoadTests"`
Expected: build error — `BundleLoadOptions`, `Bundle.Load(IBundleProvider, …)`, `Concept.Key` do not exist.

- [ ] **Step 4: Add `BundleLoadOptions`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What a host controls when loading a bundle through a provider (spec §2, §6).</summary>
public sealed record BundleLoadOptions
{
    /// <summary>The spec's defaults: default bounds, <see cref="BundleKeyPolicy.Strict"/>, no extra validator.</summary>
    public static BundleLoadOptions Default { get; } = new();

    /// <summary>The bounds enforced by the core and handed to the provider for its own walks.</summary>
    public BundleLimits Limits { get; init; } = BundleLimits.Default;

    /// <summary>The portability policy applied on top of the provider's native constraints.</summary>
    public BundleKeyPolicy KeyPolicy { get; init; } = BundleKeyPolicy.Strict;

    /// <summary>Host validators that may only restrict further; each returns a reason or <see langword="null"/>.</summary>
    public IReadOnlyList<Func<BundleKey, string?>>? ExtraKeyValidators { get; init; }
}
```

- [ ] **Step 5: Add the key bridges to `ConceptId`** (after `ToPath`)

```csharp
    /// <summary>The key of this concept's document: its parents, then <c>Name.md</c> (spec §2).</summary>
    public OKF4net.Providers.BundleKey ToKey()
    {
        var segments = new List<string>(Segments.Count);
        segments.AddRange(Segments.Take(Segments.Count - 1));
        segments.Add(Name + ".md");
        return OKF4net.Providers.BundleKey.FromSegments(segments);
    }

    /// <summary>The concept id of a <c>.md</c> key: every segment validated, the last one stripped of <c>.md</c>.</summary>
    /// <exception cref="ConceptIdException">The key does not end with <c>.md</c>, or a segment is not a valid id segment.</exception>
    public static ConceptId FromKey(OKF4net.Providers.BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.IsMarkdown)
        {
            throw new ConceptIdException($"{DebugQuote.Quote(key.ToString())} is not a markdown key");
        }

        var segments = key.Segments.ToList();
        segments[^1] = segments[^1].Substring(0, segments[^1].Length - 3);
        return New(segments);
    }
```

- [ ] **Step 6: Rework `Bundle.cs`**

Replace the `Concept` record:

```csharp
/// <summary>A loaded concept: its id, its display path (spec §8's rule), its document, and its key in the provider (always set by <see cref="Bundle.Load(IBundleProvider, BundleLoadOptions?)"/>).</summary>
public sealed record Concept(ConceptId Id, string Path, OkfDocument Document, OKF4net.Providers.BundleKey? Key = null);
```

Add `using OKF4net.Providers;` at the top. Replace the private constructor and `Load(string)` with:

```csharp
    private Bundle(
        IBundleProvider provider,
        BundleLoadOptions options,
        BundleKeyValidator validator,
        BundleVersion version,
        string? okfVersion,
        List<Concept> concepts,
        Dictionary<ConceptId, int> index,
        List<BundleKey> indexKeys,
        List<BundleKey> logKeys,
        List<BundleParseError> parseProblems,
        Dictionary<BundleKey, BundleObjectVersion> objectVersions,
        Dictionary<ConceptId, List<ResolvedLink>> outbound,
        Dictionary<ConceptId, List<ConceptId>> backlinks)
    {
        Provider = provider;
        Options = options;
        _validator = validator;
        Version = version;
        _okfVersion = okfVersion;
        Concepts = concepts;
        _index = index;
        IndexKeys = indexKeys;
        LogKeys = logKeys;
        IndexFiles = indexKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        LogFiles = logKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        ParseProblems = parseProblems;
        ParseErrors = parseProblems.Select(p => (DisplayPathOf(provider.Location, p), p.Error)).ToList();
        ObjectVersions = objectVersions;
        _outbound = outbound;
        _backlinks = backlinks;
    }

    private static string DisplayPathOf(string location, BundleParseError problem) =>
        problem.Key is { } key
            ? BundleDisplayPath.Compose(location, key)
            : System.IO.Path.Combine(location, DebugQuote.Quote(problem.RawName).Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>
    /// Loads a bundle from a directory tree through the default
    /// <see cref="FileSystemBundleProvider"/> (spec §8).
    ///
    /// Throws only for I/O failures or a non-directory root. Per-file parse
    /// failures are recorded in <see cref="ParseErrors"/>.
    /// </summary>
    /// <exception cref="BundleLoadException"><paramref name="root"/> does not exist or is not a directory, or an I/O error occurred.</exception>
    public static Bundle Load(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new BundleLoadException($"bundle root is not a directory: {root}");
        }

        return Load(new FileSystemBundleProvider(root), null);
    }

    /// <summary>
    /// Loads a bundle through <paramref name="provider"/> (spec §1, §2, §6): lists the
    /// raw names, validates them as a tree under <paramref name="options"/>' policy,
    /// reads every <c>.md</c> object within the bounds, parses the concepts and builds
    /// the link graph. Permissive (§11): a rejected name, an oversized or unparseable
    /// concept goes to <see cref="ParseProblems"/>; only a provider failure or a global
    /// bound (entries, depth) throws.
    /// </summary>
    /// <exception cref="BundleLoadException">The provider failed, refused the access, or a global bound was exceeded.</exception>
    public static Bundle Load(IBundleProvider provider, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= BundleLoadOptions.Default;
        var limits = options.Limits;
        var validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);

        List<BundleListing> listing;
        BundleVersion version;
        try
        {
            listing = provider.Enumerate(null, limits).ToList();
            version = provider.GetVersion(limits);
        }
        catch (BundleProviderException e)
        {
            throw new BundleLoadException(e.Message);
        }
        catch (IOException e)
        {
            throw new BundleLoadException($"I/O error: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            throw new BundleLoadException($"I/O error: {e.Message}");
        }

        var parseProblems = new List<BundleParseError>();
        var tree = validator.ValidateTree(listing.Select(l => l.RawName));
        foreach (var rejection in tree.Rejected)
        {
            // Only a .md name is diagnosed (spec §2, enumeration scope); other names were never examined.
            if (MarkdownPaths.HasMarkdownExtension(rejection.RawName))
            {
                parseProblems.Add(new BundleParseError(rejection.RawName, null, rejection.Reason));
            }
        }

        var mdKeys = tree.Accepted.Where(k => k.IsMarkdown).ToList();
        mdKeys.Sort((a, b) => PathOrdering.CompareComponentWise(a.ToString(), b.ToString()));

        var concepts = new List<Concept>();
        var indexKeys = new List<BundleKey>();
        var logKeys = new List<BundleKey>();
        var objectVersions = new Dictionary<BundleKey, BundleObjectVersion>();

        foreach (var key in mdKeys)
        {
            switch (key.Name)
            {
                case IndexFilename:
                    indexKeys.Add(key);
                    break;
                case LogFilename:
                    logKeys.Add(key);
                    break;
                default:
                    {
                        string text;
                        try
                        {
                            text = ReadText(provider, key, limits, out var objectVersion);
                            objectVersions[key] = objectVersion;
                        }
                        catch (BundleObjectTooLargeException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, e.Message));
                            break;
                        }
                        catch (BundleProviderException e)
                        {
                            throw new BundleLoadException(e.Message);
                        }
                        catch (IOException e)
                        {
                            throw new BundleLoadException($"I/O error: {e.Message}");
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            throw new BundleLoadException($"I/O error: {e.Message}");
                        }
                        catch (System.Text.DecoderFallbackException)
                        {
                            throw new BundleLoadException("I/O error: stream did not contain valid UTF-8");
                        }

                        OkfDocument document;
                        try
                        {
                            document = OkfDocument.Parse(text);
                        }
                        catch (DocumentParseException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, e.Message));
                            break;
                        }

                        try
                        {
                            var id = ConceptId.FromKey(key);
                            concepts.Add(new Concept(id, BundleDisplayPath.Compose(provider.Location, key), document, key));
                        }
                        catch (ConceptIdException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, $"Missing required frontmatter keys: {e.Message}"));
                        }

                        break;
                    }
            }
        }

        var index = new Dictionary<ConceptId, int>();
        for (var i = 0; i < concepts.Count; i++)
        {
            index[concepts[i].Id] = i;
        }

        var (outbound, backlinks) = BuildGraph(concepts, index);
        var okfVersion = ComputeOkfVersion(provider, limits);

        return new Bundle(provider, options, validator, version, okfVersion, concepts, index, indexKeys, logKeys, parseProblems, objectVersions, outbound, backlinks);
    }

    /// <summary>Reads one object as strict UTF-8, counting bytes against the bound (spec §6) whatever the provider says.</summary>
    private static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, out BundleObjectVersion version)
    {
        using var obj = provider.Read(key);
        version = obj.Version;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = obj.Content.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > limits.MaxObjectBytes)
            {
                throw new BundleObjectTooLargeException(key, total, limits.MaxObjectBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return OkfEncodings.Strict.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
```

Replace `ComputeOkfVersion` (the instance method) with a static one reading through the provider; every failure, including a too-large or refused root index, means "no declared version", as today:

```csharp
    private static string? ComputeOkfVersion(IBundleProvider provider, BundleLimits limits)
    {
        var key = BundleKey.Parse(IndexFilename);
        string text;
        try
        {
            text = ReadText(provider, key, limits, out _);
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }

        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(text);
        }
        catch (DocumentParseException)
        {
            return null;
        }

        return doc.Frontmatter.Get("okf_version")?.AsDisplayString();
    }
```

Delete `CollectMarkdown` (the provider's walk replaces it). Add the new members next to `Root`:

```csharp
    /// <summary>The provider this bundle was loaded from (spec §1).</summary>
    public IBundleProvider Provider { get; }

    /// <summary>The provider's displayable location; <see cref="Root"/> is the same string.</summary>
    public string Location => Provider.Location;

    /// <summary>The options the bundle was loaded with.</summary>
    public BundleLoadOptions Options { get; }

    /// <summary>The provider's bundle token at load time (spec §5.1).</summary>
    public BundleVersion Version { get; }

    /// <summary>The keys of every <c>index.md</c>, in load order; <see cref="IndexFiles"/> holds their display paths.</summary>
    public IReadOnlyList<BundleKey> IndexKeys { get; }

    /// <summary>The keys of every <c>log.md</c>, in load order; <see cref="LogFiles"/> holds their display paths.</summary>
    public IReadOnlyList<BundleKey> LogKeys { get; }

    /// <summary>Every problem met while loading (spec §2): rejected names, oversized or unparseable concepts. <see cref="ParseErrors"/> is its display-path projection.</summary>
    public IReadOnlyList<BundleParseError> ParseProblems { get; }

    /// <summary>The version of every concept object read at load time (spec §5.1).</summary>
    public IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions { get; }

    private readonly BundleKeyValidator _validator;
```

Change `Root` to `public string Root => Provider.Location;` (same value as before for the filesystem: the root as given). Keep `IndexFiles`, `LogFiles`, `ParseErrors`, `Concepts`, `Count`, `IsEmpty`, `Get`, `Contains`, `LinksFrom`, `Backlinks`, `BrokenLinks`, `OkfVersion` unchanged. Keep `TryResolveResource` and `ReadResourceText(string)` **exactly as they are** (they read `Root`, which still is the filesystem root; lot D retires them). Add, after `ReadResourceText(string)`:

```csharp
    /// <summary>
    /// Resolves a §6.2 path-valued field to a key (spec §2): lexically, through
    /// <see cref="ResourceKeyResolver"/>, then the key policy, then the provider's
    /// <see cref="IBundleProvider.Exists"/>. Never throws (§11, permissive): a
    /// provider refusal reads as <see cref="ResourceResolutionStatus.Unsafe"/>.
    /// Always returns <see langword="true"/>, like <see cref="TryResolveResource"/>.
    /// </summary>
    public bool TryResolveResourceKey(Concept concept, string rawPath, out BundleKey? key, out ResourceResolutionStatus status)
    {
        ArgumentNullException.ThrowIfNull(concept);
        ArgumentNullException.ThrowIfNull(rawPath);
        var conceptKey = concept.Key ?? concept.Id.ToKey();
        switch (ResourceKeyResolver.Resolve(conceptKey, rawPath, out var candidate))
        {
            case ResourceKeyOutcome.Url:
                key = null;
                status = ResourceResolutionStatus.Url;
                return true;
            case ResourceKeyOutcome.Unsafe:
                key = null;
                status = ResourceResolutionStatus.Unsafe;
                return true;
        }

        if (_validator.CheckKey(candidate!) is not null)
        {
            key = null;
            status = ResourceResolutionStatus.Unsafe;
            return true;
        }

        key = candidate;
        try
        {
            status = Provider.Exists(candidate!) ? ResourceResolutionStatus.Resolved : ResourceResolutionStatus.Missing;
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            key = null;
            status = ResourceResolutionStatus.Unsafe;
        }

        return true;
    }

    /// <summary>
    /// Reads a resolved resource as strict UTF-8 through the provider, which applies
    /// the STRICT guards and the confinement boundary (spec §8). A refusal surfaces as
    /// <see cref="UnauthorizedAccessException"/> and a missing object as
    /// <see cref="FileNotFoundException"/>, so existing callers classify both as before.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The provider refused the access.</exception>
    /// <exception cref="IOException">The object is missing, too large, or cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    public string ReadResourceText(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            return ReadText(Provider, key, Options.Limits, out _);
        }
        catch (BundleAccessRefusedException e)
        {
            throw new UnauthorizedAccessException(e.Message, e);
        }
        catch (BundleObjectNotFoundException e)
        {
            throw new FileNotFoundException(e.Message, key.ToString(), e);
        }
        catch (BundleProviderException e)
        {
            throw new IOException(e.Message, e);
        }
    }
```

- [ ] **Step 7: Route `Validate.cs` through the provider**

In `ValidateReserved`, replace the two reads and the root test. The index loop becomes:

```csharp
        for (var i = 0; i < bundle.IndexKeys.Count; i++)
        {
            var key = bundle.IndexKeys[i];
            var path = bundle.IndexFiles[i];
            string text;
            try
            {
                text = bundle.ReadResourceText(key);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"index.md could not be read: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }
            // … unchanged parse and checks …
            var isRoot = key.Segments.Count == 1;
```

and delete the `rootIndex` local. The log loop likewise iterates `bundle.LogKeys` with `bundle.LogFiles[i]` as the display `path` and reads `bundle.ReadResourceText(key)`. In the resource existence check (`foreach (var resource in concept.Document.FrontmatterResources())`), replace `bundle.TryResolveResource(concept, resource.RawPath, out _, out var resolutionStatus);` with `bundle.TryResolveResourceKey(concept, resource.RawPath, out _, out var resolutionStatus);`. Replace `ConceptRelativeHint`'s body:

```csharp
    internal static string? ConceptRelativeHint(Bundle bundle, Concept concept, FrontmatterResource resource)
    {
        if (resource.Kind != FrontmatterResourceKind.BundleRelative
            || resource.RawPath.StartsWith('/') || resource.RawPath.StartsWith('\\'))
        {
            return null;
        }

        // Would the same bare path exist next to the concept? Ask the resolver for its
        // concept-relative form, under the same policy and through the same provider.
        bundle.TryResolveResourceKey(concept, "./" + resource.RawPath, out var siblingKey, out var siblingStatus);
        if (siblingStatus != ResourceResolutionStatus.Resolved || siblingKey is null)
        {
            return null;
        }

        return $" — a file exists at {siblingKey}; a bare path resolves from the bundle root (this implementation's reading of §6.2) — write ./{resource.RawPath} for the concept-relative form";
    }
```

`siblingKey.ToString()` is already `/`-joined, which is what the old `Replace('\\', '/')` produced; the hint's bytes do not change. After this, `Validate.cs` has no `File.` or `ReparsePoints.` reference left — `grep -n "File\.\|ReparsePoints\." src/OKF4net/Validate.cs` must print nothing.

Convert every touched `.cs` file to CRLF (new files) or verify it still is (modified files).

- [ ] **Step 8: Run the new tests, then the whole suite**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleProviderLoadTests"` → green.
Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` → green, **0 golden diff**. `ConceptSearchTests` still compiles (`new Concept(id, path, doc)` with the optional fourth parameter). If any golden comparison fails, STOP: do not regenerate, report the diff.

- [ ] **Step 9: Commit**

```bash
git add src/OKF4net/Providers/BundleLoadOptions.cs src/OKF4net/ConceptId.cs src/OKF4net/Bundle.cs src/OKF4net/Validate.cs tests/OKF4net.Tests/Providers/InMemoryBundleProvider.cs tests/OKF4net.Tests/Providers/BundleProviderLoadTests.cs
git commit -F - <<'MSG'
feat(core): load a bundle through an IBundleProvider; the validator reads through it

Bundle.Load(string) now delegates to the filesystem provider. Keys are
validated as a tree under the host's policy, objects are read within the
bounds, concept paths follow the display rule, and the historical members
(Root, IndexFiles, ParseErrors, TryResolveResource, ReadResourceText(string))
keep their values. Validate.cs no longer touches the disk directly.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 7: Pin the display rule where the goldens cannot, document the lot

**Files:**
- Test: `tests/OKF4net.Tests/CliDisplayPathTests.cs`
- Modify: `README.md` (one new subsection under the library's API section)
- Modify: `CHANGELOG.md` (`## [Unreleased]`)

**Interfaces:**
- Consumes: `OkfCli.Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)`; `TempDir`.
- Produces: nothing new in code.

- [ ] **Step 1: Write the non-normalised display test**

`GoldenParityTests` compares after `r.Out.Replace('\\', '/')`, so it cannot see a separator change (spec §8, §11). This test feeds the CLI an absolute Windows path and asserts the exact bytes of a concept path in `validate` and `info` output, with no normalisation. It is meaningful on Windows only; elsewhere it still runs (separators are `/` on both sides).

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Spec §8's display rule, pinned without the path-separator normalisation the
/// golden tests apply: on Windows a concept path must still be printed with
/// backslashes, byte for byte what <c>Path.Combine(root, …)</c> produced before
/// the provider seam.
/// </summary>
public class CliDisplayPathTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = OkfCli.Run(args, TextReader.Null, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Validate_and_info_print_concept_paths_with_the_os_separator_unnormalised()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/orders.md", "---\ntype: Table\ntitle: Orders\ndescription: d\nresource: missing.sql\n---\n");
        tmp.Write("tables/broken.md", "---\ntype: Note\nk: *a\n---\nbody\n"); // a YAML alias: the subset rejects it, so it lands in ParseErrors
        var expectedOrders = Path.Combine(tmp.Path, "tables", "orders.md");
        var expectedBroken = Path.Combine(tmp.Path, "tables", "broken.md");

        var validate = Run("validate", tmp.Path);
        Assert.Contains(expectedOrders + ": frontmatter path 'resource'", validate.Out);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains('\\', expectedOrders);
            Assert.DoesNotContain(tmp.Path.Replace('\\', '/') + "/tables/orders.md", validate.Out);
        }

        var info = Run("info", tmp.Path);
        Assert.Contains("unparseable files:\n  " + expectedBroken + ": ", info.Out);
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliDisplayPathTests"`
Expected: green on Windows and elsewhere. If it is red on Windows, the display rule is broken — fix `BundleDisplayPath`/`Bundle.Load`, never the test.

- [ ] **Step 3: README — one subsection**

Add under the library's API section (next to where `Bundle.Load` is introduced):

```markdown
### Loading through a provider

`Bundle.Load(string root)` is now an overload over the default
`FileSystemBundleProvider`. A host can load through any `IBundleProvider`
(`OKF4net.Providers`) — a key space of objects, read-only unless the provider
declares `Capabilities.CanWrite` — with `Bundle.Load(provider, new
BundleLoadOptions { Limits = …, KeyPolicy = … })`. Keys are validated as a tree
under the `Strict` policy by default (a bundle valid on one provider is valid on
all: no Windows reserved names, no trailing dot, no case-colliding siblings, no
file that is also a directory); `Native` keeps only the provider's own
constraints. Default bounds: 16 MiB per object, 1 000 000 entries, depth 64.
Every rejected `.md` name is a `Bundle.ParseProblems` entry (and a
`ParseErrors` line); other rejected names are skipped silently. In this
release the filesystem provider is read-only and no other provider ships; the
write side and the first remote providers are tracked in #139.
```

- [ ] **Step 4: CHANGELOG**

Under `## [Unreleased]`:

```markdown
### Added

- **Bundle provider seam (#139, lot A).** `OKF4net.Providers`: `IBundleProvider`
  and its value types, `BundleKey` with three-layer validation
  (`BundleKeyConstraints`, `BundleKeyPolicy`, `BundleKeyValidator`),
  `FileSystemBundleProvider` (read-only in this release),
  `ResourceKeyResolver`, `BundleLoadOptions`, `Bundle.Load(IBundleProvider,
  BundleLoadOptions?)`, and on `Bundle`: `Provider`, `Location`, `Version`,
  `ParseProblems`, `IndexKeys`/`LogKeys`, `ObjectVersions`,
  `TryResolveResourceKey`, `ReadResourceText(BundleKey)`; `Concept.Key`;
  `ConceptId.ToKey`/`FromKey`. Every existing member keeps its value.

### Changed

- **Finite default bounds on loading** (spec §6): a concept over 16 MiB is a
  `ParseErrors` entry instead of being loaded; a listing over 1 000 000 entries
  (every entry counted, `.git`/`node_modules` included) or deeper than 64
  segments aborts the load with `BundleLoadException`.
- **Key policy `Strict` by default** (spec §2): a `.md` whose name a known
  filesystem would alias — a Windows reserved name (`con.md`, `com¹.md`), a
  trailing dot or space, a sibling differing only by case or Unicode form, a
  file that is also a directory — is rejected into `ParseErrors`; a referenced
  resource with such a name resolves as `Unsafe`. Other files were never
  examined and still are not. Pass `BundleKeyPolicy.Native` to keep only the
  host filesystem's own rules.
- **Exact-case resource resolution** on case-folding volumes (Windows, macOS):
  `references/a.sql` no longer resolves `References/A.sql`; it is `Missing`.
- **A backslash in a §6.2 path-valued field is `Unsafe`** on every OS;
  `references\a.sql` resolved on Windows before.
- **NFC under invariant globalization**: in the Native AOT `okf`/`okf-render`
  binaries, `string.Normalize` is a no-op, so `Strict` refuses non-ASCII names
  there (with a reason saying why) instead of pretending they are NFC.
```

- [ ] **Step 5: Commit**

```bash
git add tests/OKF4net.Tests/CliDisplayPathTests.cs README.md CHANGELOG.md
git commit -F - <<'MSG'
test(cli): pin the display rule unnormalised; document lot A

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

### Task 8: Lot verification, the AOT fail-closed probe, and `CLAUDE.md`

**Files:**
- Modify: `CLAUDE.md` (the lenient-site count; a `Providers/` architecture bullet)
- Fixes only if verification finds something.

- [ ] **Step 1: `CLAUDE.md`** — in the `Internal/ReparsePoints.cs` bullet, replace `three walks (`Bundle.cs:500`, the load walk `CollectMarkdown`, and the two `IndexGenerator` walks)` with `three walks (`Providers/FileSystemBundleProvider.Walk`, the load walk that replaced `Bundle.CollectMarkdown`, and the two `IndexGenerator` walks)`, and keep the sentence about `TryResolveResource` (it is unchanged in this lot). Add a bullet after the `Internal/AuditText.cs` one:

```markdown
  - `Providers/` — the storage seam under `Bundle` (design: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, lot A). `IBundleProvider` is a key space of byte objects that knows nothing of concepts; the core validates every raw name as a **tree** (`BundleKeyValidator`: base rules, the provider's `NativeConstraints`, the host's `BundleKeyPolicy` — `Strict` by default and never below `Native`), enforces the `BundleLimits` by counting bytes itself, resolves §6.2 paths lexically on keys (`ResourceKeyResolver`, never dereferencing a URL), and keeps the historical members (`Concept.Path`, `Bundle.Root`, `ParseErrors`) as projections of the new ones by the display rule (`BundleDisplayPath`: location + OS separator + segments, byte-identical to the old `Path.Combine`). `FileSystemBundleProvider` is the default and read-only until lot B; with a confinement boundary it re-checks the chain boundary→root STRICTly before **every** access, because the ancestor walk stops before the root. Under `InvariantGlobalization` `string.Normalize` is a silent no-op, so `BundleKeyValidator.NormalizationAvailable` fails NFC closed (non-ASCII refused with a reason) rather than pretending.
```

- [ ] **Step 2: Full runs, with numbers**

```bash
dotnet build OKF4net.sln                                   # 0 warnings, 0 errors
dotnet format OKF4net.sln --verify-no-changes
for i in 1 2 3; do dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"; done   # 3× green, same counts
dotnet test producers/OkfProducer.sln                      # green (references src/OKF4net; Concept's 4th param is optional)
dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleLoadPerformanceTests"              # still under its ceiling; note the load figures in the PR
```

On Windows, additionally run the `Providers` tests as an account that can create symlinks and junctions and confirm **0 skipped** at least once: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers"`.

- [ ] **Step 3: `okf validate` on every sample bundle is byte-identical to `origin/dev`**

```bash
git worktree add ../okf-dev-ref origin/dev
for b in bundles/acme_retail bundles/ga4 bundles/attestation_containers_demo bundles/meridian_transit; do
  dotnet run --project src/OKF4net.Cli -- validate $b > /tmp/new-$(basename $b).txt 2>&1
  (cd ../okf-dev-ref && dotnet run --project src/OKF4net.Cli -- validate ../$(basename $PWD)/$b > /tmp/old-$(basename $b).txt 2>&1)
  diff /tmp/old-$(basename $b).txt /tmp/new-$(basename $b).txt && echo "$b identical"
done
git worktree remove ../okf-dev-ref
```

Expected: four `identical`. (The `bundles/` paths contain no name `Strict` rejects — verified on 146 entries during the spec review — so this is a check, not a hope.)

- [ ] **Step 4: AOT publish and the invariant-globalization probe**

```bash
dotnet publish src/OKF4net.Cli -c Release      # Native AOT, InvariantGlobalization
dotnet publish src/OKF4net.Render -c Release
```

Then, with the published `okf` (`src/OKF4net.Cli/bin/Release/net10.0/<rid>/publish/okf`), build a bundle whose concept name is NFD and run `info`:

```bash
mkdir -p /tmp/nfd-probe && printf -- '---\ntype: T\ntitle: t\ndescription: d\n---\n' > "/tmp/nfd-probe/cafe$(printf '\xcc\x81').md"
./okf info /tmp/nfd-probe
```

Expected: `unparseable files:` lists the file with a reason containing `normalization is unavailable` — the fail-closed branch of `BundleKeyValidator.CheckSegment` reached for real, in the binary where `Normalize` is a no-op. The same `info` through `dotnet run` (ICU available) must instead say `is not in Unicode NFC`. Record both outputs in the PR description. If the AOT binary lists the concept as loaded, the probe `NormalizationAvailable` is wrong — fix it, never the expectation.

- [ ] **Step 5: `CHANGELOG.md` `[Unreleased]` has the lot's entries once; `grep -rn "CollectMarkdown" src/OKF4net/Bundle.cs` prints nothing; `grep -n "File\.\|Directory\." src/OKF4net/Bundle.cs src/OKF4net/Validate.cs` prints only `Directory.Exists` in `Bundle.Load(string)`.**

- [ ] **Step 6: Commit**

```bash
git add CLAUDE.md
git commit -F - <<'MSG'
docs(claude): record the provider seam and the lenient-walk site it replaces

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
MSG
```

Then open the PR against `dev` with the verification numbers, the four `identical` lines, both NFD probe outputs, and the "Decisions taken by this plan" list for the maintainer to confirm.
