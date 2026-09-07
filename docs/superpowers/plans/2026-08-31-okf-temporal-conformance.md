# OKF Temporal Conformance (`stale_after`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make OKF4net read and validate spec-conformant ISO 8601 timestamps (`2026-06-30T14:00:00Z`) for `stale_after`, `generated.at` and `verified[].at`, while still accepting the legacy date-only form with a warning.

**Architecture:** `Lifecycle` stops parsing `stale_after` as a `DateOnly` and parses it as an instant (`DateTimeOffset`), normalizing a legacy date-only value to midnight UTC and flagging it. `IOkfClock` gains a `Now` instant as a **default interface member** derived from `Today`, so no existing implementer breaks. Staleness comparison moves to instants; every public entry point keeps a `DateOnly` overload so call sites compile unchanged. The validator gains one new `Warning` code for the legacy form, following the exact pattern already used for `timestamp` → `generated.at` (§13.1).

**Tech Stack:** C# / net10.0, xunit, zero third-party runtime dependencies (BCL only in `src/OKF4net`).

**Spec:** This plan implements a conformance fix against the upstream OKF v0.2 spec §5, not a local design doc. The defect and its blast radius are recorded in `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md` §6.1 (the note block), which is where it was discovered.

## Global Constraints

- **Zero third-party runtime dependencies** in `src/OKF4net`, `src/OKF4net.Cli`, `src/OKF4net.Catalog`, `src/OKF4net.Attestation`. BCL only. Test-only packages are fine.
- **Warnings are errors.** `Directory.Build.props` sets `TreatWarningsAsErrors`; `dotnet build OKF4net.sln` must be clean.
- **Every new source file starts with** `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- **File-scoped namespaces, XML doc comments on all public API, nullable enabled.**
- **Never edit `tests/fixtures/` to make a failing test pass.** The one exception this plan uses is spelled out in Task 6 and must cite §5 in `tests/fixtures/README.md`.
- **Spec text this plan implements, verbatim** (upstream `okf/SPEC.md` §5): *"Every timestamp-valued key in OKF is an ISO 8601 datetime with an explicit UTC offset, for example `2026-06-30T14:00:00Z`."*
- **§5.5 boundary, which must not change:** a concept is stale when `now >= stale_after` — the boundary instant is **already stale**.
- Verification commands: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln`, `dotnet format OKF4net.sln --verify-no-changes`.

---

## The decision this plan encodes, and why

**Accept the legacy date-only form, warn on it. Do not reject it.**

Rejecting would turn every existing bundle — including `bundles/` and `tests/fixtures/` — into an invalid one, and it contradicts §11's permissive-loading principle. The codebase already has this exact pattern twice: `timestamp` → `generated.at` and body `# Citations` → frontmatter `sources` are both v0.2-conformant fallbacks that surface as a `Warning` (`DiagnosticCode.LegacyTimestamp`, `DiagnosticCode.LegacyCitations`). This fix is the third instance of the same pattern and must look like the other two.

**One deliberate breaking change**, documented in the CHANGELOG: `Lifecycle.StaleAfter` changes type from `DateOnly?` to `DateTimeOffset?`. The library is at 0.x. Every method that took a `DateOnly` keeps a `DateOnly` overload, so the common call sites (`IsStale(today)`, `Admits(lc, today)`) compile unchanged; only code that reads `.StaleAfter` and does date arithmetic on it must adapt.

---

## File Structure

**Modified:**
- `src/OKF4net/Lifecycle.cs` — instant-based parsing, legacy flag, `IsStale` overloads.
- `src/OKF4net/IOkfClock.cs` — `Now` as a default interface member; `SystemClock.Now`; `FixedClock(DateTimeOffset)` overload.
- `src/OKF4net/StalePolicy.cs` — grace period as a `TimeSpan` over instants, `DateOnly` overload kept.
- `src/OKF4net/Validate.cs` — new `DiagnosticCode.LegacyDateOnlyTimestamp`; emit it for `stale_after`, `generated.at`, `verified[].at`.
- `src/OKF4net/Audit.cs` — `Freshness` keeps rendering `yyyy-MM-dd` (golden-locked); `ConceptAudit.Run` compares instants.
- `tests/fixtures/okf_v02/metrics/dau.md` — migrated to the conformant form (Task 6).
- `tests/fixtures/golden/validate-computation.out` — gains the new legacy warning (Task 6).
- `tests/fixtures/README.md` — documents both, citing §5.
- `README.md`, `CHANGELOG.md` — the new diagnostic and the breaking change.

**Test files:**
- `tests/OKF4net.Tests/LifecycleTests.cs` (extend)
- `tests/OKF4net.Tests/StalePolicyTests.cs` (extend)
- `tests/OKF4net.Tests/ValidateTests.cs` (extend)
- `tests/OKF4net.Tests/AuditTests.cs` (extend)

**Not modified:** `src/OKF4net.Cli/JsonOutput.cs` exposes `StaleAfterRaw` (a `string`), so it is unaffected — Task 5 verifies this rather than assuming it.

---

### Task 1: `Lifecycle` parses instants and flags the legacy form

**Files:**
- Modify: `src/OKF4net/Lifecycle.cs:25-51`
- Test: `tests/OKF4net.Tests/LifecycleTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `Lifecycle` record struct, positional params now `(ConceptStatus Status, bool StatusIsKnown, string? StaleAfterRaw, DateTimeOffset? StaleAfter)`
  - `bool Lifecycle.StaleAfterMalformed { get; }` (unchanged meaning)
  - `bool Lifecycle.StaleAfterIsLegacyDate { get; }` — true when `StaleAfterRaw` parsed **only** as a bare `YYYY-MM-DD`
  - `bool Lifecycle.IsStale(DateTimeOffset asOf)`
  - `bool Lifecycle.IsStale(DateOnly asOf)` — lifts `asOf` to midnight UTC
  - `DateOnly? Lifecycle.StaleAfterDate { get; }` — `StaleAfter?.UtcDateTime` truncated to its date, for rendering
  - `static Lifecycle Lifecycle.From(string? statusRaw, string? staleAfterRaw)` (unchanged signature)

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/LifecycleTests.cs`:

```csharp
    [Fact]
    public void Conformant_instant_stale_after_is_parsed_and_goes_stale()
    {
        // §5: "Every timestamp-valued key in OKF is an ISO 8601 datetime with
        // an explicit UTC offset". Before this fix the value below failed to
        // parse, so IsStale silently returned false forever.
        var lc = Lifecycle.From(null, "2026-06-30T14:00:00Z");

        Assert.False(lc.StaleAfterMalformed);
        Assert.False(lc.StaleAfterIsLegacyDate);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 14, 0, 0, TimeSpan.Zero), lc.StaleAfter);
        Assert.True(lc.IsStale(new DateTimeOffset(2026, 6, 30, 14, 0, 0, TimeSpan.Zero)));  // §5.5 boundary is inclusive
        Assert.True(lc.IsStale(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(lc.IsStale(new DateTimeOffset(2026, 6, 30, 13, 59, 59, TimeSpan.Zero)));
    }

    [Fact]
    public void Non_utc_offset_is_honoured_not_ignored()
    {
        // 2026-06-30T14:00:00+02:00 is 12:00Z, so 13:00Z is already past it.
        var lc = Lifecycle.From(null, "2026-06-30T14:00:00+02:00");

        Assert.True(lc.IsStale(new DateTimeOffset(2026, 6, 30, 13, 0, 0, TimeSpan.Zero)));
        Assert.False(lc.IsStale(new DateTimeOffset(2026, 6, 30, 11, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Legacy_date_only_stale_after_still_parses_and_is_flagged()
    {
        var lc = Lifecycle.From(null, "2026-07-01");

        Assert.False(lc.StaleAfterMalformed);
        Assert.True(lc.StaleAfterIsLegacyDate);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), lc.StaleAfter);
        Assert.Equal(new DateOnly(2026, 7, 1), lc.StaleAfterDate);
    }

    [Fact]
    public void Legacy_date_only_keeps_the_inclusive_day_boundary()
    {
        // The DateOnly overload must behave exactly as it did before this
        // change: "today == stale_after" is stale. CliTests, AuditTests and
        // OkfAuditToolTests all lock this boundary.
        var lc = Lifecycle.From(null, "2026-07-01");

        Assert.True(lc.IsStale(new DateOnly(2026, 7, 1)));
        Assert.False(lc.IsStale(new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void An_instant_stale_after_is_stale_for_the_whole_day_under_the_DateOnly_overload()
    {
        // The DateOnly overload lifts to midnight UTC, so a stale_after later
        // that same day is NOT yet stale at midnight. This is the one visible
        // semantic sharpening; it is correct and must be asserted, not hidden.
        var lc = Lifecycle.From(null, "2026-07-01T23:00:00Z");

        Assert.False(lc.IsStale(new DateOnly(2026, 7, 1)));
        Assert.True(lc.IsStale(new DateOnly(2026, 7, 2)));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-07-01T25:00:00Z")]
    [InlineData("")]
    public void Genuinely_malformed_stale_after_is_still_rejected(string raw)
    {
        var lc = Lifecycle.From(null, raw);

        Assert.True(lc.StaleAfterMalformed);
        Assert.Null(lc.StaleAfter);
        Assert.False(lc.IsStale(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_datetime_without_an_offset_is_treated_as_UTC_and_flagged_legacy()
    {
        // §5 requires an explicit offset. We still read it (permissive, §11),
        // assume UTC, and flag it so the validator can warn.
        var lc = Lifecycle.From(null, "2026-07-01T12:00:00");

        Assert.False(lc.StaleAfterMalformed);
        Assert.True(lc.StaleAfterIsLegacyDate);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), lc.StaleAfter);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~LifecycleTests"`
Expected: compile errors — `StaleAfterIsLegacyDate` and `StaleAfterDate` do not exist, and `IsStale(DateTimeOffset)` has no overload.

- [ ] **Step 3: Replace the body of `Lifecycle.cs`**

Replace lines 19-52 of `src/OKF4net/Lifecycle.cs` with:

```csharp
/// <summary>
/// A concept's lifecycle fields (§5.4/§5.5): <c>status</c> and <c>stale_after</c>.
/// Parsing is lenient — an unknown status resolves to <see cref="ConceptStatus.Stable"/> with
/// <see cref="StatusIsKnown"/> false, and a malformed <c>stale_after</c> leaves
/// <see cref="StaleAfter"/> null with <see cref="StaleAfterMalformed"/> true. The validator warns on both.
/// </summary>
/// <remarks>
/// §5 requires every timestamp-valued key to be an ISO 8601 datetime with an
/// explicit UTC offset (<c>2026-06-30T14:00:00Z</c>). A bare <c>YYYY-MM-DD</c>,
/// or a datetime with no offset, is still read — normalized to midnight UTC and
/// to UTC respectively — but sets <see cref="StaleAfterIsLegacyDate"/> so the
/// validator can warn, in the same way the §13.1 legacy fields do.
/// </remarks>
public readonly record struct Lifecycle(ConceptStatus Status, bool StatusIsKnown, string? StaleAfterRaw, DateTimeOffset? StaleAfter)
{
    /// <summary>True when a <c>stale_after</c> value is present but could not be parsed at all.</summary>
    public bool StaleAfterMalformed => StaleAfterRaw is not null && StaleAfter is null;

    /// <summary>
    /// True when <c>stale_after</c> parsed but not in the §5 form — a bare
    /// <c>YYYY-MM-DD</c>, or a datetime carrying no explicit offset.
    /// </summary>
    public bool StaleAfterIsLegacyDate { get; private init; }

    /// <summary>The UTC calendar date of <see cref="StaleAfter"/>, for rendering. Null when it did not parse.</summary>
    public DateOnly? StaleAfterDate =>
        StaleAfter is { } d ? DateOnly.FromDateTime(d.UtcDateTime) : null;

    /// <summary>Whether the concept is stale as of <paramref name="asOf"/> (§5.5: <c>now &gt;= stale_after</c>).</summary>
    public bool IsStale(DateTimeOffset asOf) => StaleAfter is { } d && asOf >= d;

    /// <summary>
    /// Whether the concept is stale as of <paramref name="asOf"/>, taken as
    /// midnight UTC on that date. Preserves the day-granular behaviour of
    /// callers that hold a <see cref="DateOnly"/>.
    /// </summary>
    public bool IsStale(DateOnly asOf) =>
        IsStale(new DateTimeOffset(asOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    /// <summary>Builds a <see cref="Lifecycle"/> from raw <c>status</c> and <c>stale_after</c> display strings.</summary>
    public static Lifecycle From(string? statusRaw, string? staleAfterRaw)
    {
        var (status, known) = statusRaw switch
        {
            null => (ConceptStatus.Stable, true),
            "draft" => (ConceptStatus.Draft, true),
            "stable" => (ConceptStatus.Stable, true),
            "deprecated" => (ConceptStatus.Deprecated, true),
            _ => (ConceptStatus.Stable, false),
        };

        var (instant, legacy) = ParseStaleAfter(staleAfterRaw);

        return new Lifecycle(status, known, staleAfterRaw, instant) { StaleAfterIsLegacyDate = legacy };
    }

    /// <summary>
    /// Parses <c>stale_after</c> into an instant. Returns the §5 form as-is,
    /// lifts a bare date to midnight UTC and a zoneless datetime to UTC (both
    /// flagged legacy), and returns <c>(null, false)</c> for anything else.
    /// </summary>
    private static (DateTimeOffset? Instant, bool Legacy) ParseStaleAfter(string? raw)
    {
        if (raw is null)
        {
            return (null, false);
        }

        // The §5 form: an ISO 8601 datetime carrying an explicit offset.
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var withOffset)
            && HasExplicitOffset(raw))
        {
            return (withOffset.ToUniversalTime(), false);
        }

        // Legacy: a bare YYYY-MM-DD calendar date, read as midnight UTC.
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), true);
        }

        // Legacy: a datetime with no offset, assumed UTC.
        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var naive))
        {
            return (new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc), TimeSpan.Zero), true);
        }

        return (null, false);
    }

    /// <summary>
    /// Whether the raw value ends in an explicit zone designator (<c>Z</c>, or
    /// <c>±hh:mm</c>). <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// happily supplies the local offset for a zoneless value, so the raw text
    /// is the only reliable way to tell the two apart.
    /// </summary>
    private static bool HasExplicitOffset(string raw)
    {
        var s = raw.AsSpan().TrimEnd();
        if (s.Length == 0)
        {
            return false;
        }

        if (s[^1] is 'Z' or 'z')
        {
            return true;
        }

        // ±hh:mm — look only past the date part, so the date's own hyphens
        // are never mistaken for a negative offset.
        for (var i = 10; i < s.Length; i++)
        {
            if (s[i] is '+' or '-')
            {
                return true;
            }
        }

        return false;
    }
}
```

Add `using System.Globalization;` — it is already at the top of the file (line 2); leave it.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~LifecycleTests"`
Expected: PASS. The rest of the solution will not compile yet — that is Tasks 3 and 5.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Lifecycle.cs tests/OKF4net.Tests/LifecycleTests.cs
git commit -m "fix(lifecycle): parse stale_after as a §5 instant, flag the legacy date-only form"
```

---

### Task 2: `IOkfClock` gains an instant without breaking implementers

**Files:**
- Modify: `src/OKF4net/IOkfClock.cs`
- Test: `tests/OKF4net.Tests/LifecycleTests.cs` (append; the clock has no test file of its own)

**Interfaces:**
- Consumes: `Lifecycle.IsStale(DateTimeOffset)` from Task 1.
- Produces:
  - `DateTimeOffset IOkfClock.Now { get; }` — a **default interface member**, so existing implementers that define only `Today` keep compiling
  - `SystemClock.Now => DateTimeOffset.UtcNow`
  - `FixedClock(DateTimeOffset instant)` — new constructor overload; `FixedClock(DateOnly today)` is kept

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/LifecycleTests.cs`:

```csharp
    [Fact]
    public void FixedClock_pinned_to_an_instant_exposes_both_Now_and_Today()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero), clock.Now);
        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
    }

    [Fact]
    public void FixedClock_pinned_to_a_date_reports_midnight_UTC_as_Now()
    {
        var clock = new FixedClock(new DateOnly(2026, 7, 1));

        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), clock.Now);
    }

    [Fact]
    public void A_clock_implementing_only_Today_still_satisfies_the_interface()
    {
        // Guards the default interface member: an external implementer written
        // against the pre-fix interface must keep compiling and working.
        IOkfClock clock = new TodayOnlyClock(new DateOnly(2026, 7, 1));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), clock.Now);
    }

    private sealed class TodayOnlyClock(DateOnly today) : IOkfClock
    {
        public DateOnly Today { get; } = today;
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~LifecycleTests"`
Expected: compile errors — `IOkfClock.Now` does not exist and `FixedClock` has no `DateTimeOffset` constructor.

- [ ] **Step 3: Rewrite `IOkfClock.cs`**

Replace the whole of `src/OKF4net/IOkfClock.cs` with:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>Supplies the current date and instant, so staleness checks (§5.5) are testable and deterministic.</summary>
public interface IOkfClock
{
    /// <summary>Today's date (UTC for <see cref="SystemClock"/>).</summary>
    DateOnly Today { get; }

    /// <summary>
    /// The current instant, in UTC. §5 makes <c>stale_after</c> an instant, so
    /// staleness is an instant comparison. Defaults to midnight UTC on
    /// <see cref="Today"/> so that an implementer written before this member
    /// existed keeps working unchanged; implementations that know the time of
    /// day should override it.
    /// </summary>
    DateTimeOffset Now => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

/// <summary>The real wall-clock, in UTC.</summary>
public sealed class SystemClock : IOkfClock
{
    /// <inheritdoc/>
    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    /// <inheritdoc/>
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>
/// An <see cref="IOkfClock"/> pinned to one instant. Every API that takes a
/// clock — <see cref="BundleValidator.Validate"/>, <see cref="ConceptAudit.Run"/> —
/// exists to make staleness (§5.5) reproducible; without a shipped pinned
/// clock every caller wanting that has to write this same small type.
/// </summary>
public sealed class FixedClock : IOkfClock
{
    /// <summary>Pins the clock to <paramref name="instant"/>.</summary>
    /// <param name="instant">The instant <see cref="Now"/> returns.</param>
    public FixedClock(DateTimeOffset instant) => Now = instant.ToUniversalTime();

    /// <summary>Pins the clock to midnight UTC on <paramref name="today"/>.</summary>
    /// <param name="today">The date <see cref="Today"/> returns.</param>
    public FixedClock(DateOnly today)
        : this(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
    {
    }

    /// <inheritdoc/>
    public DateTimeOffset Now { get; }

    /// <inheritdoc/>
    public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~LifecycleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/IOkfClock.cs tests/OKF4net.Tests/LifecycleTests.cs
git commit -m "feat(clock): add IOkfClock.Now as a default interface member"
```

---

### Task 3: `StalePolicy` grace period over instants

**Files:**
- Modify: `src/OKF4net/StalePolicy.cs:26-36`
- Test: `tests/OKF4net.Tests/StalePolicyTests.cs`

**Interfaces:**
- Consumes: `Lifecycle.StaleAfter` (`DateTimeOffset?`) and `Lifecycle.IsStale` from Task 1.
- Produces:
  - `bool StalePolicy.Admits(Lifecycle lc, DateTimeOffset now)`
  - `bool StalePolicy.Admits(Lifecycle lc, DateOnly today)` — kept, lifts to midnight UTC

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/StalePolicyTests.cs`:

```csharp
    [Fact]
    public void Tolerate_counts_grace_days_from_the_instant_not_the_date()
    {
        // stale_after 2026-08-01T18:00Z + 10 days of grace = 2026-08-11T18:00Z.
        var lc = Lifecycle.From(null, "2026-08-01T18:00:00Z");
        var policy = StalePolicy.Tolerate(10);

        Assert.True(policy.Admits(lc, new DateTimeOffset(2026, 8, 11, 17, 59, 0, TimeSpan.Zero)));
        Assert.False(policy.Admits(lc, new DateTimeOffset(2026, 8, 11, 18, 0, 1, TimeSpan.Zero)));
    }

    [Fact]
    public void Strict_excludes_a_concept_past_a_conformant_stale_after()
    {
        var lc = Lifecycle.From(null, "2026-08-01T00:00:00Z");

        Assert.False(StalePolicy.Strict.Admits(lc, new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(StalePolicy.Strict.Admits(lc, new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Use_admits_everything_including_a_conformant_stale_concept()
    {
        var lc = Lifecycle.From(null, "2020-01-01T00:00:00Z");

        Assert.True(StalePolicy.Use.Admits(lc, DateTimeOffset.UtcNow));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~StalePolicyTests"`
Expected: compile error — no `Admits(Lifecycle, DateTimeOffset)` overload.

- [ ] **Step 3: Replace `Admits`**

In `src/OKF4net/StalePolicy.cs`, replace lines 29-36 with:

```csharp
    /// <summary>Whether a concept with lifecycle <paramref name="lc"/> should be surfaced as of <paramref name="now"/>.</summary>
    public bool Admits(Lifecycle lc, DateTimeOffset now) => Mode switch
    {
        StaleMode.Use => true,
        StaleMode.Strict => !lc.IsStale(now),
        StaleMode.Tolerate => lc.StaleAfter is not { } d || now <= d.AddDays(GraceDays),
        _ => true,
    };

    /// <summary>
    /// Whether a concept with lifecycle <paramref name="lc"/> should be surfaced
    /// as of midnight UTC on <paramref name="today"/>. Preserves the day-granular
    /// behaviour of callers that hold a <see cref="DateOnly"/>.
    /// </summary>
    public bool Admits(Lifecycle lc, DateOnly today) =>
        Admits(lc, new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~StalePolicyTests"`
Expected: PASS, including the pre-existing `DateOnly` tests.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/StalePolicy.cs tests/OKF4net.Tests/StalePolicyTests.cs
git commit -m "fix(stale-policy): compute the grace period over instants"
```

---

### Task 4: The validator warns on the legacy temporal form

**Files:**
- Modify: `src/OKF4net/Validate.cs` — the `DiagnosticCode` enum (near line 103), the concept loop (near lines 300-310 and 400-406), and `IsIso8601DateTime` (line 618)
- Test: `tests/OKF4net.Tests/ValidateTests.cs`

**Interfaces:**
- Consumes: `Lifecycle.StaleAfterIsLegacyDate` from Task 1.
- Produces:
  - `DiagnosticCode.LegacyDateOnlyTimestamp` — one code, `Field` distinguishes `stale_after` / `generated.at` / `verified.at`, exactly as `MissingRecommendedField` does
  - `static bool BundleValidator.IsConformantInstant(string s)` — public, so the producer and other consumers can reuse the one spelling

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/ValidateTests.cs`:

```csharp
    [Fact]
    public void A_conformant_instant_stale_after_produces_no_temporal_warning()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2099-01-01T00:00:00Z'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_legacy_date_only_stale_after_warns_but_still_parses()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2099-01-01'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "stale_after");
    }

    [Fact]
    public void A_legacy_date_only_generated_at_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated:\n  by: tool:okfgen\n  at: '2026-01-01'\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "generated.at");
    }

    [Fact]
    public void A_conformant_generated_at_produces_no_temporal_warning()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated:\n  by: tool:okfgen\n  at: '2026-01-01T00:00:00Z'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.GeneratedInvalidDate);
    }

    [Fact]
    public void A_truly_malformed_stale_after_is_still_invalid_not_legacy()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: 'not-a-date'\n");

        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ValidateTests"`
Expected: compile error — `DiagnosticCode.LegacyDateOnlyTimestamp` does not exist.

- [ ] **Step 3: Add the diagnostic code**

In `src/OKF4net/Validate.cs`, immediately after the `StaleAfterInvalid` member (line 103), insert:

```csharp
    /// <summary>
    /// A timestamp-valued key uses the legacy date-only form (or a datetime
    /// with no explicit offset) instead of the §5 ISO 8601 datetime with an
    /// explicit UTC offset. Read as a fallback, like the §13.1 legacy fields.
    /// </summary>
    LegacyDateOnlyTimestamp,
```

- [ ] **Step 4: Add the shared conformance check**

In `src/OKF4net/Validate.cs`, immediately after `IsIso8601DateTime` (line 623), insert:

```csharp
    /// <summary>
    /// Whether <paramref name="s"/> is a §5-conformant timestamp: an ISO 8601
    /// datetime carrying an explicit UTC offset (<c>2026-06-30T14:00:00Z</c>).
    /// A bare date or a zoneless datetime is readable but not conformant —
    /// <see cref="IsIso8601DateTime"/> stays the permissive check, this one is
    /// the conformance check.
    /// </summary>
    /// <param name="s">The raw frontmatter value.</param>
    public static bool IsConformantInstant(string s) =>
        !Lifecycle.From(null, s).StaleAfterMalformed
        && !Lifecycle.From(null, s).StaleAfterIsLegacyDate;
```

- [ ] **Step 5: Emit the warning for `stale_after`**

In `src/OKF4net/Validate.cs`, replace the block at lines 400-407 with:

```csharp
            if (lc.StaleAfterMalformed)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"stale_after is not an ISO-8601 datetime: {DebugQuote.Quote(lc.StaleAfterRaw!)}", DiagnosticCode.StaleAfterInvalid, "stale_after"));
            }
            else
            {
                if (lc.StaleAfterIsLegacyDate)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"stale_after {DebugQuote.Quote(lc.StaleAfterRaw!)} is a legacy date-only value; §5 wants an ISO-8601 datetime with an explicit UTC offset", DiagnosticCode.LegacyDateOnlyTimestamp, "stale_after"));
                }

                if (lc.IsStale(today))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"concept is stale (stale_after {lc.StaleAfterRaw})", DiagnosticCode.ConceptStale, "stale_after"));
                }
            }
```

> **Note on the `StaleAfterInvalid` message.** Its text changes from ``"stale_after is not `YYYY-MM-DD`"`` to `"stale_after is not an ISO-8601 datetime"`. `tests/OKF4net.Tests/ValidateTests.cs:659` asserts only on the prefix `"stale_after is not"`, so it still passes — verify this rather than assuming it, and check `tests/fixtures/golden/` for the old wording with:
> `grep -rn "stale_after is not" tests/fixtures/`

- [ ] **Step 6: Emit the warning for `generated.at` and `verified[].at`**

In `src/OKF4net/Validate.cs`, after the existing `GeneratedInvalidDate` check (around line 307-310), append inside the same `if (gen is { } g)` block:

```csharp
                if (g.At is { } gatConformance && IsIso8601DateTime(gatConformance) && !IsConformantInstant(gatConformance))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"generated.at {DebugQuote.Quote(gatConformance)} is a legacy date-only value; §5 wants an ISO-8601 datetime with an explicit UTC offset", DiagnosticCode.LegacyDateOnlyTimestamp, "generated.at"));
                }
```

And immediately after the existing `verified` stamp check at line 324, inside that loop:

```csharp
                if (stamp.At is { } vatConformance && IsIso8601DateTime(vatConformance) && !IsConformantInstant(vatConformance))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"verified.at {DebugQuote.Quote(vatConformance)} is a legacy date-only value; §5 wants an ISO-8601 datetime with an explicit UTC offset", DiagnosticCode.LegacyDateOnlyTimestamp, "verified.at"));
                }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ValidateTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net/Validate.cs tests/OKF4net.Tests/ValidateTests.cs
git commit -m "feat(validate): warn on the legacy date-only temporal form (§5)"
```

---

### Task 5: Bring every consumer to instants

**Files:**
- Modify: `src/OKF4net/Audit.cs:211` and `:248`
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs:268`, `:1152`
- Modify: `src/OKF4net.Attestation/AttestationOrchestrator.cs:195`
- Verify only: `src/OKF4net.Catalog/KnowledgePassage.cs`, `src/OKF4net.Cli/JsonOutput.cs:73,203`, the five `src/OKF4net.Catalog/*Resolver*.cs`
- Test: `tests/OKF4net.Tests/AuditTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: no new public API. `AuditVocabulary.Freshness` keeps its exact output format.

> **The constraint that governs this whole task:** `AuditVocabulary.Freshness` renders `yyyy-MM-dd` and its output is captured byte-for-byte in `tests/fixtures/golden/audit-v02.out` and `audit-v02.json`. **The rendering must stay `yyyy-MM-dd`.** Use the new `StaleAfterDate` for it. Do not "improve" the format.

- [ ] **Step 1: Write the failing test**

Append to `tests/OKF4net.Tests/AuditTests.cs`:

```csharp
    [Fact]
    public void Audit_detects_staleness_from_a_conformant_instant_stale_after()
    {
        // Before this fix, a §5-conformant stale_after failed to parse, so the
        // concept was reported fresh forever. This is the defect, end to end.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstale_after: 2026-01-01T00:00:00Z\n---\n");

        var report = ConceptAudit.Run(Bundle.Load(tmp.Path), new AuditQuery(), new FixedClock(new DateOnly(2026, 8, 21)));

        Assert.True(report.Findings.Single().IsStale);
        Assert.Equal(1, report.StaleCount);
    }

    [Fact]
    public void Freshness_still_renders_a_bare_date_for_a_conformant_instant()
    {
        // Golden-locked format: tests/fixtures/golden/audit-v02.out captures
        // "fresh 2099-01-01". Changing this rendering breaks the goldens.
        var lc = Lifecycle.From(null, "2099-01-01T00:00:00Z");

        Assert.Equal("fresh 2099-01-01", AuditVocabulary.Freshness(lc, isStale: false));
    }
```

> `TempDir` and the `ConceptAudit.Run` argument order are the ones already used in this file — read `tests/OKF4net.Tests/AuditTests.cs:50-75` first and match them exactly rather than trusting this snippet's shape.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~AuditTests"`
Expected: compile error in `Audit.cs` — `date.ToString("yyyy-MM-dd", ...)` no longer type-checks the same way, and `IsStale(asOf)` now binds to the `DateOnly` overload.

- [ ] **Step 3: Fix `AuditVocabulary.Freshness`**

In `src/OKF4net/Audit.cs`, replace the body of `Freshness` (lines 210-213) with:

```csharp
    public static string Freshness(Lifecycle lifecycle, bool isStale) =>
        lifecycle.StaleAfterDate is { } date
            ? (isStale ? "stale " : "fresh ") + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "no-stale-after";
```

- [ ] **Step 4: Compile the whole solution and fix each remaining call site**

Run: `dotnet build OKF4net.sln`

Work through the errors one at a time. The expected set, and the fix for each:

| Location | Fix |
|---|---|
| `src/OKF4net/Audit.cs:248` | `lifecycle.IsStale(asOf)` — if `asOf` is a `DateOnly`, it now binds the `DateOnly` overload and is correct as-is. Prefer threading `clock.Now` through and using the instant overload; keep `AuditReport.AsOf` as-is so the JSON golden does not move. |
| `src/OKF4net.Agents/OkfBundleTools.cs:268`, `:1152` | `lc.IsStale(Today)` / `lc.IsStale(today)` compile unchanged via the `DateOnly` overload. Verify, change nothing. |
| `src/OKF4net.Attestation/AttestationOrchestrator.cs:195` | `lifecycle.StaleAfter is not { } staleAfter` — `staleAfter` is now a `DateTimeOffset`. Read the surrounding comparison and switch it to the instant. This is a §10.6 gate: getting it wrong lets a stale computation through. |
| `src/OKF4net.Catalog/KnowledgePassage.cs` | Carries `Lifecycle` whole; expected to compile unchanged. Verify. |
| `src/OKF4net.Cli/JsonOutput.cs:73,203` | Exposes `StaleAfterRaw`, a `string`. Expected to compile unchanged, and the JSON golden must not move. Verify. |

- [ ] **Step 5: Run the full suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS except, possibly, the golden parity tests — those are Task 6. Note exactly which golden files differ and carry that list into Task 6.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net/Audit.cs src/OKF4net.Agents src/OKF4net.Attestation tests/OKF4net.Tests/AuditTests.cs
git commit -m "fix: compare staleness on instants across audit, agents and attestation"
```

---

### Task 6: Fixtures and goldens

**Files:**
- Modify: `tests/fixtures/okf_v02/metrics/dau.md:18`
- Modify: `tests/fixtures/golden/validate-computation.out`
- Modify: `tests/fixtures/README.md`
- Leave unchanged: `tests/fixtures/okf_v02_computation/computations/revenue.md:17`

**Interfaces:** none — this task changes data, not code.

> **This is the one place this plan touches `tests/fixtures/`.** CLAUDE.md forbids editing fixtures to make a failing test pass, with a narrow exception for a deliberate spec change that alters the captured output, **citing the spec section**. This qualifies — §5 — but the citation is mandatory, and the two fixtures get deliberately different treatment so both paths stay covered:
> - `okf_v02/metrics/dau.md` **migrates** to the conformant form → covers the §5 path.
> - `okf_v02_computation/computations/revenue.md` **stays** date-only → covers the legacy fallback and its new warning.

- [ ] **Step 1: Migrate the v0.2 fixture to the conformant form**

In `tests/fixtures/okf_v02/metrics/dau.md`, change line 18 from:

```yaml
stale_after: 2099-01-01
```

to:

```yaml
stale_after: 2099-01-01T00:00:00Z
```

- [ ] **Step 2: Run the golden parity tests and read every diff**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests"`

Expected, and each must be confirmed by eye before proceeding:
- `validate-v02.out` — **unchanged**. The concept is now conformant, so it gains no warning.
- `audit-v02.out` / `audit-v02.json` — **unchanged**. `Freshness` still renders `fresh 2099-01-01` (Task 5, Step 3).
- `validate-computation.out` — **changed**: `revenue.md` keeps the date-only form and now earns one `LegacyDateOnlyTimestamp` warning.

If any golden other than `validate-computation.out` moved, **stop**: that is a regression in the C# side, not a fixture to update. Investigate before continuing.

- [ ] **Step 3: Update the one golden that legitimately changed**

Regenerate `tests/fixtures/golden/validate-computation.out` from the current CLI output, then diff it against the previous version and confirm the **only** change is the added warning line for `stale_after` on `computations/revenue`. Also check whether `validate-computation.exitcode` moves — it should not, because the new diagnostic is a `Warning`, not an `Error`.

- [ ] **Step 4: Document both fixtures**

In `tests/fixtures/README.md`, near the existing description of these bundles (around lines 100 and 118), add:

```markdown
### Temporal form (§5)

OKF v0.2 §5 requires every timestamp-valued key to be an ISO 8601 datetime with
an explicit UTC offset (`2026-06-30T14:00:00Z`). OKF4net reads the legacy
date-only form as a fallback and warns (`LegacyDateOnlyTimestamp`), in the same
way it handles the §13.1 legacy fields.

These two fixtures deliberately cover both paths and must not be made uniform:

- `okf_v02/metrics/dau.md` carries the **conformant** form
  (`stale_after: 2099-01-01T00:00:00Z`). Revised on 2026-08-31 from the previous
  date-only value, under the CLAUDE.md exception for a deliberate spec change,
  citing §5.
- `okf_v02_computation/computations/revenue.md` keeps the **legacy** date-only
  form on purpose, so `validate-computation.out` captures the fallback warning.
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS, all of it.

- [ ] **Step 6: Commit**

```bash
git add tests/fixtures/
git commit -m "test(fixtures): cover both §5 temporal forms, migrate dau.md, document the exception"
```

---

### Task 7: Documentation and changelog

**Files:**
- Modify: `README.md` (the diagnostic-code table, if one exists — check first)
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md` (remove the item if this fix was listed there)

**Interfaces:** none.

- [ ] **Step 1: Find the diagnostic table**

Run: `grep -n "StaleAfterInvalid\|LegacyTimestamp\|DiagnosticCode" README.md`

If a table of diagnostic codes exists, add `LegacyDateOnlyTimestamp` in the same style and position (alphabetical or grouped by section — match what is there). If no table exists, skip to Step 2 rather than inventing one.

- [ ] **Step 2: Write the changelog entry**

In `CHANGELOG.md`, under the `Unreleased` heading:

```markdown
### Fixed

- **`stale_after` now reads the spec-conformant timestamp form.** OKF v0.2 §5
  requires every timestamp-valued key to be an ISO 8601 datetime with an
  explicit UTC offset (`2026-06-30T14:00:00Z`). `Lifecycle` previously parsed
  `stale_after` only as a bare `YYYY-MM-DD`, so a conformant value was reported
  as malformed and **staleness was never computed for it** — a concept past its
  expiry silently read as fresh. The legacy date-only form is still accepted and
  now raises a `LegacyDateOnlyTimestamp` warning, matching how the §13.1 legacy
  fields are handled. The same warning covers `generated.at` and `verified[].at`.

### Changed

- **Breaking (0.x):** `Lifecycle.StaleAfter` is now a `DateTimeOffset?` rather
  than a `DateOnly?`, and staleness is compared on instants. `IsStale` and
  `StalePolicy.Admits` keep their `DateOnly` overloads, so day-granular callers
  compile unchanged; code reading `Lifecycle.StaleAfter` directly must adapt, or
  use the new `Lifecycle.StaleAfterDate`.
- `IOkfClock` gains `Now` (a `DateTimeOffset`) as a **default interface member**,
  so existing implementers that define only `Today` keep working. `FixedClock`
  gains a `DateTimeOffset` constructor.
```

- [ ] **Step 3: Verify the whole build**

Run, all three, all must be clean:

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
```

- [ ] **Step 4: Commit**

```bash
git add README.md CHANGELOG.md ROADMAP.md
git commit -m "docs: record the §5 temporal conformance fix and its breaking change"
```

---

## Out of scope for this plan

- **`sources[].last_modified` and `log.md` date headings.** Both go through `ChangeLog.IsIsoDate` and are plausibly genuine calendar dates rather than instants. Establish what §5 and §9 actually require for them before changing anything; if they are instants too, that is a follow-up plan, not a silent extension of this one.
- **The producer's `generated.at` emission.** Covered by `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md` §6.1 and its own plan.
