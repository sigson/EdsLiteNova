# build-native.ps1 - Build edscrypto on Windows and stage it into Eds.Core.
#
# Requires cmake plus a C toolchain:
#   - MSVC (Visual Studio / Build Tools), or
#   - msys2 (gcc/clang) - run from an msys2 shell and use build-native.sh instead,
#     or pass -G "MinGW Makefiles" here with msys2 gcc on PATH.
#
# Usage:
#   pwsh scripts/build-native.ps1              # MSVC default generator
#   pwsh scripts/build-native.ps1 -Mingw       # use MinGW (msys2) gcc
param(
    [switch]$Mingw,
    [switch]$Tests
)
$ErrorActionPreference = "Stop"

$Root   = Split-Path -Parent $PSScriptRoot
$Native = Join-Path $Root "native"
$Build  = Join-Path $Native "build"

$arch = (Get-CimInstance Win32_Processor).Architecture
$ridArch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$Rid = "win-$ridArch"

Write-Host ">> Building edscrypto for RID: $Rid"
New-Item -ItemType Directory -Force -Path $Build | Out-Null

$genArgs = @("-S", $Native, "-B", $Build, "-DCMAKE_BUILD_TYPE=Release")
if ($Mingw) { $genArgs += @("-G", "MinGW Makefiles") }
if ($Tests) { $genArgs += "-DEDS_BUILD_TESTS=ON" }

cmake @genArgs
cmake --build $Build --config Release

$src = Get-ChildItem -Path $Build -Recurse -Filter "edscrypto.dll" | Select-Object -First 1
if (-not $src) { throw "edscrypto.dll not found under $Build" }

$dest = Join-Path $Root "src/Eds.Core/runtimes/$Rid/native"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $src.FullName $dest -Force
Write-Host ">> Staged edscrypto.dll -> $dest"

if ($Tests) {
    $kat = Get-ChildItem -Path $Build -Recurse -Filter "kat_test*.exe" | Select-Object -First 1
    if ($kat) { Write-Host ">> Running native KAT:"; & $kat.FullName }
}
Write-Host ">> Done."
