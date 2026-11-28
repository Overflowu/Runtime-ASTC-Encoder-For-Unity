// Unity WebGL JavaScript library bridging C# AstcWebWorkerBackend to a pool of dedicated Web Workers.
// Works across Unity 2021.3 (Emscripten 2.0), Unity 2022.3 (Emscripten 3.1), and Unity 6 without SharedArrayBuffer.

var AstcWebWorkerPlugin = {
  $AstcWorkerState: {
    workers: [],
    idleWorkers: [],
    queue: [],
    cancelledJobs: {},
    inFlight: {},
    onReadyCb: 0,
    onCompleteCb: 0,
    initialized: false,
    readySent: false,
    simdEnabled: false,

    invokeCallback: function (cbPtr, sig, args) {
      if (!cbPtr) return;
      if (typeof dynCall === 'function') {
        dynCall(sig, cbPtr, args);
      } else if (typeof wasmTable !== 'undefined' && wasmTable && typeof wasmTable.get === 'function') {
        wasmTable.get(cbPtr).apply(null, args);
      } else if (typeof Module !== 'undefined' && typeof Module['dynCall_' + sig] === 'function') {
        Module['dynCall_' + sig].apply(null, [cbPtr].concat(args));
      }
    },

    dispatchNext: function () {
      var st = AstcWorkerState;
      while (st.idleWorkers.length > 0 && st.queue.length > 0) {
        var job = st.queue.shift();
        if (st.cancelledJobs[job.jobId]) {
          delete st.cancelledJobs[job.jobId];
          continue;
        }
        var worker = st.idleWorkers.pop();
        st.inFlight[job.jobId] = worker;
        worker.postMessage(job.message, [job.message.rgbaBuffer]);
      }
    },

    handleWorkerMessage: function (worker, event) {
      var st = AstcWorkerState;
      var msg = event.data;
      if (!msg || typeof msg !== 'object') return;

      if (msg.type === 'ready') {
        if (msg.ok) {
          st.idleWorkers.push(worker);
          if (msg.simd) st.simdEnabled = true;
          if (!st.readySent) {
            st.readySent = true;
            st.initialized = true;
            st.invokeCallback(st.onReadyCb, 'vii', [1, st.simdEnabled ? 1 : 0]);
          }
          st.dispatchNext();
        } else {
          // Remove failed worker; if no workers remain and ready wasn't sent, report failure.
          var idx = st.workers.indexOf(worker);
          if (idx >= 0) st.workers.splice(idx, 1);
          try { worker.terminate(); } catch (_) {}
          if (st.workers.length === 0 && !st.readySent) {
            st.readySent = true;
            st.invokeCallback(st.onReadyCb, 'vii', [0, 0]);
          }
        }
        return;
      }

      if (msg.type === 'result') {
        var jobId = msg.jobId | 0;
        delete st.inFlight[jobId];
        st.idleWorkers.push(worker);

        if (st.cancelledJobs[jobId]) {
          delete st.cancelledJobs[jobId];
          st.dispatchNext();
          return;
        }

        var status = msg.status | 0;
        var outPtr = 0;
        var outLen = 0;

        if (status === 0 && msg.data) {
          var bytes = new Uint8Array(msg.data);
          outLen = bytes.byteLength;
          outPtr = _malloc(outLen);
          if (outPtr) {
            HEAPU8.set(bytes, outPtr);
          } else {
            status = -2;
            outLen = 0;
          }
        }

        try {
          st.invokeCallback(st.onCompleteCb, 'viiii', [jobId, status, outPtr, outLen]);
        } finally {
          if (outPtr) _free(outPtr);
        }

        st.dispatchNext();
      }
    }
  },

  AstcWebWorker_IsSupported: function () {
    return (typeof Worker !== 'undefined' && typeof WebAssembly !== 'undefined') ? 1 : 0;
  },

  AstcWebWorker_Init: function (workerUrlPtr, maxWorkers, onReadyCb, onCompleteCb) {
    var st = AstcWorkerState;
    if (typeof Worker === 'undefined' || typeof WebAssembly === 'undefined') {
      st.invokeCallback(onReadyCb, 'vii', [0, 0]);
      return 0;
    }

    // Tear down any previous pool before reinitializing
    for (var i = 0; i < st.workers.length; i++) {
      try { st.workers[i].terminate(); } catch (_) {}
    }
    st.workers = [];
    st.idleWorkers = [];
    st.queue = [];
    st.cancelledJobs = {};
    st.inFlight = {};
    st.initialized = false;
    st.readySent = false;
    st.simdEnabled = false;
    st.onReadyCb = onReadyCb;
    st.onCompleteCb = onCompleteCb;

    var url = UTF8ToString(workerUrlPtr);
    var hw = (typeof navigator !== 'undefined' && navigator.hardwareConcurrency) ? navigator.hardwareConcurrency : 2;
    var count = maxWorkers > 0 ? maxWorkers : Math.max(1, Math.min(hw, 4));
    if (count > 8) count = 8;

    try {
      for (var w = 0; w < count; w++) {
        (function () {
          var worker = new Worker(url);
          worker.onmessage = function (ev) {
            st.handleWorkerMessage(worker, ev);
          };
          worker.onerror = function () {
            var idx = st.workers.indexOf(worker);
            if (idx >= 0) st.workers.splice(idx, 1);
            try { worker.terminate(); } catch (_) {}
            if (st.workers.length === 0 && !st.readySent) {
              st.readySent = true;
              st.invokeCallback(st.onReadyCb, 'vii', [0, 0]);
            }
          };
          st.workers.push(worker);
          worker.postMessage({ type: 'init' });
        })();
      }
      return 1;
    } catch (_) {
      for (var k = 0; k < st.workers.length; k++) {
        try { st.workers[k].terminate(); } catch (__) {}
      }
      st.workers = [];
      if (!st.readySent) {
        st.readySent = true;
        st.invokeCallback(st.onReadyCb, 'vii', [0, 0]);
      }
      return 0;
    }
  },

  AstcWebWorker_CompressAsync: function (
    jobId,
    profile,
    blockX,
    blockY,
    quality,
    flags,
    alphaScaleRadius,
    swzR,
    swzG,
    swzB,
    swzA,
    width,
    height,
    mipCount,
    dataType,
    rgbaPtr,
    rgbaByteLen,
    expectedOutLen
  ) {
    var st = AstcWorkerState;
    if (!st.initialized || st.workers.length === 0) {
      return 0;
    }

    // Copy pixel data out of Unity's WASM heap into a standalone transferable ArrayBuffer
    var copy = new Uint8Array(rgbaByteLen);
    copy.set(HEAPU8.subarray(rgbaPtr, rgbaPtr + rgbaByteLen));

    st.queue.push({
      jobId: jobId | 0,
      message: {
        type: 'compress',
        jobId: jobId | 0,
        profile: profile | 0,
        blockX: blockX >>> 0,
        blockY: blockY >>> 0,
        quality: quality,
        flags: flags >>> 0,
        alphaScaleRadius: alphaScaleRadius >>> 0,
        swzR: swzR | 0,
        swzG: swzG | 0,
        swzB: swzB | 0,
        swzA: swzA | 0,
        width: width >>> 0,
        height: height >>> 0,
        mipCount: mipCount >>> 0,
        dataType: dataType | 0,
        rgbaBuffer: copy.buffer,
        expectedOutLen: expectedOutLen | 0
      }
    });

    st.dispatchNext();
    return 1;
  },

  AstcWebWorker_Cancel: function (jobId) {
    AstcWorkerState.cancelledJobs[jobId | 0] = true;
  },

  AstcWebWorker_Shutdown: function () {
    var st = AstcWorkerState;
    for (var i = 0; i < st.workers.length; i++) {
      try { st.workers[i].terminate(); } catch (_) {}
    }
    st.workers = [];
    st.idleWorkers = [];
    st.queue = [];
    st.cancelledJobs = {};
    st.inFlight = {};
    st.initialized = false;
    st.readySent = false;
    st.simdEnabled = false;
  }
};

autoAddDeps(AstcWebWorkerPlugin, '$AstcWorkerState');
mergeInto(LibraryManager.library, AstcWebWorkerPlugin);
