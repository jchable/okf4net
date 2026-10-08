# SPDX-License-Identifier: LGPL-3.0-or-later
#requires -Version 7
<#
.SYNOPSIS
    Archives one already-published binary, with the licence files that must
    travel with it, as "$BinName-$Version-$Rid.<zip|tar.gz>" plus its .sha256
    sidecar.

.DESCRIPTION
    The archive half of packaging/Publish-Cli.ps1, split out so its layout can
    be checked on any file standing in for the binary, without a Native AOT
    publish.

    Every archive has the binary AT ITS ROOT, under its published name (okf,
    okf.exe, okf-render, okf-render.exe): packaging/install.sh extracts
    "$BIN" from the archive root, winget's manifests point at
    "RelativeFilePath: <bin>.exe", and ci.yml's archive check extracts it from
    there too.

    The files that travel with each binary are declared per binary in
    $ArchiveFiles below, and a binary missing from that table is refused
    rather than archived bare:
      * okf        -- the binary alone (the archive this script always made).
      * okf-render -- NOTICE, LICENSE, LICENSE.GPL-3.0, and the three SIL OFL
                      texts of the fonts OKF4net.Viewer embeds, under
                      licenses/. The OFL (section 2) requires each font's
                      copyright notice and licence to accompany it when it is
                      redistributed bundled with software; the embedded copies
                      inside the binary are not readable by the person who
                      downloads it, so they ship beside it too.

    Windows RIDs are zipped with Compress-Archive; every other RID is
    tar.gz'd, because tar preserves the Unix executable bit and
    Compress-Archive does not. The tar entries are named explicitly (not
    "-C <stage> .") so the binary's entry stays "okf-render", not
    "./okf-render", and no "./" entry carries the staging folder's mode onto
    the directory a user extracts into.

.PARAMETER Bin
    The published binary to archive.

.PARAMETER BinName
    The binary's base name, without a platform-specific extension (okf,
    okf-render). Selects the files that travel with it and prefixes the
    archive name.

.PARAMETER Rid
    The runtime identifier the binary was published for.

.PARAMETER Version
    The version written into the archive name.

.PARAMETER OutDir
    Where the archive and its .sha256 sidecar are written.

.OUTPUTS
    The archive's SHA-256, upper-case hex.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Bin,
    [Parameter(Mandatory)][string]$BinName,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'

# Per binary: archive path => source path relative to the repo root.
$fontsDir = 'src/OKF4net.Viewer/Assets/fonts'
$ArchiveFiles = @{
    'okf'        = [ordered]@{}
    'okf-render' = [ordered]@{
        'NOTICE'                      = 'NOTICE'
        'LICENSE'                     = 'LICENSE'
        'LICENSE.GPL-3.0'             = 'LICENSE.GPL-3.0'
        'licenses/OFL-Inter.txt'      = "$fontsDir/OFL-Inter.txt"
        'licenses/OFL-InterTight.txt' = "$fontsDir/OFL-InterTight.txt"
        'licenses/OFL-SpaceMono.txt'  = "$fontsDir/OFL-SpaceMono.txt"
    }
}
if (-not $ArchiveFiles.ContainsKey($BinName)) {
    throw "no archive layout declared for '$BinName' -- add it to `$ArchiveFiles in $PSCommandPath"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$isWindowsRid = $Rid.StartsWith('win-')
$publishedName = Split-Path $Bin -Leaf
if (-not (Test-Path $Bin -PathType Leaf)) { throw "binary not found: $Bin" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$archiveExt = if ($isWindowsRid) { 'zip' } else { 'tar.gz' }
$archive = Join-Path $OutDir "$BinName-$Version-$Rid.$archiveExt"
if (Test-Path $archive) { Remove-Item $archive }

$stage = [System.IO.Directory]::CreateTempSubdirectory("okf-archive-").FullName
try {
    Copy-Item -LiteralPath $Bin -Destination (Join-Path $stage $publishedName)
    $entries = @($publishedName)
    foreach ($file in $ArchiveFiles[$BinName].GetEnumerator()) {
        $source = Join-Path $repoRoot $file.Value
        if (-not (Test-Path $source -PathType Leaf)) { throw "licence file not found: $source" }
        $target = Join-Path $stage $file.Key
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        if (-not $IsWindows) {
            # A text file, whatever mode the checkout gave its source: rw-r--r--.
            [System.IO.File]::SetUnixFileMode($target, [System.IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead')
        }
        $top = ($file.Key -split '/')[0]
        if ($entries -notcontains $top) { $entries += $top }
    }

    if ($isWindowsRid) {
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
    } else {
        # The staged copy, not the published file, is what tar reads: check
        # its executable bit again rather than trusting Copy-Item kept it.
        $mode = [System.IO.File]::GetUnixFileMode((Join-Path $stage $publishedName))
        if (($mode -band [System.IO.UnixFileMode]::UserExecute) -eq 0) {
            throw "staged binary $publishedName is not executable (mode: $mode) -- refusing to archive it"
        }
        # macOS's bsdtar would otherwise add an AppleDouble "._<name>" entry
        # for any staged file carrying extended attributes; GNU tar ignores it.
        $env:COPYFILE_DISABLE = '1'
        tar czf $archive -C $stage @entries
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
    }
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

$sha = (Get-FileHash $archive -Algorithm SHA256).Hash
Set-Content -Path "$archive.sha256" -Value $sha -NoNewline
Write-Host "Wrote $archive"
$sha
