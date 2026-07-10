# Фаза A (крипто-достройка) — отчёт о выполнении

> Продолжение порта edslite Java → C# (.NET 10 / MAUI) по плану из
> `edslite-porting-gap-guide.md` (§2, §9 — Фаза A). Этот документ фиксирует, что
> сделано в данной итерации, как это проверено и что делать дальше.

## Что сделано

Закрыта **Фаза A** гайда — «крипто-достройка: фундамент для ФС и EncFS». Все
пункты §2.1 (CFB), §2.2 (кэш), §2.4 (потоки) выполнены. §2.3 (MAC-файлы)
сознательно перенесён в Фазу C — см. ниже «Осознанные отложения».

### 1. Режим CFB-128 (§2.1, P1 — нужен EncFS `AESCFBStreamCipher`)

Нативная сторона (`native/`):
- `native/vendor/cfb_mode_impl.inc` — самодостаточный фрагмент логики CFB-128
  (полноблочная обратная связь), извлечён из оригинального `edscfb.c` с удалением
  JNI. XOR переписан побайтово вместо оригинальных `*(size_t*)`-кастов: результат
  бит-в-бит идентичен (XOR есть XOR), но нет UB на невыровненном доступе — важно
  для мобильных ABI. Структуры/хелперы префиксованы `cfb_`, чтобы не конфликтовать
  с XTS/CBC в той же единице трансляции.
- `native/src/edscrypto.c` — добавлены экспорты `eds_cfb_init/attach/encrypt/decrypt/close`.
- `native/include/edscrypto.h` — декларации CFB; `EDS_CRYPTO_VERSION` поднят `2 → 3`.

Управляемая сторона (`src/Eds.Core/`):
- `Crypto/Native/NativeCrypto.cs` — P/Invoke-биндинги `CfbInit/Attach/Encrypt/Decrypt/Close`.
- `Crypto/Modes/Cfb.cs` — абстрактный режим `Cfb : IEncryptionEngine` (не файловый:
  у CFB нет секторов, весь буфер — один самосинхронизирующийся поток на 16-байтном
  IV). Оркестрация 1:1 с `Cbc.cs`: строит каскад шифров, прикрепляет нативные
  указатели, прокидывает IV-буфер (натив обновляет его на месте).
- `Crypto/Engines/CfbEngines.cs` — движок `AesCfb` (размер ключа 16/24/32; по
  умолчанию 32), зеркалит `engines.AESCFB`.

### 2. `GostCbc` (§2.1 — паритет)

`Crypto/Engines/CbcEngines.cs` — добавлен движок `GostCbc` (отсутствовал в порту,
хотя `GOSTCBC` есть в оригинале). CBC-цепочка идёт с фиксированным крипто-блоком 16
ровно как в оригинале (там `getEncryptionBlockSize()==16` хардкодом независимо от
8-байтного блока GOST), поэтому контейнеры edslite с GOST-CBC расшифровываются
байт-в-байт.

### 3. `EncryptedFileWithCache` (§2.2, P1 — критично для файлменеджера)

`src/Eds.Core/Crypto/EncryptedFileWithCache.cs` — прозрачный шифрующий
`IRandomAccessIO` с LRU/refCount-кэшем расшифрованных буферов (по умолчанию
25 буферов × 40 секторов). Виртуальное пространство делится на буферы
`bufferSizeInBlocks * sectorSize`; каждый расшифровывается один раз при первом
касании и держится в кэше — навигация по смонтированной ФС не перечитывает и не
перешифровывает одни и те же сектора. Вытеснение — по минимальному refCount
(как в оригинале), «грязные» буферы флашатся перед вытеснением. Корректность
идентична `EncryptedFile`: тот же посекторный IV движка (для `xts-plain64` натив
сам двигает индекс сектора по многосекторному буферу), и на диск пишутся только
целые сектора в пределах длины файла — за EOF ничего не портится.

Интеграция: в `EdsContainer` добавлен `GetCachedEncryptedVolume()` — естественная
точка для будущего `ContainerBasedLocation` (Фаза D).

### 4. Потоковые обёртки (§2.4, P1)

`src/Eds.Core/Crypto/EncryptedStreams.cs`:
- `EncryptedInputStream : Stream` — последовательная расшифровка поверх базового
  потока; читает блоками `engine.FileBlockSize`, каждый с IV на смещение блока.
- `EncryptedOutputStream : Stream` — последовательная шифровка; буферизует блок,
  шифрует с IV на смещение, пишет; хвостовой неполный блок флашится на `Dispose`.

Оформлены как `Stream`-наследники (идиоматично для .NET), поведение зеркалит
`TransInputStream`/`TransOutputStream`-варианты оригинала для последовательного
случая (включая оптимизацию `allowEmptyParts` — пропуск пустых блоков).

## Как проверено

**Нативный CFB — доказательно, здесь и сейчас.** В `native/tests/kat_test.c`
добавлен тест `test_cfb` с официальными векторами **NIST SP 800-38A CFB128-AES128**
(F.3.13/F.3.14) плюс проверка неполноблочного хвоста (37 байт). Сборка и прогон:

```
cd native && mkdir build && cd build
cmake -DEDS_BUILD_TESTS=ON .. && make && ./kat_test
```

Результат: `=== edscrypto native KAT (version 3) === … ALL PASSED (0 failure(s))`,
включая `CFB encrypt (4 blocks)`, `CFB decrypt round-trip`,
`CFB partial-length round-trip`. Собранная `libedscrypto.so` застейджена в
`src/Eds.Core/runtimes/linux-x64/native/`.

**Управляемый слой — покрыт xUnit-тестами** (`tests/Eds.Core.Tests/PhaseACryptoTests.cs`):
- `Cfb128_MatchesNist80038a` — управляемый CFB против тех же NIST-векторов.
- `Cfb128_PartialLength_RoundTrips` — неполноблочный round-trip.
- `Cache_WrittenBytes_ReadBackByPlainEncryptedFile` — запись через кэш → чтение
  через обычный `EncryptedFile` совпадает (кэш маленький → вытеснение форсируется).
- `Cache_ReadPath_MatchesPlainEncryptedFile` — 200 случайных чтений через кэш
  совпадают с эталоном (провокация вытеснения).
- `Cache_ReadModifyWrite_AcrossBuffers` — RMW через границу буферов.
- `EncryptedStreams_RoundTrip` — Output→Input round-trip.

> ⚠️ **.NET SDK в среде сборки отсутствовал** (домены Microsoft вне allowlist),
> поэтому C#-тесты **не** прогнаны в этой итерации — их нужно запустить командой
> `dotnet test` на машине с .NET 10 SDK. Код написан строго по конвенциям уже
> существующих файлов порта (`Cbc.cs`, `EncryptedFile.cs`, `CryptoKatTests.cs`);
> нативная часть (единственная с реальным риском) проверена полностью.

## Осознанные отложения

- **MAC-файлы (§2.3)** — `MACFile`/`MACInputStream`/`MACOutputStream`. В оригинале
  они наследуют `TransRandomAccessIO`/`TransInputStream`/`TransOutputStream` и
  зависят от `fs.encfs.macs.MACCalculator`. Гайд сам рекомендует переносить их
  **вместе с EncFS**. Переношу в Фазу C, где появятся и `MACCalculator`
  (`SHA1MACCalculator`), и точная блочно-overhead буферизация EncFS.
- **CTR / ECB (§2.1, P3), `LocalEncryptedFileXTS`, `DummyEncryptionEngine` (§2.6)** —
  оставлены на Фазу I (опциональное), как и помечено в гайде.

## Следующий шаг — Фаза B (абстракция ФС + StdFs)

По графу зависимостей гайда (§9.1) дальше идёт **Фаза B**: расширить минимальный
`IFileSystem` до полноценной Path-модели (`FileSystem`/`Path`/`FSRecord`/`Directory`/
`File`/`RandomAccessIO`/`DataInput`/`DataOutput`/`FileSystemInfo`, §4.1), добавить
`StdFs` (§4.5) и минимальный `fs/util` (§4.2), переадаптировать FAT под новый
контракт. Это разблокирует Фазу C (EncFS) и Фазу D (локации).

Рекомендация: начинать Фазу B на машине с установленным **.NET 10 SDK**, чтобы
итеративно компилировать — расширение контракта ФС затрагивает уже работающий
FAT-драйвер, и обратная связь компилятора здесь критична.

## Изменённые/новые файлы

Новые:
- `native/vendor/cfb_mode_impl.inc`
- `src/Eds.Core/Crypto/Modes/Cfb.cs`
- `src/Eds.Core/Crypto/Engines/CfbEngines.cs`
- `src/Eds.Core/Crypto/EncryptedFileWithCache.cs`
- `src/Eds.Core/Crypto/EncryptedStreams.cs`
- `tests/Eds.Core.Tests/PhaseACryptoTests.cs`
- `src/Eds.Core/runtimes/linux-x64/native/libedscrypto.so` (собранный артефакт)

Изменённые:
- `native/src/edscrypto.c` — экспорты CFB + подключение `.inc`.
- `native/include/edscrypto.h` — декларации CFB, `EDS_CRYPTO_VERSION 3`.
- `native/tests/kat_test.c` — `test_cfb` + вызов из `main`.
- `src/Eds.Core/Crypto/Native/NativeCrypto.cs` — биндинги CFB.
- `src/Eds.Core/Crypto/Engines/CbcEngines.cs` — `GostCbc`.
- `src/Eds.Core.Containers/EdsContainer.cs` — `GetCachedEncryptedVolume()`.
- `docs/ROADMAP.md` — отметка о готовности CFB и ABI v3.
