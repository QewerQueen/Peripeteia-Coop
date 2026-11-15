# Peripeteia Coop (experimental)

An experimental co-op / multiplayer mod for **Peripeteia** (Unity 2021.3.11f1, IL2CPP, x64).

> Status: **early public prototype (M4: items and weapons)**. Two game instances
> can connect over UDP, spawn a complete cloned Marie model for the other player,
> synchronize position and rotation at 30 Hz and drive her full game animations,
> including Q/E leaning and the dragging hand. Visual held-weapon replication
> and muzzle flashes are experimental. Doors are synchronized host-authoritatively,
> and a prototype world-item layer mirrors pickup/drop/drag for items that already
> exist in the scene. The client reconnects automatically after a host restart.
> Inventory contents, ammo, damage, quests, NPC reactions and scene transitions
> are not synchronized yet. Do not expect a complete co-op playthrough at this
> stage.

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
(`-coop-connect 127.0.0.1`). It backs up `EASaves` first and creates
`local/logs/`, though BepInEx writes the useful log to
`BepInEx/LogOutput.log` and `BepInEx/LogOutput.1.log`. With `-coop-*` arguments
the plugin forces `Application.runInBackground`, so the unfocused instance keeps
running. A co-op client also refuses manual saves, queued saves and level-start
autosaves, so it cannot overwrite the host's progress. Loading is not affected.

To make the client move by itself while you watch the host window:

```powershell
.\scripts\run-two.ps1 -AutoPilot
```

Then start the same save in both windows. The autopilot waits until the loaded
save is standing on the ground before it starts moving, so it does not lock the
test client at a temporary spawn point high above the map. The other player is a complete cloned
Marie model driven by the game's own Animator, so idle, walk, crouch and jump
animations are used. The clone has no gameplay scripts, colliders, camera or
audio, which makes running the Animator safe. The remote Animator uses unscaled
time, so the other player keeps animating when you open the menu or inventory.
If the Animator ever causes trouble, launch with
`-coop-procedural-animation` to use the old lightweight fallback. Q/E leaning is
applied to the clone after the Animator, and dragging a world object bends the
remote left arm toward the carried item.
In co-op the ESC menu still opens, but `TimeController.Pause` is suppressed, so
the world keeps running; losing focus no longer opens the menu automatically.
A normal single-player launch is unchanged.
Doors opened on the host are mirrored to the client. A prototype item layer
mirrors world items that already exist in the scene: pickup hides the item on
the other side, drop restores it, and dragging moves it. It uses last-writer-wins
and does not transfer inventory contents or item ownership; newly spawned items
are best-effort. The currently held weapon is cloned from a real game `ItemInfo`
and placed on the remote puppet; firing/reload/swing body states and muzzle
flashes are replayed. This is visual only: ammo, damage, attachments and
inventory are not shared. A small overlay shows the connection state; disable it
with `Ui.ShowStatus = false` in the BepInEx config.
See [docs/SOLO_TESTING.md](docs/SOLO_TESTING.md) for details, or the Russian
step-by-step guide below.

## Пошаговый гайд: проверить кооп в игре

Это проверка на одном ПК: одно окно — хост, второе — клиент. Полноценное
совместное прохождение пока невозможно, но подключение, движение, анимации,
наклоны, перетаскивание, двери и часть мировых предметов так проверить можно.

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

- Второй игрок появится как **полный клон Марии**.
- Клон двигается и поворачивается, когда двигается второй игрок.
- У клона работают **полноценные анимации игры**: стойка, ходьба, приседание,
  прыжок. Игровой Animator запускается на клоне, с которого удалены все
  геймплейные скрипты, коллайдеры, камера и звук. Если на твоём ПК это вызовет
  проблему, запусти игру с `-coop-procedural-animation` — будет упрощённая
  процедурная анимация.
- **Наклоны Q/E** повторяются на удалённом персонаже. Если наклоны не видны,
  проверь, что в игре они вообще срабатывают у локального игрока.
- **Перетаскивание предмета** видно второму игроку: его персонаж наклоняется,
  а левая рука тянется к предмету. Это упрощённая IK-поза, а не покадровая
  копия анимации.
- В кооп-режиме **меню по ESC открывается, но мир не встаёт на паузу**.
  Потеря фокуса окном меню не открывает. Иначе второе окно замирало бы, пока
  ты смотришь на первое. В обычном одиночном запуске пауза работает как раньше.
- **Двери синхронизируются от хоста**: если хост открыл или закрыл дверь,
  клиент увидит то же состояние.
- **Мировые предметы** (которые уже лежат в сцене) частично синхронизируются:
  подбор скрывает предмет у второго игрока, бросок возвращает его, а
  перетаскивание двигает. Это прототип по принципу «побеждает последний
  изменивший»; содержимое инвентаря и владение предметом не передаются.
- **Оружие в руках** — экспериментальный визуальный прототип. На удалённом
  персонаже показывается клон настоящего игрового предмета, а выстрел,
  перезарядка и удар повторяются анимацией. Мод не создаёт оружие «из
  воздуха»: если на другой стороне нет подходящего `ItemInfo`, оружие просто
  не отображается. Патроны, урон, обвесы и инвентарь пока локальные.
- **Инвентарь, экипировка, квесты, AI и переходы между сценами** пока не
  синхронизируются.
- Если связь пропала, клиент сам переподключится примерно через 2 секунды
  после возвращения хоста. Вверху экрана есть небольшой статус подключения.

Проверка: подвигайся в окне host — клон в окне client должен повторять
позицию и шагать. Потом наоборот. Нажми Q/E — клон должен наклоняться.
Подними и перетащи предмет — клон должен потянуться рукой. Можно запустить
`.\scripts\run-two.ps1 -AutoPilot`, тогда клиент будет двигаться сам.
Открой дверь на хосте — клиент должен увидеть её открытой.

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
[avatar] cloned player model: renderers=53, enabled=53, children=3
[avatar] full animation Animator enabled on 'UpperMesh'.
[doors] registered=18 scene='tutorial'.
[net] role=Host peers=1 avatars=1 ...
```

Плохие строки:

```text
ConnectionFailed
Rejecting ...
Exception
NullReferenceException
```

Если `local/logs` пустой (так бывает, потому что BepInEx забирает Unity-лог),
смотри `BepInEx\LogOutput.log` и `BepInEx\LogOutput.1.log` в папке игры.
Отладочные подробности (`[pose]`, дампы костей) включаются только через
`[Probe] Enabled = true` в `BepInEx\config\peripeteia.coop.cfg`.

### 5. Если что-то не работает

- **`ConnectionFailed`** — подожди до 60 секунд: клиент повторяет попытки
  подключения. Хост должен быть запущен первым. Разреши `Peripeteia.exe` в
  брандмауэре Windows для частных сетей.
- **`Rejecting ...`** — у окон разные версии игры или мода. Пересобери и
  разложи плагин заново.
- **Нет клона, но `avatars=1`** — клон может стоять в другой точке уровня.
  Подвигайся и посмотри вокруг.
- **Клон выглядит неполным или в T-позе** — модель пересобирается после
  загрузки уровня; если это не помогло, пришли `BepInEx\LogOutput.log` и
  укажи, в каком окне что видно. Не удаляй `Animator` с клона вручную.
- **Удалённый персонаж застыл в анимации захвата/лазания** — проверь, что
  используется сборка 0.3.0+: анимация клона работает на unscaled time, а
  пауза в кооп-режиме отключена. Если проблема осталась, пришли логи и укажи,
  открывал ли ты инвентарь в этот момент.
- **Мир не останавливается по ESC** — это сделано специально для кооп-режима:
  меню открывается, но `TimeController.Pause` не срабатывает, чтобы неактивное
  окно не замирало. Обычный запуск без `-coop-*` не меняется.
- **Клиент не может загрузить уровень или начать новую игру** — временно
  поставь `BlockSaves = false` в `BepInEx\config\peripeteia.coop.cfg`. Но тогда
  клиент сможет затереть сохранение хоста.
- **Предметы в инвентаре, оружие, патроны или квесты ведут себя по-разному** —
  это ожидаемо: синхронизируются только двери, часть мировых предметов и
  визуальный прототип оружия в руках. AI, инвентарь, экипировка, урон, квесты
  и переходы между сценами пока локальные.

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

- Remote avatars use the game's Animator, Q/E lean and a simplified drag-hand
  pose. Held weapons are a visual prototype: the remote side clones a real
  `ItemInfo`, and fire/reload/swing body states and muzzle flashes are replayed.
  Exact IK, attachments, ammo and damage are not synchronized.
- Skins, inventory contents, equipment ownership and the other player's item
  list are not synchronized.
- Doors and a prototype layer for world items already present in the scene are
  networked. Item sync is last-writer-wins; dropped copies, containers and
  newly spawned items can still diverge. AI reactions, dialogue, quests and
  scene transitions are still local to each instance. Synchronizing them is the
  next stage (M5-M6 in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- Both instances must enter the same level manually.
- Both players need the same mod version and game version; the handshake rejects
  mismatches. A client reconnects automatically after a host restart, but the
  level itself is not transferred.
- In co-op the ESC menu opens but the world is not paused; focus loss no longer
  opens the menu. This is intentional for solo testing; a normal single-player
  launch keeps the original behavior.
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
DLLs, a default `peripeteia.coop.cfg`, install notes and user docs. The
GitHub/publishing checklist (personal data scan, version pinning, release steps)
is in [docs/PUBLISHING.md](docs/PUBLISHING.md).

## Legal

This is an unofficial fan project, not affiliated with Ninth Exodus.
Peripeteia and its assets belong to their respective owners. The repository
contains only original mod source code and documentation.

## License

MIT (see [LICENSE](LICENSE)).

---

Кратко по-русски: это экспериментальный кооп-мод. Сейчас два экземпляра игры
соединяются по UDP, видят друг друга в виде полного клона Марии и синхронизируют
позицию/поворот (30 Гц), полноценные игровые анимации, наклоны, двери и
визуальный прототип оружия в руках. Клиент не может перезаписать сохранения, но
умеет переподключаться после перезапуска хоста. Мировые предметы
синхронизируются частично; инвентарь, патроны, урон, квесты, реакции NPC и
переходы между сценами ещё не синхронизируются — это следующие этапы.
Инструкции — в `docs/`, сборка — `scripts/build.ps1`, тест на одном ПК —
`scripts/run-two.ps1`.
