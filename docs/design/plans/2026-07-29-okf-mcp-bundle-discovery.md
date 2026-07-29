# OKF-MCP Bundle Auto-Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `okf-mcp` finds its bundle by convention (walk up from CWD, marker = root `index.md` declaring `okf_version`) when neither a positional argument nor `OKF_BUNDLE_ROOT` is given.

**Architecture:** One new public static class `OkfBundleDiscovery` in `OKF4net.Mcp` holding the pure, injectable walk plus the production filesystem adapter; `OkfMcpConfig.TryResolve` gains an overload taking a `startDirectory` and falls back to discovery only when no explicit root is given. Existing precedence and the one-line-stderr startup-error contract are preserved. `Program.cs` is untouched (the existing overload delegates with the real CWD).

**Tech Stack:** C# / net10.0, xunit. Spec: `E:\Sources\okf-claude-plugin\docs\design\specs\2026-07-29-okf-claude-plugin-design.md` §1.

## Global Constraints

- Dependency policy: `OKF4net.Mcp` may reference only `ModelContextProtocol`, `Microsoft.Extensions.Hosting`, and project references already present. **No new packages.**
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces, XML doc comments on all public API, nullable enabled, `TreatWarningsAsErrors` (a missing XML doc fails the build).
- stdio invariant: stdout is JSON-RPC only; startup errors are a **single line** on stderr (no `\n`/`\r` in `FormatStartupError` output).
- Never touch `tests/fixtures/`.
- Behaviour cites OKF spec §11 (`okf_version` in root `index.md` frontmatter — the only sanctioned bundle marker).
- Do not use `OkfEncodings` (internal to `OKF4net`; `OKF4net.Mcp` has no `InternalsVisibleTo` grant and must not be added one for a one-line encoding constructor).
- Branch: create `feat/mcp-bundle-discovery` from up-to-date `dev`. Do not push without the user's go.

---

### Task 0: Branch setup

**Files:** none (git only)

- [ ] **Step 1: Sync dev and branch**

```bash
cd /e/Sources/okf
git checkout dev && git pull --ff-only origin dev
git checkout -b feat/mcp-bundle-discovery
```

- [ ] **Step 2: Verify clean baseline**

Run: `dotnet build OKF4net.sln`
Expected: build succeeds with 0 warnings.

---

### Task 1: `OkfBundleDiscovery` — pure walk, marker check, filesystem adapter

**Files:**

- Create: `src/OKF4net.Mcp/OkfBundleDiscovery.cs`
- Test: `tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs`

**Interfaces:**

- Consumes: `OKF4net.OkfDocument.TryParse(string, out OkfDocument?, out string?)`, `doc.Frontmatter.Get(string)` (never-null `Frontmatter`, returns null for an absent key).
- Produces (used by Task 2):
  - `public static bool OkfBundleDiscovery.TryDiscover(string startDirectory, Func<string, string?> readRootIndex, out string bundleRoot)`
  - `public static string? OkfBundleDiscovery.ReadRootIndexOrNull(string directory)`
  - `public const string OkfBundleDiscovery.IndexFilename = "index.md"`
  - `public const string OkfBundleDiscovery.ConventionChildName = "knowledge"`

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfBundleDiscoveryTests
{
    private const string Marked = "---\nokf_version: \"0.2\"\n---\n\n# Index\n";
    private const string Unmarked = "# Index\n";
    private const string FrontmatterWithoutVersion = "---\ntitle: Not a bundle\n---\n\n# Index\n";

    // Rooted, platform-neutral fake tree base. No real filesystem involved:
    // the walk sees only what the injected readRootIndex answers.
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "okf-disc-fake"));

    private static string At(params string[] parts) => Path.GetFullPath(Path.Combine([Base, .. parts]));

    private static Func<string, string?> Fs(params (string Dir, string IndexText)[] entries)
    {
        var map = entries.ToDictionary(e => e.Dir, e => e.IndexText, StringComparer.Ordinal);
        return dir => map.TryGetValue(Path.GetFullPath(dir), out var text) ? text : null;
    }

    [Fact]
    public void Start_directory_that_is_a_marked_bundle_wins()
    {
        var ok = OkfBundleDiscovery.TryDiscover(At("proj"), Fs((At("proj"), Marked)), out var root);

        Assert.True(ok);
        Assert.Equal(At("proj"), root);
    }

    [Fact]
    public void Knowledge_child_is_found_when_the_directory_itself_is_not_a_bundle()
    {
        var ok = OkfBundleDiscovery.TryDiscover(At("proj"), Fs((At("proj", "knowledge"), Marked)), out var root);

        Assert.True(ok);
        Assert.Equal(At("proj", "knowledge"), root);
    }

    [Fact]
    public void Directory_itself_beats_its_knowledge_child()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj"),
            Fs((At("proj"), Marked), (At("proj", "knowledge"), Marked)),
            out var root);

        Assert.True(ok);
        Assert.Equal(At("proj"), root);
    }

    [Fact]
    public void Nearest_level_beats_ancestors()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj", "sub"),
            Fs((At("proj", "sub", "knowledge"), Marked), (At("proj"), Marked)),
            out var root);

        Assert.True(ok);
        Assert.Equal(At("proj", "sub", "knowledge"), root);
    }

    [Fact]
    public void Walk_reaches_marked_ancestors()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj", "a", "b"),
            Fs((At("proj"), Marked)),
            out var root);

        Assert.True(ok);
        Assert.Equal(At("proj"), root);
    }

    [Fact]
    public void Index_without_okf_version_is_not_a_bundle()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj"),
            Fs((At("proj"), Unmarked), (At("proj", "knowledge"), FrontmatterWithoutVersion)),
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void No_marked_bundle_anywhere_returns_false()
    {
        var ok = OkfBundleDiscovery.TryDiscover(At("proj", "a", "b"), Fs(), out var root);

        Assert.False(ok);
        Assert.Equal(string.Empty, root);
    }

    [Fact]
    public void Marked_bundle_at_the_filesystem_root_is_found()
    {
        var fsRoot = Path.GetPathRoot(Base)!;

        var ok = OkfBundleDiscovery.TryDiscover(At("a", "b"), Fs((fsRoot, Marked)), out var root);

        Assert.True(ok);
        Assert.Equal(fsRoot, root);
    }

    [Fact]
    public void Empty_start_directory_returns_false_instead_of_throwing()
    {
        var ok = OkfBundleDiscovery.TryDiscover(string.Empty, Fs(), out var root);

        Assert.False(ok);
        Assert.Equal(string.Empty, root);
    }

    // ---- Production adapter (real filesystem) --------------------------------

    [Fact]
    public void Adapter_reads_root_index_text()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.md"), Marked);

            Assert.Equal(Marked, OkfBundleDiscovery.ReadRootIndexOrNull(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Adapter_returns_null_when_index_is_missing()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            Assert.Null(OkfBundleDiscovery.ReadRootIndexOrNull(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Adapter_returns_null_on_invalid_utf8()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "index.md"), [0xFF, 0xFE, 0xFA]);

            Assert.Null(OkfBundleDiscovery.ReadRootIndexOrNull(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void End_to_end_discovery_over_a_real_tree()
    {
        var top = Directory.CreateTempSubdirectory("okf-disc-e2e-").FullName;
        try
        {
            var knowledge = Directory.CreateDirectory(Path.Combine(top, "knowledge")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(top, "src", "deep")).FullName;
            File.WriteAllText(Path.Combine(knowledge, "index.md"), Marked, new UTF8Encoding(false));

            var ok = OkfBundleDiscovery.TryDiscover(nested, OkfBundleDiscovery.ReadRootIndexOrNull, out var root);

            Assert.True(ok);
            Assert.Equal(knowledge, root);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfBundleDiscoveryTests"`
Expected: compile error — `OkfBundleDiscovery` does not exist. (A compile failure of the test project is the failing state for a new-type task.)

- [ ] **Step 3: Write the implementation**

Create `src/OKF4net.Mcp/OkfBundleDiscovery.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Mcp;

/// <summary>
/// Convention-based discovery of an OKF bundle root: starting from a
/// directory and walking up to the filesystem root, the first candidate that
/// is a <em>marked</em> bundle wins. At each level the directory itself is
/// tested before its <c>knowledge/</c> child. A directory is a marked bundle
/// when its root <c>index.md</c> frontmatter declares <c>okf_version</c>
/// (§11) — the only zero-false-positive marker available, so unmarked
/// bundles are deliberately not discovered: a writable server must never
/// mistake an arbitrary docs directory for a bundle. The escape hatches are
/// the positional argument and <c>OKF_BUNDLE_ROOT</c>.
///
/// Symlink stance: the walk is purely lexical
/// (<see cref="Path.GetFullPath(string)"/> / <see cref="Path.GetDirectoryName(string)"/>
/// resolve no links, so there is no cycle risk), and reading a candidate's
/// <c>index.md</c> through a link mirrors <see cref="Bundle.OkfVersion"/>'s
/// existing stance. The library's reparse-point guards apply where they
/// always did — when the chosen root is actually loaded and served.
/// </summary>
public static class OkfBundleDiscovery
{
    /// <summary>Root index filename probed in each candidate directory.</summary>
    public const string IndexFilename = "index.md";

    /// <summary>Conventional child directory name probed at each level.</summary>
    public const string ConventionChildName = "knowledge";

    // Same strict UTF-8 as the library's internal OkfEncodings.Strict (no BOM,
    // throw on invalid bytes); reconstructed here because OKF4net.Mcp has no
    // InternalsVisibleTo grant and one constructor call does not warrant one.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Walks from <paramref name="startDirectory"/> up to the filesystem root
    /// looking for a marked bundle. Pure given
    /// <paramref name="readRootIndex"/> (candidate directory → its root
    /// <c>index.md</c> text, or <see langword="null"/> when absent or
    /// unreadable), so walk order and precedence are unit-testable without a
    /// filesystem; pass <see cref="ReadRootIndexOrNull"/> in production.
    /// </summary>
    /// <param name="startDirectory">Directory the walk starts from (made absolute first); an empty or invalid path yields <see langword="false"/>, never a throw.</param>
    /// <param name="readRootIndex">Candidate directory → root index text, or null.</param>
    /// <param name="bundleRoot">The discovered bundle root (empty when not found).</param>
    /// <returns><see langword="true"/> when a marked bundle was found.</returns>
    public static bool TryDiscover(string startDirectory, Func<string, string?> readRootIndex, out string bundleRoot)
    {
        bundleRoot = string.Empty;

        string? dir;
        try
        {
            dir = Path.GetFullPath(startDirectory);
        }
        catch (ArgumentException)
        {
            // Try-contract: an empty or malformed start path is "not found",
            // not an exception escaping a Try* method.
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }

        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var candidate in new[] { dir, Path.Combine(dir, ConventionChildName) })
            {
                var text = readRootIndex(candidate);
                if (text is not null && DeclaresOkfVersion(text))
                {
                    bundleRoot = candidate;
                    return true;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    /// <summary>
    /// Production <c>readRootIndex</c> accessor: reads
    /// <c>&lt;directory&gt;/index.md</c> as strict UTF-8, returning
    /// <see langword="null"/> on any read failure (missing file, I/O error,
    /// permission denied, invalid UTF-8) — the same "unreadable means no
    /// declared version" stance as <see cref="Bundle.OkfVersion"/>.
    /// </summary>
    /// <param name="directory">Candidate bundle root.</param>
    /// <returns>The root index text, or <see langword="null"/>.</returns>
    public static string? ReadRootIndexOrNull(string directory)
    {
        try
        {
            return StrictUtf8.GetString(File.ReadAllBytes(Path.Combine(directory, IndexFilename)));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool DeclaresOkfVersion(string indexText) =>
        OkfDocument.TryParse(indexText, out var doc, out _)
        && doc.Frontmatter.Get("okf_version") is not null;
}
```

Note: `FileNotFoundException` and `DirectoryNotFoundException` both derive from `IOException`, so the missing-file case is covered by the first catch.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfBundleDiscoveryTests"`
Expected: 13 passed.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Mcp/OkfBundleDiscovery.cs tests/OKF4net.Tests/Mcp/OkfBundleDiscoveryTests.cs
git commit -m "feat(mcp): convention-based bundle discovery (walk up, okf_version marker)"
```

---

### Task 2: Discovery fallback in `OkfMcpConfig.TryResolve`

**Files:**

- Modify: `src/OKF4net.Mcp/OkfMcpConfig.cs`
- Test: `tests/OKF4net.Tests/Mcp/OkfMcpConfigTests.cs`

**Interfaces:**

- Consumes: `OkfBundleDiscovery.TryDiscover` / `OkfBundleDiscovery.ReadRootIndexOrNull` (Task 1).
- Produces: two new overloads — `TryResolve(args, getEnv, string startDirectory, out ...)` (production reader) and `TryResolve(args, getEnv, string startDirectory, Func<string, string?> readRootIndex, out ...)` (fully injectable, used by hermetic tests). The existing 5-parameter overload keeps its signature and now delegates with `Directory.GetCurrentDirectory()`, so `Program.cs` needs **no change**.

- [ ] **Step 1: Update and extend the tests**

In `tests/OKF4net.Tests/Mcp/OkfMcpConfigTests.cs`:

Negative tests must not walk the machine's real directory ancestry (a marked bundle anywhere above the temp dir — `C:\`, `/tmp`, a developer's home — would make them fail spuriously), so they inject a reader that finds nothing: `_ => null` through the fully-injectable overload.

Replace the existing `Missing_root_fails_and_names_the_env_var` test with:

```csharp
    [Fact]
    public void Missing_root_with_no_discoverable_bundle_fails_and_names_every_fix()
    {
        var ok = OkfMcpConfig.TryResolve([], Env(), Path.GetTempPath(), _ => null, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("OKF_BUNDLE_ROOT", error);
        Assert.Contains("okf-init", error);
        Assert.DoesNotContain('\n', error);
    }
```

(The `Assert.NotNull(error)` is load-bearing: `error` is `out string?` without `[NotNullWhen(false)]`, and passing a maybe-null string to `Assert.Contains`/`Assert.DoesNotContain<char>` is CS8604 — an error under `TreatWarningsAsErrors`.)

Also update `Formatted_missing_root_error_is_a_single_line_with_message_and_usage` — its `TryResolve([], Env(), ...)` call would otherwise discover from the test runner's real CWD. New body (no temp dir needed):

```csharp
        OkfMcpConfig.TryResolve([], Env(), Path.GetTempPath(), _ => null, out _, out _, out var error);

        var line = OkfMcpConfig.FormatStartupError(error);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith("okf-mcp: ", line);
        Assert.Contains("OKF_BUNDLE_ROOT", line);
        Assert.Contains("Usage:", line);
```

Add the new discovery-behaviour tests to the same class:

```csharp
    private const string MarkedIndex = "---\nokf_version: \"0.2\"\n---\n\n# Index\n";

    [Fact]
    public void Discovery_supplies_the_root_when_no_arg_and_no_env()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        try
        {
            var knowledge = Directory.CreateDirectory(Path.Combine(top, "knowledge")).FullName;
            File.WriteAllText(Path.Combine(knowledge, "index.md"), MarkedIndex);

            var ok = OkfMcpConfig.TryResolve([], Env(), top, out var root, out var readOnly, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(knowledge, root);
            Assert.False(readOnly);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public void Env_root_beats_discovery()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        var explicitRoot = Directory.CreateTempSubdirectory("okf-cfg-env-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(top, "index.md"), MarkedIndex);

            var ok = OkfMcpConfig.TryResolve([], Env(("OKF_BUNDLE_ROOT", explicitRoot)), top, out var root, out _, out _);

            Assert.True(ok);
            Assert.Equal(explicitRoot, root);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
            Directory.Delete(explicitRoot, recursive: true);
        }
    }

    [Fact]
    public void Arg_beats_discovery()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        var explicitRoot = Directory.CreateTempSubdirectory("okf-cfg-arg-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(top, "index.md"), MarkedIndex);

            var ok = OkfMcpConfig.TryResolve([explicitRoot], Env(), top, out var root, out _, out _);

            Assert.True(ok);
            Assert.Equal(explicitRoot, root);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
            Directory.Delete(explicitRoot, recursive: true);
        }
    }

    [Fact]
    public void Nonexistent_env_root_fails_without_discovery_fallback()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(top, "index.md"), MarkedIndex);
            var missing = Path.Combine(Path.GetTempPath(), "okf-does-not-exist-" + Guid.NewGuid().ToString("N"));

            var ok = OkfMcpConfig.TryResolve([], Env(("OKF_BUNDLE_ROOT", missing)), top, out _, out _, out var error);

            Assert.False(ok);
            Assert.Contains("not found", error);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify the new ones fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpConfigTests"`
Expected: compile error — no `TryResolve` overload takes a `startDirectory`.

- [ ] **Step 3: Implement the overload and message changes**

In `src/OKF4net.Mcp/OkfMcpConfig.cs`:

First, keep the existing 5-parameter `TryResolve` but replace its body with a delegation, and add a `startDirectory` overload that plugs in the production reader:

```csharp
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
        => TryResolve(args, getEnv, Directory.GetCurrentDirectory(), out bundleRoot, out readOnly, out error);

    /// <summary>
    /// <see cref="TryResolve(IReadOnlyList{string}, Func{string, string?}, string, Func{string, string?}, out string, out bool, out string?)"/>
    /// with the production root-index reader
    /// (<see cref="OkfBundleDiscovery.ReadRootIndexOrNull"/>).
    /// </summary>
    /// <param name="args">Process arguments (positional bundle root at index 0).</param>
    /// <param name="getEnv">Environment-variable accessor.</param>
    /// <param name="startDirectory">Directory discovery walks up from when no explicit root is given.</param>
    /// <param name="bundleRoot">The resolved bundle root (empty on failure).</param>
    /// <param name="readOnly">Whether read-only mode is requested.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        string startDirectory,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
        => TryResolve(args, getEnv, startDirectory, OkfBundleDiscovery.ReadRootIndexOrNull, out bundleRoot, out readOnly, out error);
```

(Keep the 5-parameter overload's existing XML doc; add one sentence: "Discovery, when it applies, starts from the current working directory — see the `startDirectory` overload.")

Then add the fully-injectable overload containing the previous body plus the discovery fallback where the old code returned the "no bundle root given" error (the `readRootIndex` parameter is what lets negative tests stay hermetic):

```csharp
    /// <summary>
    /// Resolves configuration. The bundle root is the first positional
    /// argument, else the <c>OKF_BUNDLE_ROOT</c> environment variable, else a
    /// bundle discovered by <see cref="OkfBundleDiscovery.TryDiscover"/>
    /// walking up from <paramref name="startDirectory"/>. Discovery never
    /// overrides an explicit root: a nonexistent argument or environment root
    /// is still an error. Returns <see langword="false"/> with a
    /// human-readable <paramref name="error"/> when no root can be resolved.
    /// </summary>
    /// <param name="args">Process arguments (positional bundle root at index 0).</param>
    /// <param name="getEnv">Environment-variable accessor.</param>
    /// <param name="startDirectory">Directory discovery walks up from when no explicit root is given.</param>
    /// <param name="readRootIndex">Candidate directory → root index text accessor handed to discovery (injectable for hermetic tests).</param>
    /// <param name="bundleRoot">The resolved bundle root (empty on failure).</param>
    /// <param name="readOnly">Whether read-only mode is requested.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        string startDirectory,
        Func<string, string?> readRootIndex,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
    {
        bundleRoot = string.Empty;
        readOnly = TruthyValues.Contains(getEnv(ReadOnlyEnv)?.Trim() ?? string.Empty);

        var root = args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : getEnv(BundleRootEnv);

        if (string.IsNullOrWhiteSpace(root))
        {
            // Directory.Exists mirrors the explicit-root check below: the root
            // was just probed by discovery, but a delete in between must yield
            // the one-line error contract, not an exception at load time.
            if (OkfBundleDiscovery.TryDiscover(startDirectory, readRootIndex, out var discovered)
                && Directory.Exists(discovered))
            {
                bundleRoot = discovered;
                error = null;
                return true;
            }

            // ReplaceLineEndings guards the single-line stderr contract: a
            // (legal, on Unix) newline in the CWD path must not break it.
            error = $"no bundle root given and no marked bundle found from {startDirectory.ReplaceLineEndings(" ")} upward. "
                + $"Pass a root as the first argument, set {BundleRootEnv}, or run /okf-init (OKF Claude Code plugin) to mark or create a bundle.";
            return false;
        }

        root = root.Trim();
        if (!Directory.Exists(root))
        {
            error = $"bundle root not found: {root}";
            return false;
        }

        bundleRoot = root;
        error = null;
        return true;
    }
```

Finally, in `FormatStartupError`, update the usage suffix (single line preserved):

```csharp
        return $"okf-mcp: {message} Usage: okf-mcp <bundle-root> (or set {BundleRootEnv}, or run inside a bundle whose root index.md declares okf_version; {ReadOnlyEnv}=1 for read-only).";
```

- [ ] **Step 4: Run the MCP test suites**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Mcp"`
Expected: all pass (config, discovery, server suites).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Mcp/OkfMcpConfig.cs tests/OKF4net.Tests/Mcp/OkfMcpConfigTests.cs
git commit -m "feat(mcp): fall back to bundle discovery when no root is given"
```

---

### Task 3: Documentation and full verification

**Files:**

- Modify: `src/OKF4net.Mcp/README.md`
- Modify: `CHANGELOG.md` (Unreleased section)

**Interfaces:** none (docs only).

- [ ] **Step 1: Document the resolution order in the MCP README**

In `src/OKF4net.Mcp/README.md`, after the `## Use with Claude Desktop` section, add:

```markdown
## Bundle resolution order

`okf-mcp` resolves its bundle root in this order:

1. The first positional argument.
2. The `OKF_BUNDLE_ROOT` environment variable.
3. **Convention discovery**: starting from the current working directory and
   walking up, the first directory that is a *marked* bundle — testing at
   each level the directory itself, then its `knowledge/` child. A marked
   bundle has a root `index.md` whose frontmatter declares `okf_version`
   (§11).

Discovery is deliberately strict — an unmarked bundle (no `okf_version` in
its root `index.md`) is **not** discovered, so a writable server can never
mistake an arbitrary docs directory for a bundle. Mark the bundle (add
`okf_version` to the root `index.md` frontmatter, e.g. via the OKF Claude
Code plugin's `/okf-init`) or use an explicit root.

Note for Claude Desktop: Desktop spawns servers with an unrelated working
directory, so discovery does not apply there — keep the positional argument
or `OKF_BUNDLE_ROOT` in `claude_desktop_config.json`.
```

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, under `## [Unreleased]`, add an `### Added` section before the existing `### Fixed`:

```markdown
### Added

- **`okf-mcp` bundle auto-discovery** — when neither a positional root nor
  `OKF_BUNDLE_ROOT` is given, the server walks up from the current working
  directory looking for a marked bundle (a root `index.md` whose frontmatter
  declares `okf_version`, §11), testing each directory and then its
  `knowledge/` child. Unmarked bundles are deliberately not discovered (zero
  false positives for a writable server); the startup error now names every
  fix (argument, `OKF_BUNDLE_ROOT`, `/okf-init`).
```

- [ ] **Step 3: Full verification**

Run, expecting all three green:

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
```

(If `dotnet format` reports diffs, run `dotnet format OKF4net.sln` and re-verify.)

- [ ] **Step 4: Commit**

```bash
git add src/OKF4net.Mcp/README.md CHANGELOG.md
git commit -m "docs(mcp): document bundle resolution order and discovery"
```

---

## Out of scope

- Version bump / release (handled by the `release` skill; this feature rides the next minor release train).
- The Claude Code plugin itself (separate plan in the `okf4net-claude-plugin` repo).
- Any change to `IndexGenerator` (it still does not emit `okf_version`; marking existing bundles is `/okf-init`'s job per the design spec).
