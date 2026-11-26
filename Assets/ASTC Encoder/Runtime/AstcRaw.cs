using System;
using System.Runtime.InteropServices;

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
                return m_Disposed;
            }
        }
        private bool m_Disposed;

        public AstcRaw(IntPtr data, int length)
        {
            m_Data = data;
            m_Length = length;
            m_Disposed = false;
        }

        ~AstcRaw()
        {
            Dispose(false);
        }

        public void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        public void CopyTo(byte[] destination, int destinationOffset = 0)
        {
            ThrowIfDisposed();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destinationOffset < 0 || destination.Length - destinationOffset < m_Length)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset));

            if (m_Length > 0 && m_Data != IntPtr.Zero)
            {
                Marshal.Copy(m_Data, destination, destinationOffset, m_Length);
            }
        }

        public byte[] ToArray()
        {
            ThrowIfDisposed();
            byte[] result = new byte[m_Length];
            if (m_Length > 0 && m_Data != IntPtr.Zero)
            {
                Marshal.Copy(m_Data, result, 0, m_Length);
            }
            return result;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!m_Disposed)
            {
                if (m_Data != IntPtr.Zero)
                    Marshal.FreeHGlobal(m_Data);

                m_Data = IntPtr.Zero;
                m_Length = 0;

                m_Disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
