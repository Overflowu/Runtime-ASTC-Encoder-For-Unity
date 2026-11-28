using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aperture.Astcenc.Runtime
{
    public class AstcRaw : IDisposable
    {
        internal IntPtr Data
        {
            get
            {
                return m_Data;
            }
        }
        private IntPtr m_Data;

        public int Length
        {
            get
            {
                return m_Length;
            }
        }
        private int m_Length;

        public bool IsDisposed
        {
            get
            {
                return Volatile.Read(ref m_Disposed) != 0;
            }
        }
        private int m_Disposed;

        public AstcRaw(IntPtr data, int length)
        {
            m_Data = data;
            m_Length = length;
            m_Disposed = 0;
        }

        ~AstcRaw()
        {
            Dispose(false);
        }

        public void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        public void CopyTo(byte[] destination, int destinationOffset = 0)
        {
            ThrowIfDisposed();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destinationOffset < 0 || destination.Length - destinationOffset < m_Length)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset));

            IntPtr data = m_Data;
            if (m_Length > 0 && data != IntPtr.Zero)
            {
                Marshal.Copy(data, destination, destinationOffset, m_Length);
            }
        }

        public byte[] ToArray()
        {
            ThrowIfDisposed();
            byte[] result = new byte[m_Length];
            IntPtr data = m_Data;
            if (m_Length > 0 && data != IntPtr.Zero)
            {
                Marshal.Copy(data, result, 0, m_Length);
            }
            return result;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref m_Disposed, 1) == 0)
            {
                IntPtr data = Interlocked.Exchange(ref m_Data, IntPtr.Zero);
                if (data != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(data);
                }

                m_Length = 0;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
