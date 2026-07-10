# Фаза C (EncFS) — начало: листовые компоненты

> Продолжение после Фаз A и B. EncFS — крупная и критичная по байт-совместимости
> подсистема (~3 200 строк, §4.3 гайда), которую нельзя ответственно писать
> целиком без компилятора. Поэтому она ведётся снизу вверх: сначала
> самодостаточные «листовые» компоненты с чёткими вход/выход, каждый покрыт
> юнит-тестами; затем — на их основе — сам файловый слой (с рабочим SDK).

## Что сделано в этой итерации

### 1. B64 — кодирование имён файлов EncFS

`src/Eds.Core/Fs/EncFs/B64.cs` — точный порт `fs.encfs.B64`. Это НЕ стандартный
base64: алфавит `,-0123456789A-Za-z` намеренно без `/` (разделитель пути) и `.`
(зарезервирован для спецфайлов), чтобы зашифрованные имена были безопасны в ФС.
Портированы: рекурсивный битовый перекодировщик `ChangeBase2Inline`,
`B64ToString`/`StringToB64`, `B32ToString`/`StringToB32`, счётчики байт. Обратная
таблица ASCII→значение строится программно из прямой — исключает ошибку в
разрежённом строковом литерале оригинала.

### 2. MAC-слой — целостность блоков и имён

`src/Eds.Core/Fs/EncFs/Macs/MacCalculator.cs`:
- `MacCalculator` (база) — 8-байтная контрольная сумма + свёртки `Calc64/32/16`
  (big-endian), поддержка chained-IV. Порт `macs.MACCalculator`.
- `Sha1MacCalculator` — HMAC-SHA1 (через готовый `Hmac` + `BclDigest.Sha1()`) с
  фирменной свёрткой EncFS: первые 19 из 20 байт HMAC XOR-складываются в 8 байт;
  при chained-IV к данным дописывается реверснутый IV, а новый IV = результат.
  Порт `macs.SHA1MACCalculator` (включая `CipherBase.getKeyFromBuf`).

## Проверено

`tests/Eds.Core.Tests/EncFsLeafTests.cs`:
- **B64**: round-trip кодирования имён для длин 1/2/7/16/20/31 (декод(энкод(x))==x),
  отсутствие `/` и `.` в результате, round-trip значение↔символ для всех 0..63,
  проверка счётчиков байт.
- **SHA1-MAC**: сверка `CalcChecksum` с независимым эталоном (BCL `HMACSHA1` +
  та же свёртка) — это одновременно проверяет и HMAC-SHA1 порта, и свёртку;
  детерминизм и чувствительность к смещению; chained-IV меняет результат и
  продвигает IV.

> ⚠️ Как и ранее, **.NET SDK в среде отсутствовал** — C#-тесты прогнать
> `dotnet test` на машине с .NET 10. Компоненты выбраны так, чтобы быть
> самопроверяемыми и не зависеть от ещё не портированного файлового слоя EncFS.

## Итерация 2: слой шифров EncFS

`src/Eds.Core/Fs/EncFs/Ciphers/` — перенесён весь криптографический слой EncFS:
- `CipherBase` — обёртка над движком с HMAC-SHA1-производным IV (ключ = базовый
  ключ ‖ ivPart; IV файла 8 байт → HMAC(ivPart‖reversed(fileIV)) → IV движка).
- `StreamCipherBase` — фирменное EncFS двухпроходное потоковое кодирование
  (shuffle → enc(iv) → flip → shuffle → enc(iv+1) и обратное при decrypt).
- `AesCbcFileCipher` (блоки данных), `AesCfbStreamCipher` (имена/хвост),
  `BlockAndStreamCipher` (диспетчер: полный блок → CBC, остаток → CFB).
- Шифры имён `NullNameCipher`/`BlockNameCipher`/`StreamNameCipher` + интерфейс
  `INameCodec` — связывают B64 + MAC + шифр (паддинг, 16-битный MAC, IV из MAC
  XOR chained-IV, перекодировка в base64/base32).

Для этого в порт-версию `AesCbc` добавлен **обратно-совместимый** конструктор
`AesCbc(keySize, fileBlockSize)` (старый `AesCbc()` = 32 байта / блок 512 без
изменений); `Cbc.FileBlockSize` стал конфигурируемым (дефолт 512).

Проверено (`tests/Eds.Core.Tests/EncFsCipherTests.cs`): round-trip файлового и
потокового шифров, диспетчеризация `BlockAndStreamCipher` (полный/частичный
блок), round-trip шифров имён (base64 и base32, включая Unicode-имена),
влияние chained-IV на результат и корректный round-trip под ним. Round-trip
доказывает внутреннюю обратимость и согласованность конвейера B64+MAC+шифр;
байт-совместимость с настольным EncFS всё ещё требует реальных эталонов (K1).

## Итерация 3: конфигурация и кодеки EncFS

`src/Eds.Core/Fs/EncFs/` — перенесён слой «конфигурация → алгоритмы → шифры»:
- `Config` (`Config.cs`) — разбор и запись `encfs6.xml` через `System.Xml.Linq`.
  Важно: реальные конфиги содержат `<!DOCTYPE boost_serialization>`, а
  `XDocument.Load` по умолчанию запрещает DTD — поэтому чтение идёт через
  `XmlReader` с `DtdProcessing.Ignore` (без внешнего резолвера, безопасно).
  Имена элементов/атрибутов и сопоставление алгоритмов (name + версия)
  сохранены точно ради on-disk совместимости.
- `IAlgInfo`/`IDataCodecInfo`/`INameCodecInfo` (`AlgInfo.cs`) + реализации и
  реестр `EncFsCodecs` (`Codecs/CodecInfos.cs`): `AESDataCodecInfo` (`ssl/aes`),
  name-кодеки `Block`/`Stream`/`Null`/`BlockCS` (`nameio/*`). Config резолвит
  их по имени и версии, связывая параметры тома с фабриками шифров §итерации-2.

Проверено (`tests/Eds.Core.Tests/EncFsConfigTests.cs`): разбор репрезентативного
`encfs6.xml` (AES + block-имена, keySize 192, blockSize 1024, chainedNameIV,
base64 key/salt, kdfIterations); write→read round-trip; **сборка рабочего кодека
имён прямо из распарсенной конфигурации и round-trip имени** (сквозная проверка
Config→кодек→шифр); отказ на неизвестном алгоритме.

## Итерация 4: вывод ключа тома EncFS (криптостек завершён)

`src/Eds.Core/Fs/EncFs/EncFsVolumeKey.cs` — ключевая логика из `fs.encfs.FS`:
`DeriveKey` (PBKDF2-HMAC-SHA1 из пароля+salt+iterations → KEK длиной keySize+ivSize),
`DecryptVolumeKey` (checksum из первых 4 байт как IV → потоковая расшифровка →
сверка `Calc32`; неверный пароль → `WrongPasswordException`), `EncryptVolumeKey`
(обратная операция). Проверено round-trip'ом обёртки/разворачивания, отклонением
неверного пароля, детерминизмом PBKDF2. **Замыкает весь криптостек EncFS**
(config → пароль → мастер-ключ → шифры).

## Дальше по EncFS (нужен компилятор)

Криптостек готов; осталась только файловая обвязка:
- `crypto/MACFile` + `MACInputStream`/`MACOutputStream` (§2.3) — поверх
  `MacCalculator` и трансформирующего IO (`TransRandomAccessIO`).
- `FS`/`Directory`/`File`/`Path` — обход дерева, шифрование имён через кодек,
  блочный IO файлов поверх «сырой» ФС (per-file unique IV, MAC-заголовки блоков).
- Сверка байт-в-байт с реальными EncFS-папками (кросс-задача K1).

## Новые файлы

- `src/Eds.Core/Fs/EncFs/B64.cs`
- `src/Eds.Core/Fs/EncFs/Macs/MacCalculator.cs`
- `tests/Eds.Core.Tests/EncFsLeafTests.cs`
