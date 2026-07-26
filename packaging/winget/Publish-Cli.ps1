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
