// WebGL IDBFS auto-sync and Tuanjie MiniGame FileSystemManager bridge for AstcDiskCache.

var AstcWebFsPlugin = {
  $AstcFsState: {
    syncTimer: 0,
    pendingFlushCbs: [],

    getMiniGameFs: function () {
      var h = (typeof wx !== 'undefined' && wx && wx.getFileSystemManager) ? wx
            : (typeof tt !== 'undefined' && tt && tt.getFileSystemManager) ? tt
            : (typeof ks !== 'undefined' && ks && ks.getFileSystemManager) ? ks
            : null;
      if (!h) return null;
      try {
        var fs = h.getFileSystemManager();
        var root = (h.env && h.env.USER_DATA_PATH) ? h.env.USER_DATA_PATH : '';
        return { fs: fs, root: root };
      } catch (_) {
        return null;
      }
    },

    invokeVoidCb: function (cbPtr, arg) {
      if (!cbPtr) return;
      if (typeof dynCall === 'function') {
        dynCall('vi', cbPtr, [arg]);
      } else if (typeof wasmTable !== 'undefined' && wasmTable && typeof wasmTable.get === 'function') {
        wasmTable.get(cbPtr)(arg);
      } else if (typeof Module !== 'undefined' && typeof Module['dynCall_vi'] === 'function') {
        Module['dynCall_vi'](cbPtr, arg);
      }
    },

    invokeReadCb: function (cbPtr, ok, ptr, len) {
      if (!cbPtr) return;
      if (typeof dynCall === 'function') {
        dynCall('viii', cbPtr, [ok, ptr, len]);
      } else if (typeof wasmTable !== 'undefined' && wasmTable && typeof wasmTable.get === 'function') {
        wasmTable.get(cbPtr)(ok, ptr, len);
      } else if (typeof Module !== 'undefined' && typeof Module['dynCall_viii'] === 'function') {
        Module['dynCall_viii'](cbPtr, ok, ptr, len);
      }
    },

    runSyncFs: function () {
      var st = AstcFsState;
      st.syncTimer = 0;
      var cbs = st.pendingFlushCbs;
      st.pendingFlushCbs = [];

      if (typeof FS !== 'undefined' && FS && typeof FS.syncfs === 'function') {
        try {
          FS.syncfs(false, function (err) {
            var ok = err ? 0 : 1;
            for (var i = 0; i < cbs.length; i++) {
              st.invokeVoidCb(cbs[i], ok);
            }
          });
          return;
        } catch (_) {}
      }

      for (var i = 0; i < cbs.length; i++) {
        st.invokeVoidCb(cbs[i], 1);
      }
    },

    ensureDirSync: function (fs, fullPath) {
      var idx = fullPath.lastIndexOf('/');
      if (idx <= 0) return;
      var dir = fullPath.substring(0, idx);
      try {
        fs.mkdirSync(dir, true);
      } catch (_) {}
    }
  },

  AstcFs_RequestSync: function () {
    var st = AstcFsState;
    if (st.syncTimer) {
      clearTimeout(st.syncTimer);
    }
    st.syncTimer = setTimeout(function () {
      st.runSyncFs();
    }, 500);
  },

  AstcFs_FlushNow: function (cbPtr) {
    var st = AstcFsState;
    if (cbPtr) st.pendingFlushCbs.push(cbPtr);
    if (st.syncTimer) {
      clearTimeout(st.syncTimer);
      st.syncTimer = 0;
    }
    st.runSyncFs();
  },

  AstcFs_MgGetRoot: function () {
    var mg = AstcFsState.getMiniGameFs();
    var root = (mg && mg.root) ? (mg.root.replace(/\/+$/, '') + '/astc-cache') : '';
    var len = lengthBytesUTF8(root) + 1;
    var ptr = _malloc(len);
    stringToUTF8(root, ptr, len);
    return ptr;
  },

  AstcFs_MgExists: function (pathPtr) {
    var mg = AstcFsState.getMiniGameFs();
    if (!mg) return 0;
    var p = UTF8ToString(pathPtr);
    try {
      mg.fs.accessSync(p);
      return 1;
    } catch (_) {
      return 0;
    }
  },

  AstcFs_MgReadSync: function (pathPtr, onCompleteCb) {
    var mg = AstcFsState.getMiniGameFs();
    if (!mg) {
      AstcFsState.invokeReadCb(onCompleteCb, 0, 0, 0);
      return;
    }
    var p = UTF8ToString(pathPtr);
    try {
      var data = mg.fs.readFileSync(p);
      var bytes = new Uint8Array(data);
      var ptr = _malloc(bytes.byteLength);
      if (!ptr) {
        AstcFsState.invokeReadCb(onCompleteCb, 0, 0, 0);
        return;
      }
      HEAPU8.set(bytes, ptr);
      try {
        AstcFsState.invokeReadCb(onCompleteCb, 1, ptr, bytes.byteLength);
      } finally {
        _free(ptr);
      }
    } catch (_) {
      AstcFsState.invokeReadCb(onCompleteCb, 0, 0, 0);
    }
  },

  AstcFs_MgWriteAtomicSync: function (pathPtr, dataPtr, dataLen) {
    var mg = AstcFsState.getMiniGameFs();
    if (!mg) return 0;
    var p = UTF8ToString(pathPtr);
    var tmp = p + '.tmp.' + Math.floor(Math.random() * 1000000);
    try {
      AstcFsState.ensureDirSync(mg.fs, p);
      var copy = new Uint8Array(dataLen);
      copy.set(HEAPU8.subarray(dataPtr, dataPtr + dataLen));
      mg.fs.writeFileSync(tmp, copy.buffer);
      try {
        mg.fs.renameSync(tmp, p);
      } catch (_) {
        // Fallback: write directly if rename over existing fails on some MiniGame runtimes
        try { mg.fs.unlinkSync(tmp); } catch (__) {}
        mg.fs.writeFileSync(p, copy.buffer);
      }
      return 1;
    } catch (_) {
      try { mg.fs.unlinkSync(tmp); } catch (__) {}
      return 0;
    }
  }
};

autoAddDeps(AstcWebFsPlugin, '$AstcFsState');
mergeInto(LibraryManager.library, AstcWebFsPlugin);
