#!/usr/bin/env node
// Node.js round-trip verification for prebuilt WebGL Worker WASM modules (Scalar + SIMD128).
// Tests both astcenc_worker_compress and astcenc_worker_decompress.

import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const workerDir = path.resolve(process.argv[2] || 'Assets/ASTC Encoder Web/Plugins/WebGL/Worker~');

function makePatternRgba(width, height) {
  const buf = new Uint8Array(width * height * 4);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const idx = (y * width + x) * 4;
      buf[idx + 0] = (x * 4) & 0xff;
      buf[idx + 1] = (y * 4) & 0xff;
      buf[idx + 2] = ((x ^ y) * 3) & 0xff;
      buf[idx + 3] = 255;
    }
  }
  return buf;
}

function computePsnr(a, b) {
  let mse = 0;
  for (let i = 0; i < a.length; i++) {
    const d = a[i] - b[i];
    mse += d * d;
  }
  mse /= a.length;
  if (mse === 0) return 99.0;
  return 10 * Math.log10((255 * 255) / mse);
}

async function testVariant(variantFile, expectSimd) {
  const fullPath = path.join(workerDir, variantFile);
  const wasmPath = fullPath.replace(/\.js$/, '.wasm');
  assert.ok(fs.existsSync(fullPath), `Missing worker bundle: ${fullPath}`);
  assert.ok(fs.existsSync(wasmPath), `Missing worker wasm: ${wasmPath}`);

  const factory = require(fullPath);
  const mod = await factory({ wasmBinary: fs.readFileSync(wasmPath) });
  assert.equal(mod._astcenc_worker_is_simd(), expectSimd ? 1 : 0, `${variantFile} SIMD flag mismatch`);

  const width = 64;
  const height = 64;
  const blockX = 6;
  const blockY = 6;
  const blocksX = Math.ceil(width / blockX);
  const blocksY = Math.ceil(height / blockY);
  const astcLen = blocksX * blocksY * 16;
  const rgba = makePatternRgba(width, height);

  const inPtr = mod._malloc(rgba.byteLength);
  const astcPtr = mod._malloc(astcLen);
  const outPtr = mod._malloc(rgba.byteLength);

  try {
    mod.HEAPU8.set(rgba, inPtr);
    const encStatus = mod._astcenc_worker_compress(
      1, // ASTCENC_PRF_LDR
      blockX,
      blockY,
      10.0, // Fast
      0, // flags
      1,
      0, 1, 2, 3, // RGBA swizzle
      width,
      height,
      1, // mipCount
      0, // U8
      inPtr,
      astcPtr,
      astcLen
    );
    assert.equal(encStatus, 0, `${variantFile} compress failed with ${encStatus}`);

    const decStatus = mod._astcenc_worker_decompress(
      1, // ASTCENC_PRF_LDR
      blockX,
      blockY,
      width,
      height,
      8,
      astcPtr,
      astcLen,
      outPtr,
      rgba.byteLength
    );
    assert.equal(decStatus, 0, `${variantFile} decompress failed with ${decStatus}`);

    const decoded = mod.HEAPU8.slice(outPtr, outPtr + rgba.byteLength);
    const psnr = computePsnr(rgba, decoded);
    console.log(`[test-webgl-worker] ${variantFile}: PSNR=${psnr.toFixed(2)} dB (SIMD=${expectSimd})`);
    assert.ok(psnr >= 26.0, `${variantFile} PSNR too low: ${psnr}`);
    return mod.HEAPU8.slice(astcPtr, astcPtr + astcLen);
  } finally {
    mod._free(inPtr);
    mod._free(astcPtr);
    mod._free(outPtr);
  }
}

const scalarAstc = await testVariant('astcenc-worker-scalar.js', false);
const simdAstc = await testVariant('astcenc-worker-simd.js', true);
assert.equal(scalarAstc.length, simdAstc.length);
console.log('[test-webgl-worker] All WebGL Worker WASM tests passed.');
