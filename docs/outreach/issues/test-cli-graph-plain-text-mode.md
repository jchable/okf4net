### Title: Add a CliTests case for `okf graph` plain-text mode (no `--dot`)
**Labels:** good first issue ; test
**Difficulty / est. effort:** ~30min, small

**Context:** `tests/OKF4net.Tests/CliTests.cs` covers `okf graph --dot` extensively (`Graph_dot_prints_digraph`, `Graph_dot_styles_broken_links_dashed_and_red`, `Graph_dot_does_not_style_resolvable_links`) but has no test for the default plain-text mode. `CmdGraph`'s `else` branch in `src/OKF4net.Cli/OkfCli.cs` (around lines 358-376) prints each concept id followed by its outgoing links, marked `->` for a resolvable link and `-x` for a broken one — this behavior is currently unverified by any test.
**Files to touch:** `tests/OKF4net.Tests/CliTests.cs`, `src/OKF4net.Cli/OkfCli.cs` (read-only, for reference)
**What to do:**
1. Add a new `[Fact]` in `tests/OKF4net.Tests/CliTests.cs` near the existing `Graph_dot_*` tests (around line 119), e.g. `Graph_plain_text_prints_arrows_and_broken_link_markers`.
2. Using `TempDir`, build a small bundle with one concept linking to an existing concept and one linking to a non-existent target, similar to the setup in `Graph_dot_styles_broken_links_dashed_and_red` but without `--dot`.
3. Run `okf graph <bundle>` (no `--dot` flag).
4. Assert exit code `0`, assert the output contains the source concept id on its own line, a line with the `->` marker for the resolvable link, and a line with the `-x` marker for the broken link (matching the format built in `CmdGraph`: `"  {mark} {link.Target}\n"`).
**How to verify:** `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests"` — expect the new test and the full `CliTests` class to pass.
**Good to know:** See `CONTRIBUTING.md` for build/test basics. The README's `### As a CLI` section documents `graph` as printing "the cross-link graph (--dot for Graphviz DOT)" — this issue covers the non-`--dot` default.
