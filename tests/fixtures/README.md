# Golden fixtures

This directory contains **byte-exact reference outputs**, generated from the
Rust implementation at commit d20343c before its removal (the CLI defined in
its `src/bin/okf.rs`, subcommands `validate` / `info` / `graph` / `fmt` /
`index`). They exist to prove that the OKF4net (.NET) port is observably
identical to the Rust implementation it replaced (see Tasks 13–15 of the
migration plan).

## Layout

- `appendix_a/` — the example bundle. Reproduces the `appendix_a()` helper
  from `tests/bundle.rs` (`datasets/sales.md`, `tables/orders.md`,
  `tables/customers.md`, byte-for-byte), plus two additions to exercise more
  of the CLI:
  - `log.md` — a root-level reserved log file with one valid ISO-8601 dated
    entry, so `info`/`index` see a non-empty log and `validate` reports no
    log-related warnings.
  - `tables/users.md` — a deliberately **non-strict** concept document: it
    has `type` and `title` but is missing `description` and `timestamp`, so
    `validate` emits the two "missing recommended frontmatter field"
    warnings (§9 soft guidance — the bundle stays conformant, exit code 0).
- `golden/validate.out` — stdout of `okf validate tests/fixtures/appendix_a`.
- `golden/validate.exitcode` — the process exit code of that same run, as a
  bare ASCII digit with **no trailing newline** (currently `0`).
- `golden/info.out` — stdout of `okf info tests/fixtures/appendix_a`.
- `golden/graph.dot` — stdout of `okf graph tests/fixtures/appendix_a --dot`
  (Graphviz DOT source).
- `golden/fmt/users.md` — stdout of
  `okf fmt tests/fixtures/appendix_a/tables/users.md` (parse + re-serialize
  normalization).
- `golden/index-input/` — a full copy of `appendix_a/` **after** running
  `okf index tests/fixtures/golden/index-input` on it. The `index.md` files
  written inside it (`index-input/index.md`, `index-input/datasets/index.md`,
  `index-input/tables/index.md`) are the reference output of the index
  generator; every other file in the tree is an unmodified copy of the input
  bundle, included so the whole directory can be diffed/compared as a unit.

## Provenance

Generated on 2026-07-21 by building the Rust crate in Docker (cargo is not
installed on the host) and running each subcommand against
`tests/fixtures/appendix_a`:

```
docker image: rust:1  (pulled digest sha256:9a2cd304a852f05d3352f75bc2775242371c0169a72dbb40d5d881379d571989)
rustc 1.97.1 (8bab26f4f 2026-07-14)
cargo 1.97.1 (c980f4866 2026-06-30)
```

Build: `cargo build --release` with `CARGO_TARGET_DIR=/tmp/target` (kept
outside the mounted worktree so no build artifacts land in git).

## Rules

- **These files are byte-exact captures of the (now removed) Rust binary's
  real output.** Never hand-edit them and never regenerate them from the C#
  port — if the C# output differs, that is a bug in the port to fix on the
  C# side (the Rust behavior was the specification of record for Phase 1).
  They can only be regenerated from the Rust source as of commit d20343c
  (before its removal), and only if the `appendix_a` bundle itself
  intentionally changes.
- Line endings are exactly what the Rust binary emitted (`\n`, never
  `\r\n`). The repository's `.gitattributes` marks `tests/fixtures/** -text`
  so git never normalizes them regardless of `core.autocrlf`.
