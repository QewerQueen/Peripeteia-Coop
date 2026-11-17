<#
.SYNOPSIS
    Builds a release zip with only the mod binaries and user documentation.

.DESCRIPTION
    The archive is written to artifacts/ (git-ignored) and contains:
      BepInEx/plugins/PeripeteiaCoop/PeripeteiaCoop.dll
      BepInEx/plugins/PeripeteiaCoop/LiteNetLib.dll
      BepInEx/config/peripeteia.coop.cfg
      INSTALL.txt
      README.md
      LICENSE
      THIRD_PARTY_NOTICES.txt
      docs/*.md

    It never includes game files, interop assemblies, logs, saves, dumps or
    local paths. Build first with scripts/setup.ps1 and scripts/build.ps1.

.EXAMPLE
    .\scripts\package.ps1
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\PeripeteiaCoop\PeripeteiaCoop.csproj"

[xml]$projectXml = Get-Content -LiteralPath $project
$version = $projectXml.Project.PropertyGroup.Version
if (-not $version) {
    throw "Could not read <Version> from $project."
}

& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration

$binDir = Join-Path $root "src\PeripeteiaCoop\bin\$Configuration"
$pluginDll = Join-Path $binDir "PeripeteiaCoop.dll"
$liteNetLibDll = Join-Path $binDir "LiteNetLib.dll"

foreach ($file in @($pluginDll, $liteNetLibDll)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing build output: $file"
    }
}

$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts "stage-$version"
$zip = Join-Path $artifacts "PeripeteiaCoop-$version.zip"

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

$pluginStage = Join-Path $stage "BepInEx\plugins\PeripeteiaCoop"
$configStage = Join-Path $stage "BepInEx\config"
$docsStage = Join-Path $stage "docs"
New-Item -ItemType Directory -Force -Path $pluginStage | Out-Null
New-Item -ItemType Directory -Force -Path $configStage | Out-Null
New-Item -ItemType Directory -Force -Path $docsStage | Out-Null

Copy-Item -LiteralPath $pluginDll -Destination $pluginStage
Copy-Item -LiteralPath $liteNetLibDll -Destination $pluginStage
Copy-Item -LiteralPath (Join-Path $root "config\peripeteia.coop.cfg") -Destination $configStage
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $stage
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $stage
Copy-Item -Path (Join-Path $root "docs\*.md") -Destination $docsStage

$install = @"
Peripeteia Coop $version (experimental)

REQUIREMENTS
  - Windows 10/11 x64
  - A legally owned copy of Peripeteia (Unity 2021.3.11f1, IL2CPP, x64)
  - BepInEx 6 bleeding edge (IL2CPP win-x64), not BepInEx 5
  - Both players must use the same game version and the same mod version

INSTALL
  1. Install BepInEx 6 into the game folder.
  2. Launch the game once, wait for "Chainloader startup complete", close it.
  3. Copy BepInEx\plugins\PeripeteiaCoop into the game's BepInEx\plugins\
     directory. Copy BepInEx\config\peripeteia.coop.cfg into BepInEx\config\
     (or keep your existing config; it is generated on first launch).
  4. Host:    Peripeteia.exe -coop-host -coop-port 7777 -coop-name Host
     Client:  Peripeteia.exe -coop-connect <host-ip> -coop-port 7777 -coop-name Friend
  5. Start the same level or save in both windows.

WHAT WORKS
  - Full cloned player model with the game's animations, Q/E lean and a
    simplified dragging-hand pose.
  - Position/rotation at 30 Hz, host-authoritative doors, and a prototype
    world-item layer: pickup/drop/drag for items already in the scene.
  - Player-versus-player: every remote puppet gets a physics proxy and hits are
    applied through the game's real damage system on the victim's machine.
  - Shared world: NPCs and enemies are mirrored host-authoritatively, and a kill
    by either player counts for both.
  - One shared story branch: a one-time dialogue, quest object or scripted
    teleport is consumed by whichever player reaches it first, and the world
    changes it causes (doors, pistons, elevators, object toggles) are replayed
    on the other machine, so quest items and rewards are not duplicated.
    Cutscenes and dialogue quest-log effects are shared too.
  - Shared player death: a dead teammate goes down as a ragdoll and stands back
    up on revive, and the body cannot be searched.
  - ESC menu opens without pausing the world; focus loss does not open it.

NOTES
  - A co-op client cannot write saves (Client.BlockSaves=true by default).
  - Internet play needs UDP port 7777 forwarded, or a VPN mesh such as
    ZeroTier/Tailscale. The game has no Steam P2P networking.
  - Inventory contents, equipment ownership, ammo, money and scene transitions
    are personal per player and are not synchronized yet. Both players must
    enter the same level. This is an early prototype, not a complete co-op
    campaign.
  - Both players need the same game version and mod version. Pin the game
    version because updates can break the IL2CPP interop.

More details: README.md and docs/SOLO_TESTING.md.
"@
Set-Content -LiteralPath (Join-Path $stage "INSTALL.txt") -Value $install -Encoding UTF8

$notices = @"
Third-party notices

LiteNetLib 1.3.5
  Copyright (c) 2015 RevenantX
  MIT License
  https://github.com/RevenantX/LiteNetLib

BepInEx is not bundled with this mod. Install it separately.
Peripeteia and its assets belong to their respective owners.
"@
Set-Content -LiteralPath (Join-Path $stage "THIRD_PARTY_NOTICES.txt") -Value $notices -Encoding UTF8

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Package: $zip"
Write-Host "SHA256:  $hash"
Write-Host "Contents: plugin DLLs, README, LICENSE, INSTALL, third-party notices, docs."
