// Dedicated Web Worker for non-blocking Runtime ASTC CPU compression in Unity WebGL.
// No SharedArrayBuffer / COOP / COEP headers required (uses zero-copy Transferable ArrayBuffers).
// Automatically probes browser WebAssembly 128-bit SIMD support and selects the SIMD bundle when available.

'use strict';

let wasmModule = null;
let initPromise = null;

function supportsWasmSimd() {
  try {
    // Minimal valid 128-bit WASM SIMD module: (func (result v128) (v128.const i32x4 0 0 0 0))
    const probe = new Uint8Array([
      0, 97, 115, 109, 1, 0, 0, 0,
      1, 5, 1, 96, 0, 1, 123,
      3, 2, 1, 0,
      10, 10, 1, 8, 0, 65, 0, 253, 15, 253, 98, 11
    ]);
    return typeof WebAssembly === 'object' && WebAssembly.validate(probe);
  } catch (_) {
    return false;
  }
}

async function loadVariant(scriptName) {
  importScripts(scriptName);
  if (typeof self.createAstcWorkerModule !== 'function') {
    throw new Error('createAstcWorkerModule factory not found in ' + scriptName);
  }
  const baseUrl = (typeof self.location !== 'undefined' && self.location && self.location.href)
    ? self.location.href.substring(0, self.location.href.lastIndexOf('/') + 1)
    : '';
  return await self.createAstcWorkerModule({
    locateFile: function (path) {
      return baseUrl ? (baseUrl + path) : path;
    }
  });
}

async function initWasm() {
  if (wasmModule) return wasmModule;
  if (initPromise) return initPromise;

  initPromise = (async () => {
    const preferSimd = supportsWasmSimd();
    if (preferSimd) {
      try {
        wasmModule = await loadVariant('astcenc-worker-simd.js');
        return wasmModule;
      } catch (simdErr) {
        // Fall back to scalar WASM if SIMD bundle is absent or fails to instantiate.
        self.createAstcWorkerModule = undefined;
      }
    }
    wasmModule = await loadVariant('astcenc-worker-scalar.js');
    return wasmModule;
  })();

  return initPromise;
}

self.onmessage = async function (event) {
  const msg = event.data;
  if (!msg || typeof msg !== 'object') return;

  if (msg.type === 'init') {
    try {
      const mod = await initWasm();
      const isSimd = mod._astcenc_worker_is_simd ? (mod._astcenc_worker_is_simd() !== 0) : false;
      self.postMessage({ type: 'ready', ok: true, simd: isSimd });
    } catch (err) {
      self.postMessage({ type: 'ready', ok: false, simd: false, error: String(err && err.message ? err.message : err) });
    }
    return;
  }

  if (msg.type === 'clearCache') {
    if (wasmModule && wasmModule._astcenc_worker_clear_cache) {
      wasmModule._astcenc_worker_clear_cache();
    }
    return;
  }

  if (msg.type === 'compress') {
    const jobId = msg.jobId;
    let inPtr = 0;
    let outPtr = 0;
    try {
      const mod = await initWasm();
      const srcBytes = new Uint8Array(msg.rgbaBuffer);
      const outLen = msg.expectedOutLen | 0;

      inPtr = mod._malloc(srcBytes.byteLength);
      outPtr = mod._malloc(outLen);
      if (!inPtr || !outPtr) {
        throw new Error('WASM worker OOM allocating ' + srcBytes.byteLength + '+' + outLen + ' bytes');
      }

      mod.HEAPU8.set(srcBytes, inPtr);

      const status = mod._astcenc_worker_compress(
        msg.profile | 0,
        msg.blockX >>> 0,
        msg.blockY >>> 0,
        msg.quality,
        msg.flags >>> 0,
        msg.alphaScaleRadius >>> 0,
        msg.swzR | 0,
        msg.swzG | 0,
        msg.swzB | 0,
        msg.swzA | 0,
        msg.width >>> 0,
        msg.height >>> 0,
        msg.mipCount >>> 0,
        msg.dataType | 0,
        inPtr,
        outPtr,
        outLen >>> 0
      );

      if (status !== 0) {
        self.postMessage({ type: 'result', jobId: jobId, status: status, data: null });
        return;
      }

      // Copy out of WASM heap into a standalone transferable ArrayBuffer
      const result = new Uint8Array(outLen);
      result.set(mod.HEAPU8.subarray(outPtr, outPtr + outLen));
      self.postMessage(
        { type: 'result', jobId: jobId, status: 0, data: result.buffer },
        [result.buffer]
      );
    } catch (err) {
      self.postMessage({
        type: 'result',
        jobId: jobId,
        status: -1,
        data: null,
        error: String(err && err.message ? err.message : err)
      });
    } finally {
      if (wasmModule) {
        if (inPtr) wasmModule._free(inPtr);
        if (outPtr) wasmModule._free(outPtr);
      }
    }
    return;
  }

  if (msg.type === 'decompress') {
    const jobId = msg.jobId;
    let inPtr = 0;
    let outPtr = 0;
    try {
      const mod = await initWasm();
      const srcBytes = new Uint8Array(msg.astcBuffer);
      const outLen = msg.expectedOutLen | 0;

      inPtr = mod._malloc(srcBytes.byteLength);
      outPtr = mod._malloc(outLen);
      if (!inPtr || !outPtr) {
        throw new Error('WASM worker OOM allocating ' + srcBytes.byteLength + '+' + outLen + ' bytes');
      }

      mod.HEAPU8.set(srcBytes, inPtr);

      const status = mod._astcenc_worker_decompress(
        msg.profile | 0,
        msg.blockX >>> 0,
        msg.blockY >>> 0,
        msg.width >>> 0,
        msg.height >>> 0,
        msg.outBitness | 0,
        inPtr,
        srcBytes.byteLength >>> 0,
        outPtr,
        outLen >>> 0
      );

      if (status !== 0) {
        self.postMessage({ type: 'result', jobId: jobId, status: status, data: null });
        return;
      }

      const result = new Uint8Array(outLen);
      result.set(mod.HEAPU8.subarray(outPtr, outPtr + outLen));
      self.postMessage(
        { type: 'result', jobId: jobId, status: 0, data: result.buffer },
        [result.buffer]
      );
    } catch (err) {
      self.postMessage({
        type: 'result',
        jobId: jobId,
        status: -1,
        data: null,
        error: String(err && err.message ? err.message : err)
      });
    } finally {
      if (wasmModule) {
        if (inPtr) wasmModule._free(inPtr);
        if (outPtr) wasmModule._free(outPtr);
      }
    }
  }
};
