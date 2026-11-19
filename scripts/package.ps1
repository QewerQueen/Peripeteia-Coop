<#
.SYNOPSIS
    Builds a release zip with only the mod binaries and user documentation.

.DESCRIPTION
    The archive is written to artifacts/ (git-ignored) and contains:
      Install.bat
      install.ps1
      COOP-README.html
      BepInEx/plugins/PeripeteiaCoop/PeripeteiaCoop.dll
      BepInEx/plugins/PeripeteiaCoop/LiteNetLib.dll
      BepInEx/config/peripeteia.coop.cfg
      INSTALL.txt
      README.md
      LICENSE
      THIRD_PARTY_NOTICES.txt
      docs/*.md

    The user extracts the archive and double-clicks Install.bat. That installer
    finds the game, installs BepInEx 6 (pinned version, downloaded and verified)
    if needed, copies the mod and opens COOP-README.html with the next steps.

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

# One-click installer and the post-install guide live at the archive root so
# the user can just double-click Install.bat after extracting.
foreach ($file in @("Install.bat", "install.ps1", "COOP-README.html")) {
    $source = Join-Path $root "scripts\$file"
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing installer file: $source"
    }
    Copy-Item -LiteralPath $source -Destination $stage
}

$install = @"
Peripeteia Coop $version (experimental)

REQUIREMENTS
  - Windows 10/11 x64
  - A legally owned copy of Peripeteia (Unity 2021.3.11f1, IL2CPP, x64)
  - Both players must use the same game version and the same mod version
  - Internet for the first install (the installer downloads BepInEx once).
    Offline: put a BepInEx-Unity.IL2CPP-win-x64-*.zip next to Install.bat.

QUICK INSTALL (recommended)
  1. Extract this archive anywhere (not into the game folder).
  2. Double-click Install.bat.
  3. Pick the language and let it find the game (or choose the folder).
  4. When it finishes, COOP-README.html opens with the next steps.
  5. Launch Peripeteia, open the COOP menu and host/join. No command line.

MANUAL INSTALL (if you prefer)
  1. Install BepInEx 6 (IL2CPP win-x64) into the game folder.
  2. Copy BepInEx\plugins\PeripeteiaCoop and BepInEx\config\peripeteia.coop.cfg
     into the game's BepInEx\ folder (or keep your existing config).
  3. Launch the game once so BepInEx generates the interop assemblies.
  4. In the main menu click COOP, then Host game or pick a host from the list.

WHAT WORKS
  - Full cloned player model with the game's animations, Q/E lean and a
    simplified dragging-hand pose. The teammate can use a different character
    skin (Player.SyncSkins), and the visible weapon attachments (scope, grip,
    laser, silencer) are mirrored (Weapon.SyncAttachments).
  - Position/rotation at 30 Hz, host-authoritative doors, and a prototype
    world-item layer: pickup/drop/drag for items already in the scene. World
    items are host-authoritative by default (Items.HostAuthoritative).
  - Player-versus-player: every remote puppet gets a physics proxy with a torso,
    head and limb capsules, and hits are applied through the game's real damage
    system on the victim's machine.
  - A small teammate panel shows the other player's name, health bar and DOWN
    state (Player.TeammateHud).
  - Shared world: NPCs and enemies are mirrored host-authoritatively, and a kill
    by either player counts for both. Loot taken from crates and NPC corpses is
    removed on the other machine too (shops stay personal).
  - One shared story branch: a one-time dialogue, quest object or scripted
    teleport is consumed by whichever player reaches it first, and the world
    changes it causes (doors, pistons, elevators, object toggles) are replayed
    on the other machine, so quest items and rewards are not duplicated.
    Cutscenes and dialogue quest-log effects are shared too.
  - Shared player death: a dead teammate goes down as a ragdoll and stands back
    up on revive, and the body cannot be searched.
  - ESC menu opens without pausing the world; focus loss does not open it.

NOTES
  - Inventory, money, health and ammo are personal per player. A co-op client
    keeps its own copy of the host save as 'Coop - <name>' and can play alone.
  - Internet play needs UDP port 7777 forwarded, or a VPN mesh such as
    RadminVPN/ZeroTier/Tailscale. The game has no Steam P2P networking. The COOP
    panel has a "Connect by IP" field, and the host beacon is also sent to each
    interface's subnet, so RadminVPN/Hamachi work without the command line.
  - Level transitions are kept in step; both players should still start from the
    same save. This is an early prototype, not a complete co-op campaign.
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

BepInEx 6 (IL2CPP) is not bundled with this mod. Install.bat downloads a
pinned, tested build from https://builds.bepinex.dev and verifies it with
SHA256 before installing it. BepInEx is licensed under LGPL-2.1:
  https://github.com/BepInEx/BepInEx/blob/master/LICENSE

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
