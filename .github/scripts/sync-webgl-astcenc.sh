#!/usr/bin/env bash
set -euo pipefail

META_ONLY=false
if [[ "${1:-}" == "--meta-only" ]]; then
  META_ONLY=true
  shift
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SUBMODULE_DIR="${REPO_ROOT}/ThirdParty/astc-encoder"
SOURCE_DIR="${SUBMODULE_DIR}/Source"
TARGET_DIR="${1:-${REPO_ROOT}/Assets/ASTC Encoder Web/Plugins/WebGL}"

CPP_FILES=(
  "astcenc_averages_and_directions.cpp"
  "astcenc_block_sizes.cpp"
  "astcenc_color_quantize.cpp"
  "astcenc_color_unquantize.cpp"
  "astcenc_compress_symbolic.cpp"
  "astcenc_compute_variance.cpp"
  "astcenc_decompress_symbolic.cpp"
  "astcenc_diagnostic_trace.cpp"
  "astcenc_entry.cpp"
  "astcenc_find_best_partitioning.cpp"
  "astcenc_ideal_endpoints_and_weights.cpp"
  "astcenc_image.cpp"
  "astcenc_integer_sequence.cpp"
  "astcenc_mathlib.cpp"
  "astcenc_mathlib_softfloat.cpp"
  "astcenc_partition_tables.cpp"
  "astcenc_percentile_tables.cpp"
  "astcenc_pick_best_endpoint_format.cpp"
  "astcenc_quantization.cpp"
  "astcenc_symbolic_physical.cpp"
  "astcenc_weight_align.cpp"
  "astcenc_weight_quant_xfer_tables.cpp"
)

HEADER_FILES=(
  "astcenc.h"
  "astcenc_diagnostic_trace.h"
  "astcenc_internal.h"
  "astcenc_internal_entry.h"
  "astcenc_mathlib.h"
  "astcenc_vecmathlib.h"
  "astcenc_vecmathlib_common_4.h"
  "astcenc_vecmathlib_none_4.h"
  "astcenc_vecmathlib_sse_4.h"
)

deterministic_guid() {
  local key="$1"
  printf '%s' "aperture.astcenc.webgl.${key}" | md5sum | awk '{print $1}'
}

write_folder_meta() {
  local path="$1"
  local guid
  guid="$(deterministic_guid "folder:$(basename "${path}")")"
  cat > "${path}.meta" <<EOF
fileFormatVersion: 2
guid: ${guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
EOF
}

write_header_meta() {
  local path="$1"
  local guid
  guid="$(deterministic_guid "header:$(basename "${path}")")"
  cat > "${path}.meta" <<EOF
fileFormatVersion: 2
guid: ${guid}
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
EOF
}

write_webgl_cpp_meta() {
  local path="$1"
  local guid
  guid="$(deterministic_guid "cpp:$(basename "${path}")")"
  cat > "${path}.meta" <<EOF
fileFormatVersion: 2
guid: ${guid}
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
        Exclude Editor: 1
        Exclude Linux64: 1
        Exclude OSXUniversal: 1
        Exclude WebGL: 0
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
      enabled: 0
      settings:
        CPU: AnyCPU
        DefaultValueInitialized: true
        OS: AnyOS
  - first:
      Standalone: Linux64
    second:
      enabled: 0
      settings:
        CPU: None
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
      WebGL: WebGL
    second:
      enabled: 1
      settings: {}
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
}

mkdir -p "${TARGET_DIR}"

if [[ "${META_ONLY}" == "false" ]]; then
  if [[ ! -f "${SUBMODULE_DIR}/CMakeLists.txt" ]]; then
    echo "[sync-webgl-astcenc] Initializing ThirdParty/astc-encoder submodule..."
    git -C "${REPO_ROOT}" submodule update --init --depth 1 ThirdParty/astc-encoder
  fi

  for f in "${CPP_FILES[@]}" "${HEADER_FILES[@]}"; do
    if [[ ! -f "${SOURCE_DIR}/${f}" ]]; then
      echo "[sync-webgl-astcenc] ERROR: Missing source file ${SOURCE_DIR}/${f}" >&2
      exit 1
    fi
    cp "${SOURCE_DIR}/${f}" "${TARGET_DIR}/${f}"
  done
fi

write_folder_meta "${TARGET_DIR}"

for f in "${HEADER_FILES[@]}"; do
  write_header_meta "${TARGET_DIR}/${f}"
done

for f in "${CPP_FILES[@]}"; do
  write_webgl_cpp_meta "${TARGET_DIR}/${f}"
done

echo "[sync-webgl-astcenc] Synced ${#CPP_FILES[@]} C++ sources and ${#HEADER_FILES[@]} headers (with Unity WebGL .meta files) to ${TARGET_DIR}"
