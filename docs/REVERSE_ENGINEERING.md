# Reverse engineering notes

Всё ниже получено из локального дампа `Il2CppDumper` и **не коммитится** в
репозиторий (дампы лежат в `local/dump/`, который в `.gitignore`).

## Окружение

- Unity **2021.3.11f1**, IL2CPP, x64.
- Метаданные IL2CPP: версия **29**.
- Методов в бинарнике: ~79 844.
- Игровые типы находятся в **глобальном namespace** и часто названы в нижнем
  регистре (`doorScript`, `playerSkins`, `aispawningzone`).
- BepInEx 6 (IL2CPP, x64) + Il2CppInterop + HarmonyX работают.

## Ключевые типы

| Тип | Роль | Полезные члены |
|---|---|---|
| `PlayerMovement` | движение, прыжки, приседание, бег, лестницы | `canMove`, `TeleportPlayer(Vector3, bool)`, `deserializePos(Vector3, float)`, `IsOnGround`, `isCrouched`, `IsSprinting`, `UpdateAnimator(float)`, `movementBlockers` |
| `PlayerHealth` | конечности, урон, лечение | `TakeDMG(int,int,bool)`, `Serialize()`, `Deserialize(int[])`, `LoadInNewPlayer(PlayerHealth)` |
| `PlayerShootScript` | оружие, стрельба, перезарядка, akimbo | `AttachedGun`, `AkimboGun`, `fire(bool)`, `canShoot`, `reloading` |
| `PlayerSkins` (`playerSkins`) | модели/скины Марии | `playerPrefabs` (private `List<Transform>`), `setMarie()`, `setFirstThirdPerson(bool)`, `MarieVersion` |
| `CameraPivot` | камера от первого/третьего лица | pivot/rotation, привязка к камере |
| `Inventory3D` | инвентарь | слоты, предметы, UI |
| `Interaction` | единая точка взаимодействия | `Interact()`, `door`, `iteminfo`, `hack`, `convo`, `questAction`, `containerAction` |
| `doorScript` | двери | `open`, `openpercent`, `locked`, `OpenDoor()`, `OpenStealth(Vector3)`, `DestroyDoor()` |
| `Hackable` | взлом терминалов/панелей | состояние взлома |
| `AIController` | AI-восприятие, патрули, фракции | 204 строки дампа, `faction`, `factionStandings`, `SpatialEntry`, `switchDoors` |
| `EnemyController` | враги | `AIStatus`, `eyeballColors`, 382 строки дампа |
| `RTSaicontroller`, `squadAIController` | групповая логика | фракции, спавн |
| `SaveSystem` (static) | сохранения | 430 строк дампа |
| `SaveControllerAndSceneTransitioner` | переходы между сценами | `newSceneName`, `writeInventory()`, `disableSavingLoading()` |
| `SaveHandler` | сохранение/загрузка прогресса | `RequestSave()`, `ProcessRequestedSave()`, `AutosaveOnLevelStart()`, `SaveProcedure()`, `LoadProcedure()`, `blockSavesOnThisScene` |
| `VirtualInput` (static) | виртуальный ввод | `Press(KeyCode)`, `GetKey`, `GetKeyDown`, `GetKeyUp` |
| `Keybind` / `Axisbind` | привязки клавиш/осей | `down()`, `justNowDown()`, `getValue()`, `update()` |
| `TimeController` | управление временем | 60 строк дампа |
| `NPCConversation` / `QuestLog` / `QuestAction` | диалоги и квесты | VIDE-диалоги (`StartConversation()`), `useQuestObject()`, `activateQuestObject()`, `activatedGameobjects`/`deactivatedGameobjects`, `multipleUse` |
| `teleport` / `Triggerer` | телепорты и триггеры | `teleportto`, `adjustment`, `OnTriggerEnter(Collider)`, `tyeleport()`; `Triggerer.activatedGameobjects`, `destroyonuse` |
| `EnemyController` / `dynamicenemymover` | AI и движение врагов | `anim`, `health`, `Update()`, `LateUpdate()` (патчатся для заморозки клиента) |
| `AITarget` / `AIController.addCustomTarget` | регистрация не-врага (игрока) как цели AI | `AITarget.DMG`/`Faction`/`FactionNo`/`customTarget`; `addCustomTarget(bool add, Transform centerOfMass, float sightRange, int fact, EnemyController ec, HealthScript HP, damageScript dmg, AITarget aiTarget, Senses senses, HitBoxes hboxes)` |
| `VIDE_Data.VD` / `VD2` / `ActionNode` | диалоги VIDE и их эффекты | `VD.currentActionNode` (static) / `VD2.currentActionNode` (instance), событие `OnActionNode`; `ActionNode.gameObjectName`/`methodName`/`paramType`/`param_int`; `DoAction()` — точка патча |
| `VideoPlayer` / `cutsceneplayer` | видео-катсцены | `cutsceneplayer.vp`, `destroyobjectonend`; `UnityEngine.Video.VideoPlayer.Play()` — точка патча |
| `UnityEngine.Events.UnityEvent` / `PersistentCall` | мировые эффекты квестового объекта | `UnityEventBase.m_PersistentCalls` → `PersistentCallGroup.m_Calls` → `PersistentCall.target`/`methodName`/`mode`/`arguments`; `QuestAction.Triggers[].assign` перепроигрывается на пире (только world-эффекты) |
| `HitBoxes` / `HitBox` / `CharacterJoint` | встроенный рэгдолл игрока | `HitBoxes.Ragroll(bool)`/`unRagroll()`/`revive(bool)`; `HitBox.attachedRB`/`charjoint`; клон сохраняет риг (Rigidbody+Collider+CharacterJoint) и включает его при смерти |
| `Interaction` / `container` / `EnemyInventory` / `frob` | обыск/лут тел | «Обыскать» появляется только при наличии `Interaction`/`container`; на удалённом теле их нет, поэтому труп товарища не обыскивается |
| `HealthScript.dead` | смерть/оживление игрока | `PlayerStateSync` следит за локальным `dead` и рассылает `PlayerDeath` |

## Иерархия игрока (runtime dump)

```
Player
  Mesh                    — руки/меши от первого лица (в клоне удалены)
  UpperPivot              — WeaponPivot → WeaponSlot → WeaponLocation, ...
  DeathTheme              — эффект смерти (удалён в клоне)
  CoatActivator           — логика плаща (удалена в клоне)
  Voice                   — озвучка (удалена в клоне)
  UpperMesh               — скиненная модель: Maid_Marie, одежда, броня, ...
  SurfSphere              — коллайдер/сфера (удалена в клоне)
  Skelly                  — скелет, на который ссылаются SkinnedMeshRenderer
```

`playerSkins.playerPrefabs` — приватный `List<Transform>` из **4 элементов**:

```
[0] UpperMesh
[1] UpperMesh (1)
[2] Marie_Head
[3] Marie_Head
```

Это не полноценный префаб тела, а части для переключения вида. Поэтому для
удалённого аватара клонируется **весь корень `Player`**, а не один элемент из
списка.

Рабочий рецепт клона (проверен в игре):

1. `Instantiate(playerRoot)`, сразу `SetActive(false)`.
2. Удалить дочерние `Mesh`, `DeathTheme`, `CoatActivator`, `Voice`, `SurfSphere`.
3. Удалить все компоненты, кроме `Transform`, `MeshRenderer`,
   `SkinnedMeshRenderer`, `MeshFilter`.
4. Активировать все дочерние `GameObject` и включить все `Renderer`.
5. Активировать клон.

Результат: 6 рендереров, 3 прямых ребёнка (`UpperPivot`, `UpperMesh`,
`Skelly`), клон не читает ввод, не стреляет, не играет звук и не имеет
коллайдеров. Поза пока статичная (Animator удалён) — анимация будет следующим
этапом.

### Что не сработало и почему

- `playerSkins.onStart()` + `setMarie()` + `setFirstThirdPerson(false)` на
  клоне: метод меняет общее состояние камеры. Локальный `CameraPivot` начинал
  каждый кадр кидать `NullReferenceException` в `setMarieInhandMat`, а
  `LightController` — в `calculateAttenuation`. От этих вызовов пришлось
  отказаться; нужные меши включаются прямым включением дочерних объектов.
- Сохранение `Animator` на клоне (оба аниматора `UpperMesh` и `Skelly`,
  контроллер `PlayerAnimator`): после подключения клиента host переставал
  успевать опрашивать сеть, и peer отваливался с `PeerNotFound`. Вероятная
  причина — `StateMachineBehaviour`/IK-код контроллера, который рассчитывает
  на удалённые `PlayerMovement`/камеру. Для M2b нужен безопасный вариант:
  копия контроллера без behaviour, процедурные кости или отдельный простой
  Animator.

`PlayerMovement.TeleportPlayer(Vector3, bool ignoreFallDamage)` успешно
используется для autopilot-теста: позиция меняется, сеть её передаёт.

## Сетевой протокол прототипа

- LiteNetLib 1.3.5, UDP, ключ `peripeteia-coop`, порт 7777.
- Типы сообщений (`PacketType`): `Hello` (1), `Welcome` (2), `Pose` (3),
  `DoorState` (4), `ItemState` (5), `WeaponState` (6), `WeaponEvent` (7),
  `Damage` (8), `ItemEvent` (9), `EnemyState` (10), `EnemyDeath` (11),
  `StoryEvent` (12), `PlayerDeath` (13). Текущая версия протокола — 16.
- `Hello` — протокол, версия мода, версия игры, имя клиента; `Welcome` — id +
  те же версии + имя хоста.
- Несовпадение версии протокола/мода/игры — отказ и disconnect.
- Клиент делает до 120 попыток connect с интервалом 0,5 с: хост может быть
  ещё занят загрузкой сцены.
- `Pose`: id, x, y, z, yaw, `Horizontal`, `Vertical`, `MovementSpeed`,
  `CrouchTimer`, flags (`Crouch`, `Sprint`, `Grounded`, `InAir`, `Jumping`,
  `Shimmy`).
- Позы: 30 Гц, `DeliveryMethod.Unreliable`, интерполяция на приёмнике.
- Хост ретранслирует позы клиентов другим клиентам (для 3+ игроков).
- Идентификаторы: хост = 0, клиенты = 1..N.
- Таймаут соединения увеличен до 20 с: загрузка главного меню двух
  экземпляров иногда занимает больше стандартных 5 с.
- Клиент не пишет сейвы: Harmony-префиксы на `SaveHandler.RequestSave`,
  `ProcessRequestedSave`, `AutosaveOnLevelStart`; настройка `Client.BlockSaves`.

## Важные выводы для мультиплеера

1. **Один игрок — это набор компонентов на сцене**, а не отдельный префаб с
   сетевым контроллером. Для удалённых игроков клонируется весь корень
   `Player`, а не `playerSkins.playerPrefabs` (там только части модели), и у
   клона удаляются ввод, камера, звук, коллайдеры и gameplay-компоненты.
2. **`VirtualInput` — удобная точка инъекции ввода.** Через неё работают
   `Keybind`/`Axisbind`. Это даёт:
   - autopilot для одиночного тестирования;
   - потенциальную возможность проигрывать ввод удалённого игрока, если
     удалённому аватару оставить `PlayerMovement` (рискованно, но полезно для
     прототипа).
3. **`PlayerMovement.deserializePos` и `TeleportPlayer`** позволяют
   расставлять удалённых игроков без прямого вмешательства в `CharacterController`.
4. **`Interaction.Interact()`** — центральное событие для дверей, предметов,
   взлома, диалогов и контейнеров. Сетевые события удобно вешать сюда.
5. **AI полностью локальный.** В коопе хост должен быть авторитетом, а клиенты —
   только отображать врагов. Иначе два игрока увидят разных врагов в разных
   местах.
6. **`SaveSystem` и переходы сцен** — отдельный слой. Хост инициирует загрузку
   сцены, клиенты обязаны загрузить ту же сцену и получить снапшот состояния.
7. **У игры уже есть save/load-адаптеры** (`DoorSaveAdapter`, `EnemySaveAdapter`,
   `ItemSaveAdapter` и т.д.). Это фактически готовый набор состояния, которое
   нужно синхронизировать. Его стоит изучить в первую очередь.

## Восприятие AI (проверено дизассемблером)

- `AIController.<updateAIStatus>d__96.MoveNext` (RVA 0x327610) перебирает пары
  фракций и для каждой пары вызывает `CheckVisionCones` в обе стороны; для
  фракции `f == 0` (`Player`) вторая проверка идёт с `isplayer = true`. Перед
  вызовом проверяется флаг `alive` **источника** и его `snipper` (дальность
  обзора) против расстояния между членами.
- `CheckVisionCones` (0x2F5F90) валидирует источник, затем вызывает
  `checkCone(this, source, target, distance, isplayer, standing)`.
- `checkCone` (0x2FA130) **пропускает цель**, если у неё нет живого здоровья:
  нужно `HP != null && HP.dead == 0` (0x70) **или** `DMG != null &&
  CurrentHitPoints > 0` (0x20). Если ни того, ни другого — выход без
  `Senses.seeTarget`. Это и была причина, по которой зарегистрированная цель без
  `HealthScript` оставалась невидимой.
- Позиция членов фракции обновляется каждый цикл: `member.pos` (0x5C) берётся из
  `member.centerOfMass.position` (0x48), поэтому `addCustomTarget` достаточно
  вызвать один раз.
- `addCustomTarget` (0x2F79A0) полностью обнуляет структуру `factionMemberData`,
  поэтому всё, что не передано (`HP`, `DMG`, `hboxes`, `head`), остаётся `null`;
  `alive` выставляется в `1`, `sight`/`snipper` — в `sightRange` (у игрока это
  `-1`). Номер фракции для словаря берётся из четвёртого аргумента (`fact`).
  Порядок аргументов: `(bool add, Transform centerOfMass, float sightRange,
  int fact, EnemyController ec, HealthScript HP, damageScript dmg, AITarget
  aiTarget, Senses senses, HitBoxes hboxes)`.
- `AITarget`/`createMemberData` (0x2FB840) — игровой путь регистрации игрока: у
  игрока `HP = null`, а `DMG` берётся из `AITarget.DMG` (`damageScript`),
  `hboxes` — из `GetComponentInChildren<HitBoxes>()`, `head` — из
  `hboxes[0x58] ?? centerOfMass`.

### Почему враги не видели клиента (faction 0 == LightController хоста)

- В корутине `updateAIStatus` флаг `isplayer` вычисляется как `(f == 0)`, где `f`
  — индекс **внешней** фракции, то есть в паре «наблюдатель → цель» он означает
  «цель принадлежит фракции игрока» (0). Вторая проверка пары идёт как
  `CheckVisionCones(m2, m1, isplayer = [rdi+0x38])`.
- `calculateThreat(EnemyController enemy, Transform source, Transform target,
  Vector3 lookDirection, float dot, float distance, float elevation, bool
  isplayer)` (RVA 0x2F88D0). Ранние выходы: `const > distance` и `dot <= const`.
  Далее — LOS-луч через `alphaRaycast` (0x423AF0); хит пишется в `visiHit`
  (0x15C), `raycastColl` (0x318). Если коллайдер не `null`, `aimedat` (0x1B0) =
  `collider.transform`; затем `healthScr` (0xB0) =
  `effBatch.getHPScript(collider)` (0x3B43B0), и если он есть, `aimedat` =
  `healthScr.transform`, `ec` (0x1B8) = `healthScr.enemyC`.
- Гейт `Object.op_Equality(aimedat, target)` (0x1A8E790): **равно** →
  `[enemy+0x365] = 0` (и, если `!isplayer`, `xmm9 = xmm10` — «видим цель»);
  **не равно** → `[enemy+0x365] = isHostile(healthScr.enemyC.faction,
  enemy.faction)` (флаг «попали в чужого, не в цель»).
- **Ветка `isplayer == true`**: берётся `AIController.lightcontrol` (0xE8) —
  это `LightController` **локального** игрока. Идёт цикл по
  `lightcontrol.playerColliders` (0x80, `Collider[]`): из головы врага
  райкастится в `lightcontrol.colliderPositions[i]` (0x88, `Vector3[]`), и если
  луч попал **именно** в `playerColliders[i]`, накапливается `xmm9 += xmm10`;
  в конце `xmm9 /= playerColliders.Length`. Затем гейт `xmm9 > 0`, и только тогда
  вызывается `Senses.seeTarget(threat, target.trans)`.
- Вывод: восприятие цели фракции 0 жёстко привязано к телу **локального** игрока
  (`LightController` хоста). Второй член фракции 0 (наш клон клиента) не имеет
  своих коллайдеров в этом списке, поэтому враг его не видит.
- **Фикс (0.7.11)**: Harmony-префикс на `calculateThreat` подменяет
  `lightcontrol.playerColliders`/`colliderPositions` на единственный коллайдер
  клона (`PeripeteiaCoop_AiProxy`, слой `player`) и его `bounds.center` на время
  одного вызова, но только когда `target` — зарегистрированный корень клона;
  постфикс возвращает исходные массивы. Вся остальная логика игры (threat,
  `aimedat`, `Senses`) отрабатывает как для настоящего игрока.
- Дополнительно сам `PeripeteiaCoop_AiProxy` перенесён в центр тела
  (`localPosition = controller.center`, `capsule.center = 0`) и регистрируется
  как `centerOfMass` цели: тогда `member.pos` и `target.trans` — это центр тела, и
  враг смотрит/целится в корпус, а не в ноги. Реестр тел в `AiPerception` ключуется
  по корню клона, а `Swap` при необходимости поднимается на пару уровней вверх.
- `HealthScript.centerOfMass` — `public`; `AIController.lightcontrol` —
  `private`, но Il2CppInterop генерирует для полей публичные свойства
  (`lightcontrol`, `get_lightcontrol`), поэтому доступно из плагина.

## Следующие шаги реверса

- Разобрать `CameraPivot`, `Shoot`, `HealthScript`, `AugSelect`, `SaveSystem`
  (сигнатуры и поля).
- Найти, где происходит `SceneManager.LoadScene` / сохранение, чтобы
  перехватывать переходы.
- Определить, как создаётся игрок в сцене (prefab, `PlayerStart`, `parentplayer`).
- Проверить, можно ли клонировать игрока и отключить у клона камеру/ввод/звук
  без падений.
- Снять список слоёв (`LayerMask`) и тегов, чтобы удалённые игроки не ломали
  AI-восприятие и физику.

## Инструменты

- `Il2CppDumper` v6.7.46 → `dump.cs`, `DummyDll/`, `il2cpp.h`, `script.json`.
- `BepInEx 6` → `BepInEx/interop/*.dll`.
- ILSpy / dnSpy — для чтения `DummyDll` (только локально).
- IDA/Ghidra + `il2cpp.h`/`script.json` — если понадобятся тела методов.

Дампы и `DummyDll` **не публикуются**: это производные от кода игры.
