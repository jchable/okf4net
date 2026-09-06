# winget packaging for `okf` and `okf-render`

Distributes two binaries as two separate winget packages, sharing one set of
scripts:

| Binary       | Package                       | Command alias | Zip prefix               |
|--------------|-------------------------------|---------------|--------------------------|
| `okf`        | **`Coderise.OKF4net`**        | `okf`         | `okf-<version>-*`        |
| `okf-render` | **`Coderise.OKF4net.Render`** | `okf-render`  | `okf-render-<version>-*` |

Both are portable zips (a single `.exe` nested inside).
**`Coderise.OKF4net.Render` has never been submitted to winget-pkgs** — see
*Submitting a brand-new package* below before expecting the automated job to
publish it.

## How it works

On every `v*` tag, `.github/workflows/release.yml`'s `cli-binaries` job
builds Native AOT binaries for six RIDs on their native runners via
`packaging/Publish-Cli.ps1` (shared across binaries and OSes — it is no
longer winget- or `okf`-specific, hence living one level up from this
directory), calling it once per binary per RID: `okf` (`OKF4net.Cli`) and
`okf-render` (`OKF4net.Render`, static HTML site generation). Only the two
Windows RIDs of each binary feed winget:

1. `win-x64` (`windows-latest`) and `win-arm64` (`windows-11-arm`) each
   produce `okf-<version>-<rid>.zip` and `okf-render-<version>-<rid>.zip`
   (each containing a single `.exe`); every non-Windows archive (eight
   `.tar.gz`s across both binaries) is ignored by winget packaging. Each
   `winget-submit*` job's `installers-regex` is anchored so it matches only
   its own binary's Windows zips — see *Two packages, two regexes* below.
2. Creates the GitHub Release and attaches every archive + `checksums.txt`.
3. Generates the winget v1.12.0 manifests for **both** packages via
   `Generate-Manifests.ps1` (SHA256 read from each binary's own Windows
   `.zip.sha256` files) and attaches `Coderise.OKF4net*.yaml` (all six files,
   both packages) to the Release.
4. Opens the update PR at `microsoft/winget-pkgs` for each package
   (`winget-submit` for `okf`, `winget-submit-render` for `okf-render`) —
   each inert until the prerequisites below are met, see *Automated
   submission*.

No SHA-pinned YAML is committed; only the templates in `templates/` and the
generator script are source-controlled.

### Two packages, two regexes

`winget-submit`'s `installers-regex` is `^okf-[0-9].*-win-(x64|arm64)\.zip$` —
anchored and requiring a digit right after `okf-` so it cannot also match
`okf-render-<version>-win-*.zip` (an unanchored `okf-.*-win-...` would swallow
`render-<version>` just as happily as `<version>`).
`winget-submit-render`'s is `^okf-render-[0-9].*-win-(x64|arm64)\.zip$` —
the mirror image, requiring the literal `okf-render-` prefix so it cannot
match `okf`'s own zips. Getting either wrong submits the wrong binary to
Microsoft under the other package's identity.

## Submitting to microsoft/winget-pkgs (manual, first time)

Prerequisites: `winget install Microsoft.WingetCreate`, and a fork of
`microsoft/winget-pkgs`.

1. Download the three manifest files for the package you're submitting
   (`Coderise.OKF4net*.yaml` or `Coderise.OKF4net.Render*.yaml`) from the
   tag's Release page into a local folder (or regenerate them — see below).
2. Validate and test locally:
   ```powershell
   winget validate --manifest <folder>
   winget install --manifest <folder>
   okf --version          # or: okf-render --help
   ```
3. Submit the PR:
   ```powershell
   wingetcreate submit <folder>
   ```
   This opens a PR to `microsoft/winget-pkgs` under
   `manifests/c/Coderise/OKF4net/<version>/` (or `.../OKF4net.Render/<version>/`).
   Microsoft moderators review and merge it.
4. After merge: `winget install Coderise.OKF4net` (or
   `winget install Coderise.OKF4net.Render`).

### Submitting a brand-new package

[`winget-releaser`](https://github.com/vedantmgoyal9/winget-releaser) (the
tool behind the `winget-submit*` jobs) **updates an existing package — it
does not create one.** `Coderise.OKF4net.Render` has no published version in
winget-pkgs yet, so `winget-submit-render` will keep failing (or skipping, if
`WINGET_TOKEN` is also unset) until someone runs the manual
`wingetcreate submit` flow above for it at least once. Do not expect a tag
push to publish the render package by itself — that first PR has to be
opened by hand.

## Regenerating manifests locally

```powershell
pwsh Generate-Manifests.ps1 -PackageIdentifier Coderise.OKF4net -Version <v> `
  -UrlX64  <x64-zip-url>  -Sha256X64  <sha> `
  -UrlArm64 <arm64-zip-url> -Sha256Arm64 <sha> `
  -OutDir out/manifests

pwsh Generate-Manifests.ps1 -PackageIdentifier Coderise.OKF4net.Render -Version <v> `
  -UrlX64  <render-x64-zip-url>  -Sha256X64  <sha> `
  -UrlArm64 <render-arm64-zip-url> -Sha256Arm64 <sha> `
  -OutDir out/manifests
```

`-PackageIdentifier` selects which package's three templates
(`<id>.yaml.in`, `<id>.installer.yaml.in`, `<id>.locale.en-US.yaml.in`) get
filled — both packages' templates live side by side in `templates/`, matched
by exact filename (not a prefix match: `Coderise.OKF4net` is itself a literal
prefix of `Coderise.OKF4net.Render.yaml.in`).

SHA256 values come from the Release's `checksums.txt`, or from the local
`okf-<v>-win-x64.zip.sha256` / `okf-<v>-win-arm64.zip.sha256` (or the
`okf-render-` equivalents) files produced when you run `../Publish-Cli.ps1`
yourself (these `.sha256` sidecars are not attached to the Release itself).

## Automated submission

`release.yml` runs one [`winget-releaser`](https://github.com/vedantmgoyal9/winget-releaser)
job per package on every tag — `winget-submit` for `Coderise.OKF4net`,
`winget-submit-render` for `Coderise.OKF4net.Render` — to open the update PR
at `microsoft/winget-pkgs` by itself. Each is wired but **inert** until both
prerequisites are met for *that* package, and it skips with a notice (green,
not a failed release) when they are not:

1. **The package already published in winget-pkgs.** The action errors out on
   a package with no existing version — the *first* submission of each
   package is the manual `wingetcreate submit` flow above (see *Submitting a
   brand-new package*, which applies right now to `Coderise.OKF4net.Render`).
2. **A `WINGET_TOKEN` repo secret and a winget-pkgs fork** under the repo
   owner. The token is a classic PAT with the `public_repo` scope (winget-pkgs
   is public); `winget-releaser` pushes the branch to the fork and opens the PR
   from it. One token/fork covers both jobs — there is nothing package-specific
   about the prerequisite itself.

The action derives each new version from the manifests *already published* in
winget-pkgs (via `komac`), not from `templates/` — so the templates here stay
the source of truth for the initial submission and for metadata edits
(description, tags, URLs), which still go through a manual PR.

## Schema version

Templates target manifest schema **1.12.0**. winget-pkgs' PR template calls out
the current schema, and its Copilot reviewer flags anything older as deprecated
— an unresolved flag of that kind blocked the 0.2.0 PR from merging. When the
repo moves to a newer schema, bump every `ManifestVersion` field and `$schema`
URL together (six templates, both packages), then re-run `winget validate`
against both.
