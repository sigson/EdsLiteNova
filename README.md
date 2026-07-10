# EDS Lite — порт на .NET 10 / MAUI

Порт открытого проекта **edslite** (шифрование файлов и контейнеров, оригинал на
Java/Android) на **C# .NET 10 + .NET MAUI**. Криптоядро — байт-в-байт совместимое с
оригиналом за счёт сохранения нативных C-реализаций алгоритмов (см. `native/`),
поверх которых построен управляемый слой.

Лицензия: **GPLv2+** (как у оригинала).

---

## Что уже готово

| Слой | Статус | Где |
|------|--------|-----|
| 0. Нативная крипта (C, единый C-ABI shim) — XTS **и CBC** | ✅ собрано, все KAT проходят | `native/` |
| 1. Управляемое криптоядро (шифры, XTS, **CBC**, хеши, HMAC, PBKDF2, SecureBuffer, IO) | ✅ написано | `src/Eds.Core/` |
| 2. Контейнеры TrueCrypt + VeraCrypt + **LUKS1** (чтение и запись, перебор алгоритмов, AF-splitter) | ✅ написано | `src/Eds.Core.Containers/` |
| 3. ФС **FAT12/16/32 — чтение + запись** (BPB, цепочки, LFN-чтение, форматирование FAT16, запись файлов) | ✅ написано, есть тесты | `src/Eds.Core/Fs/Fat/` |
| Консольный отладочный хост (без эмулятора) — self-test + `open`/`ls`/`cat` | ✅ | `src/Eds.ConsoleHost/` |
| xUnit-тесты (KAT + CBC + round-trip контейнера + FAT) | ✅ | `tests/Eds.Core.Tests/` |
| MAUI-приложение: **файловый браузер** (открыть контейнер → смонтировать FAT → навигация + предпросмотр) + вкладка Diagnostics | ✅ общие файлы | `src/Eds.Maui/` |
| 3–5. FAT-запись, exFAT, EncFS, локации, полный UI-файлменеджер | 🚧 roadmap | см. `docs/ROADMAP.md` |

Подробности по оставшимся фазам — в [`docs/ROADMAP.md`](docs/ROADMAP.md).

---

## Порядок сборки

Сначала **всегда** собирается нативная библиотека (она кладётся в
`src/Eds.Core/runtimes/<RID>/native/`, откуда её подхватывает управляемая сборка),
затем — .NET.

### 1. Нативная библиотека `edscrypto`

Нужны `cmake` + компилятор C (gcc / clang / MSVC).

**Linux / macOS / Windows-msys2:**
```bash
# опционально EDS_TESTS=1 — прогнать нативные KAT после сборки
EDS_TESTS=1 ./scripts/build-native.sh
```

**Windows (MSVC):**
```powershell
pwsh scripts/build-native.ps1 -Tests
# или под msys2/MinGW:
pwsh scripts/build-native.ps1 -Mingw -Tests
```

Скрипт определяет RID (`linux-x64`, `win-x64`, `osx-arm64`, …) и копирует
`libedscrypto.so` / `edscrypto.dll` / `libedscrypto.dylib` в нужную папку
`runtimes`.

### 2. .NET-проекты

```bash
dotnet build EdsLite.sln -c Debug
```

---

## Как отлаживать БЕЗ эмулятора Android

Это было ключевым требованием. Есть два пути, оба работают на обычном десктопе:

### A. Консольный хост (самый быстрый цикл)
```bash
dotnet run --project src/Eds.ConsoleHost -- selftest
```
Прогоняет весь конвейер managed → native: AES (FIPS-197), RIPEMD-160, Whirlpool,
AES-XTS, PBKDF2 (RFC 6070) и **полный round-trip контейнера** — создаёт
TrueCrypt-контейнер во временном файле, пишет данные через прозрачный шифрослой,
переоткрывает с паролем, читает обратно и проверяет отказ при неверном пароле.

Открыть реальный контейнер и (если внутри FAT) просмотреть файлы:
```bash
dotnet run --project src/Eds.ConsoleHost -- open /path/to/volume.hc "пароль"
dotnet run --project src/Eds.ConsoleHost -- ls   /path/to/volume.hc "пароль" /
dotnet run --project src/Eds.ConsoleHost -- cat  /path/to/volume.hc "пароль" /readme.txt
```

### B. Десктопная «голова» MAUI
MAUI прекрасно запускается как настольное приложение — эмулятор не нужен:
```bash
# Windows
dotnet build src/Eds.Maui -f net10.0-windows10.0.19041.0 -t:Run
# macOS
dotnet build src/Eds.Maui -f net10.0-maccatalyst -t:Run
```
Ядро одинаково на всех платформах, поэтому точки останова и пошаговая отладка на
десктопе покрывают всю бизнес-логику. Кнопка **Run self-test** в приложении
делает то же, что консольный `selftest`.

### C. Тесты
```bash
dotnet test
```

---

## Структура репозитория

```
EdsLite.sln
Directory.Build.props          общие свойства (net10.0, nullable, GPL-заголовок)
native/                        C-ядро + единый C-ABI shim (edscrypto)
  include/edscrypto.h          публичный ABI (extern "C")
  src/edscrypto.c              реализация shim поверх vendor-алгоритмов
  vendor/                      исходники алгоритмов (скопированы 1:1 из оригинала)
  tests/kat_test.c             нативные Known-Answer Tests
  CMakeLists.txt
src/
  Eds.Core/                    управляемое криптоядро (P/Invoke, XTS, KDF, IO)
  Eds.Core.Containers/         TrueCrypt / VeraCrypt layouts + EdsContainer
  Eds.ConsoleHost/             отладочный CLI (selftest / open)
  Eds.Maui/                    MAUI-приложение (общие файлы)
tests/
  Eds.Core.Tests/              xUnit: KAT + round-trip
scripts/
  build-native.sh / .ps1       сборка и раскладка нативной библиотеки
docs/
  ROADMAP.md                   что и как делать в оставшихся фазах
  BUILDING.md                  кросс-сборка натива под Android/iOS
.github/workflows/ci.yml       CI: натив на Linux/Windows/macOS + dotnet test
.vscode/                       задачи (build-native, test) и конфиги отладки
LICENSE / NOTICE               GPLv2+ и атрибуция оригинального edslite
```

---

## Разработка, CI и лицензия

- **VS Code**: в `.vscode/` есть задача `build-native` (собирает нативную
  библиотеку и прогоняет KAT) и конфигурации запуска — `ConsoleHost: selftest`
  (отладка всего конвейера без эмулятора), `open`, `ls`. Задача `test` запускает
  xUnit-набор.
- **CI** (`.github/workflows/ci.yml`): собирает нативную библиотеку и гоняет её
  KAT на Linux/Windows/macOS, затем `dotnet test` платформенно-независимых
  проектов и консольный self-test. Это именно та проверка, которую стоит
  прогнать, чтобы валидировать управляемый код.
- **Лицензия**: проект под **GPLv2+** (наследуется от edslite). Перед
  распространением вставьте канонический текст GPLv2 в `LICENSE` (ссылка внутри
  файла); атрибуция оригинала и сторонних компонентов — в `NOTICE`.

---

## Замечание про MAUI и платформенный boilerplate

В `src/Eds.Maui/` лежат **общие** файлы (`MauiProgram.cs`, `App`, `AppShell`,
`MainPage`, `MainViewModel`). Папки `Platforms/*` (AndroidManifest, iOS Info.plist,
Windows-манифест и т.п.) генерируются инструментарием под вашу версию workload —
их лучше не писать руками. Самый надёжный путь:

```bash
dotnet new maui -n Eds.Maui -o /tmp/scaffold
# перенесите из /tmp/scaffold папки Platforms/ и Resources/ в src/Eds.Maui/,
# затем оставьте наши MauiProgram.cs / App / AppShell / Views / ViewModels.
```

После этого десктопная голова собирается и запускается как описано выше.

---

## Совместимость данных — приоритет №1

Нативные алгоритмы (RIPEMD-160, Whirlpool, Serpent, Twofish, GOST, XTS) оставлены
на C намеренно: их нет в BCL, и любое расхождение сломало бы возможность открывать
уже существующие контейнеры. Байт-совместимость доказана **официальными
published-векторами** (не только round-trip'ом): AES — FIPS-197; Twofish и
Serpent — официальные векторы; AES-XTS — IEEE 1619 (vector 1 и 2); CBC — NIST SP
800-38A; RIPEMD-160/Whirlpool — эталонные значения; PBKDF2 — RFC 6070. Всё это
прогоняется нативным KAT (`native/tests/kat_test.c`) и managed-KAT
(`tests/Eds.Core.Tests`). Перед развитием ФС-слоя стоит дополнительно собрать
корпус реальных TrueCrypt/VeraCrypt/LUKS-контейнеров (созданных официальными
утилитами) и добавить их в тесты открытия — проверка совместимости уже на уровне
форматов.
