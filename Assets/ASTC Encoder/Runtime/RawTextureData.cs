using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aperture.Astcenc.Runtime
{
    internal unsafe interface IRawData
    {
        void WriteTo(void* destPtr);
    }

    internal static class RawDataConstants
    {
        internal const uint ByteAlphaOneMask = 0xFF000000u;
        internal const ushort HalfOne = 0x3C00;
        internal const uint FloatOneBits = 0x3F800000u;
        internal const float InvUShortMax = 1.0f / 65535.0f;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct R8 : IRawData
    {
        private byte m_R;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            *(uint*)destPtr = RawDataConstants.ByteAlphaOneMask | m_R;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RG8 : IRawData
    {
        private byte m_R;
        private byte m_G;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            *(uint*)destPtr = RawDataConstants.ByteAlphaOneMask | m_R | ((uint)m_G << 8);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGB8 : IRawData
    {
        private byte m_R;
        private byte m_G;
        private byte m_B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            *(uint*)destPtr = RawDataConstants.ByteAlphaOneMask | m_R | ((uint)m_G << 8) | ((uint)m_B << 16);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGBA8 : IRawData
    {
        private byte m_R;
        private byte m_G;
        private byte m_B;
        private byte m_A;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            *(uint*)destPtr = m_R | ((uint)m_G << 8) | ((uint)m_B << 16) | ((uint)m_A << 24);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct R16 : IRawData
    {
        private ushort m_R;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            float* dst = (float*)destPtr;
            dst[0] = m_R * RawDataConstants.InvUShortMax;
            dst[1] = 0.0f;
            dst[2] = 0.0f;
            dst[3] = 1.0f;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RG16 : IRawData
    {
        private ushort m_R;
        private ushort m_G;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            float* dst = (float*)destPtr;
            dst[0] = m_R * RawDataConstants.InvUShortMax;
            dst[1] = m_G * RawDataConstants.InvUShortMax;
            dst[2] = 0.0f;
            dst[3] = 1.0f;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGB16 : IRawData
    {
        private ushort m_R;
        private ushort m_G;
        private ushort m_B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            float* dst = (float*)destPtr;
            dst[0] = m_R * RawDataConstants.InvUShortMax;
            dst[1] = m_G * RawDataConstants.InvUShortMax;
            dst[2] = m_B * RawDataConstants.InvUShortMax;
            dst[3] = 1.0f;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGBA16 : IRawData
    {
        private ushort m_R;
        private ushort m_G;
        private ushort m_B;
        private ushort m_A;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            float* dst = (float*)destPtr;
            dst[0] = m_R * RawDataConstants.InvUShortMax;
            dst[1] = m_G * RawDataConstants.InvUShortMax;
            dst[2] = m_B * RawDataConstants.InvUShortMax;
            dst[3] = m_A * RawDataConstants.InvUShortMax;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct R16F : IRawData
    {
        private ushort m_R;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            ushort* dst = (ushort*)destPtr;
            dst[0] = m_R;
            dst[1] = 0;
            dst[2] = 0;
            dst[3] = RawDataConstants.HalfOne;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RG16F : IRawData
    {
        private ushort m_R;
        private ushort m_G;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            ushort* dst = (ushort*)destPtr;
            dst[0] = m_R;
            dst[1] = m_G;
            dst[2] = 0;
            dst[3] = RawDataConstants.HalfOne;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGB16F : IRawData
    {
        private ushort m_R;
        private ushort m_G;
        private ushort m_B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            ushort* dst = (ushort*)destPtr;
            dst[0] = m_R;
            dst[1] = m_G;
            dst[2] = m_B;
            dst[3] = RawDataConstants.HalfOne;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGBA16F : IRawData
    {
        private ushort m_R;
        private ushort m_G;
        private ushort m_B;
        private ushort m_A;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            ushort* dst = (ushort*)destPtr;
            dst[0] = m_R;
            dst[1] = m_G;
            dst[2] = m_B;
            dst[3] = m_A;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct R32F : IRawData
    {
        private uint m_R;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            uint* dst = (uint*)destPtr;
            dst[0] = m_R;
            dst[1] = 0u;
            dst[2] = 0u;
            dst[3] = RawDataConstants.FloatOneBits;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RG32F : IRawData
    {
        private uint m_R;
        private uint m_G;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            uint* dst = (uint*)destPtr;
            dst[0] = m_R;
            dst[1] = m_G;
            dst[2] = 0u;
            dst[3] = RawDataConstants.FloatOneBits;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGB32F : IRawData
    {
        private uint m_R;
        private uint m_G;
        private uint m_B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            uint* dst = (uint*)destPtr;
            dst[0] = m_R;
            dst[1] = m_G;
            dst[2] = m_B;
            dst[3] = RawDataConstants.FloatOneBits;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct RGBA32F : IRawData
    {
        private uint m_R;
        private uint m_G;
        private uint m_B;
        private uint m_A;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteTo(void* destPtr)
        {
            uint* dst = (uint*)destPtr;
            dst[0] = m_R;
            dst[1] = m_G;
            dst[2] = m_B;
            dst[3] = m_A;
        }
    }
}
