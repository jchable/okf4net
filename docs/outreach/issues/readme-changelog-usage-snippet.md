### Title: Add a README usage snippet for `ChangeLog` (§7 log.md)
**Labels:** good first issue ; documentation
**Difficulty / est. effort:** ~30-45min, small

**Context:** The README's `## Usage` → `### As a library` section shows snippets for loading a bundle and parsing/serializing an `OkfDocument`, but `OKF4net.ChangeLog` — the §7 `log.md` parser/builder — has no library-usage example, even though it's listed in the "Library overview" table and is a public, documented type (`src/OKF4net/ChangeLog.cs`). A newcomer wanting to read or append to a bundle's change history has to go straight to the source.
**Files to touch:** `README.md`, `src/OKF4net/ChangeLog.cs` (read-only, for reference)
**What to do:**
1. In `README.md`, after the existing "Parsing and round-tripping a single document" code block in `### As a library` (around line 134), add a new short subsection demonstrating `ChangeLog`.
2. Show a minimal, accurate snippet using the real public API: `ChangeLog.Parse(text)`, reading `.Title` and `.Days` (each a `LogDay` with `.Date` and `.Entries`, each `LogEntry` with `.Kind`/`.Text`), and `.ToMarkdown()` to re-render. You can base the example text on the log format documented in `ChangeLog`'s class summary (`# Directory Update Log` / `## 2026-05-22` / `* **Update**: ...`).
3. Keep the snippet to 5-10 lines, consistent with the style of the existing README code blocks (compileable C#, no pseudo-code).
**How to verify:** No automated test — this is documentation-only. Manually verify the snippet's API calls exist and compile as described by reading `src/OKF4net/ChangeLog.cs` (public members: `Parse`, `ToMarkdown`, `Title`, `Days`, `InvalidDates`, `IsIsoDate`), and optionally paste the snippet into a scratch `.cs` file and `dotnet build` it against the `OKF4net` project to confirm it compiles.
**Good to know:** See `CONTRIBUTING.md` — no test changes required for a pure doc addition, but `dotnet format OKF4net.sln` doesn't apply to Markdown. Cite §7 (log files) as done elsewhere in the README's "Mapping to the spec" table.
