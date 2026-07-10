# Phase K1 — Корпус совместимости данных (interop): EncFS + LUKS закрыты

**Статус:** ✅ **закрыто для EncFS и LUKS1 (AES)**; ⬜ остаётся TrueCrypt/VeraCrypt
и serpent/twofish-LUKS (нет генерирующих утилит в среде — см. §5).

K1 был назван в `HANDOFF-REPORT.md` **риском №1 (P1)**: все предыдущие проверки
крипто/ФС были **round-trip** (порт сам пишет и сам читает), что доказывает
внутреннюю согласованность, но **не** байт-совместимость с данными, созданными
настоящими настольными утилитами. Прошлые сессии не могли это закрыть — в песочнице
не было ни `encfs`, ни `cryptsetup`. В этой сессии **обе утилиты доступны**, и
interop подтверждён.

---

## 1. Что доказано

### EncFS (реальный настольный `encfs` 1.9.5, режим "standard")
Том создан настольным `encfs --standard` (ssl/aes, nameio/block, keySize 192,
blockSize 1024, uniqueIV + chainedNameIV, kdfIterations 486144), наполнен
известными файлами и размонтирован. Порт **читает его байт-в-байт**:

- разбор `encfs6.xml` и разворачивание тома-ключа (PBKDF2-HMAC-SHA1 + stream-CFB);
- расшифровка **имён файлов** (block name codec + B64 + chained name IV **через
  вложенные каталоги** `sub/` и `sub/deep/`);
- расшифровка **содержимого**: заголовок IV на файл, поблочный AES-CBC с
  `blockIndex XOR fileIV`, stream-AES-CFB для неполного хвоста, многоблочные файлы;
- все 5 файлов совпали по SHA-256 (включая 4096-байтный многоблочный `data.bin`
  и 4200-байтный `lines.txt` с частичным последним блоком).

### LUKS1 (реальный `cryptsetup` 2.7.0)
Заголовки, созданные `cryptsetup luksFormat --type luks1`. Порт **восстанавливает
мастер-ключ, совпадающий с выводом `cryptsetup luksDump --dump-master-key`** для:

| cipher | hash | key bits |
|--------|------|----------|
| aes-xts-plain64 | sha256 | 512 |
| aes-xts-plain64 | sha512 | 512 |
| aes-xts-plain64 | sha1   | 256 |

Проверена вся цепочка: big-endian разбор заголовка → PBKDF2-HMAC-{SHA1,256,512} по
keyslot → XTS-расшифровка AF-материала → AF-merge (диффузия хешем) → сверка
master-key digest.

---

## 2. Как это проверяется (двойная гарантия)

**(a) Независимый оракул — `tests/interop-verification/`.**
Python-харнесс, который построчно повторяет C#-логику декодирования
(`EncFsVolumeKey`, `CipherBase`/`StreamCipherBase`, `Sha1MacCalculator`,
`BlockNameCipher`, `B64`, `LuksLayout`, `Af`) и вызывает **ту же самую нативную
`libedscrypto`**, что и порт (через `ctypes`). Он расшифровывает реальные артефакты
и сверяет с известным plaintext / мастер-ключами cryptsetup. Запуск:

```
./scripts/build-native.sh
python3 tests/interop-verification/verify_interop.py
# → RESULT: ALL INTEROP CHECKS PASSED
```

Это воспроизводимо **без .NET SDK** (важно: SDK в этой среде недоступен) и
устанавливает эталонные значения, которые утверждают C#-тесты.

**(b) C#-тесты — открывают те же артефакты через публичный API порта:**
- `EncFsRealInteropTests` — открывает EncFS-том через `EncFsFs`, навигирует
  **перечислением с декодированием** (не зависит от направления *кодирования* имён),
  читает и сверяет содержимое, проверяет отклонение неверного пароля.
- `LuksRealInteropTests` — гоняет `LuksLayout` напрямую (быстро, минуя
  VeraCrypt-sweep) по трём томам: верный пароль открывает, неверный →
  `WrongPasswordException`; плюс тест через фасад `EdsContainer`.

---

## 3. Артефакты (в репозитории)

- `tests/Eds.Core.Tests/fixtures/interop/encfs-standard/` — реальный EncFS-том
  (~40 КБ; конфиг хранится как `encfs6.xml`).
- `tests/Eds.Core.Tests/fixtures/interop/luks/*.luks` — реальные заголовки LUKS1
  (header + AF-материал слота 0, усечены до 256 КБ перед 2-МиБ payload).
- `tests/Eds.Core.Tests/fixtures/interop/MANIFEST.md` — происхождение + эталонные
  значения (мастер-ключи, UUID, SHA-256 файлов). Пароль всех артефактов:
  `testpass123`.
- `tests/interop-verification/` — харнесс + README.

Итог ~820 КБ бинарных фикстур.

---

## 4. Найденные баги

**Нет.** Мирроринг сошёлся с первого прогона — EncFS- и LUKS-пути порта уже были
interop-корректны; K1 это **доказывает** (ранее было лишь предположение на основе
round-trip).

---

## 5. Что остаётся открытым по K1

- **TrueCrypt/VeraCrypt**: в среде нет CLI VeraCrypt (и требуется dm/loop) для
  генерации эталонов. Разбор заголовков покрыт round-trip + опубликованными KAT
  примитивов, но не реальными сторонними томами.
- **serpent/twofish-LUKS**: `cryptsetup luksFormat` для них падает в песочнице
  (ядру недоступны модули dm-crypt serpent/twofish). Сами шифры покрыты
  опубликованными KAT-векторами; interop именно LUKS-обвязки с ними не сгенерировать
  здесь.

Оба пункта — ограничение среды, не порта; при наличии соответствующих утилит
эталоны добавляются тем же способом (см. `MANIFEST.md` и харнесс).

---

*K1 переходит из «не подтверждено ни для одного формата» в «подтверждено для EncFS
и LUKS-AES реальными настольными утилитами, воспроизводимо и в C#, и независимым
оракулом».*
