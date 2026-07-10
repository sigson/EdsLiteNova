# EDS Lite (Java → C#/.NET 10 MAUI) — отчёт по текущему этапу и хендофф

> Назначение: передать состояние работы в новый чат. Документ фиксирует **что
> сделано**, **что осталось на текущем этапе** (по `edslite-porting-gap-guide.md`
> и фактическому состоянию репозитория) и **отдельно, детально — незакрытые
> кросс-задачи**. Дополняет `docs/ROADMAP.md`.

## 0. Статус тестов и среда

- Последний подтверждённый прогон: **все тесты зелёные**, включая сквозной
  EncFS (create → write → close → reopen → read) для 3 конфигураций.
- В этой (закрывающей хвосты) итерации добавлены тесты `FatVfsTests` — их нужно
  прогнать `dotnet test` при первом запуске в новом чате.
- Среда прошлого чата: **.NET SDK был недоступен** (домены Microsoft вне
  allowlist), поэтому C#-код писался «вслепую» и проверялся вашими прогонами
  `dotnet test`. Нативный C-модуль (`edscrypto`) собирался/проверялся локально
  (cmake+gcc есть). Рабочий цикл: правка → вы собираете/гоняете → присылаете
  ошибки → точечная правка.

---

## 1. Что сделано (закрытые фазы и пункты — коротко)

### Фаза A — крипто-достройка · ЗАКРЫТА
- Режим **CFB** (нативный shim `eds_cfb_*` + managed `Cfb`/`AesCfb`), сверен NIST
  SP800-38A KAT.
- `EncryptedFileWithCache` — LRU-кэш секторов.
- `MacFile` (§2.3, RAIO-вариант) — поблочный MAC `[MAC][rand][data]`.
- `EncryptedInputStream`/`EncryptedOutputStream`.
- `GostCbc` (для паритета).

### Фаза B — абстракция ФС + устройство · ЗАКРЫТА (ядро) + хвост FAT→Vfs
- Полная Vfs-модель `Eds.Core.Fs.Vfs` (`IFileSystem`/`IPath`/`IFsRecord`/
  `IDirectory`/`IFile`/`IDirectoryContents`/`FileAccessMode`).
- `StdFs` (устройство поверх `System.IO`).
- `StringPathUtil`, `PathBase`, `RandomAccessInputStream`/`OutputStream`.
- Трансформирующий IO: `RandomAccessIOWrapper`/`BufferedRandomAccessIO`/
  `TransRandomAccessIO`.
- Декораторы: `FsRecordWrapper`/`FileWrapper`/`DirectoryWrapper`/
  `FileSystemWrapper`, `PathUtil`.
- `BufferedEncryptedFile` — по-блочный прозрачный шифрующий IO (нужен EncFS).
- **`FatVfs`** — FAT под единым Vfs-контрактом (аддитивный адаптер над ядром
  `FatFileSystem`, write-back RAIO). Покрыт тестом на FAT16.

### Фаза C — EncFS (вторая ФС) · ЗАКРЫТА функционально
- Листья: `B64`, `MacCalculator`/`Sha1MacCalculator`.
- Слой шифров: `CipherBase`/`StreamCipherBase`, `AesCbcFileCipher`/
  `AesCfbStreamCipher`/`BlockAndStreamCipher`, шифры имён `Null/Block/Stream`,
  интерфейс `INameCodec`.
- `AlgInfo`/`IDataCodecInfo`/`INameCodecInfo` + реестр `EncFsCodecs`.
- `Config` (`encfs6.xml`, DTD-tolerant), `EncFsVolumeKey` (PBKDF2 → мастер-ключ,
  проверка пароля).
- Файловый слой: `EncFsFs`/`EncFsPath`/`EncFsDirectory`/`EncFsFile` (per-file
  IV-заголовок + `BufferedEncryptedFile` + `MacFile`).
- Сквозной интеграционный тест (no-chained / chained / ±block-MAC).

> Для сравнения: ранее (до этих трёх фаз) уже были перенесены и зелёные —
> нативный крипто-shim (AES/Serpent/Twofish/GOST, XTS, CBC, RIPEMD-160,
> Whirlpool), крипто-ядро, контейнеры (TrueCrypt/VeraCrypt/LUKS1, базовый путь),
> FAT12/16/32 (чтение+запись), иерархия исключений, каркас MAUI (3 экрана).

---

## 2. Что осталось на текущем этапе (хвосты активных фаз)

Это **не новые фазы**, а незакрытые пункты уже начатых A/B/C.

### 2.1. Хвосты FAT (§4.6), заблокированные отсутствием FAT32-форматтера
- ⬜ **FAT32-root round-trip тест.** В проекте есть только `FatFormatter.FormatFat16`;
  создать FAT32-образ для теста нечем. Полноценный `FormatFat32` (FAT32 BPB +
  FSInfo-сектор + root-кластер) — это **новая фича**, поэтому в рамках «не
  начинать новое» не делался.
- ⬜ **Обновление FSInfo / метки тома** на FAT32. `Bpb` не парсит номер
  FSInfo-сектора, аллокатор его не обновляет. Не критично для целостности данных
  (FAT32-драйвер пересчитывает free-count при монтировании); нужно только для
  корректного отображения свободного места внешними инструментами (P2).

### 2.2. Косметика Фазы B (UI-сторона, здесь не проверить сборкой)
- ⬜ **Ретировать минимальный `Eds.Core.Fs.Abstract`** и перевести
  `BrowserViewModel` (MAUI, ~15 мест) на `FatVfs`/Vfs. Сейчас минимальная модель
  (`FatFileSystemAdapter`) и Vfs **сосуществуют и обе работают** — функционально
  FAT уже доступен под Vfs. Правка MAUI не делалась намеренно: тест-прогон
  (`Eds.Core.Tests`) MAUI не собирает, поэтому изменения нельзя было бы проверить
  и легко отдать в новый чат сломанную MAUI-сборку. Это чистая замена типов:
  `FatFileSystemAdapter`→`FatVfs`, `.Root`→`GetRootPath().GetDirectory()`,
  `CreateSubdirectory`→`CreateDirectory`, `WriteFile(name,bytes)`→`CreateFile`+RAIO,
  `Open()`→`GetRandomAccessIO(Read)`, `Size`→`GetSize()`, `Name`→`GetName()`,
  `IsDirectory`→`IsDirectory()`. После миграции удалить `Abstract/*` и
  `FatFileSystemAdapter`, поправить `FatWriteTests` (использует минимальный адаптер).

### 2.3. Мелкие хвосты Фазы A/C (не блокирующие)
- ⬜ `MACInputStream`/`MACOutputStream` (§2.3, потоковые варианты) — **намеренно
  заменены** на `RandomAccessInputStream`/`OutputStream` поверх RAIO в `EncFsFile`.
  Портировать дословно только если появится потребитель, которому нужны именно эти
  классы.
- ⬜ Режимы `CTR`/`ECB` + движки, `LocalEncryptedFileXTS`, `DummyEncryptionEngine`
  — P3, пока не нужны (в целевых томах только xts-plain64/cbc).

### 2.4. Ещё не начатые фазы (НОВЫЕ — вне текущего этапа)
- **Фаза D** — локации + настройки · **ЗАКРЫТА (ядро)**, см. `PHASE-D-REPORT.md`.
  Добавлены `Eds.Core/Settings` (`ISettings`, `InMemorySettings`),
  `Eds.Core/Locations` (`LocationUri`, `ILocation`/`IOpenableLocation`/
  `IEdsLocation`, `LocationBase`/`OpenableLocationBase`/`EdsLocationBase`,
  `DeviceLocation`, `EncFsLocation`, `LocationsManager`, фабрики),
  `Eds.Core/Crypto/SimpleCrypto.cs` и ключевой `ContainerLocation`
  (+`ContainerLocationFactory`) в `Eds.Core.Containers`. Milestone (контейнер/
  EncFS: регистрация → сохранение → переоткрытие по паролю после «перезапуска» →
  монтирование → read/write) покрыт `LocationsTests` и демо `eds locations`.
  Архитектурное решение: чтобы не создавать цикл Core→Containers, реестр
  диспетчеризует по схеме URI через зарегистрированные фабрики, а общий контракт
  прогресса (`ContainerProgress.cs`) физически перенесён в сборку Core с
  сохранением namespace `Eds.Core.Containers` (существующие файлы не тронуты).
  Осталось платформенное: `ISettings` над `Preferences`/`SecureStorage`, SAF.
- **Фаза E** — сервисы + файловые операции · **ЗАКРЫТА (ядро)**, см.
  `PHASE-E-REPORT.md`. `Eds.Core/Services/`: `FileOperations` (copy/move/delete/
  wipe с прогрессом/отменой), `FilesCountAndSize`/`FileOperationStatus`, `WipeUtil`,
  `IFileOperationsService`+`FileOperationsService` (последовательная async-очередь),
  `AutoCloseService`+`ISystemClock`, `TempFileManager`+`IExternalFileOpener`.
  Осталось платформенное: реализация `IExternalFileOpener`, foreground-уведомления,
  запуск авто-закрытия из хоста.
- **Фазы F–H** — UI на MAUI (список локаций/drawer, диалоги открытия,
  файлменеджер, экраны настроек).
- **Фаза I** — опциональное/платформенное (exFAT, SAF-провайдеры, виджеты).
- **Фаза G (параллельная)** — полнота форматов · **ЧАСТИЧНО ЗАКРЫТА**, см.
  `PHASE-G-REPORT.md`: keyfiles (`KeyfileMixer` — не-reflected CRC32!), VeraCrypt PIM
  (проброшен через `ContainerOpenOptions`/`ContainerCreator`/`EdsContainer.Open`),
  смена пароля (`EdsContainer.ChangePassword`, TC/VC/LUKS), метаданные форматов
  (`IContainerFormatInfo`/`ContainerFormats`), проброс keyfiles/PIM в
  `ContainerLocation`. Осталось: скрытые тома (§3.2, пока заглушка), LUKS
  keyslot-rekey, и **байт-точная сверка с реальными томами (K1)**.

---

## 3. Незакрытые кросс-задачи (детально)

Кросс-задачи идут сквозь все фазы и сейчас **не закрыты**. Это главные источники
риска на будущее.

### K1 — Корпус совместимости данных (КРИТИЧНО, P1)
**Суть.** Все текущие проверки крипто/ФС — это **round-trip**: код сам пишет и сам
читает. Это доказывает **внутреннюю** согласованность, но **НЕ** interop с реальными
данными. Байт-совместимость с томами, созданными настоящими утилитами, **не
подтверждена ни для одного формата**.

**Что именно не проверено:**
- **EncFS**: тома, созданные настольным EncFS (шифрование имён + данных + MAC,
  chained-IV, uniqueIV, external-IV). Любое расхождение в порядке
  MAC-then-encrypt, выводе IV блока (`blockIndex XOR fileIV`), кодировании имён
  (B64/свёртка MAC) или PBKDF2 = **нечитаемые чужие данные**.
- **Контейнеры**: реальные TrueCrypt/VeraCrypt (разные hash×cipher, скрытые тома,
  keyfiles, PIM) и LUKS от `cryptsetup`. Сейчас в тестах только self-made
  round-trip и опубликованные KAT примитивов (они доказывают алгоритмы, но не
  полный разбор заголовков реальных файлов).

**Что сделать:**
1. Завести бинарные **эталонные артефакты**: наборы контейнеров, созданных
   официальными TrueCrypt/VeraCrypt/cryptsetup, и EncFS-папок, созданных
   настольным EncFS (по одной на каждую значимую комбинацию алгоритмов/опций).
2. Автотесты «открыть эталон паролем → прочитать известный файл → сверить
   байт-в-байт», и обратный тест «наш порт создаёт → настольная утилита читает».
3. Приоритет: EncFS (имена+данные+MAC), затем keyfiles/hidden/PIM по мере
   реализации Фазы G.

**Почему не закрыто здесь:** для этого нужны сами утилиты (EncFS/VeraCrypt/
cryptsetup) для генерации эталонов — их нет в этой среде. Это **обязательный**
следующий шаг перед тем, как считать крипто/ФС «готовыми к продакшену».

### K2 — Аудит затирания секретов (P2)
**Суть.** `SecureBuffer` (pinned-память + реестр `CloseAll`) есть, но с добавлением
EncFS/локаций/сервисов появились новые пути, где ключи/пароли живут в обычных
`byte[]`.

**Что не закрыто:**
- Мастер-ключ EncFS (`EncFsFs._encryptionKey`), производные KEK, per-file IV,
  ключи MAC, IV шифров имён — обычные `byte[]`. Часть затирается в `Dispose`/
  `finally` (`EncFsVolumeKey.DeriveKey`, `EncFsFs.Close`), но **не всё** pinned и
  **не зарегистрировано** в `SecureBuffer.CloseAll()`.
- Пароли в тестах/логике — проверить, что нигде не оседают в `string`.

**Что сделать:** пройтись по каждому новому пути с ключами, перевести на pinned
`byte[]`/`char[]`, затирать в `Dispose`, регистрировать в реестре `SecureBuffer`.

### K3 — CI и кросс-сборка натива (P2)
**Суть.** Нативный `edscrypto` собран и проверен **только под linux-x64**
(staged в `src/Eds.Core/runtimes/linux-x64/native/`).

**Что не закрыто:**
- Сборка нативного модуля под **Android ABI** (arm64/arm/x64/x86) и **iOS**
  (статическая линковка, `DllImport("__Internal")`).
- Windows/macOS `.dll`/`.dylib`.
- CI-пайплайн: сборка на 3 ОС + `dotnet test` + кросс-компиляция под мобильные.
- exFAT: нативного модуля нет в репозитории (flavor `fsm` качался отдельно) —
  либо порт, либо managed-реализация; на iOS только статика.

**Что сделать:** настроить CI (сборка натива на всех RID + прогон тестов),
подключать по мере выхода на мобильные таргеты.

---

## 4. Как продолжать (практическое)

- **Среда:** если в новом чате будут доступны домены Microsoft
  (`dot.net`/`builds.dotnet.microsoft.com`), можно ставить .NET 10 SDK и
  компилировать/гонять тесты прямо там — это радикально ускорит и обезопасит
  перенос оставшихся фаз (особенно UI и локаций).
- **Рабочий цикл без SDK:** писать код осторожно, вы собираете/гоняете
  `dotnet test`, присылаете ошибки — точечные правки. Так пройдены A/B/C.
- **Фаза D (локации + настройки) — ЗАКРЫТА (ядро).** Ключевой класс
  `ContainerLocation` реализован (см. `PHASE-D-REPORT.md`). Проверить первым делом
  `dotnet test` (новый `LocationsTests`) — код написан «вслепую» без компилятора.
- **Фаза E (сервисы + файловые операции) — ЗАКРЫТА (ядро).** См. `PHASE-E-REPORT.md`:
  `Eds.Core/Services/` — `FileOperations` (copy/move/delete/wipe с прогрессом/
  отменой), `FileOperationsService` (последовательная async-очередь вместо
  `IntentService`), `AutoCloseService` (+`ISystemClock`) для авто-закрытия по
  таймауту, `TempFileManager` (+`IExternalFileOpener`) для temp-file цикла
  (расшифровать → редактировать внешне → зашифровать обратно). Покрыто
  `ServicesTests`. Осталось платформенное: реализация `IExternalFileOpener`,
  foreground-уведомления Android, запуск `AutoCloseService.RunAsync` из хоста.
- **Подготовка к Фазе F / упрочнение:** реализован `EditableSecureBuffer`
  (`Eds.Core/Crypto`) — редактируемый защищённый буфер для ввода пароля (pinned
  `char[]`, затирание при realloc/delete/clear/dispose, реестр + `CloseAll`);
  покрыт `SecureInputTests`. Дополнительно keyfile-CRC подтверждён публичным
  контрольным значением (CRC-32/MPEG-2 «123456789» == 0x0376E6E7,
  `KeyfileMixer.Crc32Mpeg2`) — снижает риск K1 по CRC.
- **Слой приложения для Фазы F:** добавлен `EdsAppController` (`Eds.Core.App`, в
  проекте `Eds.Core.Containers`) — единый платформенно-независимый фасад над
  `LocationsManager` + `IFileOperationsService` + `AutoCloseService`: регистрация
  локаций (device/container/EncFS), `OpenAsync` (KDF в фоне, keyfiles/PIM/reporter/
  CancellationToken), список/навигация, copy/move/delete/wipe, авто-закрытие,
  персист. Покрыт `AppControllerTests`. MAUI-слой (Фаза F) должен биндиться к нему.
- **Рекомендуемый следующий шаг:** **Фаза F** (подключить MAUI-оболочку — теперь
  проще: биндить к **`EdsAppController`**, а не повторять оркестрацию в каждой VM;
  реализовать платформенный `IExternalFileOpener` и уведомления; `BrowserViewModel`
  перевести с inline-логики на контроллер + `FatVfs`).
  Параллельно — **K1** (эталонные тома) и **Фаза G** (полнота форматов): keyfiles,
  PIM, смена пароля и метаданные форматов уже реализованы (`PHASE-G-REPORT.md`),
  осталось скрытые тома и байт-точная сверка K1. `EdsContainer.Open` уже принимает
  keyfiles/PIM через `ContainerOpenOptions`.
- **Перед «релизным» статусом крипто/ФС** обязательно закрыть **K1**, и провести
  **K2** по новым путям пароля/ключа (сохранённый пароль, мастер-ключи EncFS/
  контейнера, а теперь и temp-file копии).

---

*Документ отражает состояние репозитория `EdsLite` на момент закрытия хвостов
фаз A/B/C. Числа/детали — из исходников напрямую. Дополняет `docs/ROADMAP.md`
(живая карта прогресса) и опирается на `edslite-porting-gap-guide.md` (ТЗ на
оставшуюся работу) и `edslite-port-context.md` (архитектура оригинала).*
