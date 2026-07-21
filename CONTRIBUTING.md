# Contributing to OKF4net

Thanks for your interest in contributing! This document explains how to set up
a development environment, run the tests, and submit changes.

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later — that's it.
  OKF4net has **zero third-party runtime dependencies** by design; please keep
  it that way (test-only packages like xunit are fine).

## Building and testing

```sh
dotnet build OKF4net.sln           # core library + okf CLI + test project
dotnet test OKF4net.sln            # unit + integration tests
dotnet publish src/OKF4net.Cli -c Release   # Native AOT okf binary
```

All warnings are treated as errors, so a clean build is required.

### Golden fixtures — do not reformat

`tests/fixtures/` contains **byte-exact golden files**: several tests compare
generated output (index files, formatted documents, CLI output) byte-for-byte
against them. They are protected by `.gitattributes` (`-text`) and excluded
from `.editorconfig` normalization. Never let an editor or formatter touch
them — trailing whitespace, final newlines, and line endings are all
significant.

## Code style

- Formatting is enforced in CI with `dotnet format --verify-no-changes`; run
  `dotnet format OKF4net.sln` before pushing.
- Follow the existing conventions: file-scoped namespaces, XML doc comments on
  public API, nullable reference types enabled.
- New source files need the SPDX header used across the codebase:
  `// SPDX-License-Identifier: LGPL-3.0-or-later`

## Spec fidelity

OKF4net implements the [OKF v0.1 spec](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md).
Behavioural changes must stay conformant with the spec — cite the relevant
section (§) in your PR description. Behaviour intentionally mirrors the OKF
reference implementation; divergences need a documented reason.

## Submitting changes

1. Fork and create a topic branch from `main`.
2. Add or update tests for any behaviour change — the test suite is the
   contract (including golden comparisons for CLI-visible output).
3. Make sure `dotnet test` and `dotnet format --verify-no-changes` pass.
4. Open a pull request with a clear description of the what and why.

Bug reports and feature requests go through
[GitHub issues](https://github.com/jchable/okf4net/issues); please use the
provided templates.

## License of contributions

By contributing, you agree that your contributions are licensed under the
project license, **LGPL-3.0-or-later** (see [LICENSE](LICENSE) and
[NOTICE](NOTICE)).
