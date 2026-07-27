### Title: Add a performance baseline test for Bundle.Load on a large synthetic bundle
**Labels:** help wanted ; test
**Difficulty / est. effort:** ~half day, medium

**Context:** `ROADMAP.md`'s "Next" section lists "Performance baselines for large bundle loads," and there is currently no timing coverage at all for `Bundle.Load` (`src/OKF4net/Bundle.cs`) — every existing test in `tests/OKF4net.Tests/BundleTests.cs` uses small hand-written fixtures. Establishing a repeatable baseline (concepts/second, or wall-clock for N concepts) gives future contributors something to compare against before optimizing the walker/parser/link-scanner.
**Files to touch:** `tests/OKF4net.Tests/BundleLoadPerformanceTests.cs` (new), `src/OKF4net/Bundle.cs` (read-only, for reference)
**What to do:**
1. Add a new test file `tests/OKF4net.Tests/BundleLoadPerformanceTests.cs`. Use `TempDir` (`tests/OKF4net.Tests/TempDir.cs`) to synthesize a bundle with a meaningful number of concept files (e.g. 2,000-5,000 small `.md` files with valid frontmatter, spread across a few subdirectories, some containing cross-links to each other so `Bundle.Load`'s link-resolution path is exercised too), generated programmatically in the test's setup — not checked into `tests/fixtures/`.
2. Time `Bundle.Load(tmp.Path)` with `System.Diagnostics.Stopwatch`, and write the elapsed time and concepts/second to the xunit `ITestOutputHelper` (constructor-inject it, see other xunit test classes in the repo for the pattern if any exist, or add it fresh) so the number shows up in `dotnet test` verbose output.
3. Keep the test's pass/fail assertion generous and CI-machine-tolerant (e.g. "completes and returns the expected concept count" plus a loose upper-bound sanity assertion like "under some clearly-generous number of seconds") — the goal is a repeatable *measurement*, not a strict performance gate that flakes on slower CI runners.
4. Document the methodology in a comment at the top of the file (bundle size, shape, what's being measured) so future contributors can compare apples to apples when they optimize `Bundle.Load`.
**How to verify:** `dotnet test OKF4net.sln --filter "FullyQualifiedName~BundleLoadPerformanceTests" --logger "console;verbosity=detailed"` — expect the test to pass and the timing line to appear in the output.
**Good to know:** No new dependency needed — `Stopwatch` is BCL. Do not add a benchmarking package (e.g. BenchmarkDotNet); keep this in the existing xunit project per the zero-dependency spirit described in `CLAUDE.md`. See `CONTRIBUTING.md` for the build/test loop.
