### Title: Document the bare-word `help`/`version` command aliases in CLI usage text
**Labels:** good first issue ; documentation
**Difficulty / est. effort:** ~20min, small

**Context:** `OkfCli.Run` (`src/OKF4net.Cli/OkfCli.cs`) accepts `help` and `version` as bare positional commands, not just `-h`/`--help` and `-V`/`--version` (see the `case "-h" or "--help" or "help":` and `case "-V" or "--version" or "version":` switch arms around lines 75-82). But the `Usage` constant's `OPTIONS:` section only documents the flag forms (`-h, --help` and `-V, --version`) — the bare-word aliases (`okf help`, `okf version`) that the switch statement actually accepts are undocumented. A user reading `okf --help` output has no way to discover that `okf help` / `okf version` also work.
**Files to touch:** `src/OKF4net.Cli/OkfCli.cs`, `tests/OKF4net.Tests/CliTests.cs`
**What to do:**
1. In `src/OKF4net.Cli/OkfCli.cs`, update the `Usage` constant (lines 24-40) to also list the bare-word forms, e.g. change `"    -h, --help           Show this help\n"` to `"    -h, --help, help     Show this help\n"` and `"    -V, --version        Show version"` to `"    -V, --version, version  Show version"` (adjust spacing to keep columns aligned).
2. Keep the formatting aligned with the existing column layout used by the `COMMANDS:` section above it.
3. In `tests/OKF4net.Tests/CliTests.cs`, extend `Help_prints_usage_and_succeeds` (or add a new small `[Fact]`) to assert the usage text now mentions `version` alongside `--version`/`-V`.
**How to verify:** `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests"` and manually run `dotnet run --project src/OKF4net.Cli -- --help` to eyeball the updated `OPTIONS:` block.
**Good to know:** This text is not part of any byte-exact golden fixture (`tests/fixtures/golden/` has no usage/help capture), so it's safe to edit. See `CONTRIBUTING.md` for build/test basics.
