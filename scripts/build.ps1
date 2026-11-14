<#
.SYNOPSIS
    Builds the plugin and deploys it into BepInEx/plugins.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $root "local\gamepath.props"
$project = Join-Path $root "src\PeripeteiaCoop\PeripeteiaCoop.csproj"

if (-not (Test-Path -LiteralPath $propsPath) -and -not $env:PERIPETEIA_DIR) {
    throw "Game path is not configured. Run .\scripts\setup.ps1 -GameDir '...\Peripeteia' or set PERIPETEIA_DIR."
}

Write-Host "Building $Configuration..."
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$gameDir = $env:PERIPETEIA_DIR
if (Test-Path -LiteralPath $propsPath) {
    [xml]$props = Get-Content -LiteralPath $propsPath
    if ($props.Project.PropertyGroup.PeripeteiaDir) {
        $gameDir = $props.Project.PropertyGroup.PeripeteiaDir
    }
}

$pluginDir = Join-Path $gameDir "BepInEx\plugins\PeripeteiaCoop"
Write-Host ""
Write-Host "Deployed to: $pluginDir"
Write-Host "Launch the game and check BepInEx/LogOutput.log for '[probe]' lines."
