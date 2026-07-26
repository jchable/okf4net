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
