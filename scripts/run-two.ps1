<#
.SYNOPSIS
    Launches two game instances (host + client) on one PC for solo testing.

.DESCRIPTION
    Both instances share the same game folder and BepInEx plugins.
    BepInEx writes one log per running instance (BepInEx/LogOutput.log and
    LogOutput.1.log). local/logs/host.log and local/logs/client.log may be
    empty because BepInEx takes over the Unity log.

.EXAMPLE
    .\scripts\run-two.ps1

.EXAMPLE
    .\scripts\run-two.ps1 -ClientWidth 800 -ClientHeight 450
#>
param(
    [int]$HostWidth = 1280,
    [int]$HostHeight = 720,
    [int]$ClientWidth = 960,
    [int]$ClientHeight = 540,
    [int]$Port = 7777,
    [string]$LoadSave = "",
    [switch]$NoClient,
    [switch]$NoCoop,
    [switch]$AutoPilot,
    [switch]$AutoPilotHunt,
    [switch]$DoorTest,
    [switch]$ProceduralAnimation
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root "local\gamepath.props"

if (-not (Test-Path -LiteralPath $propsPath) -and -not $env:PERIPETEIA_DIR) {
    throw "Game path is not configured. Run .\scripts\setup.ps1 -GameDir '...\Peripeteia' or set PERIPETEIA_DIR."
}

$gameDir = $env:PERIPETEIA_DIR
if (Test-Path -LiteralPath $propsPath) {
    [xml]$props = Get-Content -LiteralPath $propsPath
    if ($props.Project.PropertyGroup.PeripeteiaDir) {
        $gameDir = $props.Project.PropertyGroup.PeripeteiaDir
    }
}

$exe = Join-Path $gameDir "Peripeteia.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Peripeteia.exe was not found in '$gameDir'."
}

$logs = Join-Path $root "local\logs"
New-Item -ItemType Directory -Force -Path $logs | Out-Null

# Both instances share the same save directory. Back it up before testing so a
# client autosave can never destroy the host's progress.
$saveRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\Ninth Exodus\Peripeteia"
$saves = Join-Path $saveRoot "EASaves"
if (Test-Path -LiteralPath $saves) {
    $backupDir = Join-Path $root ("local\saves_backup\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    Copy-Item -LiteralPath $saves -Destination $backupDir -Recurse -Force
    Write-Host "Backed up saves to: $backupDir"
}

$hostLog = Join-Path $logs "host.log"
$clientLog = Join-Path $logs "client.log"
Remove-Item -LiteralPath $hostLog, $clientLog -Force -ErrorAction SilentlyContinue

$hostCoop = if ($NoCoop) { "" } else { " -coop-host -coop-port $Port -coop-name Host" }
$clientCoop = if ($NoCoop) { "" } else { " -coop-connect 127.0.0.1 -coop-port $Port -coop-name Client" }
if ($AutoPilot -and -not $NoCoop) {
    $clientCoop += " -coop-autopilot"
}
if ($AutoPilotHunt -and -not $NoCoop) {
    $clientCoop += " -coop-autopilot-hunt -coop-autopilot-shoot"
    $hostCoop += " -coop-autopilot-hunt"
}
if ($LoadSave -and -not $NoCoop) {
    $loadArg = " -coop-load `"$LoadSave`""
    $hostCoop += $loadArg
    $clientCoop += $loadArg
}
if ($DoorTest -and -not $NoCoop) {
    $hostCoop += " -coop-door-test"
}
if ($ProceduralAnimation -and -not $NoCoop) {
    $hostCoop += " -coop-procedural-animation"
    $clientCoop += " -coop-procedural-animation"
}

$hostArgs = "-screen-fullscreen 0 -screen-width $HostWidth -screen-height $HostHeight -logFile `"$hostLog`"$hostCoop"
Write-Host "Starting host..."
$hostProcess = Start-Process -FilePath $exe -WorkingDirectory $gameDir -ArgumentList $hostArgs -PassThru
Write-Host ("  PID {0}, log: {1}" -f $hostProcess.Id, $hostLog)

if (-not $NoClient) {
    Start-Sleep -Seconds 5

    $clientArgs = "-screen-fullscreen 0 -screen-width $ClientWidth -screen-height $ClientHeight -logFile `"$clientLog`"$clientCoop"
    Write-Host "Starting client..."
    $clientProcess = Start-Process -FilePath $exe -WorkingDirectory $gameDir -ArgumentList $clientArgs -PassThru
    Write-Host ("  PID {0}, log: {1}" -f $clientProcess.Id, $clientLog)
}

Write-Host ""
if (-not $NoCoop) {
    Write-Host "Both windows keep running when unfocused (Application.runInBackground is forced in co-op mode)."
}
Write-Host "Use Alt+Tab to switch. Close the windows manually when finished."
