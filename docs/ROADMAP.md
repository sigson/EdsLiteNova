# ROADMAP — оставшиеся фазы порта

Документ описывает, что уже сделано и как доводить порт дальше. Нумерация фаз —
из исходного гайда (`edslite-port-context.md`, §10).

## Легенда
- ✅ готово
- 🚧 частично / каркас
- ⬜ не начато

---

## Фаза 0 — нативная крипта ✅

Единый C-ABI shim `edscrypto` (`native/`) заменяет весь ворох JNI-обёрток.
Экспортирует чистый `extern "C"` ABI (без `JNIEnv`/`jobject`): блочные шифры
(AES/Serpent/Twofish/GOST), режим XTS, потоковые хеши (RIPEMD-160/Whirlpool).
Ядра алгоритмов скопированы из оригинала **дословно** — это гарантирует
байт-совместимость. Все KAT проходят (`EDS_TESTS=1 ./scripts/build-native.sh`).

Что осталось на будущее в нативном слое (нужно для LUKS/EncFS):
- ✅ режим **CBC** в shim (`eds_cbc_*`) — проверен KAT против NIST SP 800-38A
  (AES-128-CBC), байт-в-байт. Управляемая обёртка — `Crypto/Modes/Cbc.cs` +
  движки `AesCbc`/`SerpentCbc`/`TwofishCbc`/`GostCbc`.
- ✅ режим **CFB-128** в shim (`eds_cfb_*`) — проверен KAT против NIST SP 800-38A
  (CFB128-AES128), байт-в-байт, включая неполноблочный хвост. Управляемая
  обёртка — `Crypto/Modes/Cfb.cs` + движок `AesCfb`. Нужен для EncFS
  (`AESCFBStreamCipher`).
- ⬜ режимы **CTR / ECB** в shim (если понадобятся для отдельных форматов).
- ⬜ `localxts` (если понадобится) и антифорензик-сплиттер AF (LUKS).
- ⬜ кросс-компиляция под Android (`.so` на все ABI) и iOS (статическая линковка,
  `__Internal`) — заготовка в `docs/BUILDING.md`.

Версия нативного ABI повышена до **3** (добавлен CFB; старые функции не менялись).

---

## Фаза 1 — управляемое криптоядро ✅

`src/Eds.Core/`:
- P/Invoke через `[LibraryImport]` (source-generated) — `Crypto/Native/`.
- Блочные шифры-обёртки, XTS-движки (AES/Serpent/Twofish/GOST).
- Хеши: SHA-1/256/512 через BCL `IncrementalHash`; RIPEMD-160/Whirlpool — натив.
- HMAC (ручной, над `IMessageDigest`) и PBKDF2 (`HashBasedPbkdf2`) с прогрессом
  через `IProgress<int>` + `CancellationToken` (замена `ProgressReporter`).
- `SecureBuffer` — pinned-память + затирание + реестр `CloseAll()`.
- `EditableSecureBuffer` — редактируемый защищённый буфер для ввода пароля (pinned
  `char[]`, insert/delete/clear с затиранием, `ToSecureBuffer()`→UTF-8, `CloseAll`).
- IO-абстракция `IRandomAccessIO` + `StreamRandomAccessIO`.
- `EncryptedFile` — прозрачный посекторный XTS поверх базового IO.

Осталось:
- ⬜ `EncryptedFileWithCache` — LRU-кэш расшифрованных секторов (оптимизация;
  текущий `EncryptedFile` корректен, но без кэша).
- ⬜ полный порт `TransRandomAccessIO` (буферизация), если понадобится точное
  поведение оригинала под нагрузкой.

---

## Фаза 2 — контейнеры ✅ (TrueCrypt/VeraCrypt/LUKS1)

`src/Eds.Core.Containers/`:
- `VolumeLayoutBase`, `StdLayout` (TrueCrypt), `VeraCryptLayout`, **`LuksLayout`**,
  `EdsContainer` (пробует LUKS → VeraCrypt → TrueCrypt).
- Реализован **перебор алгоритмов** (hash × cipher) при открытии TC/VC.
- **LUKS1**: разбор заголовка (big-endian, 8 keyslot'ов), перебор активных
  слотов, AF-разбиение мастер-ключа (`crypto/Af.cs`), проверка MK-digest.
  Реализован и **путь записи** (`FormatNew`) → полный round-trip без cryptsetup.
- Реализован **путь записи** заголовка для всех форматов → self-contained тесты.
- CRC32 через `System.IO.Hashing`.

Осталось (не блокирует основной сценарий):
- ⬜ LUKS **cbc-essiv:sha256** и режимы вне xts-plain64/cbc-plain (как и в
  оригинале edslite, ESSIV не поддержан).
- ⬜ **скрытые тома** (hidden volume) — заголовок по смещению, отдельная область
  (пока заглушка `CalcHiddenVolumeSize`=0).
- ✅ **keyfiles** (файлы-ключи) — `KeyfileMixer` (не-reflected CRC32), проброшены в
  create/open/`ContainerLocation` (Фаза G, `PHASE-G-REPORT.md`).
- ✅ **VeraCrypt PIM** — проброшен через `ContainerOpenOptions`/`ContainerCreator`.
- ✅ **смена пароля** контейнера — `EdsContainer.ChangePassword` (TC/VC/LUKS).
- ✅ **метаданные форматов** — `IContainerFormatInfo`/`ContainerFormats` для UI.
- ⬜ подключить корпус реальных контейнеров (TrueCrypt/VeraCrypt/LUKS от
  официальных утилит) в тесты открытия — главная проверка совместимости данных
  (K1); особенно важно для байт-точности keyfiles/PIM.

---

## Фаза 3 — файловые системы 🚧 (FAT read+write ✅)

Порядок по возрастанию сложности:
1. 🚧 **FAT12/16/32** — чтение готово (BPB, цепочки, LFN, открытие файлов).
   **Запись**: форматирование FAT16 (`FatFormatter`), аллокация кластеров и
   запись во все копии FAT, создание файлов с **длинными именами (LFN)**,
   **подкаталоги** (`CreateDirectory`), **удаление** (`Delete`, освобождение
   цепочки), обобщённый доступ к каталогу (корень FAT12/16 или цепочка кластеров
   с автопродлением). Path-API: `WriteFile`/`CreateDirectory`/`Delete`.
   Покрыто тестами (LFN round-trip, вложенные каталоги, удаление+реклейм,
   полный стек контейнер→FAT→файл).
   Осталось: ⬜ запись в FAT32-корень уже поддержана обобщённым кодом, но не
   протестирована на реальном FAT32-томе; ⬜ переименование/перемещение;
   ⬜ обновление метки тома/FSInfo.
2. 🚧 **EncFS** — пофайловое шифрование (не контейнер): своя конфигурация XML,
   имена файлов и содержимое шифруются отдельно; использует CBC/CFB (готовы).
   Начаты самодостаточные листовые компоненты (проверяемые юнит-тестами):
   - ✅ `B64` (`Eds.Core.Fs.EncFs`) — EncFS-специфичное кодирование имён
     (алфавит без `/` и `.`); round-trip кодирование/декодирование покрыт тестами.
   - ✅ MAC-слой (`Eds.Core.Fs.EncFs.Macs`): `MacCalculator` + `Sha1MacCalculator`
     (HMAC-SHA1 + свёртка 19→8 байт, chained-IV) — сверено с эталонным HMAC-SHA1.
   - ✅ Слой шифров (`Eds.Core.Fs.EncFs.Ciphers`): `CipherBase` (HMAC-производный
     IV), `StreamCipherBase` (двухпроходное shuffle/flip-кодирование),
     `AesCbcFileCipher`/`AesCfbStreamCipher`/`BlockAndStreamCipher`, шифры имён
     `Null/Block/Stream` + интерфейс `INameCodec`. Round-trip покрыт тестами
     (данные и имена, base64/base32, chained-IV). Для этого в порт-версию
     `AesCbc` добавлен обратно-совместимый конструктор `(keySize, fileBlockSize)`.
   - ✅ Конфигурация и кодеки (`Eds.Core.Fs.EncFs`, `.Codecs`): `Config`
     (разбор/запись `encfs6.xml` через `System.Xml.Linq`, DTD-tolerant),
     `IAlgInfo`/`IDataCodecInfo`/`INameCodecInfo`, реестр `EncFsCodecs`
     (`AESDataCodecInfo`, `Block/Stream/Null/BlockCS` name-кодеки). Покрыто
     тестами (разбор репрезентативного XML, write→read, сборка рабочего кодека
     имён из конфигурации, отказ на неизвестном алгоритме).
   - ✅ Вывод ключа тома (`Eds.Core.Fs.EncFs.EncFsVolumeKey`): PBKDF2-HMAC-SHA1
     (пароль+salt+iterations из конфига) → KEK → потоковая расшифровка
     `EncryptedVolumeKey` (первые 4 байта — MAC-32 checksum как IV), проверка
     контрольной суммы (неверный пароль → `WrongPasswordException`). Round-trip
     обёртки/разворачивания покрыт тестами. **Криптостек EncFS полон.**
   - ✅ Трансформирующий IO + MAC-файлы (§2.3): `RandomAccessIOWrapper`/
     `BufferedRandomAccessIO`/`TransRandomAccessIO` (`Eds.Core.Fs.Util`) — буферная
     база с хуками шифрования/MAC; `MacFile` (`Eds.Core.Crypto`) — поблочный MAC
     (`[MAC][rand][data]`). Round-trip и обнаружение подделки покрыты тестами.
   - ✅ Декораторы ФС (`Eds.Core.Fs.Util`): `FsRecordWrapper`/`FileWrapper`/
     `DirectoryWrapper`/`FileSystemWrapper` + `PathUtil.GetNameFromPath` — база
     паттерна «декоратор» для EncFS (и позже локаций/сервисов). Делегирование
     (create/list/read/rename с ре-маппингом путей) покрыто тестом.
   - ✅ Буферный `BufferedEncryptedFile` (`Eds.Core.Crypto`) — по-блочный
     прозрачный шифрующий IO на `TransRandomAccessIO` (каждый блок — свой IV от
     layout; последний неполный блок — на реальную длину, т.е. через потоковый
     шифр `BlockAndStreamCipher`). Нужен EncFS-файлам. Round-trip и
     байт-совместимость с контейнерным `EncryptedFile` покрыты тестами.
   - ✅ Файловый слой EncFS (`Eds.Core.Fs.EncFs`): `EncFsFs`/`EncFsPath`/
     `EncFsDirectory`/`EncFsFile` — открытие/создание тома, обход дерева с
     шифрованием имён через кодек (chained-IV), `EncFsFile` собирает per-file
     IV-заголовок + `BufferedEncryptedFile` + `MacFile`. Сквозной тест
     (создать том → записать папку/файл → закрыть → переоткрыть паролем →
     прочитать/сверить) для конфигураций no-chained/chained/±block-MAC.
     **EncFS перенесён как рабочая вторая ФС.**

   ⚠️ Осталось по совместимости (K1): сверка байт-в-байт с реальными EncFS-папками
   (созданными настольным EncFS) — round-trip доказывает внутреннюю корректность,
   но не interop; нужны эталонные тома.
3. ⬜ **exFAT** — в оригинале нативный (`fs.exfat`), нужен отдельный shim или
   управляемый порт.

Драйвер FAT доступен из консоли для реальных контейнеров:
```bash
dotnet run --project src/Eds.ConsoleHost -- ls  volume.hc "пароль" /
dotnet run --project src/Eds.ConsoleHost -- cat volume.hc "пароль" /readme.txt
```

Общая абстракция `FileSystem`/`Path`/`File`/`Directory` из `fs.*` **введена**
как полноценная Path-модель в namespace `Eds.Core.Fs.Vfs` (интерфейсы
`IFileSystem`/`IPath`/`IFsRecord`/`IDirectory`/`IFile`/`IFileSystemInfo`,
`FileAccessMode`). Она добавлена **аддитивно** — рядом с ранним минимальным
`Eds.Core.Fs.Abstract.IFileSystem`, который пока использует FAT-браузер, чтобы
ничего не сломать до переадаптации FAT.

Также в рамках Фазы B перенесены:
- ✅ `StringPathUtil` (`Eds.Core.Fs.Util`) — чистая логика путей, case-insensitive,
  разделитель `/`. Покрыт тестами.
- ✅ `PathBase` (`Eds.Core.Fs.Util`) — базовый `IPath` (parent/combine/equality).
- ✅ `StdFs` (`Eds.Core.Fs.Std`) — файловая система устройства поверх `System.IO`
  (`StdFs`/`StdFsPath`/`StdFsRecord`/`StdDirRecord`/`StdFileRecord`/`StdFsFileIO`).
  Round-trip (create/write/read/list/rename/delete) покрыт тестами.
- ✅ IO-адаптеры `RandomAccessInputStream`/`RandomAccessOutputStream`
  (`Stream` ⇄ `IRandomAccessIO`).

Осталось по Фазе B: ✅ FAT доступен под единым Vfs-контрактом через аддитивный
адаптер `FatVfs` (`Eds.Core.Fs.Fat`) — навигация/создание/чтение/запись (write-back
RAIO с delete-then-write), покрыт тестом на FAT16. ✅ `TransRandomAccessIO`/
`BufferedRandomAccessIO` перенесены. Остаются мелкие хвосты, **заблокированные
отсутствием FAT32-форматтера** (его нет в проекте — это уже новая фича, а не хвост):
⬜ FAT32-root round-trip тест, ⬜ обновление FSInfo/метки тома (§4.6, только для
внешних инструментов; FAT32-драйвер и так пересчитывает free-count при монтировании).
Косметика: ⬜ ретировать минимальный `Eds.Core.Fs.Abstract` и перевести
`BrowserViewModel` (MAUI) на `FatVfs` — обе модели сейчас сосуществуют и работают;
менять MAUI-код здесь нельзя проверить сборкой (в тест-прогоне MAUI не собирается).

---

## Фаза 4 — локации, настройки, реестр ✅ (ядро)

Сделано (платформенно-независимое ядро, см. `PHASE-D-REPORT.md`):
- ✅ `locations.*` — абстракция «где лежит контейнер/файл»: `LocationUri`
  (замена `Uri`), `ILocation`/`IOpenableLocation`/`IEdsLocation`, `DeviceLocation`
  (папка на устройстве поверх `StdFs`), `EncFsLocation`, а также ключевой
  `ContainerLocation` (в `Eds.Core.Containers`), связывающий базовую локацию +
  `EdsContainer` + `EncryptedFileWithCache` + `FatVfs`.
- ✅ `settings.*` — `ISettings` (нужный подмножество) + `InMemorySettings`
  (виртуальные `Load`/`Store` для персистентной реализации). `Parcelable`
  отброшен; `org.json` → `System.Text.Json`; вещание → события C#.
- ✅ реестр локаций `LocationsManager` (create-from-URI через `ILocationFactory`,
  add/remove/replace/find, load/save в настройки, стек открытия + close-all,
  события Added/Removed/Changed). Хуки для автоблокировки (порядок закрытия,
  `GetLastActivityTime`, `GetAutoCloseTimeout`) на месте — сам таймер в Фазе E.
- ✅ Milestone: контейнер/EncFS регистрируется → сохраняется → после
  «перезапуска» (новый менеджер над теми же настройками) переоткрывается по
  паролю → монтируется ФС → read/write. Покрыто `LocationsTests` (TrueCrypt/LUKS/
  EncFS) и демо `eds locations`.

Осталось (платформенное / поздние фазы):
- ⬜ реализация `ISettings` поверх MAUI `Preferences`/`SecureStorage`.
- ⬜ Android SAF / внешние провайдеры контента (платформенная часть).
- ⬜ сам сервис автоблокировки/таймаутов (Фаза E, хуки уже есть).

---

## Фаза 5 / F — UI (MAUI поверх `EdsAppController`) 🚧 (написано, **не собрано здесь**)

> UI переписан как тонкий слой над `EdsAppController` (Eds.Core.App). Ядро, к
> которому он биндится, проверено компилятором и тестами; **сам MAUI-хед требует
> MAUI-workload для сборки** и здесь не компилировался. Детали — `PHASE-F-REPORT.md`.

Готово (написано, ожидает сборки с MAUI-workload):
- ✅ **Вкладка Locations** (`LocationsPage`/VM) — список зарегистрированных локаций
  (персист через `JsonFileSettings`), добавить контейнер (file picker) / EncFS-папку
  (folder picker), открыть/закрыть/удалить построчно.
- ✅ **Экран разблокировки** (`OpenPage`/VM) — пароль + PIM + read-only; `OpenAsync`
  (KDF в фоне) с живым прогрессом hash×cipher; переход в браузер.
- ✅ **Браузер локации** (`VaultBrowserPage`/VM) — листинг через `Browse`
  (сортировка), навигация вглубь/вверх, при r/w — новая папка, импорт файла,
  удаление (через очередь файловых операций), закрытие.
- ✅ **Вкладка Create** — формат/шифр/хеш/размер/**PIM** → `CreateContainerAsync`
  (создать + зарегистрировать как локацию).
- ✅ Вкладка **Diagnostics** (self-test managed → native, без изменений).

Дальше (платформенное):
- ⬜ прогресс больших операций в UI; multi-select, сортировочные контролы, просмотр
  изображений.
- ⬜ `IExternalFileOpener` + «открыть во внешнем приложении» через
  `TempFileManager.OpenAndTrackAsync`.
- ⬜ уведомления (Android) и запуск `AutoCloseService.RunAsync` из жизненного цикла;
  блокировка по гашению экрана.
- ⬜ защита сохранённых паролей через платформенный `IProtectionKeyProvider`
  (Keystore/Keychain/DPAPI) — хук уже есть.
- ⬜ UI смены пароля (API `ChangeContainerPasswordAsync` готов).
- ⬜ Android SAF DocumentsProvider / виджеты (платформенная часть).

---

## Кросс-срезовые задачи

- 🚧 **Корпус совместимости**: криптопримитивы уже проверены **official
  published-векторами** (AES FIPS-197, Twofish, Serpent, XTS IEEE 1619, CBC NIST
  SP 800-38A, RIPEMD-160/Whirlpool reference) — это доказывает байт-совместимость
  на уровне алгоритмов. Осталось ⬜ добавить набор эталонных **контейнеров**
  (TrueCrypt, VeraCrypt разных hash/cipher, LUKS) + автотесты открытия — проверка
  совместимости на уровне форматов.
- ⬜ **CI**: сборка натива на трёх ОС + `dotnet test`.
- ⬜ **Локализация** строк UI.
- ⬜ **Аудит затирания секретов** (ключи/пароли) на всех путях.

---

## Идиомы Java → C# (памятка при дальнейшем портировании)

| Java | C# |
|------|-----|
| `byte` (знаковый), `& 0xff` | `byte` (беззнаковый) — лишние маски убирать |
| `>>>` | сдвиг беззнакового типа (`uint`/`ulong`) |
| `ByteBuffer` big-endian | `System.Buffers.Binary.BinaryPrimitives` |
| `MessageDigest` | `IncrementalHash` (BCL) или наш `IMessageDigest` |
| `Parcelable` | убрать |
| `RxJava` | `IObservable`/`async`/`IAsyncEnumerable` |
| `ProgressReporter` | `IProgress<int>` + `CancellationToken` |
| JNI-обёртки | единый `edscrypto` C-ABI + `[LibraryImport]` |
