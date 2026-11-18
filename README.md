# Peripeteia Coop (experimental)

An experimental co-op / multiplayer mod for **Peripeteia** (Unity 2021.3.11f1, IL2CPP, x64).

> Status: **early public prototype (M5: shared world, NPCs and story)**. Two
> game instances can connect over UDP, spawn a complete cloned Marie model for the
> other player, synchronize position and rotation at 30 Hz and drive her full game
> animations, including Q/E leaning and the dragging hand. Visual held-weapon
> replication and muzzle flashes are experimental. Doors are synchronized
> host-authoritatively, and a prototype world-item layer mirrors
> pickup/drop/drag for items that already exist in the scene. Players can shoot and
> collide with each other: every remote puppet gets a physics proxy and hits are
> applied through the game's real damage system on the victim's machine. NPCs and
> enemies are mirrored host-authoritatively, with shared deaths. The story now
> follows one shared branch: a dialogue, quest object or scripted teleport is
> consumed by whichever player reaches it first, and the world changes it causes
> are replayed on the other machine, so quest items and rewards are not duplicated.
> Player death is shared too: a dead teammate goes down as a ragdoll and stands
> back up on revive, and the body cannot be searched. The client reconnects
> automatically after a host restart.
> Inventory contents and ammo stay per-player (they are shared only when an item
> is deliberately dropped for the other player). Scene transitions, cutscenes and
> NPC AI are shared. Do not expect a complete co-op playthrough at this stage.

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
the other side, drop restores it, and dragging moves it. Pickup and drop also
send an immediate, reliable event so sharing an item is instant instead of
waiting for the next poll. It uses last-writer-wins and does not transfer
inventory contents or item ownership; a dropped item the other side never had is
cloned from a matching template (`Items.SpawnMissing`, on by default). The
currently held weapon is cloned from a real game `ItemInfo`
and attached to the remote puppet's right-hand bone, using the sender's aim
rotation; firing/reload/swing body states and muzzle flashes are replayed. This
is visual only: ammo, damage, attachments and inventory are not shared. The
grip offset can be tuned with `Weapon.GripDown` and `Weapon.GripForward` in the
BepInEx config. Players can damage and physically block each other: a bullet that
hits the other puppet is sent to that player, who applies it through the game's
real `HealthScript`, and a body/head proxy makes the other player solid. Tune or
disable this with the `Combat.*` config keys. A small overlay shows the
connection state; disable it with `Ui.ShowStatus = false` in the BepInEx config.
See [docs/SOLO_TESTING.md](docs/SOLO_TESTING.md) for details, or the Russian
step-by-step guide below.

## Пошаговый гайд: проверить кооп в игре

Это проверка на одном ПК: одно окно — хост, второе — клиент. Полноценное
совместное прохождение пока невозможно, но подключение, движение, анимации,
наклоны, перетаскивание, двери, мировые предметы, урон между игроками, общий
мир NPC и общая сюжетная ветка — это проверить можно.

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
  перетаскивание двигает. Подбор и бросок дополнительно шлют мгновенное
  надёжное событие, так что «передать» предмет другому игроку можно сразу.
  **Разбитие** (бутылка, хрупкий проп) тоже передаётся: осколки видит и тот,
  кто не бросал. Выброшенный предмет, которого у второго игрока не было
  (например награда из диалога), клонируется из подходящего шаблона —
  `Items.SpawnMissing` включён по умолчанию. Это прототип по принципу
  «побеждает последний изменивший»; содержимое инвентаря и владение предметом
  не передаются.
- **Урон и столкновения между игроками** работают: у удалённого персонажа есть
  физический прокси (капсула тела + сфера головы), поэтому в него можно
  попасть и он становится твёрдым. Попадание отправляется владельцу, а урон
  применяется на его машине через настоящий `HealthScript` (конечности, кровь,
  смерть и HUD работают). Отключается и настраивается ключами `Combat.*`
  в конфиге BepInEx.
- **Оружие в руках** — экспериментальный визуальный прототип. На удалённом
  персонаже показывается клон настоящего игрового предмета, прикреплённый к
  кости правой кисти; направление ствола берётся из прицела отправителя, а
  выстрел, перезарядка и удар повторяются анимацией. Если пистолет висит выше
  кисти, увеличь `Weapon.GripDown` в конфиге BepInEx; если ниже — уменьши.
  Мод не создаёт оружие «из воздуха»: если на другой стороне нет подходящего
  `ItemInfo`, оружие просто не отображается. Патроны, урон, обвесы и инвентарь
  пока локальные.
- **NPC и враги синхронизируются от хоста**: хост симулирует AI, клиент
  повторяет позицию, поворот, анимацию и здоровье. Движение клиент сглаживает
  каждый кадр, а анимация больше не перезапускается на каждом пакете: хост
  передаёт **параметры Animator** (float/int/bool), и клиент проигрывает те же
  клипы своей собственной машиной состояний, поэтому NPC ходят, стреляют и
  умирают с анимацией. Animator зеркальных NPC принудительно переводится в
  `AlwaysAnimate`, чтобы они не замирали вне экрана. Враги **замечают и атакуют
  клиента** так же, как хоста: хост подмешивает аватара клиента в восприятие
  врага (`Senses.seeTarget`) с учётом дистанции, угла обзора и линии видимости, а
  урон клиента по NPC пробрасывается на хост и применяется к настоящему врагу.
  Убийство NPC одним игроком засчитывается обоим. Настраивается секцией `Ai.*`
  в конфиге BepInEx.
- **Сюжет идёт по одной общей ветке**: диалог, квестовый объект или скриптовый
  телепорт «забирает» тот игрок, кто дошёл до него первым; второй в это уже не
  может вмешаться, поэтому квестовые предметы и награды не выдаются дважды.
  Диалог при этом стартует только у того, кто реально стоит у NPC. Мировые
  последствия квестового объекта (что включилось/выключилось) повторяются у
  второго игрока, так что открытые двери и появившиеся объекты видны обоим.
  Стартовое оружие и экипировку получают оба игрока как **один и тот же набор**
  (`Story.ShareItemRewards`): выдача предметов игрой (`GiftGiver`) раздаётся прямо
  в источнике, поэтому оба получают один и тот же стартовый набор — оружие,
  магазины и имплант «термооптик» из вступления. Мировые предметы при этом не
  дублируются. Награду, которую квестовый объект
  выдаёт через `ItemInfo.pickupitem`, тоже получают оба
  (`Story.ShareQuestRewards`, включено по умолчанию), поэтому награды после
  квестов у игроков совпадают. Катсцены (видео) видит вся команда
  (`Story.SyncCutscenes`). Настраивается секцией `Story.*` в конфиге BepInEx.
- **Смерть не откатывает мир**: умерший встаёт на точке последнего сохранения
  (или на входе в уровень), а двери, NPC и квестовое состояние остаются как были
  — уровень не перезагружается. Управляется `Player.RespawnOnDeath`. Оживший
  видит напарника целиком: тело напарника не пересобирается из только что
  возрождённого локального тела (оно в этот момент ещё не полностью
  инициализировано). Тело
  убитого напарника падает как ragdoll и **не обыскивается**. Сознательный
  откат к другому сохранению — решение хоста: хост грузит сейв и **тянет клиента
  за собой** (тот грузит тот же сейв). Клиент не может откатить мир в одиночку
  (`Client.BlockRollback`), чтобы не разойтись с хостом. Тело напарника можно
  **перетаскивать**: наведись на труп и держи штатную клавишу взаимодействия —
  тело поедет за прицелом, а второй игрок увидит то же самое
  (`Player.DragRemoteBody`).
- **Брошенная бутылка разбивается у обоих**: удар и разбитие (`BottleBreak`,
  `genericbreakable`) передаются мгновенным надёжным событием, поэтому осколки
  видит и тот, кто не бросал.
- **Телепорты**: телепортер, в который игрок просто входит, повторяемый и
  локальный — каждый переносится сам. Телепорт, вызванный скриптом (не входом),
  считается одноразовым и переносит **обоих** игроков.
- **Переходы между уровнями общие**: кто первым вошёл в переход (дверь,
  телепорт, загрузочный экран), тот и «тянет» второго — напарник проигрывает тот
  же переход своим загрузочным экраном и оказывается в том же уровне
  (`World.SyncSceneTransitions`).
- **Инвентарь, экипировка и патроны** пока локальные. Урон между игроками и
  убийства NPC — да, общий инвентарь и деньги — ещё нет.
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
  используется сборка 0.3.1+: анимация клона работает на unscaled time, а
  пауза в кооп-режиме отключена. Если проблема осталась, пришли логи и укажи,
  открывал ли ты инвентарь в этот момент.
- **Мир не останавливается по ESC** — это сделано специально для кооп-режима:
  меню открывается, но `TimeController.Pause` не срабатывает, чтобы неактивное
  окно не замирало. Обычный запуск без `-coop-*` не меняется.
- **Клиент не может загрузить уровень или начать новую игру** — временно
  поставь `BlockSaves = false` в `BepInEx\config\peripeteia.coop.cfg`. Но тогда
  клиент сможет затереть сохранение хоста.
- **Клиент не может откатиться к другому сохранению посреди уровня** — так и
  задумано (`Client.BlockRollback`): откат откатил бы только его копию общего
  мира и разошёлся бы с хостом. Откатывается хост, и клиент **сам загрузит тот
  же сейв** по его команде (в логе `[save] Following the host's rollback to ...`).
  Если отключить ключ, клиент сможет откатываться сам — но тогда миры разойдутся.
- **После смерти уровень не перезагружается, игрок встаёт на точке сохранения**
  — это `Player.RespawnOnDeath`. Если хочется старого поведения (полная
  перезагрузка уровня), выключи ключ.
- **Предметы в инвентаре, оружие, патроны или деньги ведут себя по-разному** —
  это ожидаемо: инвентарь, экипировка, патроны и деньги у каждого игрока свои.
  Общими являются двери, мировые предметы, NPC/враги, сюжетная ветка и урон между
  игроками. Переходы между сценами и катсцены пока локальные.

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
  Exact IK, attachments and ammo are not synchronized.
- Player-versus-player damage and collision work through a physics proxy and a
  reliable damage packet. Damage is applied on the victim's machine via the real
  `HealthScript`. It is a prototype: NPC damage/death, hit regions beyond the
  body/head capsule, and friendly-fire policy are not finished.
- Skins, inventory contents, equipment ownership and the other player's item
  list are not synchronized.
- Doors and a prototype layer for world items already present in the scene are
  networked. Pickup/drop also send an immediate reliable event, and a broken
  bottle (or another breakable prop) breaks on both machines. Item sync is
  last-writer-wins; containers can still diverge. A dropped item the other side
  never had is cloned from a matching template (`Items.SpawnMissing`, on by
  default).
- NPCs and enemies are mirrored host-authoritatively (`Ai.*` config) and deaths
  are shared. Mirrored actors are eased towards each snapshot every frame and the
  host's Animator parameters (float/int/bool) are copied onto the client, which
  runs the same state machine locally, so the actors keep their locomotion and
  attack animation. Mirrored Animators are forced to `AlwaysAnimate` and the host
  keeps nearby actors animating, so the snapshot is never a frozen statue. The
  host also registers the remote player as an AI target
  (`Ai.TargetRemotePlayer`, experimental) so enemies can notice, chase and shoot
  at the client. The game only accepts a target that carries a live
  `HealthScript`, and the puppet has none on the host, so it gets an inert
  disabled one; without it the registration succeeded but enemies ignored the
  client. The puppet also gets an inert collider on the layer the AI looks for
  the player on (`Ai.PerceptionCollider`), without which the perception rays
  passed straight through it. Registration alone is still not enough: the game's
  own threat walk never reaches a custom faction entry, and its visibility check
  for any player-faction target samples the **local** player's body
  (`AIController.lightcontrol`). A host-side Harmony hook on
  `Senses.clearTargets` therefore feeds the puppet through the game's own
  `Senses.seeTarget` whenever the enemy really has line of sight and the puppet
  is inside its sight cone and range — the same path every real target takes — so
  the enemy's suspicion, `Targets` list and `currentTarget` then follow with no
  further intervention. Enemy weapons use the same `Shoot` component as the
  player's, so an NPC shot that reaches the client is caught by the same hit path
  as player-versus-player fire and applied on the client's machine through its
  real `HealthScript` (`Ai.DamageRemotePlayer`). Damage in the other direction is
  forwarded too: when the client hits an NPC its `HealthScript.TakeDMG` is
  intercepted and replayed on the host against the real enemy, which also
  provokes it (`Ai.NpcDamageFromClient`).
- The story follows one shared branch (`Story.*` config): a one-time dialogue,
  quest object or scripted teleport is consumed by the first player to reach it,
  and the world GameObjects it switches are replayed on the other machine. A
  dialogue also only starts for the player actually standing at the NPC. Quest
  items and rewards therefore stay on the player who triggered them, except the
  starting weapons and equipment, which both players receive as the same set
  (`Story.ShareItemRewards`): the game's own item grants (`GiftGiver`) are shared
  at their source, so both players get the same kit — weapons, magazines and the
  intro Thermoptic Camo implant. World pickups are not duplicated. The
  quest-log effects of a dialogue branch are shared too
  (`Story.SyncDialogueActions`), the world-effect listeners a quest object drives
  (doors, pistons, elevators, GameObject toggles) are replayed on the other
  machine (`Story.SyncQuestEffects`), and video cutscenes are replayed on both
  machines (`Story.SyncCutscenes`). The raw VIDE node progression itself is not
  mirrored. Scene transitions are shared (`World.SyncSceneTransitions`): whoever
  triggers a level change broadcasts the target scene and the other machine
  follows it, so both players enter the same next level.
- Player death is shared (`Player.SyncDeath`): the remote body goes down where it
  fell and stands back up on revive. The game's own baked ragdoll rig is used
  (`Player.DeathRagdoll`); the ragdoll colliders are moved onto the layer of the
  surface under the body so the corpse does not fall through the floor. Once the
  fall has settled the corpse is pinned (kinematic) so it can neither keep sliding
  on its own nor be pushed around by the other player. If the model has no rig the
  body simply stops animating. A dead teammate cannot be searched or looted.
- Dying no longer reloads the level (`Player.RespawnOnDeath`): the player stands
  back up at the last save point (or where they entered the level) and the shared
  world keeps its doors, NPCs and quest state. The revived player still sees the
  teammate whole: the teammate's body is not rebuilt from the just-revived local
  body, which is not fully initialised at that moment. A deliberate rollback to another
  save is a host decision: the host loads the save and the client is told to load
  the same one, so both worlds rewind together (`SaveLoadSync`). A client cannot
  load a save mid-level on its own (`Client.BlockRollback`) because that would
  rewind only its copy of the world and desync it.
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
позицию/поворот (30 Гц), полноценные игровые анимации, наклоны, двери,
визуальный прототип оружия в руках, урон/столкновения между игроками, общий мир
NPC/врагов и одну общую сюжетную ветку (диалоги, квестовые объекты и скриптовые
телепорты «забирает» тот, кто дошёл первым, поэтому награды не дублируются).
Клиент не может перезаписать сохранения, но умеет переподключаться после
перезапуска хоста. Враги/НПС теперь видят и клиента (хост регистрирует его как
цель AI), а катсцены и эффекты веток диалога (журнал заданий) раздаются обоим.
Мировые предметы синхронизируются частично, а подбор/бросок шлёт мгновенные
события, чтобы делиться предметами; награды после квестов получают оба, переходы
между уровнями общие (кто вошёл первым, тот и тянет второго), а тело напарника
можно перетаскивать. Инвентарь, патроны и деньги ещё не синхронизируются — это
следующие этапы.
Инструкции — в `docs/`, сборка — `scripts/build.ps1`, тест на одном ПК —
`scripts/run-two.ps1`.
