#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
WEBGL_SRC_DIR="${REPO_ROOT}/Assets/ASTC Encoder/Plugins/WebGL"
BRIDGE_CPP="${REPO_ROOT}/Tools/WebGLWorker/astcenc_worker_bridge.cpp"
WORKER_JS="${REPO_ROOT}/Tools/WebGLWorker/astcenc-worker.js"
OUT_DIR="${1:-${REPO_ROOT}/Assets/StreamingAssets/AstcWebWorker}"

if ! command -v em++ >/dev/null 2>&1; then
  echo "[build-webgl-worker] ERROR: em++ (Emscripten) is not installed or not on PATH." >&2
  exit 1
fi

if [[ ! -f "${WEBGL_SRC_DIR}/astcenc_entry.cpp" ]]; then
  echo "[build-webgl-worker] Syncing WebGL astcenc sources first..."
  "${SCRIPT_DIR}/sync-webgl-astcenc.sh"
fi

mkdir -p "${OUT_DIR}"

COMMON_FLAGS=(
  -O3
  -std=c++14
  -I"${WEBGL_SRC_DIR}"
  -sMODULARIZE=1
  -sEXPORT_NAME=createAstcWorkerModule
  -sENVIRONMENT=worker,node
  -sALLOW_MEMORY_GROWTH=1
  -sINITIAL_MEMORY=33554432
  -sNO_EXIT_RUNTIME=1
  -sNO_FILESYSTEM=1
  -sEXPORTED_FUNCTIONS='["_malloc","_free","_astcenc_worker_compress","_astcenc_worker_clear_cache","_astcenc_worker_is_simd"]'
  -sEXPORTED_RUNTIME_METHODS='["HEAPU8"]'
)

echo "[build-webgl-worker] Building WASM Scalar worker bundle..."
em++ "${COMMON_FLAGS[@]}" \
  "${WEBGL_SRC_DIR}"/*.cpp "${BRIDGE_CPP}" \
  -o "${OUT_DIR}/astcenc-worker-scalar.js"

echo "[build-webgl-worker] Building WASM SIMD128 (SSE4.1) worker bundle..."
em++ "${COMMON_FLAGS[@]}" \
  -msimd128 -msse4.1 \
  "${WEBGL_SRC_DIR}"/*.cpp "${BRIDGE_CPP}" \
  -o "${OUT_DIR}/astcenc-worker-simd.js"

cp "${WORKER_JS}" "${OUT_DIR}/astcenc-worker.js"

echo "[build-webgl-worker] Built Web Worker bundles into ${OUT_DIR}:"
ls -lh "${OUT_DIR}"/astcenc-worker*
