# BUILDING — кросс-сборка нативной библиотеки

Для десктопной отладки достаточно `scripts/build-native.sh` / `.ps1` (см. README).
Этот документ — про сборку `edscrypto` под мобильные платформы, когда дело дойдёт
до реальных Android/iOS-сборок.

## Android (.so под все ABI)

Нужен Android NDK. Собираем каждую ABI и кладём в
`src/Eds.Core/runtimes/android-<abi>/native/` — MAUI/NuGet упакует их в APK.

```bash
NDK=$ANDROID_NDK_HOME
for ABI in arm64-v8a armeabi-v7a x86_64; do
  cmake -S native -B native/build-$ABI \
    -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
    -DANDROID_ABI=$ABI -DANDROID_PLATFORM=android-24 \
    -DCMAKE_BUILD_TYPE=Release
  cmake --build native/build-$ABI -j
done
```

В .NET RID соответствие: `arm64-v8a → android-arm64`, `armeabi-v7a → android-arm`,
`x86_64 → android-x64`. (Проще всего добавить в `Eds.Core.csproj` `<None ...>`
элементы с `PackagePath`/`Link`, копирующие нужный `.so` в
`lib/<abi>` при сборке Android-головы.)

## iOS / MacCatalyst (статическая линковка)

На iOS динамические сторонние `.dylib` не приветствуются — линкуем **статически**
и резолвим через `__Internal` (уже учтено в `NativeLibraryResolver`).

```bash
cmake -S native -B native/build-ios \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
  -DCMAKE_BUILD_TYPE=Release
cmake --build native/build-ios -j
```

`CMakeLists.txt` уже собирает и SHARED, и STATIC цель. Для iOS берите статическую
(`libedscrypto.a`) и подключайте её к iOS-голове MAUI как нативную зависимость с
`__Internal`-резолвингом.

## Проверка

После любой сборки полезно собрать с `-DEDS_BUILD_TESTS=ON` и прогнать
`kat_test` на целевой платформе (где возможно) — это подтверждает, что алгоритмы
дают эталонные байты именно на этом ABI/компиляторе.
