# Publishing checklist

Этот документ — для публикации репозитория и релизов. Он не обязателен для
игры, но помогает не выложить личные данные, файлы игры или дампы.

## 1. Что публикуем

В репозиторий попадают только:

- исходники плагина (`src/`);
- скрипты сборки/запуска/упаковки (`scripts/`);
- шаблон конфигурации (`config/peripeteia.coop.cfg`);
- документация (`docs/`, `README.md`, `LICENSE`);
- настройки проекта (`.editorconfig`, `.gitattributes`, `.gitignore`,
  `Directory.Build.props`, `*.csproj`).

Не попадают:

- `local/` — путь к игре, дампы, логи, бэкапы сейвов, локальные заметки;
- `tools/` — архивы BepInEx и другие сторонние бинарники;
- `bin/`, `obj/`, `artifacts/` — результаты сборки;
- `BepInEx/`, `interop/`, `GameAssembly.dll`, `UnityPlayer.dll`,
  `Peripeteia_Data/`, `*.dll`, `*.zip`, `*.log`, `EASaves`;
- любые декомпилированные файлы игры и `dump.cs`.

Это уже закрыто в `.gitignore`, но перед первым коммитом проверь глазами.

## 2. Проверка на личные данные

Из корня репозитория:

```powershell
git add -A --dry-run
git status --short
```

В выводе не должно быть `local/`, `tools/`, `bin/`, `obj/`, логов, сейвов,
дампов и `docs/game_root_before_bepinex.txt` (он перенесён в `local/docs/`).

Поиск типичных личных путей и имён:

```powershell
git grep -n -I -E "[A-Z]:\\\\Users|[A-Z]:\\\\SteamLibrary|AppData|<your-user-name>|Desktop|Documents" -- . 2>$null
```

Проверь также:

- `local/gamepath.props` не добавлен;
- в `README.md` и `docs/` нет пути к твоей установке игры;
- в `LICENSE` указан тот автор/псевдоним, который ты готов опубликовать;
- в коде нет закомментированных токенов, Steam ID, e-mail и т.п.

## 3. Проверка сборки

```powershell
.\scripts\setup.ps1 -GameDir "C:\Path\To\Peripeteia"
.\scripts\build.ps1
.\scripts\run-two.ps1 -AutoPilot
```

Ожидаемо:

- `[net] Peer connected`, `[net] Player 1 is 'Client'`;
- `[avatar] cloned player model: renderers=53, enabled=...`, клон виден после
  первого `Grounded`-пакета и не висит в небе;
- `poses: 1@(x,y,z)` меняются;
- `[items] known=... groups=...` без массовых `applied hidden` для оружия NPC;
- `[weapon] local item='...'` появляется при настоящем оружии в руках;
- `[avatar] ... anchor=hand`, `gripDown=...`, `weaponWorld` рядом с
  `handWorld` — оружие удалённого игрока закреплено в правой кисти, а не за
  спиной;
- в логах нет `Exception`, `NullReference`, `Rejecting`;
- обычный одиночный запуск без `-coop-*` не меняет поведение игры;
- ESC открывает меню, но `[pause] timeScale` остаётся `1,00`.

Если версия игры обновилась, interop-сборки и дампы нужно сгенерировать
заново, а совместимость — перепроверить.

## 4. Версионирование и совместимость

Сейчас проект привязан к:

| Компонент | Версия |
|---|---|
| Peripeteia | `Metanoia`, build `22841583` |
| Unity | `2021.3.11f1`, IL2CPP x64 |
| BepInEx | `6.0.0-be.793+dbc82f0` (bleeding edge, IL2CPP) |
| LiteNetLib | `1.3.5` |
| Мод | `0.7.21` |
| Протокол | `21` |

Демо-версия (`Peripeteia Demo`, строка версии игры `Demo`) поддерживается тем же
билдом. Её interop отличается, поэтому проверяй мод на обеих сборках; помни, что
демо соединяется только с демо, а полная версия — только с полной.

Правила:

- при изменении сетевого протокола поднимай `ProtocolVersion` в
  `Net/CoopNetwork.cs`;
- при изменении поведения мода поднимай `Version` в
  `PeripeteiaCoop.csproj` и `PluginInfo.cs`;
- в release notes указывай поддерживаемую версию игры и build id;
- не обещай совместимость с будущими обновлениями игры: IL2CPP-interop
  ломается от патчей.

## 5. Упаковка релиза

```powershell
.\scripts\package.ps1
```

Скрипт собирает плагин и создаёт:

```
artifacts/PeripeteiaCoop-<version>.zip
```

Внутри только:

```
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
```

Пользователь распаковывает архив и дважды кликает `Install.bat`. Установщик
находит игру, ставит BepInEx 6 (зафиксированную версию) при необходимости,
копирует мод и открывает `COOP-README.html` со следующими шагами.

Важно: `scripts/install.ps1` должен храниться в кодировке **UTF-8 с BOM**.
PowerShell 5.1 читает файл без BOM как ANSI, из-за чего русский текст ломается
и скрипт не парсится. Если правишь файл — проверь, что BOM на месте.

`artifacts/` не коммитится. ZIP прикладывается к GitHub Release.

## 6. Репозиторий и релиз на GitHub

1. Создай **пустой** репозиторий (без README/gitignore от GitHub), например
   `peripeteia-coop`.
2. Выбери, под каким именем/псевдонимом публикуешь; при необходимости
   поправь `LICENSE` и `README.md`.
3. Создай первый коммит:

   ```powershell
   git add -A
   git commit -m "Release 0.3.1: fix remote weapon hand placement"
   git branch -M main
   git remote add origin https://github.com/<user>/<repo>.git
   git push -u origin main
   ```

4. Создай тег и релиз:

   ```powershell
   git tag v0.3.1
   git push origin v0.3.1
   ```

5. В GitHub Release:

   - приложи ZIP из `artifacts/`;
   - укажи SHA256 (печатает `package.ps1`);
   - перечисли ограничения: инвентарь и патроны у каждого свои (обмен — только
     через намеренно выброшенный предмет); сырой прогресс узлов VIDE не
     повторяется; откат к другому сохранению — решение хоста;
   - укажи поддерживаемую версию игры и BepInEx;
   - добавь инструкцию по порту 7777/UDP, полю «Connect by IP» и VPN-мешу
     (RadminVPN/ZeroTier/Tailscale).

## 7. Лицензии и юридические заметки

- Код мода — MIT (`LICENSE`).
- LiteNetLib — MIT, укажи в `THIRD_PARTY_NOTICES.txt`.
- BepInEx не входит в архив. `Install.bat` скачивает зафиксированную версию
  (`6.0.0-be.793+dbc82f0`) с <https://builds.bepinex.dev> и проверяет её по
  SHA256 перед установкой. BepInEx распространяется под LGPL-2.1.
- Не выкладывай ассеты, модели, звуки, `GameAssembly.dll`,
  `Assembly-CSharp.dll`, interop-сборки и дампы.
- Проект неофициальный и не связан с Ninth Exodus.
