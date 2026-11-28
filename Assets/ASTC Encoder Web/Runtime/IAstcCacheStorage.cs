using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using UnityEngine;

namespace Aperture.Astcenc.Runtime
{
#if UNITY_WEBGL && !UNITY_EDITOR
    internal sealed class IdbfsCacheStorage : IAstcCacheStorage
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FlushCallback(int ok);

        [DllImport("__Internal")]
        private static extern void AstcFs_RequestSync();

        [DllImport("__Internal")]
        private static extern void AstcFs_FlushNow(FlushCallback cb);

        private static TaskCompletionSource<bool> s_FlushTcs;

        [MonoPInvokeCallback(typeof(FlushCallback))]
        private static void OnFlushComplete(int ok)
        {
            TaskCompletionSource<bool> tcs = s_FlushTcs;
            s_FlushTcs = null;
            tcs?.TrySetResult(ok != 0);
        }

        public string DefaultRoot => Path.Combine(Application.persistentDataPath, "AstcCache");

        public bool Exists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(File.ReadAllBytes(path));
        }

        public Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
        {
            try
            {
                FileCacheStorage.WriteAtomicSync(path, data, cancellationToken);
                try { AstcFs_RequestSync(); } catch { }
                return Task.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                return Task.FromCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        public Task FlushAsync()
        {
            if (s_FlushTcs != null)
                return s_FlushTcs.Task;

            s_FlushTcs = new TaskCompletionSource<bool>();
            try
            {
                AstcFs_FlushNow(OnFlushComplete);
            }
            catch
            {
                TaskCompletionSource<bool> tcs = s_FlushTcs;
                s_FlushTcs = null;
                tcs?.TrySetResult(false);
                return Task.CompletedTask;
            }
            return s_FlushTcs.Task;
        }
    }

    internal sealed class MiniGameCacheStorage : IAstcCacheStorage
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MgReadCallback(int ok, IntPtr ptr, int len);

        [DllImport("__Internal")]
        private static extern IntPtr AstcFs_MgGetRoot();

        [DllImport("__Internal")]
        private static extern int AstcFs_MgExists(string path);

        [DllImport("__Internal")]
        private static extern void AstcFs_MgReadSync(string path, MgReadCallback cb);

        [DllImport("__Internal")]
        private static extern int AstcFs_MgWriteAtomicSync(string path, IntPtr dataPtr, int dataLen);

        [DllImport("__Internal", EntryPoint = "free")]
        private static extern void NativeFree(IntPtr ptr);

        private static byte[] s_LastReadBytes;
        private static string s_CachedRoot;

        [MonoPInvokeCallback(typeof(MgReadCallback))]
        private static void OnMgReadComplete(int ok, IntPtr ptr, int len)
        {
            if (ok != 0 && ptr != IntPtr.Zero && len > 0)
            {
                s_LastReadBytes = new byte[len];
                Marshal.Copy(ptr, s_LastReadBytes, 0, len);
            }
            else
            {
                s_LastReadBytes = null;
            }
        }

        public string DefaultRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(s_CachedRoot))
                    return s_CachedRoot;
                try
                {
                    IntPtr ptr = AstcFs_MgGetRoot();
                    if (ptr != IntPtr.Zero)
                    {
                        s_CachedRoot = Marshal.PtrToStringUTF8(ptr);
                        NativeFree(ptr);
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(s_CachedRoot))
                    s_CachedRoot = Path.Combine(Application.persistentDataPath, "AstcCache");
                return s_CachedRoot;
            }
        }

        public bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return AstcFs_MgExists(path) != 0; }
            catch { return File.Exists(path); }
        }

        public byte[] ReadAllBytes(string path)
        {
            s_LastReadBytes = null;
            AstcFs_MgReadSync(path, OnMgReadComplete);
            byte[] res = s_LastReadBytes;
            s_LastReadBytes = null;
            return res ?? File.ReadAllBytes(path);
        }

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadAllBytes(path));
        }

        public unsafe Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixed (byte* ptr = data)
            {
                int ok = AstcFs_MgWriteAtomicSync(path, (IntPtr)ptr, data.Length);
                if (ok == 0)
                {
                    FileCacheStorage.WriteAtomicSync(path, data, cancellationToken);
                }
            }
            return Task.CompletedTask;
        }

        public Task FlushAsync() => Task.CompletedTask;
    }
#endif
}
