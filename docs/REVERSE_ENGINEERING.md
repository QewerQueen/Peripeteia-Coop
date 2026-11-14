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
| `NPCConversation` / `QuestLog` / `QuestAction` | диалоги и квесты | VIDE-диалоги, флаги квестов |

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
- Сообщения: `Hello` (протокол, версия мода, версия игры, имя клиента),
  `Welcome` (id + те же версии + имя хоста), `Pose`.
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
