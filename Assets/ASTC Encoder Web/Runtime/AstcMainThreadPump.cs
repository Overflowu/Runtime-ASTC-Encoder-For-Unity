using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Hidden main-thread MonoBehaviour pump used on WebGL (and as fallback) for:
    /// 1. Scheduling end-of-frame GPU readbacks without stalling mid-frame rendering.
    /// 2. Polling Emscripten pthread jobs and invoking completion callbacks on the main thread.
    /// 3. Running debounced IDBFS sync callbacks.
    /// </summary>
    internal sealed class AstcMainThreadPump : MonoBehaviour
    {
        private static AstcMainThreadPump s_Instance;
        private static readonly List<Action> s_TickCallbacks = new List<Action>();
        private static readonly List<Action> s_EndOfFrameQueue = new List<Action>();
        private static readonly List<Action> s_EndOfFrameScratch = new List<Action>();
        private static bool s_EndOfFrameRoutineActive;

        private static AstcMainThreadPump EnsureInstance()
        {
            if (s_Instance != null)
                return s_Instance;

            var go = new GameObject("[AstcMainThreadPump]")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<AstcMainThreadPump>();
            return s_Instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_TickCallbacks.Clear();
            s_EndOfFrameQueue.Clear();
            s_EndOfFrameScratch.Clear();
            s_EndOfFrameRoutineActive = false;
            if (s_Instance != null)
            {
                Destroy(s_Instance.gameObject);
                s_Instance = null;
            }
        }

        internal static void RegisterTick(Action tick)
        {
            if (tick == null) return;
            EnsureInstance();
            if (!s_TickCallbacks.Contains(tick))
            {
                s_TickCallbacks.Add(tick);
            }
        }

        internal static void UnregisterTick(Action tick)
        {
            if (tick == null) return;
            s_TickCallbacks.Remove(tick);
        }

        internal static void EnqueueEndOfFrame(Action action)
        {
            if (action == null) return;
            var inst = EnsureInstance();
            s_EndOfFrameQueue.Add(action);
            if (!s_EndOfFrameRoutineActive)
            {
                s_EndOfFrameRoutineActive = true;
                inst.StartCoroutine(inst.RunEndOfFrameLoop());
            }
        }

        private void Update()
        {
            for (int i = s_TickCallbacks.Count - 1; i >= 0; i--)
            {
                try
                {
                    s_TickCallbacks[i]?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private IEnumerator RunEndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (s_EndOfFrameQueue.Count > 0)
            {
                yield return wait;
                s_EndOfFrameScratch.Clear();
                s_EndOfFrameScratch.AddRange(s_EndOfFrameQueue);
                s_EndOfFrameQueue.Clear();

                for (int i = 0; i < s_EndOfFrameScratch.Count; i++)
                {
                    try
                    {
                        s_EndOfFrameScratch[i]?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
            s_EndOfFrameRoutineActive = false;
        }
    }
}
