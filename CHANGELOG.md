# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.1] - 2026-07-24

### Added

- **winget distribution** for the `okf` CLI: `winget install Coderise.OKF4net`
  (portable package, command alias `okf`). Tagged releases now build Native AOT
  binaries for `win-x64` and `win-arm64`, publish a GitHub Release with the
  zipped binaries and `checksums.txt`, and generate the winget v1.6.0 manifests.
  See `packaging/winget/README.md` for the one-time submission to
  `microsoft/winget-pkgs`.
- Project website and developer documentation — getting-started guide, CLI and
  library API reference, and spec-section mapping — deployed to GitHub Pages.

### Changed

- CI/dependencies: bumped `actions/checkout` 4→7 and `actions/setup-dotnet` 4→6,
  and the test-dependencies group.

## [0.1.0] - 2026-07-22

### Added

- Initial C# implementation of OKF v0.1, ported from this repository's former
  Rust `okf` implementation (byte-exact parity proven before removal):
  - `OKF4net` library — YAML-subset parser/emitter, `OkfDocument`,
    `Frontmatter`, `ConceptId`, `LinkScanner`, `Bundle`, `IndexGenerator`,
    `ChangeLog`, `BundleValidator`.
  - `okf` CLI (`validate`, `info`, `index`, `graph`, `parse`, `fmt`),
    published as a Native AOT single-file binary.
- Test suite (unit, integration, and byte-exact golden CLI comparisons).

### Changed

- Relicensed from Apache-2.0 to LGPL-3.0-or-later; Apache-2.0 attribution for
  upstream ported portions is preserved in `NOTICE` and `LICENSE.Apache-2.0`.

[Unreleased]: https://github.com/jchable/okf4net/compare/v0.1.1...main
[0.1.1]: https://github.com/jchable/okf4net/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jchable/okf4net/releases/tag/v0.1.0
