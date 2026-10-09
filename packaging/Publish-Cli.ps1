# SPDX-License-Identifier: LGPL-3.0-or-later
#requires -Version 7
<#
.SYNOPSIS
    Native-AOT publishes one of this repo's binaries for one RID and archives
    the result.

.DESCRIPTION
    The archiving is delegated to New-ReleaseArchive.ps1 (which also decides
    the licence files that travel with each binary); this script publishes
    and checks the published binary.

    Windows RIDs (win-x64, win-arm64) are zipped with Compress-Archive.
    Linux/macOS RIDs are tar.gz'd instead: Compress-Archive does not preserve
    the Unix executable permission bit, so a .zip built from a Unix binary
    would extract as non-executable -- the classic first-Linux-release bug.
    tar preserves file mode, so it is used for every non-Windows RID, and the
    published binary's executable bit is verified before archiving so a
    silently non-executable publish fails loudly here instead of after a user
    downloads it.

    Kept as PowerShell (not bash) because pwsh is cross-platform and already
    present on every GitHub-hosted runner this script targets.

    Generalized to take -ProjectDir/-BinName (rather than hard-coding
    src/OKF4net.Cli and okf) once a second Native AOT binary -- okf-render,
    src/OKF4net.Render -- needed the exact same publish-and-archive steps: one
    script, two call sites in release.yml, instead of a copy that would drift.

.PARAMETER ProjectDir
    The project to publish, relative to the repo root (e.g. src/OKF4net.Cli).

.PARAMETER BinName
    The published binary's base name, without a platform-specific extension
    (e.g. okf, okf-render). Must match the project's <AssemblyName>. Archives
    are named "$BinName-$Version-$Rid.<zip|tar.gz>", so this is also the
    prefix every consumer of the release artifacts (github-release,
    winget-manifests, winget-submit) matches against.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$ProjectDir,
    [Parameter(Mandatory)][string]$BinName
)
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$proj = Join-Path $repoRoot $ProjectDir
$isWindowsRid = $Rid.StartsWith('win-')
$publishedName = if ($isWindowsRid) { "$BinName.exe" } else { $BinName }

Write-Host "Publishing $BinName for $Rid (version $Version)..."
dotnet publish $proj -c Release -r $Rid -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Rid" }

$bin = Join-Path $proj "bin/Release/net10.0/$Rid/publish/$publishedName"
if (-not (Test-Path $bin)) { throw "published $publishedName not found at $bin" }

if (-not $isWindowsRid) {
    # File.GetUnixFileMode is cross-platform across Linux/macOS pwsh (unlike
    # shelling out to `stat`, whose flags differ between GNU and BSD), and
    # avoids depending on any behaviour of the dotnet publish step itself.
    $mode = [System.IO.File]::GetUnixFileMode($bin)
    $executable = ($mode -band [System.IO.UnixFileMode]::UserExecute) -ne 0
    if (-not $executable) {
        throw "published binary $bin is not executable (mode: $mode) -- refusing to archive it"
    }
}

# The archive itself -- the binary at the archive root plus whatever licence
# files travel with it (okf-render: NOTICE, LICENSE, LICENSE.GPL-3.0 and the
# fonts' OFL texts and marked's MIT text under licenses/) -- is New-ReleaseArchive.ps1's job, so
# its layout can be checked without a publish. It writes the .sha256
# sidecar and returns the hash.
& (Join-Path $PSScriptRoot 'New-ReleaseArchive.ps1') -Bin $bin -BinName $BinName -Rid $Rid -Version $Version -OutDir $OutDir
