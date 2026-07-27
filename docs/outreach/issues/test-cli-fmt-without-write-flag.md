### Title: Add a CliTests case for `okf fmt` without `-w` (stdout mode)
**Labels:** good first issue ; test
**Difficulty / est. effort:** ~30min, small

**Context:** `tests/OKF4net.Tests/CliTests.cs` has `Fmt_write_normalizes_file_in_place` for `okf fmt <file> -w`, but no test exercises the default (no `-w`) path where `CmdFmt` writes the normalized document to stdout and leaves the source file untouched (`src/OKF4net.Cli/OkfCli.cs`, `CmdFmt`, the `else` branch that calls `stdout.Write(outText)`). This is the CLI's default formatting mode and currently has no direct coverage.
**Files to touch:** `tests/OKF4net.Tests/CliTests.cs`, `src/OKF4net.Cli/OkfCli.cs` (read-only, for reference)
**What to do:**
1. In `tests/OKF4net.Tests/CliTests.cs`, add a new `[Fact]` near `Fmt_write_normalizes_file_in_place` (around line 141), e.g. `Fmt_without_write_flag_prints_to_stdout_and_leaves_file_untouched`.
2. Use `TempDir` (see `tests/OKF4net.Tests/TempDir.cs`) to write a small unformatted concept document, e.g. `tmp.Write("doc.md", "---\ntype: Thing\n---\nbody\n")`, capturing the original file bytes/text before running the command.
3. Run `okf fmt <path>` (no `-w`) via the existing `Run(...)` helper.
4. Assert exit code `0`, assert `r.Out` contains the normalized document body (mirroring the existing `-w` test's expectations), and assert the on-disk file content is unchanged from what was written in step 2 (no `"formatted"` message either, since that only prints in `-w` mode).
**How to verify:** `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests"` — expect the new test and the full `CliTests` class to pass.
**Good to know:** See `CONTRIBUTING.md` for build/test basics. `okf fmt` is documented in the README's `### As a CLI` section — the `-w` flag is optional, and the doc string in `OkfCli.cs`'s `Usage` constant already says `fmt <file> Normalize a document by parse + re-serialize (-w writes)`.
