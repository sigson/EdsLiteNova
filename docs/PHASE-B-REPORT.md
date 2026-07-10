# Фаза B (абстракция ФС + StdFs) — отчёт о выполнении

> Продолжение после Фазы A (см. `PHASE-A-REPORT.md`). Выполнено **ядро Фазы B**
> из `edslite-porting-gap-guide.md` (§4.1, §4.5, часть §4.2). Работа велась
> **строго аддитивно**: полная модель ФС добавлена рядом с уже работающим
> минимальным контрактом, поэтому FAT-браузер и MAUI-каркас не сломаны.

## Что сделано

### 1. Полная абстракция ФС (§4.1, P1)

`src/Eds.Core/Fs/Vfs/FileSystemModel.cs` — Path-ориентированная модель, точный
перенос интерфейсов `com.sovworks.eds.fs`:
`IFileSystem`, `IPath`, `IFsRecord`, `IDirectoryContents`, `IDirectory`,
`IFile`, `IFileSystemInfo`, enum `FileAccessMode`, `IFileProgressInfo`.

**Ключевое решение — namespace `Eds.Core.Fs.Vfs`.** В порту уже был ранний
минимальный `Eds.Core.Fs.Abstract.IFileSystem` (его использует FAT-адаптер и
`BrowserViewModel`, причём последний импортирует и `Eds.Core.Fs`). Если положить
новые одноимённые `IFileSystem`/`IDirectory`/`IFile` в `Eds.Core.Fs`, они бы
стали неоднозначными и сломали бы рабочий MAUI-код. Поэтому полная модель
вынесена в отдельный `Eds.Core.Fs.Vfs` — обе модели сосуществуют, FAT
переадаптируется на `Vfs` позже (когда будет доступен компилятор для итеративной
проверки). `DataInput`/`DataOutput` уже свёрнуты в существующий `IRandomAccessIO`;
Android-специфичный `ParcelFileDescriptor`-аксессор намеренно опущен (SAF — в
Android-таргете, §9.6).

### 2. Логика путей (§4.2, подмножество)

- `src/Eds.Core/Fs/Util/StringPathUtil.cs` — порт `fs.util.StringPathUtil` 1:1:
  split/join/parent/subpath/combine, имя/расширение, `IsParentDir`. Регистро-
  независимые сравнение/хеш/equals (как в оригинале), разделитель `/`.
- `src/Eds.Core/Fs/Util/PathBase.cs` — базовый `IPath` (parent/combine/equality
  через `StringPathUtil`).

### 3. StdFs — файловая система устройства (§4.5, P1)

`src/Eds.Core/Fs/Std/` — порт `fs.std.*` поверх `System.IO`:
- `StdFsFileIO` — `IRandomAccessIO` над `FileStream` (маппинг режимов
  Read/Write/ReadWrite/WriteAppend/ReadWriteTruncate «r»/«rw»; fsync в `Dispose`).
- `StdFs` / `StdFsPath` / `StdFsRecord` / `StdDirRecord` / `StdFileRecord` —
  реализация полной Vfs-модели: навигация, list, create dir/file, read/write через
  RAIO, rename/move, delete (с проверкой непустого каталога), total/free space
  через `DriveInfo`, copy-to/from-stream с прогрессом и отменой.

В отличие от оригинала (был режим «корень устройства» через пустой rootDir), порт
всегда принимает явный host-каталог-корень — это и типичный кейс MAUI, и
кроссплатформенно безопасно (не полагается на POSIX-абсолютные пути).

### 4. IO-адаптеры Stream ⇄ RAIO (§4.2)

`src/Eds.Core/Fs/Util/RandomAccessStreams.cs` — `RandomAccessInputStream` и
`RandomAccessOutputStream` (seekable `Stream` поверх `IRandomAccessIO`). Нужны
для импорта/экспорта файлов и копирования.

## Проверено

xUnit-тесты `tests/Eds.Core.Tests/PhaseBFsTests.cs`:
- `StringPathUtilTests` — split/join/parent/combine, root/empty, регистро-
  независимое равенство, `IsParentDir`/`GetSubPath`.
- `StdFsTests` — полный round-trip на временной папке: create dir → create file →
  write через RAIO → size → read back → list → rename → delete file → delete dir;
  плюс round-trip через `RandomAccessInputStream`/`OutputStream`.

> ⚠️ Как и в Фазе A, **.NET SDK в среде отсутствовал** → C#-тесты нужно прогнать
> `dotnet test` на машине с .NET 10 SDK. Код написан по конвенциям существующих
> файлов порта. **Ключевой риск — конфликт неймспейсов — устранён и проверен
> текстово**: ни один файл не импортирует одновременно `Eds.Core.Fs.Vfs` и
> `Eds.Core.Fs.Abstract`, а `Eds.Core.Fs` больше не содержит `IFileSystem`/
> `IDirectory`/`IFile` (они в `Vfs`), поэтому `BrowserViewModel`/FAT-адаптер
> продолжают резолвиться на минимальную модель без неоднозначности.

## Следующие шаги

- **Завершить Фазу B** (нужен компилятор): переадаптировать FAT-драйвер на
  `Eds.Core.Fs.Vfs`, ретировать минимальный `Eds.Core.Fs.Abstract.IFileSystem`,
  обновить `BrowserViewModel`/`FatFileSystemAdapter`; закрыть хвосты FAT
  (FAT32-root тест, FSInfo/метка тома); при необходимости добавить
  `TransRandomAccessIO`/`BufferedRandomAccessIO`.
- **Фаза C — EncFS**: теперь разблокирована (есть Vfs-контракт, CBC/CFB, потоки).
  Вместе с ней переносятся MAC-файлы (§2.3), отложенные из Фазы A.

## Новые файлы Фазы B

- `src/Eds.Core/Fs/Vfs/FileSystemModel.cs`
- `src/Eds.Core/Fs/Util/StringPathUtil.cs`
- `src/Eds.Core/Fs/Util/PathBase.cs`
- `src/Eds.Core/Fs/Util/RandomAccessStreams.cs`
- `src/Eds.Core/Fs/Std/StdFsFileIO.cs`
- `src/Eds.Core/Fs/Std/StdFs.cs`
- `tests/Eds.Core.Tests/PhaseBFsTests.cs`
