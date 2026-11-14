<#
.SYNOPSIS
    Saves the local Peripeteia installation path for the build.

.DESCRIPTION
    Writes local/gamepath.props (git-ignored). The path is never committed.
    Run it again if the game moves or after a reinstall.

.EXAMPLE
    .\scripts\setup.ps1 -GameDir "C:\Games\Peripeteia"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$resolvedGameDir = (Resolve-Path -LiteralPath $GameDir).Path

$exe = Join-Path $resolvedGameDir "Peripeteia.exe"
$interop = Join-Path $resolvedGameDir "BepInEx\interop\Assembly-CSharp.dll"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Peripeteia.exe was not found in '$resolvedGameDir'."
}

if (-not (Test-Path -LiteralPath $interop)) {
    throw @"
BepInEx interop assemblies were not found.

Install BepInEx 6 (IL2CPP, x64) into the game folder, launch the game once,
wait until the BepInEx log says "Chainloader startup complete", then close it.
Expected file: $interop
"@
}

$localDir = Join-Path $root "local"
New-Item -ItemType Directory -Force -Path $localDir | Out-Null

$escaped = [System.Security.SecurityElement]::Escape($resolvedGameDir)
$content = @"
<Project>
  <PropertyGroup>
    <PeripeteiaDir>$escaped</PeripeteiaDir>
  </PropertyGroup>
</Project>
"@

Set-Content -LiteralPath (Join-Path $localDir "gamepath.props") -Value $content -Encoding UTF8

Write-Host "Saved local game path: $resolvedGameDir"
Write-Host "This file is git-ignored: local/gamepath.props"
Write-Host ""
Write-Host "Next: .\scripts\build.ps1"
