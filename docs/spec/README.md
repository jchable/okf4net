# The OKF specification, vendored

`SPEC.md` in this directory is a **verbatim, unmodified copy** of the Open
Knowledge Format specification that OKF4net implements. It is checked in so the
normative text is available offline and at a fixed version — every `§` citation
in this repo's code comments, commit messages and design docs resolves against
*this* file rather than against whatever `main` says today.

## Provenance

| | |
|---|---|
| Upstream | [`GoogleCloudPlatform/knowledge-catalog`](https://github.com/GoogleCloudPlatform/knowledge-catalog), path `okf/SPEC.md` |
| Version | OKF **v0.2** |
| Pinned at | [`62432a095456147ee71e70ac6e4dc0d2dea3ac30`](https://github.com/GoogleCloudPlatform/knowledge-catalog/commit/62432a095456147ee71e70ac6e4dc0d2dea3ac30) — the commit that last touched `okf/SPEC.md` |
| Retrieved | 2026-08-31 |
| `sha256` | `26aa5da029278939f914e578107242d9607d4f2dc5fe153272b82f9ed1030101` |
| Size | 37 748 bytes, 1006 lines, LF endings |
| Copyright | Google LLC |
| Licence | Apache License 2.0 — see `LICENSE.Apache-2.0` at the repo root and the `NOTICE` entry |

`.gitattributes` marks this file `-text`, so it is stored and checked out
byte-for-byte on every platform, exactly like `bundles/acme_retail/**` and
`tests/fixtures/**`. That is what makes the `sha256` above verifiable:

```sh
sha256sum docs/spec/SPEC.md
```

## Rules

- **Never edit this file.** It is not ours. To take a newer spec version,
  re-download it, update the whole provenance table above (commit, date,
  `sha256`, size) and say in the commit message what changed normatively.
- It is documentation, not test data: no test reads it, and `okf` does not
  ship it. Conformance is asserted by `tests/`, not by diffing this text.
- Where OKF4net knowingly diverges from this text, the divergence is recorded
  in [`../spec-conformance/`](../spec-conformance/) with a reason — the spec
  copy stays clean.

## Known drift between this spec and upstream's own sample bundles

`bundles/acme_retail/` is an upstream copy too (verbatim for every file carried
over — one generated artifact, `viz.html`, was deliberately not carried; see
`NOTICE` and `bundles/README.md`), and it does **not** satisfy §5's timestamp
rule: its `stale_after` values, and its §5.1 `sources[].last_modified` and
`usage_window` bounds, use the bare `YYYY-MM-DD` form where §5 requires an ISO
8601 datetime with an explicit UTC offset. That is drift in the reference
content, not a defect in OKF4net — `okf validate bundles/acme_retail` reports
them as `LegacyDateOnlyTimestamp`. Both files stay as they are; neither is
"fixed" locally. `bundles/README.md` carries the current warning breakdown —
counts live there, not here, so they drift in one place only.
