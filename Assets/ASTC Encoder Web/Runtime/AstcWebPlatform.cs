using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Aperture.Astcenc.Web.Tests")]

namespace Aperture.Astcenc.Runtime
{
    internal enum MiniGameHostKind
    {
        None = 0,
        WeChat = 1,
        Douyin = 2,
        Kuaishou = 3,
    }

    /// <summary>
    /// Centralized WebGL / Tuanjie Web / MiniGame platform capability detection.
    /// </summary>
    internal static class AstcWebPlatform
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        internal const bool IsWebPlayer = true;

        [DllImport("__Internal")]
        private static extern int AstcWeb_HardwareConcurrency();

        [DllImport("__Internal")]
        private static extern int AstcWeb_IsCrossOriginIsolated();

        [DllImport("__Internal")]
        private static extern int AstcWeb_MiniGameHost();
#else
        internal const bool IsWebPlayer = false;
#endif

#if !UNITY_EDITOR && (UNITY_WEIXINMINIGAME || MINIGAME_SUBPLATFORM_WEIXIN || MINIGAME_SUBPLATFORM_DOUYIN || MINIGAME_SUBPLATFORM_KUAISHOU)
        internal const bool IsMiniGameCompileTime = true;
#else
        internal const bool IsMiniGameCompileTime = false;
#endif

#if !UNITY_EDITOR && UNITY_WEBGL && ASTC_WEBGL_PTHREADS
        internal const bool HasPthreadsCompileTime = true;
#else
        internal const bool HasPthreadsCompileTime = false;
#endif

        private static int s_CachedHwConcurrency;
        private static int s_CachedMiniGameHost = -1;
        private static int s_CachedCrossOriginIsolated = -1;

        internal static MiniGameHostKind MiniGameHost
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (s_CachedMiniGameHost < 0)
                {
                    try
                    {
                        s_CachedMiniGameHost = AstcWeb_MiniGameHost();
                    }
                    catch
                    {
                        s_CachedMiniGameHost = IsMiniGameCompileTime ? (int)MiniGameHostKind.WeChat : (int)MiniGameHostKind.None;
                    }
                }
                return (MiniGameHostKind)s_CachedMiniGameHost;
#else
                return MiniGameHostKind.None;
#endif
            }
        }

        internal static bool IsMiniGame => IsMiniGameCompileTime || MiniGameHost != MiniGameHostKind.None;

        internal static bool IsCrossOriginIsolated
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (s_CachedCrossOriginIsolated < 0)
                {
                    try
                    {
                        s_CachedCrossOriginIsolated = AstcWeb_IsCrossOriginIsolated() != 0 ? 1 : 0;
                    }
                    catch
                    {
                        s_CachedCrossOriginIsolated = 0;
                    }
                }
                return s_CachedCrossOriginIsolated == 1;
#else
                return false;
#endif
            }
        }

        internal static bool CanUseNativeThreads =>
            IsWebPlayer && HasPthreadsCompileTime && !IsMiniGame && IsCrossOriginIsolated;

        internal static int HardwareConcurrency
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                if (s_CachedHwConcurrency <= 0)
                {
                    try
                    {
                        s_CachedHwConcurrency = Math.Max(1, AstcWeb_HardwareConcurrency());
                    }
                    catch
                    {
                        s_CachedHwConcurrency = 1;
                    }
                }
                return s_CachedHwConcurrency;
#else
                return Math.Max(1, Environment.ProcessorCount);
#endif
            }
        }
    }
}
