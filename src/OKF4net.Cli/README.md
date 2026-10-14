# OKF4net.Cli

`okf`, the command-line validator for [Open Knowledge Format](https://github.com/jchable/okf4net)
(OKF) v0.2 knowledge bundles, packaged as a .NET tool. Use this package when
the machine already has the .NET SDK, and above all to pin `okf` per
repository and run it in CI.

The same `okf` also ships as a self-contained Native AOT binary that needs no
.NET runtime at all: `winget install Coderise.OKF4net` on Windows, or the
[install script](https://github.com/jchable/okf4net/blob/main/packaging/install.sh)
on Linux and macOS. Both builds come from the same source and give the same
verdicts.

## Install

Pinned in a repository, which is the recommended way for CI:

```sh
dotnet new tool-manifest            # once per repository; creates dotnet-tools.json
dotnet tool install OKF4net.Cli     # records the version in the manifest
dotnet okf validate ./knowledge
```

Commit the manifest. Anyone cloning the repository, and every CI run, then gets
exactly that version with `dotnet tool restore`.

Globally, for your own shell:

```sh
dotnet tool install -g OKF4net.Cli
okf validate ./knowledge
```

Once, without installing anything (.NET 10 SDK or later):

```sh
dnx OKF4net.Cli --yes validate ./knowledge
```

`--yes` confirms the package download. Without it, the first run asks, and a
non-interactive run fails instead of asking.

The tool runs on the .NET 10 runtime or any later one. If `okf` is also
installed through winget or the install script, whichever comes first on your
`PATH` wins. `dotnet okf` always runs the manifest's version.

## In CI

GitHub Actions, with the tool manifest committed at the repository root:

```yaml
- uses: actions/checkout@v7
- uses: actions/setup-dotnet@v6
  with:
    dotnet-version: 10.0.x
- run: dotnet tool restore
- run: dotnet okf validate ./knowledge --as-of 2026-01-01
```

`okf validate` exits 1 when the bundle is not conformant, so the step fails the
build. Warnings are reported but do not fail it. Staleness (`stale_after`, §5.5)
depends on the day the check runs, so a job that wants the same verdict every
time pins the evaluation date with `--as-of`.

## Commands

```text
okf validate <bundle>    Check a bundle against OKF v0.2 conformance (§11)
okf audit    <bundle>    Report trust, freshness and lifecycle across the bundle
okf verify   <bundle> <id>…   Record a review of one or more concepts (--by <actor>)
okf info     <bundle>    Summarize a bundle (concepts, types, links, version)
okf index    <bundle>    (Re)generate every index.md in the bundle
okf graph    <bundle>    Print the cross-link graph (--dot for Graphviz DOT)
okf parse    <file>      Parse one concept document and print its structure
okf fmt      <file>      Normalize a document by parse + re-serialize (-w writes)
okf help                 Show this help (same as -h, --help)
okf version              Show version (same as -V, --version)
```

Every verb takes `-h`/`--help`. An option a verb does not define is an error,
never silently ignored. `validate`, `audit` and `info` take `--json` for
machine-readable output.

Full reference with real output samples:
[CLI docs](https://jchable.github.io/okf4net/docs/cli/).

## License

LGPL-3.0-or-later. The package carries no third-party code; see `NOTICE`.
