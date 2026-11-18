# NOTE: This file must stay UTF-8 **with BOM**. PowerShell 5.1 reads a BOM-less
# script as ANSI, which corrupts the Russian text and breaks the parser.
<#
.SYNOPSIS
    One-click installer for the Peripeteia Coop mod.

.DESCRIPTION
    Finds the Peripeteia installation, installs BepInEx 6 (IL2CPP, win-x64) if
    it is missing, copies the mod into BepInEx\plugins, and opens a short guide
    with the next steps for playing together.

    BepInEx is downloaded from builds.bepinex.dev at a pinned, tested version
    and verified by SHA256. If a matching BepInEx zip sits next to the installer
    (or in tools\), it is used instead, so the install also works offline.

    The script is meant to be started by double-clicking Install.bat. It asks
    for the language, finds the game by itself (Steam libraries, common paths,
    local/gamepath.props) and only falls back to a folder picker if needed.

.EXAMPLE
    .\Install.bat
#>
param(
    [string]$GameDir = "",
    [ValidateSet("ru", "en")]
    [string]$Language = "",
    [switch]$Elevated,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Pinned BepInEx. This is the exact build the mod is tested against; do not
# silently switch to "latest", a newer bleeding-edge build can break interop.
# ---------------------------------------------------------------------------
$BepInExBuild  = "6.0.0-be.793+dbc82f0"
$BepInExUrl    = "https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip"
$BepInExSha256 = "5B63D7D0E92A2448CCD13B738FF95FF1CC1026C6D718555D4DF8E6C824953AC4"

$ScriptDir = Split-Path -Parent $PSCommandPath
if (-not $ScriptDir) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoRoot = Split-Path -Parent $ScriptDir

# ---------------------------------------------------------------------------
# Localization
# ---------------------------------------------------------------------------
$Strings = @{
    ru = @{
        chooseLang    = "Выбери язык / Choose language:  [1] Русский  [2] English"
        badLang       = "Не понял ответ, беру русский."
        title         = "Установка Peripeteia Coop"
        searching     = "Ищу папку с игрой..."
        foundGame     = "Нашёл игру: {0}"
        notFoundGame  = "Автоматически найти игру не удалось. Выбери папку с Peripeteia.exe в открывшемся окне."
        pickFolder    = "Выбери папку, где лежит Peripeteia.exe"
        badFolder     = "В этой папке нет Peripeteia.exe. Попробуй ещё раз."
        noGame        = "Папка с игрой не выбрана. Установка отменена."
        gameRunning   = "Игра сейчас запущена. Закрой Peripeteia и запусти установщик снова."
        needAdmin     = "Для записи в папку игры нужны права администратора. Открываю окно UAC..."
        adminCancelled= "Без прав администратора установка невозможна. Запусти Install.bat от имени администратора."
        bepinexOk     = "BepInEx уже установлен ({0})."
        bepinexNeed   = "BepInEx не найден. Ставлю версию {0}."
        downloading   = "Скачиваю BepInEx (~33 МБ) с builds.bepinex.dev..."
        downloadFail  = "Не удалось скачать BepInEx: {0}"
        downloadHint  = "Проверь интернет. Либо положи рядом с установщиком файл BepInEx-Unity.IL2CPP-win-x64-*.zip и запусти снова."
        usingLocal    = "Использую локальный архив BepInEx: {0}"
        hashBad       = "Контрольная сумма BepInEx не совпала (ожидалось {0}). Установка остановлена ради безопасности."
        extracting    = "Распаковываю BepInEx в папку игры..."
        bepinexDone   = "BepInEx установлен."
        modMissing    = "Не нашёл файлы мода (PeripeteiaCoop.dll) рядом с установщиком. Скачай архив мода целиком и распакуй перед запуском."
        copyingMod    = "Копирую мод в игру..."
        copiedMod     = "Мод скопирован в BepInEx\plugins\PeripeteiaCoop."
        configNew     = "Записал конфиг peripeteia.coop.cfg."
        configKept    = "Существующий конфиг peripeteia.coop.cfg оставлен без изменений (новые настройки BepInEx добавит сам)."
        openingGuide  = "Открываю инструкцию со следующими шагами..."
        guideMissing  = "Файл-инструкция не найден, но установка завершена."
        done          = "ГОТОВО. Мод установлен в: {0}"
        launchPrompt  = "Запустить игру сейчас? [y/N]"
        launching     = "Запускаю Peripeteia..."
        bye           = "Готово. Приятной игры!"
        pressEnter    = "Нажми Enter, чтобы закрыть окно."
    }
    en = @{
        chooseLang    = "Choose language / Выбери язык:  [1] Русский  [2] English"
        badLang       = "Did not understand, using English."
        title         = "Peripeteia Coop installer"
        searching     = "Looking for the game folder..."
        foundGame     = "Found the game: {0}"
        notFoundGame  = "Could not find the game automatically. Pick the folder that contains Peripeteia.exe in the dialog."
        pickFolder    = "Pick the folder that contains Peripeteia.exe"
        badFolder     = "Peripeteia.exe is not in that folder. Try again."
        noGame        = "No game folder selected. Install cancelled."
        gameRunning   = "The game is running right now. Close Peripeteia and run the installer again."
        needAdmin     = "Writing into the game folder needs administrator rights. Opening a UAC prompt..."
        adminCancelled= "The install cannot continue without administrator rights. Run Install.bat as administrator."
        bepinexOk     = "BepInEx is already installed ({0})."
        bepinexNeed   = "BepInEx was not found. Installing version {0}."
        downloading   = "Downloading BepInEx (~33 MB) from builds.bepinex.dev..."
        downloadFail  = "Could not download BepInEx: {0}"
        downloadHint  = "Check your connection. Or put a BepInEx-Unity.IL2CPP-win-x64-*.zip next to the installer and run it again."
        usingLocal    = "Using the local BepInEx archive: {0}"
        hashBad       = "The BepInEx checksum did not match (expected {0}). Stopping the install for safety."
        extracting    = "Extracting BepInEx into the game folder..."
        bepinexDone   = "BepInEx installed."
        modMissing    = "Could not find the mod files (PeripeteiaCoop.dll) next to the installer. Download the full mod archive and extract it before running."
        copyingMod    = "Copying the mod into the game..."
        copiedMod     = "The mod was copied to BepInEx\plugins\PeripeteiaCoop."
        configNew     = "Wrote the peripeteia.coop.cfg config."
        configKept    = "The existing peripeteia.coop.cfg was kept (BepInEx adds new settings by itself)."
        openingGuide  = "Opening the guide with the next steps..."
        guideMissing  = "The guide file was not found, but the install is finished."
        done          = "DONE. The mod is installed into: {0}"
        launchPrompt  = "Launch the game now? [y/N]"
        launching     = "Launching Peripeteia..."
        bye           = "All set. Have fun!"
        pressEnter    = "Press Enter to close this window."
    }
}

$script:Lang = "en"

function T([string]$key) {
    $table = $script:Strings[$script:Lang]
    if ($table.ContainsKey($key)) { return $table[$key] }
    return $key
}

function Step([string]$message) {
    Write-Host ""
    Write-Host ("==> " + $message) -ForegroundColor Cyan
}

function Ok([string]$message) {
    Write-Host ("    " + $message) -ForegroundColor Green
}

function Warn([string]$message) {
    Write-Host ("    " + $message) -ForegroundColor Yellow
}

function Fail([string]$message) {
    Write-Host ("    " + $message) -ForegroundColor Red
}

function Pause-Exit([int]$code) {
    if (-not $NoPause) {
        Write-Host ""
        Write-Host (T 'pressEnter') -ForegroundColor DarkGray
        [void](Read-Host)
    }
    exit $code
}

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

function Test-GameDir([string]$dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir "Peripeteia.exe")) -and
           (Test-Path -LiteralPath (Join-Path $dir "GameAssembly.dll"))
}

function Test-Writable([string]$dir) {
    try {
        $probe = Join-Path $dir (".coop-write-test-" + [guid]::NewGuid().ToString("N"))
        [System.IO.File]::WriteAllText($probe, "x")
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        return $true
    } catch {
        return $false
    }
}

function Get-SteamLibraries {
    $libs = New-Object System.Collections.Generic.List[string]
    $steam = $null
    foreach ($key in @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )) {
        if (-not $steam -and (Test-Path $key)) {
            $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
            if ($props) {
                if ($props.SteamPath) { $steam = $props.SteamPath }
                elseif ($props.InstallPath) { $steam = $props.InstallPath }
            }
        }
    }

    if ($steam) {
        $steam = $steam.Replace("/", "\")
        if (Test-Path -LiteralPath $steam) { [void]$libs.Add($steam) }

        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            $text = Get-Content -LiteralPath $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $path = $m.Groups[1].Value.Replace("\\\\", "\").Replace("/", "\")
                if ($path -and (Test-Path -LiteralPath $path) -and -not $libs.Contains($path)) { [void]$libs.Add($path) }
            }
        }
    }

    return $libs
}

function Find-GameDir {
    $candidates = New-Object System.Collections.Generic.List[string]

    # 1. Explicit parameter.
    if ($GameDir) { [void]$candidates.Add($GameDir) }

    # 2. Local build config (handy in the repo).
    $propsPath = Join-Path $RepoRoot "local\gamepath.props"
    if (Test-Path -LiteralPath $propsPath) {
        try {
            [xml]$props = Get-Content -LiteralPath $propsPath
            $fromProps = $props.Project.PropertyGroup.PeripeteiaDir
            if ($fromProps) { [void]$candidates.Add($fromProps) }
        } catch { }
    }

    # 3. Environment override.
    if ($env:PERIPETEIA_DIR) { [void]$candidates.Add($env:PERIPETEIA_DIR) }

    # 4. Steam libraries (covers extra drives).
    foreach ($lib in Get-SteamLibraries) {
        if (Test-Path -LiteralPath $lib) {
            [void]$candidates.Add((Join-Path $lib "steamapps\common\Peripeteia"))
        }
    }

    # 5. Usual manual locations.
    foreach ($drive in @("C:", "D:", "E:", "F:", "G:")) {
        if (-not (Test-Path ($drive + "\"))) { continue }
        [void]$candidates.Add((Join-Path $drive "SteamLibrary\steamapps\common\Peripeteia"))
        [void]$candidates.Add((Join-Path $drive "Steam\steamapps\common\Peripeteia"))
        [void]$candidates.Add((Join-Path $drive "Games\Peripeteia"))
        [void]$candidates.Add((Join-Path $drive "Peripeteia"))
    }

    foreach ($candidate in $candidates) {
        if (Test-GameDir $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Select-Folder([string]$description) {
    try {
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = $description
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            return $dialog.SelectedPath
        }
    } catch {
        Warn $_.Exception.Message
    }
    return $null
}

function Test-BepInExInstalled([string]$game) {
    return (Test-Path -LiteralPath (Join-Path $game "BepInEx\core\BepInEx.Unity.IL2CPP.dll")) -and
           (Test-Path -LiteralPath (Join-Path $game "winhttp.dll"))
}

function Get-BepInExVersion([string]$game) {
    $config = Join-Path $game "BepInEx\core\BepInEx.Unity.IL2CPP.dll"
    if (Test-Path -LiteralPath $config) {
        try {
            $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($config)
            $v = $info.ProductVersion
            if (-not $v) { $v = $info.FileVersion }
            if ($v) {
                # Trim the full commit hash to the short 7-char form: 6.0.0-be.793+dbc82f0
                if ($v -match "^([0-9.]+-be\.[0-9]+\+[0-9a-fA-F]{7})") { return $Matches[1] }
                return $v
            }
        } catch { }
    }
    return "unknown"
}

function Get-LocalBepInExArchive {
    foreach ($dir in @($ScriptDir, (Join-Path $RepoRoot "tools"), (Join-Path $RepoRoot "local"))) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $match = Get-ChildItem -LiteralPath $dir -Filter "BepInEx-Unity.IL2CPP-win-x64-*.zip" -File -ErrorAction SilentlyContinue |
                 Sort-Object Name -Descending | Select-Object -First 1
        if ($match) { return $match.FullName }
    }
    return $null
}

function Install-BepInEx([string]$game) {
    $zip = Get-LocalBepInExArchive
    if ($zip) {
        Ok ((T 'usingLocal') -f $zip)
    } else {
        $zip = Join-Path $env:TEMP ("PeripeteiaCoop-BepInEx-" + [guid]::NewGuid().ToString("N") + ".zip")
        Step ((T 'downloading'))
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
        } catch {
            Fail ((T 'downloadFail') -f $_.Exception.Message)
            Warn (T 'downloadHint')
            Pause-Exit 1
        }
    }

    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($actual -ne $BepInExSha256) {
        Fail ((T 'hashBad') -f $BepInExSha256)
        Fail ("got: " + $actual)
        Pause-Exit 1
    }

    Step (T 'extracting')
    Expand-Archive -LiteralPath $zip -DestinationPath $game -Force
    if ($zip -like (Join-Path $env:TEMP "*")) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    }
    Ok (T 'bepinexDone')
}

function Get-ModPayload {
    # Release layout: the installer sits next to a staged BepInEx folder.
    $releaseDll = Join-Path $ScriptDir "BepInEx\plugins\PeripeteiaCoop\PeripeteiaCoop.dll"
    if (Test-Path -LiteralPath $releaseDll) {
        return @{
            Dll    = $releaseDll
            Lite   = Join-Path $ScriptDir "BepInEx\plugins\PeripeteiaCoop\LiteNetLib.dll"
            Config = Join-Path $ScriptDir "BepInEx\config\peripeteia.coop.cfg"
        }
    }

    # Repo layout: use the Release build output and the config template.
    $binDll = Join-Path $RepoRoot "src\PeripeteiaCoop\bin\Release\PeripeteiaCoop.dll"
    if (Test-Path -LiteralPath $binDll) {
        return @{
            Dll    = $binDll
            Lite   = Join-Path $RepoRoot "src\PeripeteiaCoop\bin\Release\LiteNetLib.dll"
            Config = Join-Path $RepoRoot "config\peripeteia.coop.cfg"
        }
    }

    return $null
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

# Language.
if (-not $Language) {
    Write-Host ""
    Write-Host (T 'chooseLang')
    $answer = Read-Host "  >"
    if ($answer -eq "2") { $Language = "en" } else { $Language = "ru" }
}
$script:Lang = $Language

Write-Host ""
Write-Host ("  " + (T 'title')) -ForegroundColor White

# Game folder.
Step (T 'searching')
$game = Find-GameDir
if ($game) {
    Ok ((T 'foundGame') -f $game)
} else {
    Warn (T 'notFoundGame')
    while (-not $game) {
        $picked = Select-Folder (T 'pickFolder')
        if (-not $picked) { Fail (T 'noGame'); Pause-Exit 1 }
        if (Test-GameDir $picked) { $game = (Resolve-Path -LiteralPath $picked).Path }
        else { Fail (T 'badFolder') }
    }
    Ok ((T 'foundGame') -f $game)
}

# Do not touch a running game (files are locked).
$running = Get-Process -Name "Peripeteia" -ErrorAction SilentlyContinue
if ($running) {
    Fail (T 'gameRunning')
    Pause-Exit 1
}

# Elevation when the game lives in a protected folder.
if (-not (Test-Writable $game)) {
    if (-not $Elevated) {
        Warn (T 'needAdmin')
        $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -GameDir `"$game`" -Language $script:Lang -Elevated"
        try {
            Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argLine | Out-Null
        } catch {
            Fail (T 'adminCancelled')
            Pause-Exit 1
        }
        exit 0
    } else {
        Fail (T 'adminCancelled')
        Pause-Exit 1
    }
}

# BepInEx.
if (Test-BepInExInstalled $game) {
    Step ((T 'bepinexOk') -f (Get-BepInExVersion $game))
} else {
    Step ((T 'bepinexNeed') -f $BepInExBuild)
    Install-BepInEx $game
}

# Mod files.
$payload = Get-ModPayload
if (-not $payload) {
    Fail (T 'modMissing')
    Pause-Exit 1
}

Step (T 'copyingMod')
$pluginDir = Join-Path $game "BepInEx\plugins\PeripeteiaCoop"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $payload.Dll -Destination $pluginDir -Force
if ($payload.Lite -and (Test-Path -LiteralPath $payload.Lite)) {
    Copy-Item -LiteralPath $payload.Lite -Destination $pluginDir -Force
}
Ok (T 'copiedMod')

$configDir = Join-Path $game "BepInEx\config"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$configDest = Join-Path $configDir "peripeteia.coop.cfg"
if (-not (Test-Path -LiteralPath $configDest)) {
    if ($payload.Config -and (Test-Path -LiteralPath $payload.Config)) {
        Copy-Item -LiteralPath $payload.Config -Destination $configDest -Force
        Ok (T 'configNew')
    }
} else {
    Ok (T 'configKept')
}

# Guide with the next steps.
$guideSrc = Join-Path $ScriptDir "COOP-README.html"
$guideDest = Join-Path $pluginDir "COOP-README.html"
if (Test-Path -LiteralPath $guideSrc) {
    Copy-Item -LiteralPath $guideSrc -Destination $guideDest -Force
    Step (T 'openingGuide')
    try { Start-Process $guideDest } catch { }
} else {
    Warn (T 'guideMissing')
}

Write-Host ""
Write-Host ("  " + ((T 'done') -f $game)) -ForegroundColor Green

# Offer to launch.
if (-not $NoPause) {
    Write-Host ""
    Write-Host (T 'launchPrompt') -ForegroundColor DarkGray
    $launch = Read-Host "  >"
    if ($launch -match "^(y|yes|д|да)$") {
        Ok (T 'launching')
        Start-Process -FilePath (Join-Path $game "Peripeteia.exe") -WorkingDirectory $game
    }
    Write-Host ""
    Write-Host (T 'bye') -ForegroundColor Green
    Write-Host (T 'pressEnter') -ForegroundColor DarkGray
    [void](Read-Host)
}

exit 0
