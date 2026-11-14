# Peripeteia Coop (experimental)

An experimental co-op / multiplayer mod for **Peripeteia** (Unity 2021.3.11f1, IL2CPP, x64).

> Status: **early prototype (M2)**. Two game instances can connect over UDP,
> spawn a cloned Marie model for the other player and synchronize position and
> rotation at 30 Hz. The remote model is currently static (animation is the next
> milestone). AI, interactions, inventory, quests and scene transitions are not
> synchronized yet. Do not expect a complete co-op playthrough at this stage.

## Why this is a big project

Peripeteia is a single-player immersive sim with no netcode. Co-op is not a small
toggle: player movement, weapons, AI, doors, terminals, inventory, dialogue,
quests, physics and scene loading all have to be synchronized. The plan is a
host-authoritative model with a thin client-side presentation layer.

## Requirements

- Windows 10/11 x64
- Legally owned copy of Peripeteia (Steam or itch.io)
- [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) or newer
- BepInEx 6 (IL2CPP, x64) installed into the game folder

> BepInEx 5 does **not** support IL2CPP. Use BepInEx 6 bleeding edge:
> `BepInEx-Unity.IL2CPP-win-x64-*.zip` from <https://builds.bepinex.dev/projects/bepinex_be>

The current prototype was tested against **Peripeteia: Metanoia**, build
`22841583` (Unity `2021.3.11f1`, IL2CPP x64) with BepInEx
`6.0.0-be.793+dbc82f0`. Game updates can invalidate the generated interop
assemblies and break the mod; pin the game version when testing or publishing.

## Setup

1. Install BepInEx 6 into the game folder.
2. Launch the game once. BepInEx generates the `BepInEx/interop` assemblies.
   Close the game.
3. Point the build at your game folder:

   ```powershell
   .\scripts\setup.ps1 -GameDir "C:\Path\To\Peripeteia"
   ```

   This writes `local/gamepath.props`, which is git-ignored.
   Alternatively set the `PERIPETEIA_DIR` environment variable.

4. Build and deploy:

   ```powershell
   .\scripts\build.ps1
   ```

   The plugin is copied to `BepInEx\plugins\PeripeteiaCoop\`.

## Running two instances on one PC (solo testing)

Solo testing is a first-class requirement for this project. Use:

```powershell
.\scripts\run-two.ps1
```

The script starts two instances: a host (`-coop-host`) and a client
(`-coop-connect 127.0.0.1`). It backs up `EASaves` first and writes separate
Unity logs to `local/logs/`. With `-coop-*` arguments the plugin forces
`Application.runInBackground`, so the unfocused instance keeps running. A co-op
client also refuses manual saves,
queued saves and level-start autosaves, so it cannot overwrite the host's
progress. Loading is not affected.

To make the client move by itself while you watch the host window:

```powershell
.\scripts\run-two.ps1 -AutoPilot
```

Then start the same save in both windows. The other player is a cloned Marie
model that slides between received poses; it does not animate yet. See
[docs/SOLO_TESTING.md](docs/SOLO_TESTING.md) for details, or the Russian
step-by-step guide below.

## Пошаговый гайд: проверить кооп в игре

Это проверка на одном ПК: одно окно — хост, второе — клиент. Полноценное
совместное прохождение пока невозможно, но подключение и синхронизацию
движения так проверить можно.

### 1. Создай общее сохранение

Если в `EASaves` ещё нет сохранений, обоим игрокам нечего загружать. Создай
сохранение один раз:

```powershell
cd C:\Path\To\PeripeteiaCoop
.\scripts\run-two.ps1 -NoClient
```

Путь замени на свой. Запустится одно окно игры. Начни новую игру, дойди до первого уровня и
сохранись через меню. Если ручного сохранения нет — дождись автосейва при
входе на уровень. Закрой игру.

### 2. Запусти два окна

```powershell
.\scripts\run-two.ps1
```

Появятся два окна:

- **host** — 1280×720;
- **client** — 960×540.

Сначала в окне **host** выбери `Load` → своё сохранение и дождись загрузки
уровня. Потом в окне **client** выбери `Load` → **то же самое сохранение** и
тоже дождись загрузки.

Клиент не сможет сохраняться — это специально, чтобы не затереть прогресс
хоста.

### 3. Что должно получиться

- Второй игрок появится как **клон Марии**.
- Клон двигается и поворачивается, когда двигается второй игрок.
- Модель пока **статичная**: ходьба, прыжок и приседание не проигрываются,
  клон просто скользит между позициями.
- Мир у каждого свой: двери, AI, предметы, инвентарь и квесты не
  синхронизируются.

Проверка: подвигайся в окне host — клон в окне client должен повторять
позицию. Потом наоборот. Можно запустить `.\scripts\run-two.ps1 -AutoPilot`,
тогда хост будет двигаться сам.

### 4. Смотри логи

В отдельном PowerShell:

```powershell
cd C:\Path\To\PeripeteiaCoop
Select-String -Path .\local\logs\host.log, .\local\logs\client.log `
  -Pattern "\[net\]|\[avatar\]|Exception|Rejecting|ConnectionFailed"
```

Нормальные строки:

```text
[net] Peer connected: id=1 ...
[net] Player 1 is 'Client'.
[avatar] cloned player model: renderers=6, enabled=6, children=3
[net] role=Host peers=1 avatars=1 ...
```

Плохие строки:

```text
ConnectionFailed
Rejecting ...
Exception
NullReferenceException
```

Если `local/logs` пустой, смотри `BepInEx\LogOutput.log` и
`BepInEx\LogOutput.1.log` в папке игры.

### 5. Если что-то не работает

- **`ConnectionFailed`** — подожди до 60 секунд: клиент повторяет попытки
  подключения. Хост должен быть запущен первым. Разреши `Peripeteia.exe` в
  брандмауэре Windows для частных сетей.
- **`Rejecting ...`** — у окон разные версии игры или мода. Пересобери и
  разложи плагин заново.
- **Нет клона, но `avatars=1`** — клон может стоять в другой точке уровня.
  Подвигайся и посмотри вокруг.
- **Клиент не может загрузить уровень или начать новую игру** — временно
  поставь `BlockSaves = false` в `BepInEx\config\peripeteia.coop.cfg`. Но тогда
  клиент сможет затереть сохранение хоста.
- **Двери/AI/предметы ведут себя по-разному** — это ожидаемо, мир пока не
  синхронизирован.

После закрытия окон сейвы автоматически копируются в
`local/saves_backup/<дата-время>/EASaves`.

## Игра с другом по сети

На хосте:

```text
Peripeteia.exe -coop-host -coop-port 7777 -coop-name Host
```

У друга:

```text
Peripeteia.exe -coop-connect <IP-хоста> -coop-port 7777 -coop-name Friend
```

Для интернета нужен проброс **UDP 7777** на роутере или VPN-меш
(ZeroTier/Tailscale). Steam P2P в игре нет.

Важно: сохранение не передаётся автоматически. Оба игрока должны загрузить
один и тот же уровень/сохранение; файл `EASaves` нужно скопировать вручную.
Клиент по-прежнему не сможет сохраняться.

## Current limitations

- Remote avatars are static; walk/jump/crouch animation is not applied yet.
- Skins, equipment and weapons of the other player are not synchronized.
- Only player poses are networked. AI, doors, items, inventory, dialogue,
  quests and scene transitions are still local to each instance.
- Both instances must enter the same level manually.
- Both players need the same mod version and game version; the handshake rejects
  mismatches. There is no automatic reconnect after a host restart yet.
- Internet play needs port forwarding or a VPN mesh (ZeroTier/Tailscale);
  the game has no Steam P2P/Workshop networking.

## Repository layout

```
src/PeripeteiaCoop/      BepInEx plugin source
scripts/                 setup, build, deploy, two-instance test launcher
docs/                    architecture, reverse-engineering, testing, publishing
local/                   git-ignored: game path, dumps, logs, tools
```

## What is NOT in this repository

- No game files, assets, `GameAssembly.dll`, `Assembly-CSharp.dll`, or
  BepInEx interop assemblies.
- No decompiled game code dumps.
- No personal paths, logs, saves or account data.

Contributors generate the interop assemblies locally by running the game once
with BepInEx. The build references them from `BepInEx/interop`.

## Release packaging

```powershell
.\scripts\package.ps1
```

This builds `artifacts/PeripeteiaCoop-<version>.zip` with only the two plugin
DLLs, install notes and user docs. The GitHub/publishing checklist (personal
data scan, version pinning, release steps) is in
[docs/PUBLISHING.md](docs/PUBLISHING.md).

## Legal

This is an unofficial fan project, not affiliated with Ninth Exodus.
Peripeteia and its assets belong to their respective owners. The repository
contains only original mod source code and documentation.

## License

MIT (see [LICENSE](LICENSE)).

---

Кратко по-русски: это экспериментальный кооп-мод. Сейчас два экземпляра игры
соединяются по UDP, видят друг друга в виде клона Марии и синхронизируют
позицию/поворот (30 Гц). Удалённая модель пока статична — анимация следующий
этап. Клиент не может перезаписать сохранения. AI, двери, предметы, инвентарь,
квесты и переходы между сценами ещё не синхронизируются. Инструкции — в
`docs/`, сборка — `scripts/build.ps1`, тест на одном ПК — `scripts/run-two.ps1`.
