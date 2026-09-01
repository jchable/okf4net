# Producer fixtures — the golden bundle and the repository it is generated from

Two directories:

- **`fixture-repo/`** — a deliberately tiny C# repository, the input.
- **`golden/`** — the OKF bundle the producer generates from it, the output, committed byte for byte.

`CheckTests` regenerates `golden/` over a copy of itself and compares the bytes; `BlastRadiusTests`
mutates a copy of `fixture-repo/` and asserts exactly which concepts move.

## Read this first: the discipline here is the OPPOSITE of `tests/fixtures/`

The repository's other golden directory, `tests/fixtures/`, holds **byte-exact captures of the
reference implementation's CLI output**, and a hard rule in `CLAUDE.md` forbids editing them to make
a test pass: a difference there is a regression on the C# side.

**This golden is a different animal.** It captures **our own** output. It is regenerable by
construction, and it **must** be regenerated whenever the generator changes intentionally — then the
diff is reviewed as part of that change. Never carry the `tests/fixtures/` rule across to this
directory; applying it here would freeze the producer.

The corollary matters too: a diff you did **not** intend is a real failure. `--check` exists to make
an unintended one loud, so read the diff before you accept it.

## Regenerating

```sh
OKFGEN_UPDATE_GOLDEN=1 dotnet test producers/OkfProducer.sln --filter "FullyQualifiedName~CheckTests.Check_passes_on_an_unchanged_bundle"
```

That rewrites `golden/` from scratch (it is machine output, so it is captured, never merged into) and
then asserts against what it just wrote. Review `git diff producers/tests/OkfProducer.Tests/fixtures/golden`
and commit it with the change that caused it.

**Two intentional changes will rewrite the whole golden**, and neither is drift:

- **A version bump.** `generated.by` is derived from `OkfProducer.Core`'s assembly version
  (`okfgen/0.1.0`), so bumping it rewrites the `generated` block of every concept. Regenerate,
  read the diff, commit it as part of the bump.
- **A tool-version bump.** Determinism is guaranteed *at a fixed extractor version*, not in the
  absolute: a tree-sitter grammar or Roslyn upgrade can move symbols, spans or descriptions over
  unchanged source (§6.2). Same treatment — a reviewed migration, never a silent drift.

## Line endings: the guarantee depends on `.gitattributes`

Everything here is committed with **LF** endings and compared **byte for byte**, so it is covered by

```
producers/tests/OkfProducer.Tests/fixtures/** -text
```

in the repository root `.gitattributes`. Without that line git would translate endings on checkout on
Windows, and the comparison would fail on a fresh clone for a reason no diff of this directory would
show — the files would be right in the repository and wrong on disk. This has already happened once
in this plan, on a fixture that survived only because nobody had checked it out.
`producers/` is outside CI by decision, so nothing else will catch the next instance.

## What `fixture-repo/` contains, and why each piece is there

Fifteen concepts, one occurrence of each shape. A golden of 480 concepts is not reviewable in a diff,
and a diff nobody can read is not a test.

| Shape | Where | Concept |
|---|---|---|
| a package manifest | `src/Fixture.csproj` | `packages/fixture` |
| a documentation file | `README.md` | `docs/fixture-repository` |
| a namespace (synthesized container) | `namespace N` | `code/csharp/n` |
| a nested container | `namespace N.Sub` | `code/csharp/n/sub` |
| a type | `Scanner`, `Registry`, `Formatter` | `code/csharp/n/scanner`, … |
| a merged overload pair (§3.2) | `Registry.Register` ×2 | `code/csharp/n/registry/register` |
| a resolved call (§4.5) | `Register` → `Scanner.Normalize` | `## Calls` on `register` |
| an unresolved call | `Count` → `int.Parse` | `## Calls (unresolved)` on `count` |
| a description from a doc comment | most members | `description_source: doc-comment` |
| a description from a signature | `Registry.Count` | `description_source: generated` |
| a private member, which gets no concept (§5.4) | `Scanner.Cache` | — none, deliberately |
| a symbol a mutation deletes (§6.3) | `Scanner.Gone` | `code/csharp/n/scanner/gone` |

Two placement rules the tests depend on, so keep them if you edit the fixture:

- **`Scanner.Gone` is the last declaration in its file.** Deleting text above another declaration
  moves that declaration's lines and rewrites its concept, and the blast-radius test would then be
  measuring the edit's position instead of the deletion.
- **Nothing calls `Scan`.** Adding an overload of a method gives its name two declarations, which
  makes every call to it ambiguous and therefore *unresolved* — so a call to `Scan` would make the
  caller's concept move too, for a reason that has nothing to do with overload merging.

## What the golden does NOT cover

`fixture-repo/` is a plain directory, not a git repository, and the tests copy it to a temporary
directory **outside** every git checkout before generating. So:

- `overview`'s `generated.at` falls back to the wall clock and no `revision` is written at all.
  The `generated.at` you see in `golden/overview.md` is a frozen capture-time value and means
  nothing; it is one of the two fields `--check` excludes outside a git repository.
- **The golden therefore does not exercise the HEAD-commit stamp of §6.1 at all.**
  `DeterminismTests` covers that path against this repository's own history and against a throwaway
  git repository whose commit dates it controls, and
  `CheckTests.Inside_a_git_repository_the_stamp_fields_are_compared_like_any_other` covers the other
  row of §6.2's exclusion table.

This is deliberate (ruling R5): nesting a real git repository inside this one to serve a fixture is a
heavier mechanism than the property under test needs. `BlastRadiusTests` builds one in a temporary
directory when it needs a HEAD, which is where that cost belongs.
