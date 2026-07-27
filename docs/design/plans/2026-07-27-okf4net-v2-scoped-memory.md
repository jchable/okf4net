# OKF4net V2 — Scoped Memory (Lot 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `OkfContextProvider` capture conversational memory on a multi-user deployment without cross-scope leakage, by modelling memory as catalog `role:memory` sources, promoting the atomic bundle-write primitive into core, and evolving the provider into a scope-aware knowledge∪memory adapter.

**Architecture:** Promote the atomic read-modify-write append-to-concept + the process-wide per-path lock registry from `OKF4net.Agents.OkfBundleTools` into core `OKF4net` (`BundleConceptWriter`) so both `OkfBundleTools` and a new `FileMemoryStore` reuse one write path. Add the scoped-memory contracts (`KnowledgeAccessScope`, `SourceRole.Memory` + `MemoryTier`, `MemoryPath.For`, `IMemoryStore`) to `OKF4net.Catalog`, implement the **user tier** in `FileMemoryStore`, and evolve `OkfContextProvider` (in `OKF4net.Agents`, which gains a reference to `OKF4net.Catalog`) to read knowledge (resolver) ∪ memory (store) under a split token budget and capture to one tier via the store — never throwing toward the pipeline, always injecting as message data (never `AIContext.Instructions`).

**Tech Stack:** .NET 10 / C# 14, xUnit. `OKF4net` (BCL only), `OKF4net.Catalog` (BCL + `OKF4net`), `OKF4net.Agents` (`Microsoft.Agents.AI` + `OKF4net` + **new**: `OKF4net.Catalog`), `OKF4net.Catalog.Hosting` (`Microsoft.Extensions.DependencyInjection.Abstractions` + `OKF4net.Catalog`).

## Global Constraints

- **Target framework:** `net10.0`. Requires .NET SDK 10.0+.
- **Zero third-party runtime dependencies, per project.** `OKF4net`, `OKF4net.Cli`, `OKF4net.Catalog`: BCL only (no `PackageReference`). `OKF4net.Agents`: `Microsoft.Agents.AI` only — **plus this lot adds a `ProjectReference` to `OKF4net.Catalog`** (the one new dependency edge; graph stays acyclic: `Agents → Catalog → OKF4net`, `Agents → Microsoft.Agents.AI`). `OKF4net.Catalog.Hosting`: `Microsoft.Extensions.DependencyInjection.Abstractions` only. Test-only packages (xunit, etc.) are fine everywhere.
- **`OKF4net.Catalog` stays BCL + core only** — it must NOT reference `Microsoft.Agents.AI` or `Microsoft.Extensions.*`.
- **`TreatWarningsAsErrors`** is on (Directory.Build.props). Warnings fail the build.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces; XML doc comments on all public API; nullable enabled; LangVersion 14 (all enforced).
- **Never touch `tests/fixtures/`** — byte-exact golden captures (LF, significant trailing whitespace). If C# output differs from a golden, the C# is wrong.
- Errors are **data, never expected exceptions** (mirror the `RunTool` / catalog `errors-as-data` philosophy): the manifest parser never throws; `IMemoryStore` operations never throw for a data condition; the provider never throws toward the invocation pipeline.
- Comparisons use `StringComparison.Ordinal` (or `OrdinalIgnoreCase` only where an existing seam already does — path canonicalization, tag match). Generated text uses `"\n"`. Bundle files are UTF-8 no BOM on write (`OkfEncodings.NoBom`), strict UTF-8 on read (`OkfEncodings.Strict`).
- **Verification commands (run after every task):**
  - `dotnet build OKF4net.sln`
  - `dotnet test OKF4net.sln`
  - `dotnet format OKF4net.sln --verify-no-changes`
- Every commit message uses Conventional Commits and ends with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

## Task Group 1 — Promote the core write primitive (spec §4.1, §11.1)

Extract the atomic read-modify-write append-to-concept + the process-wide per-path lock registry from `OKF4net.Agents.OkfBundleTools` into core `OKF4net` as a public `BundleConceptWriter`, and refactor `OkfBundleTools` to consume it with **behaviour unchanged** (every existing test stays green). This unblocks `FileMemoryStore` (Catalog) reusing it.

### Task 1.1 — Core `BundleConceptWriter` + refactor `OkfBundleTools` onto it

**Files:**
- Create: `src/OKF4net/BundleConceptWriter.cs`
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (delete the promoted machinery; delegate to the writer)
- Test: `tests/OKF4net.Tests/BundleConceptWriterTests.cs` (new, direct core tests)
- Parity (must stay green, unchanged): `tests/OKF4net.Tests/Agents/OkfWriteToolsTests.cs`, `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs`, `tests/OKF4net.Tests/Agents/OkfContextProviderMemoryTests.cs`

**Interfaces:**
- Consumes (existing core, all in `OKF4net` / `OKF4net.Internal`): `ConceptId.TryParse`, `ConceptId.ToPath`, `OkfDocument.Parse`/`.Validate()`/`.Serialize()`, `Frontmatter.FromMapping`/`.AsMapping().ToYamlString()`, `Yaml.YamlValue.Parse`, `Yaml.YamlMapping`, `Yaml.YamlNull`, `Internal.ReparsePoints.IsReparsePoint`/`.HasReparsePointAncestor`/`.IsWithin`, `Internal.OkfEncodings.Strict`/`.NoBom`, `OkfException`.
- Produces (core, public, BCL-only):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// The core, thread-safe write primitive for OKF bundles: producer-validated,
/// reparse-guarded, atomically-serialized create/update of a concept and an
/// atomic read-modify-write append-to-concept, over a single bundle root.
/// Promoted verbatim from <c>OKF4net.Agents.OkfBundleTools</c> so both that
/// type and <c>OKF4net.Catalog.FileMemoryStore</c> share one write path and one
/// process-wide per-path lock registry (no duplicate lock registry, no divergent
/// second write path). Never throws for an expected error — I/O, YAML,
/// validation, and reparse-point rejections are returned as an
/// <c>Error: ...</c> result string.
/// </summary>
public sealed class BundleConceptWriter
{
    /// <summary>Creates a writer rooted at <paramref name="bundleRoot"/>.</summary>
    /// <param name="bundleRoot">The bundle's root directory.</param>
    /// <param name="onWriteCommitted">
    /// Optional callback invoked inside the write lock immediately after a
    /// successful file write, before the lock is released — the seam
    /// <c>OkfBundleTools</c> uses to invalidate its bundle cache atomically with
    /// the write. <see langword="null"/> (the default) for callers with no cache.
    /// </param>
    public BundleConceptWriter(string bundleRoot, Action? onWriteCommitted = null);

    /// <summary>The bundle root, as passed to the constructor.</summary>
    public string BundleRoot { get; }

    /// <summary>Creates or updates one concept (producer-validated before any write).</summary>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body);

    /// <summary>Atomic read-modify-write append-to-concept under the shared per-path lock.</summary>
    public string AppendToConceptAtomic(string conceptId, string frontmatterYamlIfCreating, Func<string?, string> buildBody);

    /// <summary>
    /// The shared per-path lock object for this bundle root, obtained from the
    /// process-wide registry keyed by the canonicalized root. Exposed so a
    /// co-located caller (<c>OkfBundleTools.AppendLog</c>/<c>RegenerateIndexes</c>/
    /// cache access) can serialize its own read-modify-write sequences against
    /// this writer's writes.
    /// </summary>
    internal object WriteLock { get; }

    /// <summary>Test-only hook fired immediately before the late reparse re-check (always null outside tests).</summary>
    internal Action? BeforeLateReparseCheckForTest { get; set; }
}
```

- [ ] **Step 1: Write the failing core test**

Create `tests/OKF4net.Tests/BundleConceptWriterTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Direct tests for the promoted core write primitive
/// <see cref="BundleConceptWriter"/>. The exhaustive tool-surface parity is
/// still carried by the existing OkfWriteToolsTests / OkfBundleToolsTests /
/// OkfContextProviderMemoryTests suites (which now run over the same primitive
/// via OkfBundleTools); these assert the primitive directly.
/// </summary>
public class BundleConceptWriterTests
{
    private const string ValidFrontmatter =
        "type: BigQuery Table\n"
        + "title: Refunds\n"
        + "description: One row per refund.\n"
        + "timestamp: 2026-07-22T00:00:00Z\n";

    [Fact]
    public void WriteConcept_creates_a_validated_file()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);

        var result = writer.WriteConcept("tables/refunds", ValidFrontmatter, "# Refunds\n\nBody.\n");

        Assert.Contains("Written", result);
        var path = Path.Combine(tmp.Path, "tables", "refunds.md");
        Assert.True(File.Exists(path));
        OkfDocument.Parse(File.ReadAllText(path)).Validate();
    }

    [Fact]
    public void WriteConcept_missing_required_frontmatter_writes_nothing()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);

        var result = writer.WriteConcept("tables/refunds", "type: X\n", "# body\n");

        Assert.StartsWith("Error:", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "refunds.md")));
    }

    [Fact]
    public void AppendToConceptAtomic_creates_then_appends()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);

        var r1 = writer.AppendToConceptAtomic("memory/2026-07-24", ValidFrontmatter, cur => cur is null ? "first\n" : cur + "second\n");
        var r2 = writer.AppendToConceptAtomic("memory/2026-07-24", ValidFrontmatter, cur => cur is null ? "first\n" : cur.TrimEnd('\n') + "\n\nsecond\n");

        Assert.StartsWith("Written", r1);
        Assert.StartsWith("Written", r2);
        var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "memory", "2026-07-24.md"))).Body;
        Assert.Contains("first", body, StringComparison.Ordinal);
        Assert.Contains("second", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWriteCommitted_fires_after_a_successful_write()
    {
        using var tmp = new TempDir();
        var fired = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () => fired++);

        writer.WriteConcept("a/b", ValidFrontmatter, "# body\n");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Two_writers_over_the_same_root_share_one_lock_and_never_lose_an_append()
    {
        using var tmp = new TempDir();
        var writerA = new BundleConceptWriter(tmp.Path);
        var writerB = new BundleConceptWriter(tmp.Path + Path.DirectorySeparatorChar); // different spelling, same canonical root
        const int iterations = 16;

        Parallel.For(0, iterations, i =>
        {
            var w = i % 2 == 0 ? writerA : writerB;
            w.AppendToConceptAtomic(
                "memory/day",
                ValidFrontmatter,
                cur => (cur is null ? string.Empty : cur.TrimEnd('\n') + "\n") + $"line {i}\n");
        });

        var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "memory", "day.md"))).Body;
        for (var i = 0; i < iterations; i++)
        {
            Assert.Contains($"line {i}", body, StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterTests"`
Expected: FAIL — `BundleConceptWriter` does not exist (compile error `CS0246`).

- [ ] **Step 3: Create the core primitive**

Create `src/OKF4net/BundleConceptWriter.cs`. Move — **byte-for-byte** where the logic is identical — the following members out of `OkfBundleTools` and into this class, adapting names/visibility:
- the static `BundleLocks` registry (`ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase)`), the canonicalization (`Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot))`), and the `_bundleLock` field (renamed to back `WriteLock`);
- `AppendToConceptAtomic`, `ConceptTarget`, `ValidateConceptTarget`, `BuildValidatedContent`, `LateReparseGuard`, `BeforeLateReparseCheckForTest`, `WriteValidatedContentLocked`;
- the reparse helpers `IsWithinBundleRoot` / `HasReparsePointAncestor` (private static; the writer calls `ReparsePoints` directly — same assembly);
- a new public `WriteConcept(conceptId, frontmatterYaml, body)` that performs the exact null/blank/NUL guards and `RunTool` wrapping `OkfBundleTools.WriteConcept` did inline, then the `lock (WriteLock) { WriteValidatedContentLocked(...) }` call;
- a private `RunTool(Func<string>)` catch-all identical to `OkfBundleTools.RunTool`.

The only behavioural change from the promoted code: `WriteValidatedContentLocked` replaces its `_bundle = null;` line with `_onWriteCommitted?.Invoke();` (invoked inside the lock, right after `File.WriteAllText`).

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Concurrent;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net;

public sealed class BundleConceptWriter
{
    private static readonly ConcurrentDictionary<string, object> BundleLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _bundleLock;
    private readonly Action? _onWriteCommitted;

    /// <inheritdoc cref="BundleConceptWriter(string, Action?)"/>
    public BundleConceptWriter(string bundleRoot, Action? onWriteCommitted = null)
    {
        ArgumentNullException.ThrowIfNull(bundleRoot);
        BundleRoot = bundleRoot;
        _onWriteCommitted = onWriteCommitted;
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        _bundleLock = BundleLocks.GetOrAdd(canonicalRoot, static _ => new object());
    }

    /// <summary>The bundle root, as passed to the constructor.</summary>
    public string BundleRoot { get; }

    internal object WriteLock => _bundleLock;

    internal Action? BeforeLateReparseCheckForTest { get; set; }

    /// <summary>Creates or updates one concept document (producer-validated before any write). Never throws for an expected error.</summary>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            var (content, buildError) = BuildValidatedContent(frontmatterYaml, body);
            if (buildError is not null)
            {
                return buildError;
            }

            lock (_bundleLock)
            {
                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }

    // AppendToConceptAtomic, ConceptTarget, ValidateConceptTarget,
    // BuildValidatedContent, LateReparseGuard, WriteValidatedContentLocked,
    // RunTool, IsWithinBundleRoot, HasReparsePointAncestor:
    // moved verbatim from OkfBundleTools (see Step 3 checklist above), with the
    // single change that WriteValidatedContentLocked calls
    // `_onWriteCommitted?.Invoke();` where it previously set `_bundle = null;`.
}
```

- [ ] **Step 4: Refactor `OkfBundleTools` to consume the writer**

In `src/OKF4net.Agents/OkfBundleTools.cs`:
- Delete the moved members (`BundleLocks`, `AppendToConceptAtomic`, `ConceptTarget`, `ValidateConceptTarget`, `BuildValidatedContent`, `LateReparseGuard`, `WriteValidatedContentLocked`). Keep `IsWithinBundleRoot`/`HasReparsePointAncestor` (still used by `Browse`) and `RunTool` (still used by the read tools/`AppendLog`).
- Replace the `_bundleLock` field with `private readonly BundleConceptWriter _writer;` and `private readonly object _bundleLock;`. In the constructor:

```csharp
_writer = new BundleConceptWriter(bundleRoot, onWriteCommitted: () => _bundle = null);
_bundleLock = _writer.WriteLock;
```

- `WriteConcept(...)` becomes a thin delegate: `public string WriteConcept(string conceptId, string frontmatterYaml, string body) => _writer.WriteConcept(conceptId, frontmatterYaml, body);` (drop the local guards/`RunTool` — the writer performs the identical guards and returns the identical strings).
- Restore the internal `AppendToConceptAtomic` seam as a delegate so `OkfContextProvider` (V1 path) keeps compiling:
  `internal string AppendToConceptAtomic(string conceptId, string frontmatterYamlIfCreating, Func<string?, string> buildBody) => _writer.AppendToConceptAtomic(conceptId, frontmatterYamlIfCreating, buildBody);`
- Forward the test hook so `OkfWriteToolsTests` keeps setting `tools.BeforeLateReparseCheckForTest`:

```csharp
private Action? _beforeLateReparseCheckForTest;

internal Action? BeforeLateReparseCheckForTest
{
    get => _beforeLateReparseCheckForTest;
    set
    {
        _beforeLateReparseCheckForTest = value; // still consulted by AppendLog's own late re-check
        _writer.BeforeLateReparseCheckForTest = value;
    }
}
```

- `AppendLog` / `RegenerateIndexes` / `GetBundle` / `InvalidateBundle` are unchanged — they still `lock (_bundleLock)`, which is now the writer's shared lock object, so their serialization against writes is preserved. `AppendLog`'s own inline `BeforeLateReparseCheckForTest?.Invoke()` now reads `_beforeLateReparseCheckForTest`.

- [ ] **Step 5: Run the new core tests to confirm they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the parity suites to confirm behaviour is unchanged**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfWriteToolsTests|FullyQualifiedName~OkfBundleToolsTests|FullyQualifiedName~OkfContextProviderMemoryTests"`
Expected: PASS (all — including the E2 and two-instance concurrency tests, which now exercise the promoted primitive).

- [ ] **Step 7: Full build + test + format**

Run: `dotnet build OKF4net.sln && dotnet test OKF4net.sln && dotnet format OKF4net.sln --verify-no-changes`
Expected: all green, no format diffs.

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net/BundleConceptWriter.cs src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/BundleConceptWriterTests.cs
git commit -m "$(cat <<'EOF'
refactor: promote atomic concept-write primitive into core OKF4net

Extract the read-modify-write append-to-concept, the producer-validated
write, and the process-wide per-path lock registry from OkfBundleTools
into a public core BundleConceptWriter. OkfBundleTools now delegates to
it (behaviour unchanged, all existing tests green), unblocking Catalog's
FileMemoryStore reusing one write path and one lock registry.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task Group 2 — Contracts (spec §5, §11.2)

Add the scoped-memory contracts to `OKF4net.Catalog` (BCL + core only): `KnowledgeAccessScope`, `MemoryTier`, `SourceRole.Memory` + manifest `tier` parsing, `MemoryPath.For`, and the `IMemoryStore` interface with its result/entry types. No behaviour is wired into the resolver — `IKnowledgeResolver` stays read-only and unchanged.

### Task 2.1 — `KnowledgeAccessScope`

**Files:**
- Create: `src/OKF4net.Catalog/KnowledgeAccessScope.cs`
- Test: `tests/OKF4net.Tests/Catalog/KnowledgeAccessScopeTests.cs`

**Interfaces:**
- Consumes: `OKF4net.ConceptId.ValidateSegment(string)` (throws `OKF4net.ConceptIdException`).
- Produces:

```csharp
public sealed class KnowledgeAccessScope
{
    public KnowledgeAccessScope(string? tenantId = null, string? userId = null, string? sessionId = null);
    public string? TenantId { get; }
    public string? UserId { get; }
    public string? SessionId { get; }
    public bool IsLocal { get; }               // all three null
    public static KnowledgeAccessScope Local { get; }
}
```

- [ ] **Step 1: Write the failing test**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

public class KnowledgeAccessScopeTests
{
    [Fact]
    public void All_null_is_local()
    {
        var scope = new KnowledgeAccessScope();
        Assert.True(scope.IsLocal);
        Assert.True(KnowledgeAccessScope.Local.IsLocal);
    }

    [Fact]
    public void Non_null_segments_are_kept()
    {
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice", sessionId: "s1");
        Assert.False(scope.IsLocal);
        Assert.Equal("acme", scope.TenantId);
        Assert.Equal("alice", scope.UserId);
        Assert.Equal("s1", scope.SessionId);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("")]
    public void Invalid_segment_is_rejected(string bad)
    {
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(tenantId: bad));
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(userId: bad));
        Assert.Throws<ArgumentException>(() => new KnowledgeAccessScope(sessionId: bad));
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~KnowledgeAccessScopeTests"`
Expected: FAIL — `KnowledgeAccessScope` does not exist (`CS0246`).

- [ ] **Step 3: Write the implementation**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// An immutable, host-authenticated access scope: opaque tenant/user/session
/// identifiers, each validated via <see cref="OKF4net.ConceptId.ValidateSegment"/>
/// so a scope is a path-safe key by construction. All-null is the degenerate
/// "local" (desktop/CLI) single-scope case. Never derived from a message.
/// </summary>
public sealed class KnowledgeAccessScope
{
    /// <summary>The shared all-null "local" scope.</summary>
    public static KnowledgeAccessScope Local { get; } = new();

    /// <summary>Creates a scope, validating every non-null segment.</summary>
    /// <exception cref="ArgumentException">A non-null segment is not a valid concept-id segment.</exception>
    public KnowledgeAccessScope(string? tenantId = null, string? userId = null, string? sessionId = null)
    {
        TenantId = Validate(tenantId, nameof(tenantId));
        UserId = Validate(userId, nameof(userId));
        SessionId = Validate(sessionId, nameof(sessionId));
    }

    /// <summary>The tenant identifier, or <see langword="null"/>.</summary>
    public string? TenantId { get; }

    /// <summary>The user identifier, or <see langword="null"/>.</summary>
    public string? UserId { get; }

    /// <summary>The session identifier, or <see langword="null"/>.</summary>
    public string? SessionId { get; }

    /// <summary><see langword="true"/> when every segment is <see langword="null"/> (the "local" case).</summary>
    public bool IsLocal => TenantId is null && UserId is null && SessionId is null;

    private static string? Validate(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            OKF4net.ConceptId.ValidateSegment(value);
        }
        catch (OKF4net.ConceptIdException ex)
        {
            throw new ArgumentException($"{paramName} must be a valid concept-id segment: {ex.Message}", paramName, ex);
        }

        return value;
    }
}
```

- [ ] **Step 4: Run it to confirm it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~KnowledgeAccessScopeTests"`
Expected: PASS (3 test cases + theory rows).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog/KnowledgeAccessScope.cs tests/OKF4net.Tests/Catalog/KnowledgeAccessScopeTests.cs
git commit -m "$(cat <<'EOF'
feat: add KnowledgeAccessScope contract to OKF4net.Catalog

Immutable {TenantId?, UserId?, SessionId?}, each validated via
ConceptId.ValidateSegment; all-null is the degenerate "local" scope.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

### Task 2.2 — `MemoryTier`, `SourceRole.Memory`, manifest `tier` parsing + diagnostics

**Files:**
- Create: `src/OKF4net.Catalog/MemoryTier.cs`
- Modify: `src/OKF4net.Catalog/SourceRole.cs` (add `Memory`)
- Modify: `src/OKF4net.Catalog/KnowledgeCatalogSource.cs` (add `MemoryTier? Tier`)
- Modify: `src/OKF4net.Catalog/CatalogDiagnosticCode.cs` (add `IllegalTier`, `DuplicateMemoryTier`)
- Modify: `src/OKF4net.Catalog/CatalogManifestParser.cs` (parse `role:memory` + `tier`; enforce one source per tier)
- Test: `tests/OKF4net.Tests/Catalog/CatalogManifestTests.cs` (extend)

**Interfaces:**
- Consumes: `System.Text.Json` (already used by the parser), `CatalogDiagnostic`.
- Produces:

```csharp
public enum MemoryTier { Session, User, Tenant }

// SourceRole gains: Memory
// KnowledgeCatalogSource gains a trailing: MemoryTier? Tier
// CatalogDiagnosticCode gains: IllegalTier, DuplicateMemoryTier
```

> **Resolved ambiguity (per-tier uniqueness):** the spec says "one memory source per tier" but does not name where it is enforced. This plan enforces it at **parse time** (consistent with the existing `DuplicateSourceId` rule), via the new `DuplicateMemoryTier` code, so a manifest with two `role:memory` sources of the same tier is rejected as data. A `tier` on a non-memory source, and a `role:memory` source with a missing/unrecognized `tier`, are both rejected via the single new `IllegalTier` code.

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/Catalog/CatalogManifestTests.cs` (the class already exists; add these facts). Note: the existing `Rejects_role_other_than_knowledge` fact currently asserts `role:"memory"` is `IllegalRole` — **update it** to a still-illegal role string so it does not clash with the now-legal `memory` role:

```csharp
    // ---- role:memory + tier (V2) ----------------------------------------

    [Fact]
    public void Accepts_role_memory_with_valid_tier()
    {
        const string json = """
            {
              "version": 1,
              "sources": [ { "id": "user-mem", "path": "./mem/user", "role": "memory", "tier": "user" } ]
            }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);
        var source = Assert.Single(snapshot!.Sources);
        Assert.Equal(SourceRole.Memory, source.Role);
        Assert.Equal(MemoryTier.User, source.Tier);
    }

    [Theory]
    [InlineData("session", MemoryTier.Session)]
    [InlineData("user", MemoryTier.User)]
    [InlineData("tenant", MemoryTier.Tenant)]
    public void Accepts_every_memory_tier(string tier, MemoryTier expected)
    {
        var json = $$"""
            { "version": 1, "sources": [ { "id": "m", "path": "./m", "role": "memory", "tier": "{{tier}}" } ] }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(expected, Assert.Single(snapshot!.Sources).Tier);
    }

    [Fact]
    public void Rejects_role_memory_without_a_tier()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "m", "path": "./m", "role": "memory" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.IllegalTier);
    }

    [Fact]
    public void Rejects_role_memory_with_an_unknown_tier()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "m", "path": "./m", "role": "memory", "tier": "global" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.IllegalTier);
    }

    [Fact]
    public void Rejects_tier_on_a_non_memory_source()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "d", "path": "./d", "role": "knowledge", "tier": "user" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.IllegalTier);
    }

    [Fact]
    public void Rejects_two_memory_sources_of_the_same_tier()
    {
        const string json = """
            {
              "version": 1,
              "sources": [
                { "id": "u1", "path": "./u1", "role": "memory", "tier": "user" },
                { "id": "u2", "path": "./u2", "role": "memory", "tier": "user" }
              ]
            }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.DuplicateMemoryTier);
    }

    [Fact]
    public void Accepts_up_to_three_memory_sources_one_per_tier_alongside_knowledge()
    {
        const string json = """
            {
              "version": 1,
              "sources": [
                { "id": "docs", "path": "./docs" },
                { "id": "sess", "path": "./m/s", "role": "memory", "tier": "session" },
                { "id": "usr",  "path": "./m/u", "role": "memory", "tier": "user" },
                { "id": "ten",  "path": "./m/t", "role": "memory", "tier": "tenant" }
              ]
            }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(4, snapshot!.Sources.Count);
    }
```

Replace the body of the pre-existing `Rejects_role_other_than_knowledge` fact so its illegal role is no longer `memory`:

```csharp
    [Fact]
    public void Rejects_role_other_than_knowledge_or_memory()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs", "role": "audit" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.IllegalRole);
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CatalogManifestTests"`
Expected: FAIL — `MemoryTier`/`SourceRole.Memory`/`Tier`/`IllegalTier`/`DuplicateMemoryTier` do not exist, and the new accept/reject facts fail.

- [ ] **Step 3: Add the enum, the diagnostic codes, and the source field**

Create `src/OKF4net.Catalog/MemoryTier.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// The tier a <see cref="SourceRole.Memory"/> catalog source stores memory at.
/// Session and tenant tiers are recognized by the manifest parser this lot;
/// only the user tier's storage is implemented (see <c>FileMemoryStore</c>).
/// </summary>
public enum MemoryTier
{
    /// <summary>Per-session memory (contract only this lot; storage staged).</summary>
    Session,

    /// <summary>Per-user memory (durable; implemented this lot).</summary>
    User,

    /// <summary>Per-tenant memory (contract only this lot; storage staged).</summary>
    Tenant,
}
```

In `src/OKF4net.Catalog/SourceRole.cs`, add `Memory` (and update the remarks):

```csharp
    /// <summary>An ordinary read-only knowledge bundle source.</summary>
    Knowledge,

    /// <summary>
    /// A scoped read+write memory source. Requires a <see cref="MemoryTier"/>
    /// (<c>tier</c> in the manifest); not searched by <see cref="IKnowledgeResolver"/>;
    /// fed to <see cref="IMemoryStore"/> instead.
    /// </summary>
    Memory,
```

In `src/OKF4net.Catalog/CatalogDiagnosticCode.cs`, add two members (with XML docs):

```csharp
    /// <summary>A <c>role:"memory"</c> source is missing a valid <c>tier</c>, or a non-memory source carries a <c>tier</c>.</summary>
    IllegalTier,

    /// <summary>Two or more <c>role:"memory"</c> sources declare the same <c>tier</c> (one memory source per tier is allowed).</summary>
    DuplicateMemoryTier,
```

In `src/OKF4net.Catalog/KnowledgeCatalogSource.cs`, add a trailing `Tier` parameter and its XML doc:

```csharp
/// <param name="Tier">
/// The memory tier, for a <see cref="SourceRole.Memory"/> source; <see langword="null"/>
/// for a <see cref="SourceRole.Knowledge"/> source.
/// </param>
public sealed record KnowledgeCatalogSource(
    string Id, string Path, int Priority, bool Enabled, SourceRole Role, MemoryTier? Tier = null);
```

- [ ] **Step 4: Extend the parser**

In `src/OKF4net.Catalog/CatalogManifestParser.cs`:
- Add constants: `private const string TierProperty = "tier";`, `private const string MemoryRoleValue = "memory";`.
- Add `TierProperty` to the known-source-property check in `ParseSource` (so `tier` is not flagged `UnknownSourceProperty`).
- Rewrite `ParseRole` to accept `knowledge` and `memory`; keep `IllegalRole` for anything else:

```csharp
    private static SourceRole ParseRole(JsonElement source, List<CatalogDiagnostic> diags)
    {
        if (!source.TryGetProperty(RoleProperty, out var roleProperty))
        {
            return SourceRole.Knowledge;
        }

        if (roleProperty.ValueKind == JsonValueKind.String)
        {
            var value = roleProperty.GetString();
            if (value == KnowledgeRoleValue)
            {
                return SourceRole.Knowledge;
            }

            if (value == MemoryRoleValue)
            {
                return SourceRole.Memory;
            }
        }

        diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.IllegalRole, "Source 'role' must be \"knowledge\" or \"memory\"."));
        return SourceRole.Knowledge;
    }
```

- Add `ParseTier`, applying the `IllegalTier` rules (memory requires a valid tier; non-memory must not carry one):

```csharp
    private static MemoryTier? ParseTier(JsonElement source, SourceRole role, List<CatalogDiagnostic> diags)
    {
        var hasTier = source.TryGetProperty(TierProperty, out var tierProperty);

        if (role != SourceRole.Memory)
        {
            if (hasTier)
            {
                diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.IllegalTier, "Source 'tier' is only valid on a role:\"memory\" source."));
            }

            return null;
        }

        var value = hasTier && tierProperty.ValueKind == JsonValueKind.String ? tierProperty.GetString() : null;
        switch (value)
        {
            case "session": return MemoryTier.Session;
            case "user": return MemoryTier.User;
            case "tenant": return MemoryTier.Tenant;
            default:
                diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.IllegalTier, "A role:\"memory\" source requires 'tier' to be \"session\", \"user\", or \"tenant\"."));
                return null;
        }
    }
```

- In `ParseSource`, wire the role → tier order and pass `Tier`:

```csharp
        var role = ParseRole(source, diags);
        var tier = ParseTier(source, role, diags);

        return new KnowledgeCatalogSource(id, path, priority, enabled, role, tier);
```

- In `ParseSources`, after the loop, enforce one memory source per tier (before returning):

```csharp
        var seenTiers = new HashSet<MemoryTier>();
        foreach (var s in sources)
        {
            if (s.Role == SourceRole.Memory && s.Tier is { } t && !seenTiers.Add(t))
            {
                diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.DuplicateMemoryTier, $"More than one role:\"memory\" source declares tier '{t}'."));
            }
        }
```

- [ ] **Step 5: Run it to confirm it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CatalogManifestTests"`
Expected: PASS (existing accept/reject cases + the new memory-tier cases).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/MemoryTier.cs src/OKF4net.Catalog/SourceRole.cs src/OKF4net.Catalog/KnowledgeCatalogSource.cs src/OKF4net.Catalog/CatalogDiagnosticCode.cs src/OKF4net.Catalog/CatalogManifestParser.cs tests/OKF4net.Tests/Catalog/CatalogManifestTests.cs
git commit -m "$(cat <<'EOF'
feat: parse role:memory sources with a required tier

Add SourceRole.Memory and MemoryTier; the manifest parser accepts a
role:"memory" source carrying tier session|user|tenant, rejects a missing/
invalid tier (IllegalTier), a tier on a non-memory source (IllegalTier),
and two memory sources of the same tier (DuplicateMemoryTier) — all as
data, never throwing.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

### Task 2.3 — `MemoryPath.For` (path derivation, one isolated function)

**Files:**
- Create: `src/OKF4net.Catalog/MemoryPath.cs`
- Test: `tests/OKF4net.Tests/Catalog/MemoryPathTests.cs`

**Interfaces:**
- Consumes: `MemoryTier`, `KnowledgeAccessScope`.
- Produces:

```csharp
public static class MemoryPath
{
    public const string LocalSentinel = "_local";
    /// <summary>The '/'-joined, readable-prefix concept-path prefix for a tier + scope.</summary>
    public static string For(MemoryTier tier, KnowledgeAccessScope scope);
}
```

> **Resolved ambiguity (null non-tenant segments):** the spec states the `_local` sentinel for a null tenant. To keep the degenerate all-null "local" scope a valid, fully-defined path for **every** tier (spec §9: the local scope is the degenerate single-scope case served by one user-tier memory source), this plan applies the same `_local` sentinel to a null user or session segment. `MemoryPath.For` is a pure path function — it never decides tier *applicability* (that lives in `FileMemoryStore`, Task 3.1); it only renders a path.

- [ ] **Step 1: Write the failing test**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

public class MemoryPathTests
{
    [Fact]
    public void Tenant_tier_prefix()
    {
        Assert.Equal("memory-tenant/acme", MemoryPath.For(MemoryTier.Tenant, new KnowledgeAccessScope(tenantId: "acme")));
    }

    [Fact]
    public void User_tier_nests_under_tenant()
    {
        Assert.Equal("memory-user/acme/alice", MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(tenantId: "acme", userId: "alice")));
    }

    [Fact]
    public void Session_tier_prefix()
    {
        Assert.Equal("memory-session/s1", MemoryPath.For(MemoryTier.Session, new KnowledgeAccessScope(sessionId: "s1")));
    }

    [Fact]
    public void Null_tenant_renders_the_local_sentinel_and_user_nests_under_it()
    {
        Assert.Equal("memory-user/_local/alice", MemoryPath.For(MemoryTier.User, new KnowledgeAccessScope(userId: "alice")));
        Assert.Equal("memory-tenant/_local", MemoryPath.For(MemoryTier.Tenant, KnowledgeAccessScope.Local));
    }

    [Fact]
    public void Fully_local_scope_is_defined_for_every_tier()
    {
        var local = KnowledgeAccessScope.Local;
        Assert.Equal("memory-user/_local/_local", MemoryPath.For(MemoryTier.User, local));
        Assert.Equal("memory-session/_local", MemoryPath.For(MemoryTier.Session, local));
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~MemoryPathTests"`
Expected: FAIL — `MemoryPath` does not exist (`CS0246`).

- [ ] **Step 3: Write the implementation**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Maps a <see cref="MemoryTier"/> + <see cref="KnowledgeAccessScope"/> to a
/// readable-prefix, '/'-joined concept-path prefix beneath a memory source's
/// root. The single point that decides scope-key storage form — switching to
/// hashed keys later changes only this function. A null scope segment renders
/// as the <see cref="LocalSentinel"/>, so cross-tenant collision is impossible
/// by construction (user memory nests under tenant) and the all-null "local"
/// scope is a valid path for every tier.
/// </summary>
public static class MemoryPath
{
    /// <summary>The sentinel segment substituted for a null scope segment (desktop/CLI).</summary>
    public const string LocalSentinel = "_local";

    /// <summary>
    /// The '/'-joined concept-path prefix for <paramref name="tier"/> under
    /// <paramref name="scope"/> (e.g. <c>memory-user/acme/alice</c>).
    /// </summary>
    public static string For(MemoryTier tier, KnowledgeAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var tenant = scope.TenantId ?? LocalSentinel;
        var user = scope.UserId ?? LocalSentinel;
        var session = scope.SessionId ?? LocalSentinel;

        return tier switch
        {
            MemoryTier.Tenant => $"memory-tenant/{tenant}",
            MemoryTier.User => $"memory-user/{tenant}/{user}",
            MemoryTier.Session => $"memory-session/{session}",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
        };
    }
}
```

- [ ] **Step 4: Run it to confirm it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~MemoryPathTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Catalog/MemoryPath.cs tests/OKF4net.Tests/Catalog/MemoryPathTests.cs
git commit -m "$(cat <<'EOF'
feat: add MemoryPath.For readable-prefix scope-path derivation

One isolated function maps (tier, scope) to memory-tenant/user/session
prefixes; a null segment renders as the _local sentinel so the local
scope is valid for every tier and cross-tenant collision is impossible.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

### Task 2.4 — `IMemoryStore` + result/entry types

**Files:**
- Create: `src/OKF4net.Catalog/IMemoryStore.cs`
- Create: `src/OKF4net.Catalog/MemoryTypes.cs` (`MemoryEntry`, `MemoryReadResult`, `MemoryWriteResult`, `MemoryDeleteResult`, `MemoryConcept`)
- Test: none yet (interface only; exercised by Task 3.1's `FileMemoryStore` tests).

**Interfaces:**
- Consumes: `KnowledgeAccessScope`, `MemoryTier`, `KnowledgeQuery`, `KnowledgePassage`, `KnowledgeDiagnostic` (all existing/earlier-task Catalog types).
- Produces:

```csharp
public interface IMemoryStore
{
    ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default);
    ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default);
    ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default);
}

public sealed record MemoryEntry(string ConceptName, string FrontmatterYamlIfCreating, string SectionMarkdown);
public sealed record MemoryReadResult(IReadOnlyList<KnowledgePassage> Passages, IReadOnlyList<KnowledgeDiagnostic> Diagnostics);
public sealed record MemoryWriteResult(bool Written, string? Error);
public sealed record MemoryDeleteResult(int TiersDeleted, string? Error);
public sealed record MemoryConcept(MemoryTier Tier, string ConceptId, string? Title);
```

- [ ] **Step 1: Write the interface**

Create `src/OKF4net.Catalog/IMemoryStore.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A scoped read+write memory sink. READ unions the scope's applicable tiers
/// (most-specific first: session → user → tenant), scored via the shared core
/// <c>ConceptSearch</c>. WRITE targets exactly one tier. Deletion/enumeration
/// support RGPD/audit. <see cref="IKnowledgeResolver"/> stays read-only and
/// unchanged. Every operation is errors-as-data — none throws for a data
/// condition (unresolvable path, unreadable bundle, reparse-point subtree);
/// those are reported via result fields/diagnostics.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Reads the scope's applicable-tier memory, scored against <paramref name="query"/>, most-specific first.</summary>
    ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default);

    /// <summary>Writes <paramref name="entry"/> into the scope's <paramref name="tier"/> memory (create-or-append, atomic).</summary>
    ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default);

    /// <summary>Deletes a scope's memory subtree for one tier, or (when <paramref name="tier"/> is null) every applicable configured tier.</summary>
    ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default);

    /// <summary>Lists the concepts stored for a scope across its applicable configured tiers (audit / DSAR).</summary>
    ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default);
}
```

Create `src/OKF4net.Catalog/MemoryTypes.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// One deterministic memory-capture entry to persist: the per-day concept leaf
/// name, the frontmatter used only if the concept must be created, and the
/// already-formatted (neutralized) markdown section to append.
/// </summary>
/// <param name="ConceptName">The concept's leaf name (a single concept-id segment), e.g. <c>2026-07-27</c>.</param>
/// <param name="FrontmatterYamlIfCreating">Producer frontmatter applied only when the concept does not yet exist.</param>
/// <param name="SectionMarkdown">The formatted section body appended on every capture.</param>
public sealed record MemoryEntry(string ConceptName, string FrontmatterYamlIfCreating, string SectionMarkdown);

/// <summary>The result of a scoped memory read: matching passages plus errors-as-data diagnostics.</summary>
public sealed record MemoryReadResult(IReadOnlyList<KnowledgePassage> Passages, IReadOnlyList<KnowledgeDiagnostic> Diagnostics);

/// <summary>The result of a scoped memory write: whether it was written, and the error text if not.</summary>
public sealed record MemoryWriteResult(bool Written, string? Error);

/// <summary>The result of a scoped memory deletion: how many tier subtrees were removed, and the error text if any.</summary>
public sealed record MemoryDeleteResult(int TiersDeleted, string? Error);

/// <summary>One stored memory concept, for enumeration/audit.</summary>
public sealed record MemoryConcept(MemoryTier Tier, string ConceptId, string? Title);
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build OKF4net.sln`
Expected: PASS (no test yet; the interface + records compile).

- [ ] **Step 3: Commit**

```bash
git add src/OKF4net.Catalog/IMemoryStore.cs src/OKF4net.Catalog/MemoryTypes.cs
git commit -m "$(cat <<'EOF'
feat: add IMemoryStore contract (scoped read+write memory sink)

ReadAsync (tier union), WriteAsync (one tier), DeleteScopeAsync, and
EnumerateAsync, with MemoryEntry/MemoryReadResult/MemoryWriteResult/
MemoryDeleteResult/MemoryConcept. IKnowledgeResolver stays unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task Group 3 — `FileMemoryStore` (user tier) (spec §4, §6, §7, §11.3)

### Task 3.1 — `FileMemoryStore` over the core write primitive

**Files:**
- Create: `src/OKF4net.Catalog/FileMemoryStore.cs`
- Test: `tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs`

**Interfaces:**
- Consumes: `IMemoryStore` + result/entry types (Task 2.4), `KnowledgeAccessScope` (2.1), `MemoryTier` (2.2), `MemoryPath.For` (2.3), `OKF4net.BundleConceptWriter` (Task 1.1), `OKF4net.Bundle.Load`, `OKF4net.ConceptSearch.Search`/`.Excerpt`, `OKF4net.Internal.ReparsePoints` (via the existing `InternalsVisibleTo("OKF4net.Catalog")` grant), `KnowledgeQuery`/`KnowledgePassage`/`KnowledgeDiagnostic`/`KnowledgeDiagnosticCode`.
- Produces:

```csharp
public sealed class FileMemoryStore : IMemoryStore
{
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots);
}
```

`tierRoots` maps each configured tier to its **already-resolved, absolute** memory source root directory. This lot wires only `MemoryTier.User` (Task Group 5); a tier not present in the map is treated as "no source configured" (errors-as-data), so session/tenant remain staged.

**Applicability** (which tiers a scope reads/enumerates/deletes-all): a tier is applicable iff it is present in `tierRoots` **and** either the scope is local (`scope.IsLocal`) or the tier's discriminating segment is present (`Session`→`SessionId`, `User`→`UserId`, `Tenant`→`TenantId`). READ unions applicable tiers in most-specific-first order: session, user, tenant.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// User-tier <see cref="FileMemoryStore"/>: write/read/enumerate/delete
/// round-trip, cross-scope isolation (the crux), and never-throw. Uses
/// <see cref="TempDir"/> for the memory source root — never touches
/// tests/fixtures/.
/// </summary>
public class FileMemoryStoreTests
{
    private const string Frontmatter =
        "type: AgentMemory\n"
        + "title: Agent memory 2026-07-27\n"
        + "description: Captured exchanges.\n"
        + "timestamp: 2026-07-27T10:00:00Z\n";

    private static MemoryEntry Entry(string body) =>
        new("2026-07-27", Frontmatter, $"## 10:00:00 UTC\n\n{body}\n");

    private static FileMemoryStore UserStore(TempDir tmp) =>
        new(new Dictionary<MemoryTier, string> { [MemoryTier.User] = tmp.Path });

    [Fact]
    public async Task Write_then_read_round_trips_under_the_user_tier()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");

        var write = await store.WriteAsync(scope, Entry("orders and refunds notes"), MemoryTier.User);
        Assert.True(write.Written);
        Assert.Null(write.Error);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "memory-user", "acme", "alice", "2026-07-27.md")));

        var read = await store.ReadAsync(scope, new KnowledgeQuery("orders"));
        Assert.Empty(read.Diagnostics);
        Assert.NotEmpty(read.Passages);
        Assert.All(read.Passages, p => Assert.Equal("memory:User", p.SourceId));
    }

    [Fact]
    public async Task A_tenant_A_scope_cannot_read_tenant_B_memory()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        var b = new KnowledgeAccessScope(tenantId: "b", userId: "bob");

        await store.WriteAsync(a, Entry("secret-a-nonce"), MemoryTier.User);

        var readB = await store.ReadAsync(b, new KnowledgeQuery("secret-a-nonce"));
        Assert.Empty(readB.Passages);
    }

    [Fact]
    public async Task Delete_removes_only_the_target_scope_subtree()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var a = new KnowledgeAccessScope(tenantId: "a", userId: "alice");
        var b = new KnowledgeAccessScope(tenantId: "a", userId: "bob");
        await store.WriteAsync(a, Entry("alice data"), MemoryTier.User);
        await store.WriteAsync(b, Entry("bob data"), MemoryTier.User);

        var del = await store.DeleteScopeAsync(a, MemoryTier.User);
        Assert.Equal(1, del.TiersDeleted);
        Assert.Null(del.Error);

        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "memory-user", "a", "alice")));
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "memory-user", "a", "bob")));
    }

    [Fact]
    public async Task Enumerate_lists_only_the_scopes_own_concepts()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        await store.WriteAsync(scope, Entry("day one"), MemoryTier.User);

        var listed = await store.EnumerateAsync(scope);
        var concept = Assert.Single(listed);
        Assert.Equal(MemoryTier.User, concept.Tier);
        Assert.Equal("memory-user/acme/alice/2026-07-27", concept.ConceptId);
    }

    [Fact]
    public async Task Write_to_an_unconfigured_tier_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp); // only User configured
        var scope = new KnowledgeAccessScope(sessionId: "s1");

        var write = await store.WriteAsync(scope, Entry("x"), MemoryTier.Session);
        Assert.False(write.Written);
        Assert.NotNull(write.Error);
    }

    [Fact]
    public async Task Local_scope_reads_and_writes_the_local_user_subtree()
    {
        using var tmp = new TempDir();
        var store = UserStore(tmp);

        await store.WriteAsync(KnowledgeAccessScope.Local, Entry("local orders"), MemoryTier.User);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "memory-user", "_local", "_local", "2026-07-27.md")));

        var read = await store.ReadAsync(KnowledgeAccessScope.Local, new KnowledgeQuery("orders"));
        Assert.NotEmpty(read.Passages);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileMemoryStoreTests"`
Expected: FAIL — `FileMemoryStore` does not exist (`CS0246`).

- [ ] **Step 3: Write the implementation**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
/// Filesystem <see cref="IMemoryStore"/>. Path derivation is isolated in
/// <see cref="MemoryPath.For"/>; writes reuse the core
/// <see cref="OKF4net.BundleConceptWriter"/> (producer validation + per-path
/// lock + reparse guards) over the tier's memory source root. The user tier is
/// implemented; a tier absent from the configured roots is treated as "no
/// source configured" (errors-as-data), so session/tenant remain staged.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Most-specific first (spec §6.1).
    private static readonly MemoryTier[] ReadOrder = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    private readonly IReadOnlyDictionary<MemoryTier, string> _tierRoots;

    /// <summary>Creates a store over the given per-tier, resolved absolute source roots.</summary>
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots)
    {
        ArgumentNullException.ThrowIfNull(tierRoots);
        _tierRoots = tierRoots;
    }

    /// <inheritdoc/>
    public ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var passages = new List<KnowledgePassage>();
        var diagnostics = new List<KnowledgeDiagnostic>();

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierRoots.TryGetValue(tier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, tier, scope);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            if (IsReparseEscaped(root, subDir))
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' path is a reparse point; refusing to read."));
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(subDir);
            }
            catch (OkfException e)
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' could not be loaded: {e.Message}"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(query.Text))
            {
                continue;
            }

            foreach (var hit in ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag))
            {
                passages.Add(new KnowledgePassage(
                    SourceId: $"memory:{tier}",
                    ConceptId: hit.Concept.Id.ToString(),
                    Title: hit.Concept.Document.Frontmatter.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    BundleRelativePath: Path.GetRelativePath(bundle.Root, hit.Concept.Path).Replace(Path.DirectorySeparatorChar, '/')));
            }
        }

        return new ValueTask<MemoryReadResult>(new MemoryReadResult(passages.AsReadOnly(), diagnostics.AsReadOnly()));
    }

    /// <inheritdoc/>
    public ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        if (!_tierRoots.TryGetValue(tier, out var root))
        {
            return new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, $"No memory source configured for tier '{tier}'."));
        }

        var conceptId = $"{MemoryPath.For(tier, scope)}/{entry.ConceptName}";
        var writer = new BundleConceptWriter(root);
        var section = entry.SectionMarkdown;
        var result = writer.AppendToConceptAtomic(
            conceptId,
            entry.FrontmatterYamlIfCreating,
            current => current is null ? section : current.TrimEnd('\n') + "\n\n" + section);

        return result.StartsWith("Error:", StringComparison.Ordinal)
            ? new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, result))
            : new ValueTask<MemoryWriteResult>(new MemoryWriteResult(true, null));
    }

    /// <inheritdoc/>
    public ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var tiers = tier is { } t ? [t] : ReadOrder.Where(x => IsApplicable(x, scope));
        var deleted = 0;
        string? error = null;

        foreach (var currentTier in tiers)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tierRoots.TryGetValue(currentTier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, currentTier, scope);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            if (IsReparseEscaped(root, subDir))
            {
                error = $"Memory tier '{currentTier}' path is a reparse point; refusing to delete.";
                continue;
            }

            try
            {
                Directory.Delete(subDir, recursive: true);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error = $"Memory tier '{currentTier}' subtree could not be deleted: {e.Message}";
            }
        }

        return new ValueTask<MemoryDeleteResult>(new MemoryDeleteResult(deleted, error));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var concepts = new List<MemoryConcept>();

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierRoots.TryGetValue(tier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, tier, scope);
            if (!Directory.Exists(subDir) || IsReparseEscaped(root, subDir))
            {
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(subDir);
            }
            catch (OkfException)
            {
                continue;
            }

            foreach (var concept in bundle.Concepts)
            {
                concepts.Add(new MemoryConcept(tier, concept.Id.ToString(), concept.Document.Frontmatter.Title));
            }
        }

        return new ValueTask<IReadOnlyList<MemoryConcept>>(concepts.AsReadOnly());
    }

    private static bool IsApplicable(MemoryTier tier, KnowledgeAccessScope scope) => scope.IsLocal || tier switch
    {
        MemoryTier.Session => scope.SessionId is not null,
        MemoryTier.User => scope.UserId is not null,
        MemoryTier.Tenant => scope.TenantId is not null,
        _ => false,
    };

    private static string ScopedDir(string root, MemoryTier tier, KnowledgeAccessScope scope)
    {
        var relative = MemoryPath.For(tier, scope).Split('/');
        return Path.Combine([root, .. relative]);
    }

    private static bool IsReparseEscaped(string root, string subDir)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(subDir);
        return ReparsePoints.IsReparsePoint(full) || ReparsePoints.HasReparsePointAncestor(fullRoot, full, PathComparison);
    }
}
```

- [ ] **Step 4: Run it to confirm it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~FileMemoryStoreTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Full build + test + format**

Run: `dotnet build OKF4net.sln && dotnet test OKF4net.sln && dotnet format OKF4net.sln --verify-no-changes`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog/FileMemoryStore.cs tests/OKF4net.Tests/Catalog/FileMemoryStoreTests.cs
git commit -m "$(cat <<'EOF'
feat: implement user-tier FileMemoryStore on the core write primitive

Write/read/delete/enumerate over MemoryPath.For scoped subtrees, using
BundleConceptWriter (producer validation, per-path lock, reparse guards)
and the shared ConceptSearch scorer. Cross-scope isolation is structural
(path nesting). Session/tenant tiers stay staged (no source wired).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task Group 4 — Adapter: evolve `OkfContextProvider` (spec §4, §6, §8, §11.4)

The provider gains the Agents→Catalog reference and a new scoped V2 mode (resolver knowledge ∪ store memory under a split budget; scoped capture WRITE). The existing V1 (`OkfBundleTools`) constructor and its behaviour are **retained unchanged** so the entire existing test suite stays green; the new mode is a second constructor with its own code path.

> **Resolved ambiguity (Agents→Catalog reference timing):** spec §11 stages DI/wiring last, but the adapter itself consumes Catalog types, so the `ProjectReference` is added at the **top of this group** (Task 4.1). Group 5 completes the Hosting-side wiring.
>
> **Resolved ambiguity (V1 vs V2 as one type):** the V1 `<okf-context>` output (root index first, then `ReadConcept`-rendered scored concepts) is asserted byte-position by existing tests and cannot be reproduced by the resolver/passage path without breaking them. The provider therefore keeps two constructors and branches internally (nullable `_tools` for V1; `_resolver`/`_memoryStore` for V2). This honours "the provider evolves to consume resolver + store + scope delegate" additively while keeping the suite green.
>
> **Resolved ambiguity (scope on the WRITE path):** `ScopeAccessor` is locked to `Func<InvokingContext, KnowledgeAccessScope>`, but `StoreAIContextAsync` receives an `InvokedContext` (no `InvokingContext`). The provider resolves scope in `ProvideAIContextAsync` and correlates it to the paired `StoreAIContextAsync` via a `ConditionalWeakTable<AgentSession, KnowledgeAccessScope>` keyed on the invocation's session (thread-safe across concurrent invocations with distinct sessions); when there is no session, or no prior provide, the WRITE falls back to `KnowledgeAccessScope.Local`. Scope is thus always host-supplied via the delegate, never derived from a message.

### Task 4.1 — Add the Agents→Catalog reference and evolve the options

**Files:**
- Modify: `src/OKF4net.Agents/OKF4net.Agents.csproj` (add `ProjectReference` to `OKF4net.Catalog`)
- Modify: `src/OKF4net.Agents/OkfContextProviderOptions.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs` (extend the defaults fact)

**Interfaces:**
- Consumes: `OKF4net.Catalog.KnowledgeAccessScope`, `OKF4net.Catalog.MemoryTier`, `Microsoft.Agents.AI.AIContextProvider.InvokingContext`.
- Produces (new members on `OkfContextProviderOptions`):

```csharp
public Func<AIContextProvider.InvokingContext, KnowledgeAccessScope>? ScopeAccessor { get; init; } // absent ⇒ Local
public MemoryTier CaptureTier { get; init; } = MemoryTier.User;
public double KnowledgeBudgetShare { get; init; } = 0.6;
public double MemoryBudgetShare { get; init; } = 0.4;
[Obsolete("...")] public string MemoryDirectory { get; init; } = "memory";
```

- [ ] **Step 1: Add the project reference**

In `src/OKF4net.Agents/OKF4net.Agents.csproj`, add to the existing `ProjectReference` `ItemGroup`:

```xml
    <ProjectReference Include="..\OKF4net.Catalog\OKF4net.Catalog.csproj" />
```

- [ ] **Step 2: Write the failing test**

In `tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs`, extend `Options_defaults_match_the_documented_values` (add these assertions; `using OKF4net.Catalog;` at the top):

```csharp
        Assert.Null(options.ScopeAccessor);
        Assert.Equal(MemoryTier.User, options.CaptureTier);
        Assert.Equal(0.6, options.KnowledgeBudgetShare);
        Assert.Equal(0.4, options.MemoryBudgetShare);
```

- [ ] **Step 3: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfContextProviderTests.Options_defaults_match_the_documented_values"`
Expected: FAIL — the new option members do not exist (`CS1061`).

- [ ] **Step 4: Evolve the options**

In `src/OKF4net.Agents/OkfContextProviderOptions.cs`, add `using Microsoft.Agents.AI;` and `using OKF4net.Catalog;`, add the members, and mark `MemoryDirectory` obsolete:

```csharp
    /// <summary>
    /// The host-authenticated scope for an invocation. Absent (<see langword="null"/>)
    /// ⇒ <see cref="KnowledgeAccessScope.Local"/>. Used only by the scoped (V2)
    /// provider constructor. Never derive scope from a message.
    /// </summary>
    public Func<AIContextProvider.InvokingContext, KnowledgeAccessScope>? ScopeAccessor { get; init; }

    /// <summary>The tier scoped memory capture writes to. Defaults to <see cref="MemoryTier.User"/>.</summary>
    public MemoryTier CaptureTier { get; init; } = MemoryTier.User;

    /// <summary>
    /// The floor fraction (0..1) of <see cref="TokenBudget"/> guaranteed to the
    /// knowledge surface before spillover. Defaults to <c>0.6</c> (knowledge
    /// slightly prioritized; memory augments).
    /// </summary>
    public double KnowledgeBudgetShare { get; init; } = 0.6;

    /// <summary>
    /// The floor fraction (0..1) of <see cref="TokenBudget"/> guaranteed to the
    /// memory surface before spillover. Defaults to <c>0.4</c>. Must satisfy
    /// <see cref="KnowledgeBudgetShare"/> + this ≤ 1.
    /// </summary>
    public double MemoryBudgetShare { get; init; } = 0.4;
```

Add `[Obsolete(...)]` to `MemoryDirectory` (keep the property so the V1 path compiles):

```csharp
    [Obsolete("MemoryDirectory (single-bundle capture) is deprecated in favour of role:memory catalog sources and the scoped IMemoryStore. Used only by the V1 OkfBundleTools-based provider constructor.")]
    public string MemoryDirectory { get; init; } = "memory";
```

Because `TreatWarningsAsErrors` is on, suppress `CS0618` at the two V1 read sites inside `OkfContextProvider` (the constructor's `ValidateSegment(effectiveOptions.MemoryDirectory)` and `CaptureMemory`'s `$"{_options.MemoryDirectory}/{dateStr}"`) with `#pragma warning disable CS0618` / `restore` — those uses are the deliberately-retained V1 path.

**Rename the capture-mode enum value `SharedBundle` → `Enabled`** (arbitration A — the pre-1.0 enum reads wrong in scoped mode). In `OkfContextProviderOptions.cs`, change `enum MemoryCaptureMode { Disabled, SharedBundle }` to `{ Disabled, Enabled }` and rewrite the `Enabled` XML doc to describe "captures the deterministic exchange into memory (V1: the single bundle; V2: the scope's tier via `IMemoryStore`)". Then update **every** reference across the repo — run `git grep -n "MemoryCaptureMode.SharedBundle"` and replace each with `MemoryCaptureMode.Enabled` (this includes the existing V1 tests `OkfContextProviderMemoryTests.cs` / `OkfContextProviderTests.cs`, any README snippet under `src/OKF4net.Agents/`, and this plan's own scoped tests below). The V1 `CaptureMemory` gate `if (_options.MemoryCapture == MemoryCaptureMode.Disabled) return;` is unchanged (still keyed on `Disabled`).

- [ ] **Step 5: Run it to confirm it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfContextProviderTests.Options_defaults_match_the_documented_values"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Agents/OKF4net.Agents.csproj src/OKF4net.Agents/OkfContextProviderOptions.cs src/OKF4net.Agents/OkfContextProvider.cs tests/OKF4net.Tests/Agents/OkfContextProviderTests.cs
git commit -m "$(cat <<'EOF'
feat: add Agents->Catalog reference and scoped-memory provider options

OkfContextProviderOptions gains ScopeAccessor, CaptureTier (default User),
and Knowledge/Memory budget shares; MemoryDirectory is deprecated in
favour of role:memory catalog sources.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

### Task 4.2 — Scoped V2 provider: split-budget READ + scoped capture WRITE

**Files:**
- Modify: `src/OKF4net.Agents/OkfContextProvider.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfContextProviderScopedTests.cs`

**Interfaces:**
- Consumes: `OKF4net.Catalog.IKnowledgeResolver`, `OKF4net.Catalog.IMemoryStore`, `OKF4net.Catalog.KnowledgeQuery`/`KnowledgeContext`/`KnowledgePassage`, `OKF4net.Catalog.KnowledgeAccessScope`, `OkfContextProviderOptions` (Task 4.1), `TokenEstimate.Chars` (Agents-internal), the existing private `RenderBlock`/`ExtractLastUserMessageText`/`Neutralize`/`SanitizeNul`.
- Produces (new public constructor + internal test seam behaviour):

```csharp
public OkfContextProvider(IKnowledgeResolver resolver, IMemoryStore memoryStore, OkfContextProviderOptions options);
```

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Catalog;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Scoped (V2) <see cref="OkfContextProvider"/>: split-budget READ (knowledge
/// ∪ memory), scoped user-tier capture WRITE, never-throw, and
/// injection-as-message-not-instructions. Builds a resolver over a fixture-copy
/// knowledge source and a user-tier <see cref="FileMemoryStore"/> over a
/// TempDir; never touches tests/fixtures/ directly.
/// </summary>
public class OkfContextProviderScopedTests
{
    // Microsoft.Agents.AI.AgentSession is abstract with only protected ctors, so
    // `new AgentSession()` does not compile. A sealed no-member subclass IS
    // constructible (AgentSession has no abstract members) and provides the
    // reference identity the provider's ConditionalWeakTable keys on.
    private sealed class TestAgentSession : Microsoft.Agents.AI.AgentSession { }

    private const string MemoryFrontmatter =
        "type: AgentMemory\ntitle: Agent memory\ndescription: x\ntimestamp: 2026-07-27T00:00:00Z\n";

    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    private static (IKnowledgeResolver Resolver, FileMemoryStore Store, TempDir Root) SetUp(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "kb"));
        Directory.CreateDirectory(Path.Combine(root.Path, "mem"));
        root.Write("catalog.json", """
            { "version": 1, "sources": [ { "id": "kb", "path": "./kb", "role": "knowledge" } ] }
            """);

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var resolver = new DefaultKnowledgeResolver(catalog);
        var store = new FileMemoryStore(new Dictionary<MemoryTier, string> { [MemoryTier.User] = Path.Combine(root.Path, "mem") });
        return (resolver, store, root);
    }

    private static AIContextProvider.InvokingContext Invoking(AgentSession? session, string? userText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var ai = new AIContext { Messages = userText is null ? null : [new ChatMessage(ChatRole.User, userText)] };
#pragma warning disable MAAI001
        return new AIContextProvider.InvokingContext(agent, session, ai);
#pragma warning restore MAAI001
    }

    private static AIContextProvider.InvokedContext Invoked(AgentSession? session, string userText, string agentText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
#pragma warning disable MAAI001
        return new AIContextProvider.InvokedContext(agent, session, [new ChatMessage(ChatRole.User, userText)], [new ChatMessage(ChatRole.Assistant, agentText)]);
#pragma warning restore MAAI001
    }

    private static OkfContextProviderOptions ScopedOptions(KnowledgeAccessScope scope) => new()
    {
        MemoryCapture = MemoryCaptureMode.Enabled,
        CaptureTier = MemoryTier.User,
        ScopeAccessor = _ => scope,
    };

    [Fact]
    public async Task Read_injects_knowledge_as_message_data_never_instructions()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(userId: "alice")));

        var result = await provider.ProvideForTest(Invoking(session: null, "orders"), CancellationToken.None);

        Assert.DoesNotContain("orders", result.Instructions ?? string.Empty, StringComparison.Ordinal);
        var text = Assert.Single(result.Messages!).Text;
        Assert.Contains("tables/orders", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_then_recall_round_trips_under_the_user_scope()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scope = new KnowledgeAccessScope(tenantId: "acme", userId: "alice");
        var session = new TestAgentSession();
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(scope));
        provider.UtcNow = () => new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        // Provide first so the scope is correlated to this session, then store.
        await provider.ProvideForTest(Invoking(session, "hello"), CancellationToken.None);
        await provider.StoreForTest(Invoked(session, "remember nonce-zx99", "acknowledged nonce-zx99"));

        Assert.Null(provider.LastMemoryError);
        Assert.True(File.Exists(Path.Combine(root.Path, "mem", "memory-user", "acme", "alice", "2026-07-27.md")));

        // A later provide for the same scope recalls the captured memory.
        var recall = await provider.ProvideForTest(Invoking(session, "nonce-zx99"), CancellationToken.None);
        var text = Assert.Single(recall.Messages!).Text;
        Assert.Contains("nonce-zx99", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_is_scoped_a_different_tenant_recalls_nothing()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var sessionA = new TestAgentSession();
        var providerA = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(tenantId: "a", userId: "alice")));
        providerA.UtcNow = () => new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

        await providerA.ProvideForTest(Invoking(sessionA, "hi"), CancellationToken.None);
        await providerA.StoreForTest(Invoked(sessionA, "tenant-a-secret-qq", "noted qq"));
        Assert.Null(providerA.LastMemoryError);

        var sessionB = new TestAgentSession();
        var providerB = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(tenantId: "b", userId: "bob")));
        await providerB.ProvideForTest(Invoking(sessionB, "hi"), CancellationToken.None);
        var recallB = await providerB.ProvideForTest(Invoking(sessionB, "tenant-a-secret-qq"), CancellationToken.None);

        var text = Assert.Single(recallB.Messages!).Text;
        Assert.DoesNotContain("tenant-a-secret-qq", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_never_throws_when_the_memory_write_fails()
    {
        using var root = new TempDir();
        var (resolver, _, _) = SetUp(root);
        // A store with NO configured tiers: every write is reported, never thrown.
        var emptyStore = new FileMemoryStore(new Dictionary<MemoryTier, string>());
        var session = new TestAgentSession();
        var provider = new OkfContextProvider(resolver, emptyStore, ScopedOptions(new KnowledgeAccessScope(userId: "alice")));

        await provider.ProvideForTest(Invoking(session, "hi"), CancellationToken.None);
        var ex = await Record.ExceptionAsync(async () => await provider.StoreForTest(Invoked(session, "q", "a")));

        Assert.Null(ex);
        Assert.NotNull(provider.LastMemoryError);
    }

    [Fact]
    public async Task Scoped_capture_is_skipped_when_the_scope_cannot_be_correlated()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var provider = new OkfContextProvider(resolver, store, ScopedOptions(new KnowledgeAccessScope(tenantId: "acme", userId: "alice")));

        // A ScopeAccessor IS configured, but StoreAIContextAsync runs with no
        // session and no prior ProvideAIContextAsync => the scope cannot be
        // correlated, so the capture is skipped (never misfiled into _local).
        await provider.StoreForTest(Invoked(session: null, "q", "a"));

        Assert.NotNull(provider.LastMemoryError);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "mem", "memory-user")));
    }

    [Fact]
    public async Task Split_budget_reserves_a_memory_floor_so_memory_is_not_starved_by_knowledge()
    {
        using var root = new TempDir();
        var (resolver, store, _) = SetUp(root);
        var scope = new KnowledgeAccessScope(userId: "alice");
        var session = new TestAgentSession();

        // Pre-seed a memory concept mentioning a distinctive term.
        await store.WriteAsync(scope, new MemoryEntry("2026-07-27", MemoryFrontmatter, "## note\n\nremembered orders detail\n"), MemoryTier.User);

        var provider = new OkfContextProvider(resolver, store, new OkfContextProviderOptions
        {
            ScopeAccessor = _ => scope,
            KnowledgeBudgetShare = 0.5,
            MemoryBudgetShare = 0.5,
        });

        var result = await provider.ProvideForTest(Invoking(session, "orders"), CancellationToken.None);
        var text = Assert.Single(result.Messages!).Text;

        // Both surfaces are represented (memory got its floor share).
        Assert.Contains("memory:User", text, StringComparison.Ordinal);
        Assert.Contains("tables/orders", text, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfContextProviderScopedTests"`
Expected: FAIL — the `(IKnowledgeResolver, IMemoryStore, OkfContextProviderOptions)` constructor and `provider.UtcNow` do not exist.

- [ ] **Step 3: Add the V2 constructor, fields, and scope correlation**

In `src/OKF4net.Agents/OkfContextProvider.cs`, add `using System.Runtime.CompilerServices;` and `using OKF4net.Catalog;`. Change the fields so V1 and V2 coexist:

```csharp
    private readonly OkfBundleTools? _tools;              // V1 mode
    private readonly IKnowledgeResolver? _resolver;       // V2 mode
    private readonly IMemoryStore? _memoryStore;          // V2 mode
    private readonly OkfContextProviderOptions _options;

    // Correlates the scope resolved in ProvideAIContextAsync to the paired
    // StoreAIContextAsync, keyed by the invocation's session.
    private readonly ConditionalWeakTable<AgentSession, ScopeBox> _scopeBySession = new();
    private sealed class ScopeBox { public KnowledgeAccessScope Scope = KnowledgeAccessScope.Local; }

    /// <summary>The UTC clock used by the scoped (V2) capture path; overridable in tests.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
```

Add the V2 constructor (the existing V1 constructor stays; set `_tools` there and leave `_resolver`/`_memoryStore` null):

```csharp
    /// <summary>
    /// Creates the scoped (V2) provider: READ = knowledge (resolver) ∪ memory
    /// (store) under a split token budget; WRITE = deterministic scoped capture
    /// to <see cref="OkfContextProviderOptions.CaptureTier"/> via the store.
    /// </summary>
    public OkfContextProvider(IKnowledgeResolver resolver, IMemoryStore memoryStore, OkfContextProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(memoryStore);
        ArgumentNullException.ThrowIfNull(options);

        if (options.KnowledgeBudgetShare < 0 || options.MemoryBudgetShare < 0
            || options.KnowledgeBudgetShare + options.MemoryBudgetShare > 1.0)
        {
            throw new ArgumentException("KnowledgeBudgetShare and MemoryBudgetShare must be >= 0 and sum to <= 1.", nameof(options));
        }

        _resolver = resolver;
        _memoryStore = memoryStore;
        _options = options;
    }
```

- [ ] **Step 4: Branch `ProvideAIContextAsync` into the V2 path**

At the top of `ProvideAIContextAsync`, after the `totalBudget <= 0` early return, delegate to a scoped assembler when in V2 mode:

```csharp
        if (_resolver is not null && _memoryStore is not null)
        {
            return ProvideScopedAsync(context, totalBudget, cancellationToken);
        }
```

Add the scoped assembler (split-budget algorithm, never-throws, message-data-only injection):

```csharp
    private async ValueTask<AIContext> ProvideScopedAsync(InvokingContext context, int totalBudget, CancellationToken ct)
    {
        var scope = _options.ScopeAccessor?.Invoke(context) ?? KnowledgeAccessScope.Local;
        if (context.Session is { } session)
        {
            _scopeBySession.GetValue(session, static _ => new ScopeBox()).Scope = scope;
        }

        var query = ExtractLastUserMessageText(context);
        if (query is null)
        {
            return new AIContext();
        }

        var knowledge = new List<KnowledgePassage>();
        var memory = new List<KnowledgePassage>();
        try
        {
            var kc = await _resolver!.SearchAsync(new KnowledgeQuery(query), ct).ConfigureAwait(false);
            knowledge.AddRange(kc.Passages);
        }
        catch (Exception ex) when (ex is OperationCanceledException) { throw; }
        catch (Exception) { /* errors-as-data: knowledge degrades to empty */ }

        try
        {
            var mr = await _memoryStore!.ReadAsync(scope, new KnowledgeQuery(query), ct).ConfigureAwait(false);
            memory.AddRange(mr.Passages);
        }
        catch (Exception ex) when (ex is OperationCanceledException) { throw; }
        catch (Exception) { /* errors-as-data: memory degrades to empty */ }

        // Split budget with floors + spillover (spec §6.3).
        var mFloor = (int)(totalBudget * _options.MemoryBudgetShare);
        var knowledgeCap = Math.Max(0, totalBudget - mFloor);
        var sb = new StringBuilder();

        var kUsed = AppendPassages(sb, knowledge, "knowledge", knowledgeCap);
        var mUsed = AppendPassages(sb, memory, "memory", totalBudget - kUsed);
        // Spill unused memory back to any remaining knowledge.
        AppendPassages(sb, knowledge.Skip(CountRendered(sb, "knowledge")), "knowledge", totalBudget - kUsed - mUsed);

        if (sb.Length == 0)
        {
            return new AIContext();
        }

        return new AIContext
        {
            Instructions = FixedInstructions,
            Messages = [new ChatMessage(ChatRole.User, sb.ToString())],
        };
    }

    private static int CountRendered(StringBuilder sb, string surface)
    {
        var text = sb.ToString();
        var marker = $"<okf-context id=\"{surface}:";
        var count = 0;
        var i = 0;
        while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += marker.Length;
        }

        return count;
    }

    private static int AppendPassages(StringBuilder sb, IEnumerable<KnowledgePassage> passages, string surface, int budget)
    {
        var used = 0;
        var remaining = budget;
        foreach (var p in passages)
        {
            if (remaining <= 0)
            {
                break;
            }

            var content = (p.Title is null ? string.Empty : p.Title + "\n") + p.Excerpt;
            var (block, blockUsed) = RenderBlock($"{surface}:{p.SourceId}:{p.ConceptId}", content, remaining, alwaysInclude: false);
            if (block is null)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(block);
            remaining -= blockUsed;
            used += blockUsed;
        }

        return used;
    }
```

> Note: `RenderBlock`, `ExtractLastUserMessageText`, `FixedInstructions`, `TruncatedMarker`, and `Neutralize`/`SanitizeNul` already exist as private/const members and are reused verbatim. `CountRendered`/`AppendPassages`'s second knowledge pass is the bidirectional spillover; it re-renders only knowledge passages not already rendered in the first pass.

- [ ] **Step 5: Branch `StoreAIContextAsync` into the V2 capture path**

At the top of `StoreAIContextAsync`, after `LastMemoryError = null;`, branch:

```csharp
        if (_resolver is not null && _memoryStore is not null)
        {
            return StoreScopedAsync(context, cancellationToken);
        }
```

Add the scoped capture (deterministic, neutralized, one tier, never throws):

```csharp
    private async ValueTask StoreScopedAsync(InvokedContext context, CancellationToken ct)
    {
        if (_options.MemoryCapture == MemoryCaptureMode.Disabled)
        {
            return;
        }

        if (context.InvokeException is not null || context.ResponseMessages is null)
        {
            return;
        }

        var userText = ExtractLastMessageText(context.RequestMessages, ChatRole.User);
        var agentText = ExtractLastMessageText(context.ResponseMessages, ChatRole.Assistant);
        if (userText is null && agentText is null)
        {
            return;
        }

        // Scope resolution for capture (arbitration B):
        //  - No ScopeAccessor configured  => local mode; capture to the local subtree.
        //  - ScopeAccessor configured but we cannot recover the invocation's scope
        //    (no session, or no prior ProvideAIContextAsync in this session) => SKIP
        //    the capture and record why, rather than misfiling it into _local.
        KnowledgeAccessScope scope;
        if (_options.ScopeAccessor is null)
        {
            scope = KnowledgeAccessScope.Local;
        }
        else if (context.Session is { } session && _scopeBySession.TryGetValue(session, out var box))
        {
            scope = box.Scope;
        }
        else
        {
            LastMemoryError = "Scoped capture skipped: the invocation scope could not be determined (no session, or no prior context provide in this session).";
            return;
        }

        var now = UtcNow();
        var dateStr = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var section = new StringBuilder()
            .Append("## ").Append(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC").Append('\n').Append('\n')
            .Append("**User:**").Append('\n')
            .Append(Neutralize(SanitizeNul(userText) ?? NoContentPlaceholder)).Append('\n').Append('\n')
            .Append("**Agent:**").Append('\n')
            .Append(Neutralize(SanitizeNul(agentText) ?? NoContentPlaceholder)).Append('\n')
            .ToString();

        var timestamp = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        var frontmatter =
            "type: AgentMemory\n"
            + $"title: Agent memory {dateStr}\n"
            + $"description: Captured user/agent exchanges for {dateStr}.\n"
            + $"timestamp: {timestamp}\n";

        try
        {
            var result = await _memoryStore!.WriteAsync(scope, new MemoryEntry(dateStr, frontmatter, section), _options.CaptureTier, ct).ConfigureAwait(false);
            if (!result.Written)
            {
                LastMemoryError = result.Error;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastMemoryError = ex.Message;
        }
    }
```

- [ ] **Step 6: Run the scoped tests to confirm they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfContextProviderScopedTests"`
Expected: PASS (6 tests).

- [ ] **Step 7: Run the V1 provider suites to confirm no regression**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfContextProviderTests|FullyQualifiedName~OkfContextProviderMemoryTests|FullyQualifiedName~ContextProviderIntegrationTests"`
Expected: PASS (V1 behaviour unchanged).

- [ ] **Step 8: Full build + test + format**

Run: `dotnet build OKF4net.sln && dotnet test OKF4net.sln && dotnet format OKF4net.sln --verify-no-changes`
Expected: all green.

- [ ] **Step 9: Commit**

```bash
git add src/OKF4net.Agents/OkfContextProvider.cs tests/OKF4net.Tests/Agents/OkfContextProviderScopedTests.cs
git commit -m "$(cat <<'EOF'
feat: scope-aware OkfContextProvider (knowledge union memory, capture)

Add the (IKnowledgeResolver, IMemoryStore, options) constructor: READ
assembles resolver knowledge union store memory under a split token
budget with floors and spillover, injected as message data only; WRITE
captures deterministically (blockquote-neutralized) to one tier via the
store, scope correlated per session. Never throws toward the pipeline.
The V1 OkfBundleTools path is retained unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Task Group 5 — Wiring / DI (spec §9, §11.5)

### Task 5.1 — Hosting `AddMemory` builds a `FileMemoryStore` from `role:memory` sources

**Files:**
- Create: `src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs`
- Test: `tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.DependencyInjection` (already referenced by Hosting), `IKnowledgeCatalog` (registered by `AddKnowledge`), `KnowledgeCatalogSnapshot.Sources`, `SourceRole.Memory`, `MemoryTier`, `CatalogPathResolver.TryResolve`, `FileMemoryStore`, `IMemoryStore`.
- Produces:

```csharp
public static IServiceCollection AddMemory(this IServiceCollection services);
```

Registers `IMemoryStore` as a singleton `FileMemoryStore`, whose per-tier roots are the catalog's currently-enabled `role:memory` sources resolved via `CatalogPathResolver.TryResolve` (a source that fails to resolve is skipped — errors-as-data). Depends on `AddKnowledge` having registered `IKnowledgeCatalog`.

- [ ] **Step 1: Write the failing test**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog.Hosting;

public class MemoryServiceCollectionExtensionsTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    [Fact]
    public async Task AddMemory_registers_a_store_wired_to_the_user_tier_source()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "mem", "user"));
        Directory.CreateDirectory(Path.Combine(root.Path, "kb"));
        foreach (var f in Directory.GetFiles(BundlePath))
        {
            File.Copy(f, Path.Combine(root.Path, "kb", Path.GetFileName(f)));
        }

        root.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./kb", "role": "knowledge" },
                { "id": "user-mem", "path": "./mem/user", "role": "memory", "tier": "user" }
              ]
            }
            """);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(Path.Combine(root.Path, "catalog.json")));
        services.AddMemory();
        using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IMemoryStore>();
        var scope = new KnowledgeAccessScope(userId: "alice");
        var write = await store.WriteAsync(
            scope,
            new MemoryEntry("2026-07-27", "type: AgentMemory\ntitle: t\ndescription: d\ntimestamp: 2026-07-27T00:00:00Z\n", "## s\n\nhello orders\n"),
            MemoryTier.User);

        Assert.True(write.Written);
        Assert.True(File.Exists(Path.Combine(root.Path, "mem", "user", "memory-user", "_local", "alice", "2026-07-27.md")));
    }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~MemoryServiceCollectionExtensionsTests"`
Expected: FAIL — `AddMemory` does not exist (`CS1061`).

- [ ] **Step 3: Write the implementation**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OKF4net.Catalog.Hosting;

/// <summary>
/// Registers a scoped <see cref="IMemoryStore"/> built from the catalog's
/// <see cref="SourceRole.Memory"/> sources. Requires
/// <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/> to have
/// registered an <see cref="IKnowledgeCatalog"/>.
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IMemoryStore"/> (<see cref="FileMemoryStore"/>)
    /// whose per-tier roots are the catalog's currently-enabled
    /// <c>role:memory</c> sources, each resolved via
    /// <see cref="CatalogPathResolver.TryResolve"/>. This lot wires the user
    /// tier; a source that fails to resolve, or a tier not present in the
    /// manifest, is simply absent from the store.
    /// </summary>
    public static IServiceCollection AddMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMemoryStore>(sp =>
        {
            var catalog = sp.GetRequiredService<IKnowledgeCatalog>();
            var snapshot = catalog.Current;
            var tierRoots = new Dictionary<MemoryTier, string>();

            foreach (var source in snapshot.Sources)
            {
                if (!source.Enabled || source.Role != SourceRole.Memory || source.Tier is not { } tier)
                {
                    continue;
                }

                if (CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var resolved, out _))
                {
                    tierRoots[tier] = resolved!;
                }
            }

            return new FileMemoryStore(tierRoots);
        });

        return services;
    }
}
```

- [ ] **Step 4: Run it to confirm it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~MemoryServiceCollectionExtensionsTests"`
Expected: PASS.

- [ ] **Step 5: Full build + test + format**

Run: `dotnet build OKF4net.sln && dotnet test OKF4net.sln && dotnet format OKF4net.sln --verify-no-changes`
Expected: all green (whole solution).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Catalog.Hosting/MemoryServiceCollectionExtensions.cs tests/OKF4net.Tests/Catalog/Hosting/MemoryServiceCollectionExtensionsTests.cs
git commit -m "$(cat <<'EOF'
feat: AddMemory DI facade wiring a FileMemoryStore from role:memory sources

Registers IMemoryStore as a singleton whose per-tier roots are the
catalog's enabled role:memory sources resolved via CatalogPathResolver
(errors-as-data: an unresolvable source is skipped). Wires the user tier.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage.** Every locked decision maps to a task:
- Dependency edge `Agents → Catalog` — Task 4.1. Graph stays acyclic; `Catalog` gains no non-BCL/non-core reference.
- Core write primitive promotion (§4.1, §11.1) — Task 1.1 (`BundleConceptWriter`, refactor, behaviour-unchanged parity).
- `KnowledgeAccessScope` (§5.1) — Task 2.1.
- `SourceRole.Memory` + required `tier` + new diagnostics + one-per-tier (§5.3) — Task 2.2.
- `MemoryPath.For` readable prefixes + `_local` sentinel (§5.2) — Task 2.3.
- `IMemoryStore` incl. `DeleteScopeAsync`/`EnumerateAsync`; `IKnowledgeResolver` unchanged (§4, §6, §7) — Tasks 2.4, 3.1.
- `FileMemoryStore` user tier on the core primitive + ReparsePoints + producer validation; session/tenant staged (§4, §11.3) — Task 3.1.
- Provider evolves: split-budget READ (knowledge ∪ memory, floors + spillover), scoped capture WRITE to one tier, never-throws, message-data-only injection (§6, §8) — Task 4.2.
- Options: `ScopeAccessor`, `CaptureTier`=User, budget shares, `MemoryCapture` Disabled default, `MemoryDirectory` deprecated (§9) — Tasks 4.1, 4.2.
- Wiring/DI (§11.5) — Tasks 4.1 (reference) + 5.1 (`AddMemory`).
- Non-goals (multi-source fusion, hashed keys, session/tenant storage) — untouched; session/tenant remain contract/parse-only.
- Testing strategy (§10): scope isolation (3.1, 4.2), path derivation/sentinel/validation (2.1, 2.3), user-tier round-trip (3.1), manifest parser accept/reject (2.2), adapter E2E never-throw + injection-as-message (4.2). Covered.

**2. Placeholder scan.** No "TBD"/"similar to"/"add error handling" left; every code step carries complete C#. Task 1.1 Step 3 references "moved verbatim from OkfBundleTools" for the bodies of `AppendToConceptAtomic`/`ValidateConceptTarget`/`BuildValidatedContent`/`LateReparseGuard`/`WriteValidatedContentLocked` — these are an explicit *move of existing, cited source* (with the one documented `_bundle = null` → `_onWriteCommitted?.Invoke()` change), not an unspecified placeholder.

**3. Type consistency.** `BundleConceptWriter`, `KnowledgeAccessScope`, `MemoryTier`, `MemoryPath.For`, `IMemoryStore`, `MemoryEntry`, `MemoryReadResult`, `MemoryWriteResult`, `MemoryDeleteResult`, `MemoryConcept`, `FileMemoryStore`, `KnowledgePassage.SourceId` (`"memory:{tier}"`), `CatalogDiagnosticCode.IllegalTier`/`DuplicateMemoryTier`, `OkfContextProviderOptions.{ScopeAccessor,CaptureTier,KnowledgeBudgetShare,MemoryBudgetShare}`, and the provider's `UtcNow` seam are used identically across the tasks that define and consume them. `KnowledgePassage`/`KnowledgeDiagnostic`/`KnowledgeQuery` are the existing Catalog types, reused not redefined.

---

## Verification & arbitration record (pre-execution)

Every SDK/codebase symbol this plan assumes was verified against the real code
(`Microsoft.Agents.AI.Abstractions` 1.14.0 XML/DLL + already-compiling repo
usage). All confirmed except one, now fixed here; three design points were
arbitrated:

- **Fixed — `AgentSession` is abstract with protected ctors**, so `new
  AgentSession()` will not compile. The scoped tests use a
  `sealed class TestAgentSession : AgentSession {}` double (no abstract members;
  reference identity is all the `ConditionalWeakTable` needs). *(Baked into Task
  4.2 tests.)*
- **Arbitration A — enum rename** `MemoryCaptureMode.SharedBundle` → `Enabled`
  (reads correctly in V1 local and V2 scoped). *(Task 4.1.)*
- **Arbitration B — capture scope fallback**: when a `ScopeAccessor` is
  configured but the scope cannot be correlated (no session / no prior provide),
  the capture is **skipped** with `LastMemoryError`, never misfiled into
  `_local`; no accessor ⇒ local capture. *(Task 4.2 `StoreScopedAsync` + the
  `Scoped_capture_is_skipped…` test.)*
- **Arbitration C — default token split** knowledge/memory = **0.6 / 0.4**
  (knowledge slightly prioritized), with spillover. *(Task 4.1 defaults.)*

Confirmed-as-assumed (no change needed): `InvokingContext.Session` /
`InvokedContext.{Session,RequestMessages,ResponseMessages,InvokeException}`, the
`MAAI001`-experimental public ctors, `AIContext.{Instructions,Messages}`; the
promoted `OkfBundleTools` members incl. the exact `_bundle = null;` line; all
core (`ConceptSearch`/`Concept`/`Bundle`/`Frontmatter`/`ReparsePoints`/
`OkfEncodings`) and catalog (`CatalogPathResolver.TryResolve`,
`KnowledgeDiagnosticCode.SourceUnavailable`, `AddKnowledge`) symbols; and the
`appendix_a` fixture containing `tables/orders`.
