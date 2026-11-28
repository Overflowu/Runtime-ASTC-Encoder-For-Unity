#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PLUGINS_DIR="${REPO_ROOT}/Assets/ASTC Encoder/Plugins"

REQUIRED_SYMBOLS=(
  "astcenc_config_init"
  "astcenc_context_alloc"
  "astcenc_compress_image"
  "astcenc_compress_reset"
  "astcenc_context_free"
)

check_not_lfs_pointer() {
  local path="$1"
  if [[ ! -f "${path}" ]]; then
    echo "ERROR: Missing native plugin: ${path}" >&2
    exit 1
  fi
  if head -n 1 "${path}" | grep -q "^version https://git-lfs.github.com/spec/v1"; then
    echo "ERROR: Native plugin is still a Git LFS pointer (run 'git lfs pull'): ${path}" >&2
    exit 1
  fi
}

check_elf_symbols() {
  local path="$1"
  local exports
  exports="$(nm -D "${path}")"
  for sym in "${REQUIRED_SYMBOLS[@]}"; do
    if ! grep -q " T ${sym}$" <<< "${exports}"; then
      echo "ERROR: Missing exported symbol '${sym}' in ${path}" >&2
      exit 1
    fi
  done
}

check_binary_contains_symbols() {
  local path="$1"
  local dump
  dump="$(strings "${path}")"
  for sym in "${REQUIRED_SYMBOLS[@]}"; do
    if ! grep -q "${sym}" <<< "${dump}"; then
      echo "ERROR: Missing expected symbol reference '${sym}' in ${path}" >&2
      exit 1
    fi
  done
}

echo "[verify-native-plugins] Checking prebuilt native plugins..."

WIN_DLL="${PLUGINS_DIR}/x64/astcenc-sse4.1-shared.dll"
MAC_DYLIB="${PLUGINS_DIR}/Mac/libastcenc-shared.dylib"
ANDROID_ARM64="${PLUGINS_DIR}/Android/arm64-v8a/libastcenc-neon-shared.so"
ANDROID_ARMV7="${PLUGINS_DIR}/Android/armeabi-v7a/libastcenc-none-shared.so"
IOS_STATIC="${PLUGINS_DIR}/iOS/libastcenc-neon-static.a"

for f in "${WIN_DLL}" "${MAC_DYLIB}" "${ANDROID_ARM64}" "${ANDROID_ARMV7}" "${IOS_STATIC}"; do
  check_not_lfs_pointer "${f}"
  file "${f}"
done

file "${WIN_DLL}" | grep -q "PE32+.*x86-64"
file "${MAC_DYLIB}" | grep -q "Mach-O universal binary"
file "${ANDROID_ARM64}" | grep -q "ELF 64-bit.*ARM aarch64"
file "${ANDROID_ARMV7}" | grep -q "ELF 32-bit.*ARM"
file "${IOS_STATIC}" | grep -q "ar archive"

check_elf_symbols "${ANDROID_ARM64}"
check_elf_symbols "${ANDROID_ARMV7}"
check_binary_contains_symbols "${WIN_DLL}"
check_binary_contains_symbols "${MAC_DYLIB}"
check_binary_contains_symbols "${IOS_STATIC}"

if [[ -f "${PLUGINS_DIR}/Linux/x86_64/libastcenc-sse4.1-shared.so" ]]; then
  LINUX_SO="${PLUGINS_DIR}/Linux/x86_64/libastcenc-sse4.1-shared.so"
  file "${LINUX_SO}" | grep -q "ELF 64-bit.*x86-64"
  check_elf_symbols "${LINUX_SO}"
fi

echo "[verify-native-plugins] All native plugin binaries and required P/Invoke symbols verified."
