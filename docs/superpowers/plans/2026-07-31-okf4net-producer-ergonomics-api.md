# OKF4net Producer-Ergonomics API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add four additive, zero-breaking-change API members to `src/OKF4net` (`Provenance.ToYaml`, `ConceptId.Slugify`, a `Frontmatter`-typed `BundleConceptWriter.WriteConcept` overload, and a new `OkfDocumentBuilder` fluent type) so a programmatic caller can construct and write an OKF concept entirely in memory, without a serialize/re-parse round trip through YAML text.

**Architecture:** Each of the four members is independently addable to existing (or, for the builder, one new) files in `src/OKF4net`. `OkfDocumentBuilder` is the only one with an internal dependency: it calls `Provenance.ToYaml` (Task 1) to serialize its accumulated `sources`. Tasks 1–3 have no dependency on each other and could be done in any order; Task 4 must come after Task 1.

**Tech Stack:** C# / .NET 10, xunit, zero third-party runtime dependencies (BCL only) — same as the rest of `src/OKF4net`.

**Spec:** `docs/superpowers/specs/2026-07-31-okf4net-producer-ergonomics-api-design.md` — every behavioral decision below traces back to a section of that spec; read it if a "why" is unclear here.

## Global Constraints

- Zero third-party runtime dependencies — `src/OKF4net` is BCL-only, no new `PackageReference`.
- Zero breaking changes — every change below is either a new file, a new public static method, a new public instance method overload, or a private-method refactor with no observable behavior change. No existing public signature changes.
- `TreatWarningsAsErrors` is on repo-wide (`Directory.Build.props`) — the build fails on any warning, including missing XML doc comments on public members.
- Every new/modified source file keeps its `// SPDX-License-Identifier: LGPL-3.0-or-later` header, file-scoped namespaces, `Nullable` enabled, XML doc comments on every public member.
- No new golden fixture is required or permitted for this work — none of it touches `tests/OKF4net.Cli` behavior covered by `tests/fixtures/`.
- Run `dotnet format OKF4net.sln` before every commit that touches `src/` or `tests/` (CI runs `dotnet format --verify-no-changes`).
- Test filter pattern used throughout: `dotnet test OKF4net.sln --filter "FullyQualifiedName~<ClassName>"`.

---

## Task 1: `Provenance.ToYaml`

**Files:**
- Modify: `src/OKF4net/Provenance.cs`
- Test: `tests/OKF4net.Tests/ProvenanceTests.cs`

**Interfaces:**
- Consumes: existing `Source` record (`Id`, `Resource`, `Title`, `Author`, `UsageCount`, `LastModified`), existing `Actor.Raw`, `OKF4net.Yaml.YamlMapping`/`YamlSequence`/`YamlString`/`YamlInt`.
- Produces: `public static YamlSequence Provenance.ToYaml(IEnumerable<Source> sources)` — Task 4 (`OkfDocumentBuilder.Build()`) calls this directly.

- [ ] **Step 1: Write the failing tests**

Add to `tests/OKF4net.Tests/ProvenanceTests.cs` (append inside the existing `ProvenanceTests` class, after `ParseUsageWindow_null_or_non_mapping_is_null`):

```csharp
    [Fact]
    public void ToYaml_round_trips_through_ParseSources_in_order()
    {
        var sources = new List<Source>
        {
            new(Id: "ga4-schema", Resource: "https://example.com/schema", Title: "GA4 schema",
                Author: Actor.Parse("team:ga4"), UsageCount: 5000, LastModified: "2026-05-30"),
            new(Id: null, Resource: "README.md", Title: null, Author: null, UsageCount: null, LastModified: null),
        };

        var yaml = Provenance.ToYaml(sources);
        var roundTripped = Provenance.ParseSources(yaml);

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal(sources[0], roundTripped[0]);
        Assert.Equal(sources[1], roundTripped[1]);
    }

    [Fact]
    public void ToYaml_omits_absent_optional_fields_from_the_mapping()
    {
        var yaml = Provenance.ToYaml([new Source(Id: null, Resource: "README.md", Title: null, Author: null, UsageCount: null, LastModified: null)]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.False(entry.ContainsKey("id"));
        Assert.True(entry.ContainsKey("resource"));
        Assert.False(entry.ContainsKey("title"));
        Assert.False(entry.ContainsKey("author"));
        Assert.False(entry.ContainsKey("usage_count"));
        Assert.False(entry.ContainsKey("last_modified"));
    }

    [Fact]
    public void ToYaml_uses_canonical_per_entry_key_order()
    {
        var yaml = Provenance.ToYaml([new Source(Id: "x", Resource: "y", Title: "z", Author: Actor.Parse("process:p"), UsageCount: 1, LastModified: "2026-01-01")]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.Equal(["id", "resource", "title", "author", "usage_count", "last_modified"], entry.Keys.ToList());
    }

    [Fact]
    public void ToYaml_serializes_author_via_actor_raw_for_every_actor_kind()
    {
        foreach (var raw in new[] { "human:alice", "process:etl-job", "team:ga4" })
        {
            var yaml = Provenance.ToYaml([new Source(Id: null, Resource: "r", Title: null, Author: Actor.Parse(raw), UsageCount: null, LastModified: null)]);
            var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
            Assert.Equal(raw, entry.Get("author")!.AsString());
        }
    }

    [Fact]
    public void ToYaml_enumerates_the_source_sequence_exactly_once()
    {
        var counting = new CountingSources([new Source(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: null)]);

        Provenance.ToYaml(counting);

        Assert.Equal(1, counting.EnumerationCount);
    }

    private sealed class CountingSources(IReadOnlyList<Source> items) : IEnumerable<Source>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<Source> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
```

Add `using System.Collections;` and `using System.Collections.Generic;` are already implicit (`ImplicitUsings` enabled) — no new `using` lines needed in this file beyond the existing `using OKF4net.Yaml;`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ProvenanceTests"`
Expected: FAIL to compile — `Provenance.ToYaml` does not exist yet.

- [ ] **Step 3: Implement `Provenance.ToYaml`**

In `src/OKF4net/Provenance.cs`, add this method to the `Provenance` static class, after `ParseUsageWindow`:

```csharp
    /// <summary>
    /// Serializes §5.1 provenance sources to the <see cref="YamlSequence"/> <see cref="ParseSources"/>
    /// reads back. Each entry uses the canonical key order <c>id, resource, title, author,
    /// usage_count, last_modified</c> (the order <see cref="ParseSources"/> itself reads them in); a
    /// <see langword="null"/> field on a <see cref="Source"/> is omitted from its mapping rather than
    /// written as an explicit YAML null. <paramref name="sources"/> is enumerated exactly once, and
    /// the order of its elements is preserved unchanged in the returned sequence (no sorting, no
    /// deduplication).
    /// </summary>
    public static YamlSequence ToYaml(IEnumerable<Source> sources)
    {
        var items = new List<YamlValue>();
        foreach (var source in sources)
        {
            var map = new YamlMapping();
            if (source.Id is not null)
            {
                map.Insert("id", new YamlString(source.Id));
            }

            map.Insert("resource", new YamlString(source.Resource));

            if (source.Title is not null)
            {
                map.Insert("title", new YamlString(source.Title));
            }

            if (source.Author is not null)
            {
                map.Insert("author", new YamlString(source.Author.Value.Raw));
            }

            if (source.UsageCount is not null)
            {
                map.Insert("usage_count", new YamlInt(source.UsageCount.Value));
            }

            if (source.LastModified is not null)
            {
                map.Insert("last_modified", new YamlString(source.LastModified));
            }

            items.Add(map);
        }

        return new YamlSequence(items);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ProvenanceTests"`
Expected: PASS (all `ProvenanceTests`, including the 5 new ones).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OKF4net.sln
git add src/OKF4net/Provenance.cs tests/OKF4net.Tests/ProvenanceTests.cs
git commit -m "feat(core): add Provenance.ToYaml, the serialize direction of ParseSources"
```

---

## Task 2: `ConceptId.Slugify`

**Files:**
- Modify: `src/OKF4net/ConceptId.cs`
- Test: `tests/OKF4net.Tests/ConceptIdTests.cs`

**Interfaces:**
- Consumes: existing private `ConceptId.IsValidFirstChar(char)`, `ConceptId.IsValidLaterChar(char)`, `OKF4net.Internal.UnicodeCaseFold.ToLowercase(string)`, `OKF4net.Internal.DebugQuote.Quote(string)` (already used elsewhere in this file for exception messages), `ConceptIdException(string)`.
- Produces: `public static string ConceptId.Slugify(string input)` — not consumed by any other task in this plan (the future `producers/OkfProducer` will use it, out of scope here).

- [ ] **Step 1: Write the failing tests**

Add to `tests/OKF4net.Tests/ConceptIdTests.cs` (append inside the existing `ConceptIdTests` class):

```csharp
    [Theory]
    [InlineData("My Package Name", "my-package-name")]
    [InlineData("  leading spaces", "leading-spaces")]
    [InlineData("3D Print", "3d-print")]
    [InlineData("café", "caf-")]
    [InlineData("my.package", "my.package")]
    [InlineData(".hidden", "hidden")]
    [InlineData("--double--dash--", "double-dash-")]
    [InlineData("already-valid_segment.ext", "already-valid_segment.ext")]
    [InlineData("🎉 emoji", "emoji")]
    public void Slugify_produces_expected_output_and_the_result_always_validates(string input, string expected)
    {
        var result = ConceptId.Slugify(input);

        Assert.Equal(expected, result);
        ConceptId.ValidateSegment(result); // must never throw for a Slugify() output
    }

    [Fact]
    public void Slugify_throws_when_nothing_survives_normalization()
    {
        var ex = Assert.Throws<ConceptIdException>(() => ConceptId.Slugify("!!!"));
        Assert.Contains("!!!", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Slugify_is_idempotent_on_an_already_valid_segment()
    {
        const string valid = "already-valid_segment.ext";
        Assert.Equal(valid, ConceptId.Slugify(valid));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptIdTests"`
Expected: FAIL to compile — `ConceptId.Slugify` does not exist yet.

- [ ] **Step 3: Implement `ConceptId.Slugify`**

In `src/OKF4net/ConceptId.cs`, add this method to the `ConceptId` class, right after `ValidateSegment` (before `IsValidFirstChar`):

```csharp
    /// <summary>
    /// Normalizes a free-form string into a segment that always passes <see cref="ValidateSegment"/>.
    ///
    /// Algorithm, in order: (1) full-Unicode case-fold via
    /// <see cref="OKF4net.Internal.UnicodeCaseFold.ToLowercase"/> (not <c>string.ToLowerInvariant</c>,
    /// which misses Final_Sigma and İ); (2) map each character to itself if it satisfies
    /// <see cref="IsValidLaterChar"/>, otherwise to <c>'-'</c>; (3) collapse every run of 2+ <c>'-'</c>
    /// (whether original or substituted) into one; (4) strip characters from the front while the
    /// first character fails <see cref="IsValidFirstChar"/> (a leading <c>'-'</c> or <c>'.'</c>) —
    /// nothing is trimmed from the end, since a trailing <c>'-'</c>/<c>'.'</c> is a valid
    /// <see cref="IsValidLaterChar"/>. Operates on <see cref="char"/> (UTF-16 code units, not code
    /// points): a surrogate pair (e.g. an emoji) simply becomes two adjacent substitutions, merged by
    /// step 3 like any other run.
    ///
    /// Does not attempt transliteration: a non-ASCII letter (e.g. an accented or non-Latin character)
    /// is replaced by <c>'-'</c>, not folded to an ASCII approximation — seeded from the ASCII-only
    /// rule <see cref="ValidateSegment"/> already enforces (see the design spec and the upstream
    /// issue tracking whether that restriction should ever be relaxed).
    /// </summary>
    /// <exception cref="ConceptIdException">The result, after normalization, is an empty string.</exception>
    public static string Slugify(string input)
    {
        // UnicodeCaseFold resolves via this file's existing `using OKF4net.Internal;` (same
        // using DebugQuote below already relies on) -- no extra qualification needed.
        var folded = UnicodeCaseFold.ToLowercase(input);

        var mapped = new System.Text.StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            mapped.Append(IsValidLaterChar(c) ? c : '-');
        }

        var collapsed = new System.Text.StringBuilder(mapped.Length);
        var previousWasDash = false;
        foreach (var c in mapped.ToString())
        {
            var isDash = c == '-';
            if (isDash && previousWasDash)
            {
                continue;
            }

            collapsed.Append(c);
            previousWasDash = isDash;
        }

        var candidate = collapsed.ToString();
        var start = 0;
        while (start < candidate.Length && !IsValidFirstChar(candidate[start]))
        {
            start++;
        }

        var result = candidate[start..];
        if (result.Length == 0)
        {
            throw new ConceptIdException($"Cannot derive a non-empty concept id segment from {DebugQuote.Quote(input)}.");
        }

        return result;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptIdTests"`
Expected: PASS (all `ConceptIdTests`, including the 3 new test methods / 11 total cases).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OKF4net.sln
git add src/OKF4net/ConceptId.cs tests/OKF4net.Tests/ConceptIdTests.cs
git commit -m "feat(core): add ConceptId.Slugify to derive a valid segment from a free-form title"
```

---

## Task 3: `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` overload

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs`
- Test: `tests/OKF4net.Tests/BundleConceptWriterTests.cs`

**Interfaces:**
- Consumes: existing `Frontmatter.AsMapping()`, existing private `BuildValidatedContent(YamlValue, string)`, `ValidateConceptTarget`, `WriteValidatedContentLocked`, `RunTool`, `_bundleLock`, `AutoStampGenerated`, `ProducerActor`, `UtcNow` (all already defined in this file).
- Produces: `public string BundleConceptWriter.WriteConcept(string conceptId, Frontmatter frontmatter, string body)` — Task 4's usage example (and the future producer) call this.

This task first does a **behavior-preserving refactor** of the existing private `ParseFrontmatterAndMaybeStamp`, splitting out the auto-stamp logic into its own method so the new overload can reuse it directly on an already-built mapping instead of re-parsing YAML text. The refactor changes no observable behavior of the existing string overload — verify this by running the full existing `BundleConceptWriterTests` suite before and after Step 3 below.

- [ ] **Step 1: Write the failing tests**

Add `using OKF4net.Yaml;` to the top of `tests/OKF4net.Tests/BundleConceptWriterTests.cs` (it currently has none), then add these methods inside the `BundleConceptWriterTests` class:

```csharp
    [Fact]
    public void WriteConcept_Frontmatter_overload_creates_a_validated_file()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var frontmatter = new Frontmatter();
        frontmatter.Set("type", new YamlString("BigQuery Table"));
        frontmatter.Set("title", new YamlString("Refunds"));
        frontmatter.Set("description", new YamlString("One row per refund."));

        var result = writer.WriteConcept("tables/refunds", frontmatter, "# Refunds\n\nBody.\n");

        Assert.Contains("Written", result);
        var path = Path.Combine(tmp.Path, "tables", "refunds.md");
        Assert.True(File.Exists(path));
        OkfDocument.Parse(File.ReadAllText(path)).Validate();
    }

    [Fact]
    public void WriteConcept_Frontmatter_overload_missing_required_frontmatter_writes_nothing()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var frontmatter = new Frontmatter();
        frontmatter.Set("type", new YamlString("X"));

        var result = writer.WriteConcept("tables/refunds", frontmatter, "# body\n");

        Assert.StartsWith("Error:", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "refunds.md")));
    }

    [Fact]
    public void WriteConcept_Frontmatter_overload_rejects_reserved_concept_id()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var frontmatter = new Frontmatter();
        frontmatter.Set("type", new YamlString("X"));

        var result = writer.WriteConcept("index", frontmatter, "# body\n");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void WriteConcept_Frontmatter_overload_updates_an_existing_concept()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var frontmatter = new Frontmatter();
        frontmatter.Set("type", new YamlString("BigQuery Table"));
        frontmatter.Set("title", new YamlString("Refunds"));
        frontmatter.Set("description", new YamlString("One row per refund."));

        writer.WriteConcept("tables/refunds", frontmatter, "# v1\n");
        var second = writer.WriteConcept("tables/refunds", frontmatter, "# v2\n");

        Assert.Contains("updated", second);
        var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "tables", "refunds.md"))).Body;
        Assert.Contains("v2", body, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConcept_Frontmatter_overload_auto_stamps_without_mutating_the_callers_frontmatter()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path)
        {
            AutoStampGenerated = true,
            UtcNow = () => new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
        };
        var frontmatter = new Frontmatter();
        frontmatter.Set("type", new YamlString("BigQuery Table"));
        frontmatter.Set("title", new YamlString("Refunds"));
        frontmatter.Set("description", new YamlString("One row per refund."));

        var result = writer.WriteConcept("tables/refunds", frontmatter, "# Refunds\n");

        Assert.StartsWith("Written", result);
        Assert.False(
            frontmatter.AsMapping().ContainsKey("generated"),
            "the caller's own Frontmatter object must not be mutated by auto-stamping");
        var written = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "tables", "refunds.md")));
        Assert.NotNull(written.Frontmatter.Generated);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterTests"`
Expected: FAIL to compile — `WriteConcept(string, Frontmatter, string)` does not exist yet.

- [ ] **Step 3: Refactor the existing auto-stamp helper, then add the new overload**

In `src/OKF4net/BundleConceptWriter.cs`, find the existing private method `ParseFrontmatterAndMaybeStamp` (currently parses YAML text and conditionally stamps in one method) and replace it with:

```csharp
    /// <summary>
    /// Parses <paramref name="frontmatterYaml"/> once and delegates the auto-stamp decision to
    /// <see cref="MaybeStampGenerated"/>. Returns the parsed (and possibly stamped) <see cref="YamlValue"/>
    /// for <see cref="BuildValidatedContent(YamlValue, string)"/> to validate and serialize directly —
    /// so the write path parses exactly once, never re-serializing and re-parsing a stamped mapping.
    /// Throws <see cref="Yaml.YamlParseException"/> on malformed frontmatter, caught by the caller's
    /// <see cref="RunTool"/> wrapper before anything is written.
    /// </summary>
    private YamlValue ParseFrontmatterAndMaybeStamp(string frontmatterYaml)
    {
        var parsed = YamlValue.Parse(frontmatterYaml);
        if (parsed is YamlMapping map)
        {
            MaybeStampGenerated(map);
        }

        return parsed;
    }

    /// <summary>
    /// When <see cref="AutoStampGenerated"/> is on and <paramref name="map"/> has no <c>generated</c>
    /// key of its own, stamps a <c>generated: { by, at }</c> block (§5.2) into it **in place** — the
    /// single stamping decision shared by the string-based <see cref="WriteConcept(string, string, string)"/>
    /// path (via <see cref="ParseFrontmatterAndMaybeStamp"/>, on a freshly parsed, caller-invisible
    /// mapping) and the <see cref="Frontmatter"/>-based <see cref="WriteConcept(string, Frontmatter, string)"/>
    /// overload (which passes a defensive copy — see that overload's remarks — precisely so this
    /// in-place mutation never reaches the caller's own <see cref="Frontmatter"/> object). No-op when
    /// the flag is off or a <c>generated</c> key is already present.
    /// </summary>
    private void MaybeStampGenerated(YamlMapping map)
    {
        if (AutoStampGenerated && !map.ContainsKey("generated"))
        {
            var generated = new YamlMapping();
            generated.Insert("by", new YamlString(ProducerActor));
            generated.Insert("at", new YamlString(OkfTimestamp.FormatUtc(UtcNow())));
            map.Insert("generated", generated);
        }
    }
```

Then add the new overload, directly after the existing `WriteConcept(string, string, string)` method:

```csharp
    /// <summary>
    /// Like <see cref="WriteConcept(string, string, string)"/>, but takes an already-built
    /// <see cref="Frontmatter"/> instead of pre-serialized YAML text — skips the serialize/re-parse
    /// round trip for a programmatic caller (e.g. <see cref="OkfDocumentBuilder"/>). Same
    /// producer-grade validation, per-bundle lock, and reparse-point guards as the string overload
    /// (both share <see cref="ValidateConceptTarget"/>, <see cref="BuildValidatedContent(YamlValue, string)"/>,
    /// and <see cref="WriteValidatedContentLocked"/>).
    ///
    /// Operates on a shallow copy of <paramref name="frontmatter"/>'s underlying mapping, never the
    /// caller's own <see cref="YamlMapping"/> instance — <see cref="Frontmatter.AsMapping"/> returns
    /// that instance directly (no defensive copy of its own), and mutating it in place (e.g. via
    /// auto-stamping, see <see cref="MaybeStampGenerated"/>) would otherwise silently modify an object
    /// the caller may still hold and inspect afterward.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatter">The frontmatter to write. Not mutated by this call.</param>
    /// <param name="body">The markdown body.</param>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

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

            var mapping = ShallowCopy(frontmatter.AsMapping());
            MaybeStampGenerated(mapping);

            var (content, buildError) = BuildValidatedContent(mapping, body);
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

    /// <summary>
    /// Copies <paramref name="map"/>'s entries into a fresh <see cref="YamlMapping"/>, preserving
    /// order and every entry verbatim (including a duplicate or non-string key, via
    /// <see cref="YamlMapping.PushRaw"/>) — a shallow copy, sufficient because
    /// <see cref="WriteConcept(string, Frontmatter, string)"/> only ever inserts a new top-level key
    /// into the copy, never mutates a nested value.
    /// </summary>
    private static YamlMapping ShallowCopy(YamlMapping map)
    {
        var copy = new YamlMapping();
        foreach (var (key, value) in map.Entries)
        {
            copy.PushRaw(key, value);
        }

        return copy;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleConceptWriterTests"`
Expected: PASS (all `BundleConceptWriterTests`, including the 5 new tests and every pre-existing test — confirming the `ParseFrontmatterAndMaybeStamp` refactor changed nothing observable).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OKF4net.sln
git add src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/BundleConceptWriterTests.cs
git commit -m "feat(core): add a Frontmatter-typed WriteConcept overload for programmatic callers"
```

---

## Task 4: `OkfDocumentBuilder`

**Files:**
- Create: `src/OKF4net/OkfDocumentBuilder.cs`
- Test: Create `tests/OKF4net.Tests/OkfDocumentBuilderTests.cs`

**Interfaces:**
- Consumes: `Provenance.ToYaml` (Task 1), existing `Frontmatter.FromMapping(YamlMapping)`, `OkfDocument(Frontmatter, string)` constructor, `OKF4net.Yaml.YamlMapping`/`YamlSequence`/`YamlString`, `Actor`, `Source`.
- Produces: `OkfDocumentBuilder.ForType(string)`, `.Title(string)`, `.Description(string)`, `.Resource(string)`, `.Tags(params string[])`, `.AddTags(params string[])`, `.AddSource(string, string?, string?, Actor?, long?, string?)`, `.Extension(string, YamlValue)`, `.Body(string)`, `.Build() -> OkfDocument` — not consumed by any other task in this plan; the future `producers/OkfProducer` is the intended caller, alongside Task 3's `WriteConcept(string, Frontmatter, string)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/OkfDocumentBuilderTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

public class OkfDocumentBuilderTests
{
    [Fact]
    public void Build_produces_expected_frontmatter_and_body_in_canonical_key_order()
    {
        var doc = OkfDocumentBuilder
            .ForType("CLI Tool")
            .Title("okfgen")
            .Description("Generates OKF bundles for repositories")
            .Resource("https://example.com/okfgen")
            .Tags("cli", "okf")
            .AddSource(resource: "README.md", title: "README")
            .AddSource(resource: "package.json")
            .Extension("custom_field", new YamlString("custom_value"))
            .Body("# Summary\n")
            .Build();

        Assert.Equal("# Summary\n", doc.Body);
        Assert.Equal(
            new[] { "type", "title", "description", "resource", "tags", "sources", "custom_field" },
            doc.Frontmatter.AsMapping().Keys.ToList());
        Assert.Equal("CLI Tool", doc.Frontmatter.Type);
        Assert.Equal("okfgen", doc.Frontmatter.Title);
        Assert.Equal("https://example.com/okfgen", doc.Frontmatter.Resource);
        Assert.Equal(2, doc.Frontmatter.Sources.Count);
        Assert.Equal("README.md", doc.Frontmatter.Sources[0].Resource);
        Assert.Equal("README", doc.Frontmatter.Sources[0].Title);
        Assert.Equal("package.json", doc.Frontmatter.Sources[1].Resource);
    }

    [Fact]
    public void Build_without_title_or_description_fails_strict_validate_but_passes_conformance()
    {
        var doc = OkfDocumentBuilder.ForType("CLI Tool").Body("body").Build();

        Assert.Throws<DocumentValidationException>(() => doc.Validate());
        doc.ValidateConformance(); // does not throw: §11 requires only `type`
    }

    [Fact]
    public void Build_without_body_throws()
    {
        var builder = OkfDocumentBuilder.ForType("CLI Tool");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Tags_overwrites_a_previous_Tags_call()
    {
        var doc = OkfDocumentBuilder.ForType("t").Tags("a", "b").Tags("c").Body("").Build();

        Assert.Equal(new[] { "c" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void AddTags_accumulates_across_calls()
    {
        var doc = OkfDocumentBuilder.ForType("t").AddTags("a").AddTags("b").Body("").Build();

        Assert.Equal(new[] { "a", "b" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Tags_after_AddTags_replaces_everything_regardless_of_call_order()
    {
        var doc = OkfDocumentBuilder.ForType("t").AddTags("a").Tags("b").Body("").Build();

        Assert.Equal(new[] { "b" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void AddTags_after_Tags_accumulates_on_top_of_the_base_list()
    {
        var doc = OkfDocumentBuilder.ForType("t").Tags("a").AddTags("b").Body("").Build();

        Assert.Equal(new[] { "a", "b" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Tags_never_called_omits_the_tags_key()
    {
        var doc = OkfDocumentBuilder.ForType("t").Body("").Build();

        Assert.False(doc.Frontmatter.AsMapping().ContainsKey("tags"));
    }

    [Fact]
    public void AddSource_never_called_omits_the_sources_key()
    {
        var doc = OkfDocumentBuilder.ForType("t").Body("").Build();

        Assert.False(doc.Frontmatter.AsMapping().ContainsKey("sources"));
    }

    [Fact]
    public void Extension_targeting_a_well_known_key_wins_over_the_typed_setter_regardless_of_call_order()
    {
        var doc = OkfDocumentBuilder.ForType("t")
            .Tags("a", "b")
            .Extension("tags", new YamlSequence([new YamlString("override")]))
            .Body("")
            .Build();

        Assert.Equal(new[] { "override" }, doc.Frontmatter.Tags);
    }

    [Fact]
    public void Build_is_idempotent_and_non_destructive()
    {
        var builder = OkfDocumentBuilder.ForType("t").Title("x").Body("body");

        var first = builder.Build();
        var second = builder.Build();

        Assert.Equal(first.Frontmatter, second.Frontmatter);
        Assert.Equal(first.Body, second.Body);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfDocumentBuilderTests"`
Expected: FAIL to compile — `OkfDocumentBuilder` does not exist yet.

- [ ] **Step 3: Implement `OkfDocumentBuilder`**

Create `src/OKF4net/OkfDocumentBuilder.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>
/// Fluent, in-memory builder for an <see cref="OkfDocument"/> — for a programmatic caller (e.g. a
/// producer) that constructs a concept entirely in memory, as an alternative to hand-writing YAML
/// frontmatter text. Does not validate; call <see cref="OkfDocument.Validate"/> or
/// <see cref="OkfDocument.ValidateConformance"/> on the built document explicitly.
/// </summary>
public sealed class OkfDocumentBuilder
{
    private readonly string _type;
    private string? _title;
    private string? _description;
    private string? _resource;
    private readonly List<string> _tags = [];
    private readonly List<Source> _sources = [];
    private readonly YamlMapping _extensions = new();
    private string? _body;

    private OkfDocumentBuilder(string type) => _type = type;

    /// <summary>Starts a new builder for a concept of the given <c>type</c> — §4.1's one required field.</summary>
    public static OkfDocumentBuilder ForType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new OkfDocumentBuilder(type);
    }

    /// <summary>Sets (overwriting any previous value) the <c>title</c> field.</summary>
    public OkfDocumentBuilder Title(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        _title = title;
        return this;
    }

    /// <summary>Sets (overwriting any previous value) the <c>description</c> field.</summary>
    public OkfDocumentBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _description = description;
        return this;
    }

    /// <summary>Sets (overwriting any previous value) the <c>resource</c> field.</summary>
    public OkfDocumentBuilder Resource(string resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _resource = resource;
        return this;
    }

    /// <summary>
    /// Replaces the entire accumulated tag list — including anything a prior <see cref="AddTags"/>
    /// call added — with <paramref name="tags"/>, in the given order. This happens regardless of
    /// whether the <see cref="AddTags"/> call came before or after this one in the fluent chain.
    /// Call <see cref="AddTags"/> instead to accumulate rather than replace.
    /// </summary>
    public OkfDocumentBuilder Tags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.Clear();
        _tags.AddRange(tags);
        return this;
    }

    /// <summary>
    /// Appends to the accumulated tag list, in call order. Call <see cref="Tags"/> instead to
    /// replace the whole list.
    /// </summary>
    public OkfDocumentBuilder AddTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags.AddRange(tags);
        return this;
    }

    /// <summary>
    /// Appends one §5.1 provenance source, in call order. Does not validate <paramref name="resource"/>
    /// (producer-grade validation stays in <see cref="BundleValidator"/>/<see cref="OkfDocument.Validate"/>).
    /// </summary>
    public OkfDocumentBuilder AddSource(
        string resource,
        string? id = null,
        string? title = null,
        Actor? author = null,
        long? usageCount = null,
        string? lastModified = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _sources.Add(new Source(id, resource, title, author, usageCount, lastModified));
        return this;
    }

    /// <summary>
    /// Sets an arbitrary frontmatter key — a producer-defined extension key, or (with no collision
    /// guard) one of the well-known keys also covered by a typed method above. See <see cref="Build"/>'s
    /// remarks for the resulting key order and what a collision resolves to.
    /// </summary>
    public OkfDocumentBuilder Extension(string key, YamlValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _extensions.Insert(key, value);
        return this;
    }

    /// <summary>Sets (overwriting any previous value) the document body. Mandatory before <see cref="Build"/>.</summary>
    public OkfDocumentBuilder Body(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _body = body;
        return this;
    }

    /// <summary>
    /// Builds an <see cref="OkfDocument"/> from the accumulated state. Idempotent and non-destructive:
    /// may be called more than once on the same builder; each call returns a fresh document
    /// reflecting the builder's current state at that moment. Does not validate.
    ///
    /// Key order is fixed, not call order: <c>type, title, description, resource, tags, sources</c>
    /// (the subset of <see cref="Frontmatter.KnownKeys"/>'s own order this builder covers — only
    /// present when the corresponding field was set, except <c>type</c> which is always present),
    /// followed by any <see cref="Extension"/> keys in their own call order. Because
    /// <see cref="Extension"/> is always applied after the six well-known keys, an
    /// <see cref="Extension"/> call targeting one of them (e.g. <c>Extension("tags", ...)</c>) always
    /// wins over the corresponding typed setter's value, regardless of the two calls' order in the
    /// fluent chain — a deliberate simplification (fixed application order, not call-order tracking),
    /// not a bug.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Body"/> was never called.</exception>
    public OkfDocument Build()
    {
        if (_body is null)
        {
            throw new InvalidOperationException("OkfDocumentBuilder: Body(...) must be called before Build().");
        }

        var map = new YamlMapping();
        map.Insert("type", new YamlString(_type));

        if (_title is not null)
        {
            map.Insert("title", new YamlString(_title));
        }

        if (_description is not null)
        {
            map.Insert("description", new YamlString(_description));
        }

        if (_resource is not null)
        {
            map.Insert("resource", new YamlString(_resource));
        }

        if (_tags.Count > 0)
        {
            map.Insert("tags", new YamlSequence(_tags.Select(t => (YamlValue)new YamlString(t)).ToList()));
        }

        if (_sources.Count > 0)
        {
            map.Insert("sources", Provenance.ToYaml(_sources));
        }

        foreach (var (key, value) in _extensions.Entries)
        {
            map.Insert(key.AsString()!, value);
        }

        return new OkfDocument(Frontmatter.FromMapping(map), _body);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfDocumentBuilderTests"`
Expected: PASS (all 11 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OKF4net.sln
git add src/OKF4net/OkfDocumentBuilder.cs tests/OKF4net.Tests/OkfDocumentBuilderTests.cs
git commit -m "feat(core): add OkfDocumentBuilder, a fluent in-memory OkfDocument builder"
```

---

## Task 5: CHANGELOG entry and full-suite verification

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:** None — this task adds documentation and re-verifies the whole solution; it introduces no new code.

- [ ] **Step 1: Add the CHANGELOG entry**

Open `CHANGELOG.md` and, under the `[Unreleased]` heading's `### Added` subsection (create that subsection under `[Unreleased]` if it does not already exist, following the existing file's own heading style), add:

```markdown
- `Provenance.ToYaml`, `ConceptId.Slugify`, a `Frontmatter`-typed `BundleConceptWriter.WriteConcept`
  overload, and `OkfDocumentBuilder`: producer-facing API for constructing and writing an OKF concept
  entirely in memory, without a serialize/re-parse round trip through YAML text. Motivated by the
  upcoming native OKF producer (`producers/`), usable independently by any programmatic caller.
```

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS, full suite green (no regressions from Tasks 1–4).

- [ ] **Step 3: Verify formatting is clean**

Run: `dotnet format OKF4net.sln --verify-no-changes`
Expected: exits 0, no output (nothing to reformat).

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note the new producer-ergonomics API under Unreleased"
```

---

## Self-Review Notes (for the plan author, not a task to execute)

- **Spec coverage:** §3.1 → Task 1. §3.2 → Task 2. §3.3 → Task 3. §3.4 → Task 4. §5 (test plan) → each task's Step 1. §6 (CHANGELOG) → Task 5. §7 (constraints) → Global Constraints section above + each task's file-placement (§7's "Emplacement des fichiers" line matches every task's **Files** block exactly).
- **No placeholders:** every step above has complete, real C# — no "add appropriate tests" or "similar to Task N" shorthand.
- **Type/signature consistency check:** `Source` is used with its actual positional record-struct order (`Id, Resource, Title, Author, UsageCount, LastModified`) in both Task 1's tests and Task 4's `AddSource`/`Build`. `Provenance.ToYaml(IEnumerable<Source>)` (Task 1) is the exact signature Task 4's `Build()` calls. `WriteConcept(string, Frontmatter, string)` (Task 3) is the exact signature Task 4's doc comment references (not called by any task's code here, only documented as the intended pairing).
