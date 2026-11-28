#!/usr/bin/env bash
set -euo pipefail

ASTCENC_VERSION="${ASTCENC_VERSION:-4.4.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PLUGIN_ROOT="${1:-${REPO_ROOT}/Assets/ASTC Encoder/Plugins/Linux}"
WORK_DIR="$(mktemp -d)"

cleanup() {
  rm -rf "${WORK_DIR}"
}
trap cleanup EXIT

echo "[build-linux-astcenc] Building ARM astc-encoder v${ASTCENC_VERSION} (SSE4.1 shared library)..."
git clone --depth 1 --branch "${ASTCENC_VERSION}" https://github.com/ARM-software/astc-encoder.git "${WORK_DIR}/astc-encoder"

cmake -S "${WORK_DIR}/astc-encoder" -B "${WORK_DIR}/build" \
  -DCMAKE_BUILD_TYPE=Release \
  -DSHAREDLIB=ON \
  -DCLI=OFF \
  -DISA_SSE41=ON \
  -DISA_NATIVE=OFF \
  -DISA_AVX2=OFF \
  -DISA_SSE2=OFF \
  -DISA_NONE=OFF

cmake --build "${WORK_DIR}/build" --config Release --parallel "$(nproc)"

BUILT_SO="$(find "${WORK_DIR}/build" -name 'libastcenc-sse4.1-shared.so' | head -n 1)"
if [[ -z "${BUILT_SO}" || ! -f "${BUILT_SO}" ]]; then
  echo "[build-linux-astcenc] ERROR: libastcenc-sse4.1-shared.so was not produced." >&2
  exit 1
fi

TARGET_DIR="${PLUGIN_ROOT}/x86_64"
mkdir -p "${TARGET_DIR}"
cp "${BUILT_SO}" "${TARGET_DIR}/libastcenc-sse4.1-shared.so"

cat > "${PLUGIN_ROOT}.meta" <<'EOF'
fileFormatVersion: 2
guid: 8e4a1c9b6d5f4028fb3a95647d1c0e86
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
EOF

cat > "${TARGET_DIR}.meta" <<'EOF'
fileFormatVersion: 2
guid: 9f5b2d0c7e6a4139ac4b06758e2d1f97
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
EOF

cat > "${TARGET_DIR}/libastcenc-sse4.1-shared.so.meta" <<'EOF'
fileFormatVersion: 2
guid: a06c3e1d8f7b4240bd5c17869f3e2a08
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      : Any
    second:
      enabled: 0
      settings:
        Exclude Android: 1
        Exclude Editor: 0
        Exclude Linux64: 0
        Exclude OSXUniversal: 1
        Exclude Win: 1
        Exclude Win64: 1
        Exclude iOS: 1
  - first:
      Android: Android
    second:
      enabled: 0
      settings:
        CPU: ARMv7
  - first:
      Any: 
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        DefaultValueInitialized: true
        OS: Linux
  - first:
      Standalone: Linux64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 0
      settings:
        CPU: None
  - first:
      Standalone: Win
    second:
      enabled: 0
      settings:
        CPU: None
  - first:
      Standalone: Win64
    second:
      enabled: 0
      settings:
        CPU: None
  - first:
      iPhone: iOS
    second:
      enabled: 0
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags: 
        FrameworkDependencies: 
  userData: 
  assetBundleName: 
  assetBundleVariant: 
EOF

echo "[build-linux-astcenc] Installed ${TARGET_DIR}/libastcenc-sse4.1-shared.so"
file "${TARGET_DIR}/libastcenc-sse4.1-shared.so"
nm -D "${TARGET_DIR}/libastcenc-sse4.1-shared.so" | grep -E " T astcenc_"
