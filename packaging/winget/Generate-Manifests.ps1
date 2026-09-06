# SPDX-License-Identifier: LGPL-3.0-or-later
#requires -Version 7
<#
.SYNOPSIS
    Fills the winget manifest templates for one of this repo's winget
    packages with runtime values (version, installer URLs, SHA256).

.DESCRIPTION
    templates/ holds two packages' worth of template files side by side,
    each named "<PackageIdentifier><suffix>.yaml.in" (suffix: '',
    '.installer', '.locale.en-US') -- e.g. "Coderise.OKF4net.yaml.in" and
    "Coderise.OKF4net.Render.installer.yaml.in". -PackageIdentifier picks
    which package's three templates to fill, so one script serves both
    packages instead of forking a near-identical copy per package.

    Templates are matched by exact filename, not a prefix/glob match:
    "Coderise.OKF4net" is itself a literal prefix of
    "Coderise.OKF4net.Render.yaml.in", so filtering with something like
    `-like "$PackageIdentifier*"` would silently pull the other package's
    templates in too when generating "Coderise.OKF4net". Building the three
    exact filenames from -PackageIdentifier avoids that trap entirely.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Coderise.OKF4net', 'Coderise.OKF4net.Render')]
    [string]$PackageIdentifier,
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
$suffixes = '.yaml.in', '.installer.yaml.in', '.locale.en-US.yaml.in'
foreach ($suffix in $suffixes) {
    $name = "$PackageIdentifier$suffix"
    $src = Join-Path $tpl $name
    if (-not (Test-Path $src)) { throw "Template not found: $src" }
    $text = Get-Content $src -Raw
    foreach ($k in $map.Keys) { $text = $text.Replace($k, $map[$k]) }
    if ($text -match '{{') { throw "Unreplaced placeholder in $name" }
    $dest = Join-Path $OutDir ($name -replace '\.in$', '')
    # winget accepts LF; write without BOM.
    [IO.File]::WriteAllText($dest, $text)
    Write-Host "Wrote $dest"
}
