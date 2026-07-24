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
3. Generates the winget v1.6.0 manifests via `Generate-Manifests.ps1` (SHA256
   read from the built artifacts) and attaches `Coderise.OKF4net*.yaml` to the
   Release.

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

SHA256 values are in the Release's `checksums.txt` (or each
`okf-<v>-<rid>.zip.sha256` produced by `Publish-Cli.ps1`).

## Future: automated submission

Once the package is accepted, wire the `winget-releaser` GitHub Action into
`release.yml` to open the update PR automatically on each release. The existing
generator/templates remain the source of truth for metadata.
