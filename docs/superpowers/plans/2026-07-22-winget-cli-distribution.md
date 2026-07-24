# Winget CLI Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the `okf` CLI installable on Windows via `winget install Coderise.OKF4net`.

**Architecture:** Two reusable PowerShell scripts under `packaging/winget/` — `Publish-Cli.ps1` (AOT-publish + zip a single RID) and `Generate-Manifests.ps1` (fill winget manifest templates with version/URLs/SHA256). The release workflow calls `Publish-Cli.ps1` in a Windows matrix (x64 + arm64), attaches the zips + checksums to a GitHub Release, then calls `Generate-Manifests.ps1` and attaches the manifests. First submission to `microsoft/winget-pkgs` is manual.

**Tech Stack:** GitHub Actions, PowerShell 7, .NET 10 Native AOT, winget manifest schema v1.6.0, wingetcreate.

## Global Constraints

- **Zero third-party runtime dependencies** in `src/OKF4net/` and `src/OKF4net.Cli/` — this plan touches neither's source; only CI, scripts, docs, and manifests.
- **Never touch `tests/fixtures/`.**
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later` — N/A here (no new C# files); scripts get an equivalent comment header.
- **PackageIdentifier:** `Coderise.OKF4net` (Publisher `Coderise`, Package `OKF4net`).
- **Command alias / Moniker:** `okf`.
- **Architectures:** `win-x64` **and** `win-arm64`.
- **Winget manifest schema:** `ManifestVersion: 1.6.0`.
- **Asset naming:** `okf-<version>-<rid>.zip` (e.g. `okf-0.2.0-win-x64.zip`), each zip containing a single `okf.exe`.
- **License:** `LGPL-3.0-or-later`; `LICENSE` lives at repo root.
- **Repo URLs:** `https://github.com/jchable/okf4net`.
- **Version source:** the pushed tag `v*` → `VERSION=${GITHUB_REF_NAME#v}`. The CLI's hardcoded `CliVersion` const is out of scope.
- Windows shell for scripts: PowerShell 7 (`pwsh`). Line endings: scripts LF is fine; manifests must be valid YAML (winget accepts LF).

---

### Task 1: Reusable publish+zip script (`Publish-Cli.ps1`)

Produces the exact release artifact for one RID, usable both locally and in CI (DRY — CI does not inline the publish/zip logic).

**Files:**
- Create: `packaging/winget/Publish-Cli.ps1`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `Publish-Cli.ps1 -Rid <win-x64|win-arm64> -Version <ver> -OutDir <dir>` → writes `<OutDir>/okf-<Version>-<Rid>.zip` (containing `okf.exe`) and `<OutDir>/okf-<Version>-<Rid>.zip.sha256` (uppercase hex, single line). Emits the sha to stdout as the last line.

- [ ] **Step 1: Write the script**

Create `packaging/winget/Publish-Cli.ps1`:

```powershell
# SPDX-License-Identifier: LGPL-3.0-or-later
#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$proj = Join-Path $repoRoot 'src/OKF4net.Cli'

Write-Host "Publishing okf CLI for $Rid (version $Version)..."
dotnet publish $proj -c Release -r $Rid -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Rid" }

$exe = Join-Path $proj "bin/Release/net10.0/$Rid/publish/okf.exe"
if (-not (Test-Path $exe)) { throw "published okf.exe not found at $exe" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "okf-$Version-$Rid.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $exe -DestinationPath $zip

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value $sha -NoNewline
Write-Host "Wrote $zip"
$sha
```

- [ ] **Step 2: Run it for win-x64 and verify the artifact**

Run (from repo root):
```powershell
pwsh packaging/winget/Publish-Cli.ps1 -Rid win-x64 -Version 0.0.0-test -OutDir packaging/winget/out
```
Expected: exits 0; prints a 64-char uppercase hex sha on the last line; `packaging/winget/out/okf-0.0.0-test-win-x64.zip` and `.zip.sha256` exist.

- [ ] **Step 3: Verify the zip contains a clean `okf.exe`**

Run:
```powershell
(Get-ChildItem (New-Object IO.Compression.ZipArchive([IO.File]::OpenRead((Resolve-Path packaging/winget/out/okf-0.0.0-test-win-x64.zip)))).Entries).Name
```
Simpler alternative:
```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead((Resolve-Path packaging/winget/out/okf-0.0.0-test-win-x64.zip).Path).Entries.FullName
```
Expected: single entry `okf.exe`.

- [ ] **Step 4: Clean the test output (do not commit binaries)**

Run:
```powershell
Remove-Item -Recurse -Force packaging/winget/out
```

- [ ] **Step 5: Add ignore rule + commit**

Add to `.gitignore` (create the entry if the file exists; otherwise create `.gitignore`):
```
packaging/winget/out/
```

Then:
```bash
git add packaging/winget/Publish-Cli.ps1 .gitignore
git commit -m "feat: winget Publish-Cli.ps1 (AOT publish + zip per RID)"
```

---

### Task 2: Manifest templates + generation script (`Generate-Manifests.ps1`)

Fills winget v1.6.0 templates with runtime values; the only manifest source of truth (no SHA-pinned YAML committed). Fully testable locally via `winget validate`.

**Files:**
- Create: `packaging/winget/templates/Coderise.OKF4net.yaml.in`
- Create: `packaging/winget/templates/Coderise.OKF4net.locale.en-US.yaml.in`
- Create: `packaging/winget/templates/Coderise.OKF4net.installer.yaml.in`
- Create: `packaging/winget/Generate-Manifests.ps1`

**Interfaces:**
- Consumes: nothing at runtime (uses its own templates).
- Produces: `Generate-Manifests.ps1 -Version <v> -UrlX64 <u> -Sha256X64 <h> -UrlArm64 <u> -Sha256Arm64 <h> -OutDir <dir>` → writes `<OutDir>/Coderise.OKF4net.yaml`, `Coderise.OKF4net.locale.en-US.yaml`, `Coderise.OKF4net.installer.yaml` with all `{{...}}` placeholders replaced.

- [ ] **Step 1: Create the version manifest template**

`packaging/winget/templates/Coderise.OKF4net.yaml.in`:
```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: Coderise.OKF4net
PackageVersion: {{Version}}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
```

- [ ] **Step 2: Create the locale manifest template**

`packaging/winget/templates/Coderise.OKF4net.locale.en-US.yaml.in`:
```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: Coderise.OKF4net
PackageVersion: {{Version}}
PackageLocale: en-US
Publisher: Coderise
PublisherUrl: https://github.com/jchable
PublisherSupportUrl: https://github.com/jchable/okf4net/issues
Author: Julien CHABLE
PackageName: OKF4net
PackageUrl: https://github.com/jchable/okf4net
License: LGPL-3.0-or-later
LicenseUrl: https://github.com/jchable/okf4net/blob/main/LICENSE
Copyright: Copyright 2026 Julien CHABLE
ShortDescription: Zero-dependency .NET implementation of the Open Knowledge Format (OKF) v0.1 — parse, validate, index, and graph OKF knowledge bundles.
Moniker: okf
Tags:
  - okf
  - open-knowledge-format
  - knowledge
  - markdown
  - yaml
  - frontmatter
  - knowledge-graph
ManifestType: defaultLocale
ManifestVersion: 1.6.0
```

- [ ] **Step 3: Create the installer manifest template**

`packaging/winget/templates/Coderise.OKF4net.installer.yaml.in`:
```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: Coderise.OKF4net
PackageVersion: {{Version}}
MinimumOSVersion: 10.0.0.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: okf.exe
    PortableCommandAlias: okf
Installers:
  - Architecture: x64
    InstallerUrl: {{Url_X64}}
    InstallerSha256: {{Sha256_X64}}
  - Architecture: arm64
    InstallerUrl: {{Url_Arm64}}
    InstallerSha256: {{Sha256_Arm64}}
ManifestType: installer
ManifestVersion: 1.6.0
```

- [ ] **Step 4: Write the generation script**

`packaging/winget/Generate-Manifests.ps1`:
```powershell
# SPDX-License-Identifier: LGPL-3.0-or-later
#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$UrlX64,
    [Parameter(Mandatory)][string]$Sha256X64,
    [Parameter(Mandatory)][string]$UrlArm64,
    [Parameter(Mandatory)][string]$Sha256Arm64,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'

$tpl = Join-Path $PSScriptRoot 'templates'
$map = @{
    '{{Version}}'      = $Version
    '{{Url_X64}}'      = $UrlX64
    '{{Sha256_X64}}'   = $Sha256X64.ToUpperInvariant()
    '{{Url_Arm64}}'    = $UrlArm64
    '{{Sha256_Arm64}}' = $Sha256Arm64.ToUpperInvariant()
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($src in Get-ChildItem $tpl -Filter '*.yaml.in') {
    $text = Get-Content $src.FullName -Raw
    foreach ($k in $map.Keys) { $text = $text.Replace($k, $map[$k]) }
    if ($text -match '{{') { throw "Unreplaced placeholder in $($src.Name)" }
    $dest = Join-Path $OutDir ($src.Name -replace '\.in$', '')
    # winget accepts LF; write without BOM.
    [IO.File]::WriteAllText($dest, $text)
    Write-Host "Wrote $dest"
}
```

- [ ] **Step 5: Run the generator with sample values**

Run:
```powershell
pwsh packaging/winget/Generate-Manifests.ps1 -Version 0.0.0-test `
  -UrlX64 https://example.com/okf-0.0.0-test-win-x64.zip -Sha256X64 ("A"*64) `
  -UrlArm64 https://example.com/okf-0.0.0-test-win-arm64.zip -Sha256Arm64 ("B"*64) `
  -OutDir packaging/winget/out/manifests
```
Expected: three files written; no thrown error (proves no leftover `{{`).

- [ ] **Step 6: Validate the generated manifests with winget**

Run:
```powershell
winget validate --manifest packaging/winget/out/manifests
```
Expected: `Manifest validation succeeded.` (A warning about the URL not being reachable is acceptable; an error is not.)

- [ ] **Step 7: Clean and commit**

Run:
```powershell
Remove-Item -Recurse -Force packaging/winget/out
```
Then:
```bash
git add packaging/winget/templates packaging/winget/Generate-Manifests.ps1
git commit -m "feat: winget manifest templates + Generate-Manifests.ps1"
```

---

### Task 3: Wire the release workflow (`release.yml`)

Build the Windows binaries on native runners, publish the GitHub Release, and attach generated manifests. The existing `nuget` job is unchanged.

**Files:**
- Modify: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `packaging/winget/Publish-Cli.ps1` (Task 1), `packaging/winget/Generate-Manifests.ps1` (Task 2).
- Produces: GitHub Release for the tag with assets `okf-<v>-win-x64.zip`, `okf-<v>-win-arm64.zip`, `checksums.txt`, and `manifests/*.yaml`.

- [ ] **Step 1: Add the `cli-binaries` matrix job**

Append to `.github/workflows/release.yml` under `jobs:` (after the existing `nuget` job). Note `shell: pwsh` and native runners per arch:

```yaml
  cli-binaries:
    name: build okf (${{ matrix.rid }})
    strategy:
      fail-fast: false
      matrix:
        include:
          - rid: win-x64
            runner: windows-latest
          - rid: win-arm64
            runner: windows-11-arm
    runs-on: ${{ matrix.runner }}
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x
      - name: Derive version from tag
        shell: pwsh
        run: echo "VERSION=$($env:GITHUB_REF_NAME -replace '^v','')" >> $env:GITHUB_ENV
      - name: Publish + zip
        shell: pwsh
        run: packaging/winget/Publish-Cli.ps1 -Rid ${{ matrix.rid }} -Version $env:VERSION -OutDir dist
      - uses: actions/upload-artifact@v4
        with:
          name: cli-${{ matrix.rid }}
          path: dist/*
```

- [ ] **Step 2: Add the `github-release` job (create Release + upload zips + checksums)**

Append after `cli-binaries`:

```yaml
  github-release:
    name: publish GitHub Release
    needs: cli-binaries
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - uses: actions/download-artifact@v4
        with:
          pattern: cli-*
          path: dist
          merge-multiple: true
      - name: Build checksums.txt
        run: |
          cd dist
          sha256sum okf-*.zip > checksums.txt
          cat checksums.txt
      - name: Create/attach Release
        uses: softprops/action-gh-release@v2
        with:
          files: |
            dist/okf-*.zip
            dist/checksums.txt
```

- [ ] **Step 3: Add the `winget-manifests` job (generate + attach manifests)**

Append after `github-release`. It reads the two zips' SHA256 from the downloaded artifacts and constructs the Release asset URLs from the tag:

```yaml
  winget-manifests:
    name: generate winget manifests
    needs: github-release
    runs-on: windows-latest
    permissions:
      contents: write
    steps:
      - uses: actions/checkout@v7
      - uses: actions/download-artifact@v4
        with:
          pattern: cli-*
          path: dist
          merge-multiple: true
      - name: Generate manifests
        shell: pwsh
        run: |
          $ver = $env:GITHUB_REF_NAME -replace '^v',''
          $base = "https://github.com/${env:GITHUB_REPOSITORY}/releases/download/${env:GITHUB_REF_NAME}"
          $shaX64 = (Get-Content "dist/okf-$ver-win-x64.zip.sha256" -Raw).Trim()
          $shaArm = (Get-Content "dist/okf-$ver-win-arm64.zip.sha256" -Raw).Trim()
          packaging/winget/Generate-Manifests.ps1 -Version $ver `
            -UrlX64 "$base/okf-$ver-win-x64.zip" -Sha256X64 $shaX64 `
            -UrlArm64 "$base/okf-$ver-win-arm64.zip" -Sha256Arm64 $shaArm `
            -OutDir manifests
      - name: Attach manifests to Release
        uses: softprops/action-gh-release@v2
        with:
          files: manifests/*.yaml
```

- [ ] **Step 4: Locally reproduce the full x64 chain (de-risk before merge)**

Run from repo root (proves Publish-Cli → sha → Generate-Manifests → winget validate all compose, exactly as the CI steps do):
```powershell
$sha = pwsh packaging/winget/Publish-Cli.ps1 -Rid win-x64 -Version 0.0.0-test -OutDir dist | Select-Object -Last 1
pwsh packaging/winget/Generate-Manifests.ps1 -Version 0.0.0-test `
  -UrlX64 https://example.com/okf-0.0.0-test-win-x64.zip -Sha256X64 $sha `
  -UrlArm64 https://example.com/okf-0.0.0-test-win-arm64.zip -Sha256Arm64 ("B"*64) `
  -OutDir dist/manifests
winget validate --manifest dist/manifests
Remove-Item -Recurse -Force dist
```
Expected: publish succeeds, `winget validate` reports success. (arm64 build + `windows-11-arm` runner are verified only when the workflow runs on a real tag — this is the plan's known deferred risk.)

- [ ] **Step 5: Sanity-check the YAML edits**

Run:
```powershell
python -c "import yaml,sys; [yaml.safe_load_all(open(p, encoding='utf-8')) or None for p in ['.github/workflows/release.yml']]; print('yaml ok')"
```
If `python`/`pyyaml` is unavailable, instead confirm indentation visually and that each new job sits at the same level as `nuget:` (2-space indent under `jobs:`).
Expected: `yaml ok` (or a clean visual pass).

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: build Windows CLI binaries + GitHub Release + winget manifests"
```

---

### Task 4: Submission documentation (`packaging/winget/README.md`)

**Files:**
- Create: `packaging/winget/README.md`

**Interfaces:**
- Consumes: the Release assets + generated manifests from Task 3.
- Produces: human-run procedure; nothing programmatic.

- [ ] **Step 1: Write the README**

`packaging/winget/README.md`:
```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add packaging/winget/README.md
git commit -m "docs: winget packaging + submission guide"
```

---

## Self-Review

**Spec coverage:**
- Component 1 (release pipeline: cli-binaries / github-release / winget-manifests, x64+arm64, zips, checksums) → Task 3, using Task 1's `Publish-Cli.ps1`. ✓
- Component 2 (templates + generation script, no pinned SHA, schema 1.6.0) → Task 2. ✓
- Component 3 (submission docs, wingetcreate, future winget-releaser note) → Task 4. ✓
- Success criteria (AOT x64+arm64, `winget validate` passes, local `winget install --manifest`, final `winget install Coderise.OKF4net`) → covered by Task 2 Step 6, Task 3 Step 4, and Task 4's documented procedure. ✓
- Risk (arm64 on `windows-11-arm`) → surfaced explicitly in Task 3 Step 1 (native runner) and Step 4 (deferred verification note). ✓

**Placeholder scan:** No TBD/TODO. `{{...}}` tokens are intentional template placeholders, and the generator throws if any survive (Task 2 Step 4).

**Type consistency:** `Publish-Cli.ps1` params (`-Rid`, `-Version`, `-OutDir`) match their call sites in Task 3 Step 1 and Task 3 Step 4. `Generate-Manifests.ps1` params (`-Version`, `-UrlX64`, `-Sha256X64`, `-UrlArm64`, `-Sha256Arm64`, `-OutDir`) match call sites in Task 2 Step 5, Task 3 Step 3, and Task 3 Step 4. Asset name `okf-<version>-<rid>.zip` and sha file `<zip>.sha256` are consistent between Task 1 (producer) and Task 3 (consumer). Manifest filenames (`Coderise.OKF4net.yaml`, `.locale.en-US.yaml`, `.installer.yaml`) consistent across Tasks 2–4.
