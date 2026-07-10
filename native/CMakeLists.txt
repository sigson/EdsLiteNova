cmake_minimum_required(VERSION 3.16)
project(edscrypto C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# -------- sources --------------------------------------------------------
set(VENDOR ${CMAKE_CURRENT_SOURCE_DIR}/vendor)

set(EDS_SOURCES
    src/edscrypto.c
    ${VENDOR}/aes/aescrypt.c
    ${VENDOR}/aes/aeskey.c
    ${VENDOR}/aes/aestab.c
    ${VENDOR}/serpent/serpent.c
    ${VENDOR}/twofish/twofish.c
    ${VENDOR}/gost/gost89.c
    ${VENDOR}/ripemd160/ripemd160.c
    ${VENDOR}/whirlpool/whirlpool.c
)

# -------- include paths --------------------------------------------------
set(EDS_INCLUDE_DIRS
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${VENDOR}
    ${VENDOR}/common
    ${VENDOR}/aes
    ${VENDOR}/serpent
    ${VENDOR}/twofish
    ${VENDOR}/gost
    ${VENDOR}/ripemd160
    ${VENDOR}/whirlpool
)

# -------- shared library (Android .so, Windows .dll, Linux/mac .so/.dylib)
add_library(edscrypto SHARED ${EDS_SOURCES})
target_include_directories(edscrypto PRIVATE ${EDS_INCLUDE_DIRS})
set_target_properties(edscrypto PROPERTIES C_VISIBILITY_PRESET hidden)

# -------- static library (for iOS, where dynamic loading is disallowed) ---
add_library(edscrypto_static STATIC ${EDS_SOURCES})
target_include_directories(edscrypto_static PRIVATE ${EDS_INCLUDE_DIRS})
set_target_properties(edscrypto_static PROPERTIES OUTPUT_NAME edscrypto)

# -------- optional in-tree KAT harness (C) -------------------------------
# Build with: cmake -DEDS_BUILD_TESTS=ON ..
option(EDS_BUILD_TESTS "Build the native Known-Answer-Test harness" OFF)
if(EDS_BUILD_TESTS)
    add_executable(kat_test tests/kat_test.c)
    target_include_directories(kat_test PRIVATE ${EDS_INCLUDE_DIRS})
    target_link_libraries(kat_test edscrypto_static)
    enable_testing()
    add_test(NAME kat COMMAND kat_test)
endif()
