### Title: Include the bundle root in ConceptId.FromPath's "not under bundle root" error
**Labels:** good first issue ; enhancement
**Difficulty / est. effort:** ~30min, small

**Context:** `ConceptId.FromPath` (§2 concept-id derivation, `src/OKF4net/ConceptId.cs`) throws `ConceptIdException($"{path} is not under bundle root")` when a path isn't under the given root — but the message never says what the bundle root actually was, only the offending path. That makes the error harder to debug for a caller passing paths from multiple bundles. This is a message-text-only change (same exception type, same throw condition), not a behavioral change, so no spec citation is required beyond noting the surrounding method already implements §2.
**Files to touch:** `src/OKF4net/ConceptId.cs`, `tests/OKF4net.Tests/ConceptIdTests.cs`
**What to do:**
1. In `src/OKF4net/ConceptId.cs`, find the `throw new ConceptIdException($"{path} is not under bundle root");` line inside `FromPath` (in the `else` branch that handles a path outside the root).
2. Change the message to include `bundleRoot`, e.g. `$"{path} is not under bundle root {bundleRoot}"`, keeping the existing wording style used by other `ConceptIdException` messages in the same file (see `New`, `Parse`, `ValidateSegment` for tone/format).
3. Update the XML doc comment on `FromPath` if it references the old message text (it currently only documents the exception condition, not the exact wording, so likely no change needed — check anyway).
4. In `tests/OKF4net.Tests/ConceptIdTests.cs`, extend `FromPath_throws_when_path_is_outside_bundle_root` (around line 158) to assert on the exception's `Message`, confirming it now contains both the offending path and the bundle root.
**How to verify:** `dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptIdTests"` — expect all tests, including the updated one, to pass.
**Good to know:** See `CONTRIBUTING.md`'s "Spec fidelity" section — this is a diagnostics-only change, no behavioral divergence from §2. Run `dotnet format OKF4net.sln` before submitting.
