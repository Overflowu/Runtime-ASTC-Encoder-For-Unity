using System;

namespace Aperture.Astcenc.Runtime
{
    public class AstcHeader : IDisposable
    {
        public const int HeaderSize = 16;
        private const uint MAGIC_FILE_CONSTANT = 0x5CA1AB13;

        private byte[] m_Bytes;
        private bool m_Disposed;

        internal byte[] Bytes => m_Bytes;

        public AstcHeader(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length < HeaderSize)
                throw new ArgumentException($"ASTC header requires at least {HeaderSize} bytes.", nameof(data));

            m_Bytes = new byte[HeaderSize];
            Buffer.BlockCopy(data, 0, m_Bytes, 0, HeaderSize);
            m_Disposed = false;
        }

        public AstcHeader(uint dimX, uint dimY, uint dimZ, int sizeX, int sizeY, int sizeZ)
        {
            m_Bytes = new byte[HeaderSize];
            WriteTo(m_Bytes, 0, dimX, dimY, dimZ, sizeX, sizeY, sizeZ);
            m_Disposed = false;
        }

        public static void WriteTo(byte[] buffer, int offset, uint dimX, uint dimY, uint dimZ, int sizeX, int sizeY, int sizeZ)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || buffer.Length - offset < HeaderSize)
                throw new ArgumentOutOfRangeException(nameof(offset));

            buffer[offset + 0] = (byte)(MAGIC_FILE_CONSTANT & 0xFF);
            buffer[offset + 1] = (byte)((MAGIC_FILE_CONSTANT >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((MAGIC_FILE_CONSTANT >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((MAGIC_FILE_CONSTANT >> 24) & 0xFF);

            buffer[offset + 4] = (byte)dimX;
            buffer[offset + 5] = (byte)dimY;
            buffer[offset + 6] = (byte)dimZ;

            buffer[offset + 7] = (byte)(sizeX & 0xFF);
            buffer[offset + 8] = (byte)((sizeX >> 8) & 0xFF);
            buffer[offset + 9] = (byte)((sizeX >> 16) & 0xFF);

            buffer[offset + 10] = (byte)(sizeY & 0xFF);
            buffer[offset + 11] = (byte)((sizeY >> 8) & 0xFF);
            buffer[offset + 12] = (byte)((sizeY >> 16) & 0xFF);

            buffer[offset + 13] = (byte)(sizeZ & 0xFF);
            buffer[offset + 14] = (byte)((sizeZ >> 8) & 0xFF);
            buffer[offset + 15] = (byte)((sizeZ >> 16) & 0xFF);
        }

        public void CopyTo(byte[] destination, int destinationOffset = 0)
        {
            ThrowIfDisposed();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destinationOffset < 0 || destination.Length - destinationOffset < HeaderSize)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset));

            Buffer.BlockCopy(m_Bytes, 0, destination, destinationOffset, HeaderSize);
        }

        public void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!m_Disposed)
            {
                m_Bytes = null;
                m_Disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
