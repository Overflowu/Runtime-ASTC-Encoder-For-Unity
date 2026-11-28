var AstcWebPlatformPlugin = {
  $AstcWebEnv: {
    detectHost: function () {
      if (typeof wx !== 'undefined' && wx && typeof wx.getFileSystemManager === 'function') {
        return 1; // WeChat MiniGame
      }
      if (typeof tt !== 'undefined' && tt && typeof tt.getFileSystemManager === 'function') {
        return 2; // Douyin / ByteDance MiniGame
      }
      if (typeof ks !== 'undefined' && ks && typeof ks.getFileSystemManager === 'function') {
        return 3; // Kuaishou MiniGame
      }
      return 0;
    },

    getHostObject: function () {
      var kind = AstcWebEnv.detectHost();
      if (kind === 1) return wx;
      if (kind === 2) return tt;
      if (kind === 3) return ks;
      return null;
    }
  },

  AstcWeb_HardwareConcurrency: function () {
    if (typeof navigator !== 'undefined' && navigator && navigator.hardwareConcurrency) {
      var n = navigator.hardwareConcurrency | 0;
      if (n > 0) return n;
    }
    var host = AstcWebEnv.getHostObject();
    if (host && typeof host.getDeviceInfo === 'function') {
      try {
        var info = host.getDeviceInfo();
        if (info && info.cpuCores > 0) return info.cpuCores | 0;
      } catch (_) {}
    }
    return 1;
  },

  AstcWeb_IsCrossOriginIsolated: function () {
    if (typeof self !== 'undefined' && self && self.crossOriginIsolated === true && typeof SharedArrayBuffer !== 'undefined') {
      return 1;
    }
    return 0;
  },

  AstcWeb_MiniGameHost: function () {
    return AstcWebEnv.detectHost();
  }
};

autoAddDeps(AstcWebPlatformPlugin, '$AstcWebEnv');
mergeInto(LibraryManager.library, AstcWebPlatformPlugin);
