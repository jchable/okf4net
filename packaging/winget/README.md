# winget packaging for `okf`

Distributes the `okf` CLI as the winget package **`Coderise.OKF4net`**
(portable, command alias `okf`).

## How it works

On every `v*` tag, `.github/workflows/release.yml`:

1. Builds Native AOT binaries on native runners — `win-x64`
   (`windows-latest`) and `win-arm64` (`windows-11-arm`) — via
   `Publish-Cli.ps1`, producing `okf-<version>-<rid>.zip` (each containing a
   single `okf.exe`).
2. Creates the GitHub Release and attaches the two zips + `checksums.txt`.
3. Generates the winget v1.12.0 manifests via `Generate-Manifests.ps1` (SHA256
   read from the built artifacts) and attaches `Coderise.OKF4net*.yaml` to the
   Release.
4. Opens the update PR at `microsoft/winget-pkgs` (`winget-submit` job) —
   inert until the prerequisites below are met, see *Automated submission*.

No SHA-pinned YAML is committed; only the templates in `templates/` and the
generator script are source-controlled.

## Submitting to microsoft/winget-pkgs (manual, first time)

Prerequisites: `winget install Microsoft.WingetCreate`, and a fork of
`microsoft/winget-pkgs`.

1. Download the three `Coderise.OKF4net*.yaml` files from the tag's Release
   page into a local folder (or regenerate them — see below).
2. Validate and test locally:
   ```powershell
   winget validate --manifest <folder>
   winget install --manifest <folder>
   okf --version
   ```
3. Submit the PR:
   ```powershell
   wingetcreate submit <folder>
   ```
   This opens a PR to `microsoft/winget-pkgs` under
   `manifests/c/Coderise/OKF4net/<version>/`. Microsoft moderators review and
   merge it.
4. After merge: `winget install Coderise.OKF4net`.

## Regenerating manifests locally

```powershell
pwsh Generate-Manifests.ps1 -Version <v> `
  -UrlX64  <x64-zip-url>  -Sha256X64  <sha> `
  -UrlArm64 <arm64-zip-url> -Sha256Arm64 <sha> `
  -OutDir out/manifests
```

SHA256 values come from the Release's `checksums.txt`, or from the local
`okf-<v>-<rid>.zip.sha256` files produced when you run `Publish-Cli.ps1`
yourself (these `.sha256` sidecars are not attached to the Release itself).

## Automated submission

`release.yml`'s `winget-submit` job runs
[`winget-releaser`](https://github.com/vedantmgoyal9/winget-releaser) on every
tag to open the update PR at `microsoft/winget-pkgs` by itself. It is wired but
**inert** until both prerequisites are met, and it skips with a notice (green,
not a failed release) when they are not:

1. **`Coderise.OKF4net` published in winget-pkgs.** The action errors out on a
   package with no existing version — the *first* submission is the manual
   `wingetcreate submit` flow above.
2. **A `WINGET_TOKEN` repo secret and a winget-pkgs fork** under the repo
   owner. The token is a classic PAT with the `public_repo` scope (winget-pkgs
   is public); `winget-releaser` pushes the branch to the fork and opens the PR
   from it.

The action derives each new version from the manifests *already published* in
winget-pkgs (via `komac`), not from `templates/` — so the templates here stay
the source of truth for the initial submission and for metadata edits
(description, tags, URLs), which still go through a manual PR.

## Schema version

Templates target manifest schema **1.12.0**. winget-pkgs' PR template calls out
the current schema, and its Copilot reviewer flags anything older as deprecated
— an unresolved flag of that kind blocked the 0.2.0 PR from merging. When the
repo moves to a newer schema, bump the three `ManifestVersion` fields and the
three `$schema` URLs together, then re-run `winget validate`.
