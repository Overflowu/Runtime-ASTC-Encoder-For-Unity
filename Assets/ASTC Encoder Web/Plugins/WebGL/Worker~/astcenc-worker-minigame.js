// MiniGame Worker entrypoint (WeChat / Douyin / Kuaishou) for non-blocking ASTC compression/decompression.
// Uses global `worker` messaging and WXWebAssembly (or standard WebAssembly fallback).

'use strict';

var wasmModule = null;
var initPromise = null;

function getWasmGlobal() {
  if (typeof WXWebAssembly !== 'undefined' && WXWebAssembly && typeof WXWebAssembly.instantiate === 'function') {
    return WXWebAssembly;
  }
  if (typeof WebAssembly !== 'undefined' && WebAssembly && typeof WebAssembly.instantiate === 'function') {
    return WebAssembly;
  }
  return null;
}

function loadMiniGameVariant(scriptRel, wasmRel) {
  var factory = require(scriptRel);
  if (typeof factory !== 'function') {
    throw new Error('Worker factory not exported from ' + scriptRel);
  }
  var wasmApi = getWasmGlobal();
  if (!wasmApi) {
    throw new Error('Neither WXWebAssembly nor WebAssembly is available in MiniGame worker');
  }

  return factory({
    instantiateWasm: function (imports, receiveInstance) {
      wasmApi.instantiate(wasmRel, imports).then(function (result) {
        receiveInstance(result.instance, result.module);
      });
      return {};
    }
  });
}

async function initWasm() {
  if (wasmModule) return wasmModule;
  if (initPromise) return initPromise;

  initPromise = (async function () {
    try {
      wasmModule = await loadMiniGameVariant('./astcenc-worker-simd.js', 'workers/astcenc/astcenc-worker-simd.wasm');
      return wasmModule;
    } catch (_) {
      wasmModule = await loadMiniGameVariant('./astcenc-worker-scalar.js', 'workers/astcenc/astcenc-worker-scalar.wasm');
      return wasmModule;
    }
  })();

  return initPromise;
}

if (typeof worker !== 'undefined' && worker && typeof worker.onMessage === 'function') {
  worker.onMessage(async function (msg) {
    if (!msg || typeof msg !== 'object') return;

    if (msg.type === 'init') {
      try {
        var mod = await initWasm();
        var isSimd = mod._astcenc_worker_is_simd ? (mod._astcenc_worker_is_simd() !== 0) : false;
        worker.postMessage({ type: 'ready', ok: true, simd: isSimd });
      } catch (err) {
        worker.postMessage({ type: 'ready', ok: false, simd: false, error: String(err && err.message ? err.message : err) });
      }
      return;
    }

    if (msg.type === 'clearCache') {
      if (wasmModule && wasmModule._astcenc_worker_clear_cache) {
        wasmModule._astcenc_worker_clear_cache();
      }
      return;
    }

    if (msg.type === 'compress' || msg.type === 'decompress') {
      var jobId = msg.jobId;
      var inPtr = 0;
      var outPtr = 0;
      try {
        var m = await initWasm();
        var srcBuf = msg.type === 'compress' ? msg.rgbaBuffer : msg.astcBuffer;
        var srcBytes = new Uint8Array(srcBuf);
        var outLen = msg.expectedOutLen | 0;

        inPtr = m._malloc(srcBytes.byteLength);
        outPtr = m._malloc(outLen);
        if (!inPtr || !outPtr) {
          throw new Error('MiniGame worker OOM');
        }
        m.HEAPU8.set(srcBytes, inPtr);

        var status = msg.type === 'compress'
          ? m._astcenc_worker_compress(
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
            )
          : m._astcenc_worker_decompress(
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
          worker.postMessage({ type: 'result', jobId: jobId, status: status, data: null });
          return;
        }

        var result = new Uint8Array(outLen);
        result.set(m.HEAPU8.subarray(outPtr, outPtr + outLen));
        worker.postMessage({ type: 'result', jobId: jobId, status: 0, data: result.buffer });
      } catch (err) {
        worker.postMessage({ type: 'result', jobId: jobId, status: -1, data: null, error: String(err && err.message ? err.message : err) });
      } finally {
        if (wasmModule) {
          if (inPtr) wasmModule._free(inPtr);
          if (outPtr) wasmModule._free(outPtr);
        }
      }
    }
  });
}
