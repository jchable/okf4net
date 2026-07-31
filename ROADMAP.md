# OKF4net Roadmap

OKF4net implements the [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on the .NET base class library with zero third-party runtime dependencies.
This roadmap shows where the project is heading. It is a living document —
issues labelled [`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue)
and [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted)
are the concrete entry points.

## Now (in progress)

- Broaden test coverage and worked examples for the CLI verbs and the agents layer.
- Documentation: end-to-end tutorials for both audiences (library users, agent builders).

## Next

- More `OKF4net.Agents` samples with Microsoft Agent Framework — the first,
  `samples/acme-retail-agent`, shipped in 0.4.0; more welcome.
- CLI ergonomics: richer diagnostics and machine-readable (`--json`) output where it aids tooling.
- Performance baselines for large bundle loads.
- Bundle viewer: browse a bundle interactively (static HTML render + local live
  server) — implementation approach (zero-dep `HttpListener`, ASP.NET Core, or a
  standalone web tool) still open, see
  [#40](https://github.com/jchable/okf4net/issues/40).

## Later

- Ecosystem integrations driven by user demand.
- Tracking upstream OKF spec evolution beyond v0.2.
- **Open question upstream: concept id character set.** The spec (§2) does not
  restrict which characters a concept id may contain; `ConceptId.ValidateSegment`
  currently restricts to ASCII regardless. Whether to allow full Unicode (any
  alphabet, no transliteration) is an open, deliberately deferred decision —
  raised upstream, see
  [docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md](docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md).
  Blocks nothing today (the new `ConceptId.Slugify` helper folds non-ASCII to
  `'-'` in the meantime), but revisit once upstream responds — a decision to
  broaden `ValidateSegment` needs its own design pass (cross-platform Unicode
  normalization, golden-fixture impact).

## Out of scope

- Third-party runtime dependencies in the library or CLI (BCL-only is a hard rule).
- Divergence from the OKF v0.2 spec without a documented, cited reason.

## How to influence the roadmap

Open a [Discussion](https://github.com/jchable/okf4net/discussions) or comment on an
existing issue. Roadmap items graduate to labelled issues before work starts.
