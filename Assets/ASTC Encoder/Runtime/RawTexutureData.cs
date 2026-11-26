using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Aperture.Astcenc.Runtime
{
    public interface IRawData
    {
        void WriteTo(IntPtr destPtr);
    }

    internal static class RawDataConstants
    {
        internal const int ByteAlphaOneMask = unchecked((int)0xFF000000);
        internal const short HalfOne = 0x3C00;
        internal const int FloatOneBits = 0x3F800000;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUIntUnion
        {
            [FieldOffset(0)] public uint UIntValue;
            [FieldOffset(0)] public float FloatValue;
        }

        internal static float UIntBitsToSingle(uint bits)
        {
            return new FloatUIntUnion { UIntValue = bits }.FloatValue;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct R8 : IRawData
    {
        public float RFloat
        {
            get
            {
                return m_R / 255.0f;
            }
        }
        private byte m_R;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, RawDataConstants.ByteAlphaOneMask | m_R);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RG8 : IRawData
    {
        public float RFloat
        {
            get
            {
                return m_R / 255.0f;
            }
        }
        private byte m_R;

        public float GFloat
        {
            get
            {
                return m_G / 255.0f;
            }
        }
        private byte m_G;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, RawDataConstants.ByteAlphaOneMask | m_R | (m_G << 8));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGB8 : IRawData
    {
        public float RFloat
        {
            get
            {
                return m_R / 255.0f;
            }
        }
        private byte m_R;

        public float GFloat
        {
            get
            {
                return m_G / 255.0f;
            }
        }
        private byte m_G;

        public float BFloat
        {
            get
            {
                return m_B / 255.0f;
            }
        }
        private byte m_B;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, RawDataConstants.ByteAlphaOneMask | m_R | (m_G << 8) | (m_B << 16));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGBA8 : IRawData
    {
        public float RFloat
        {
            get
            {
                return m_R / 255.0f;
            }
        }
        private byte m_R;

        public float GFloat
        {
            get
            {
                return m_G / 255.0f;
            }
        }
        private byte m_G;

        public float BFloat
        {
            get
            {
                return m_B / 255.0f;
            }
        }
        private byte m_B;

        public float AFloat
        {
            get
            {
                return m_A / 255.0f;
            }
        }
        private byte m_A;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, unchecked(m_R | (m_G << 8) | (m_B << 16) | (m_A << 24)));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct R16 : IRawData
    {
        internal ushort RHalf
        {
            get
            {
                return Mathf.FloatToHalf(RFloat);
            }
        }

        public float RFloat
        {
            get
            {
                return m_R / 65535.0f;
            }
        }
        private ushort m_R;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)RHalf));
            Marshal.WriteInt16(destPtr, 2, 0);
            Marshal.WriteInt16(destPtr, 4, 0);
            Marshal.WriteInt16(destPtr, 6, RawDataConstants.HalfOne);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RG16 : IRawData
    {
        internal ushort RHalf
        {
            get
            {
                return Mathf.FloatToHalf(RFloat);
            }
        }

        public float RFloat
        {
            get
            {
                return m_R / 65535.0f;
            }
        }
        private ushort m_R;

        internal ushort GHalf
        {
            get
            {
                return Mathf.FloatToHalf(GFloat);
            }
        }

        public float GFloat
        {
            get
            {
                return m_G / 65535.0f;
            }
        }
        private ushort m_G;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)RHalf));
            Marshal.WriteInt16(destPtr, 2, unchecked((short)GHalf));
            Marshal.WriteInt16(destPtr, 4, 0);
            Marshal.WriteInt16(destPtr, 6, RawDataConstants.HalfOne);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGB16 : IRawData
    {
        internal ushort RHalf
        {
            get
            {
                return Mathf.FloatToHalf(RFloat);
            }
        }

        public float RFloat
        {
            get
            {
                return m_R / 65535.0f;
            }
        }
        private ushort m_R;

        internal ushort GHalf
        {
            get
            {
                return Mathf.FloatToHalf(GFloat);
            }
        }

        public float GFloat
        {
            get
            {
                return m_G / 65535.0f;
            }
        }
        private ushort m_G;

        internal ushort BHalf
        {
            get
            {
                return Mathf.FloatToHalf(BFloat);
            }
        }

        public float BFloat
        {
            get
            {
                return m_B / 65535.0f;
            }
        }
        private ushort m_B;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)RHalf));
            Marshal.WriteInt16(destPtr, 2, unchecked((short)GHalf));
            Marshal.WriteInt16(destPtr, 4, unchecked((short)BHalf));
            Marshal.WriteInt16(destPtr, 6, RawDataConstants.HalfOne);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGBA16 : IRawData
    {
        internal ushort RHalf
        {
            get
            {
                return Mathf.FloatToHalf(RFloat);
            }
        }

        public float RFloat
        {
            get
            {
                return m_R / 65535.0f;
            }
        }
        private ushort m_R;

        internal ushort GHalf
        {
            get
            {
                return Mathf.FloatToHalf(GFloat);
            }
        }

        public float GFloat
        {
            get
            {
                return m_G / 65535.0f;
            }
        }
        private ushort m_G;

        internal ushort BHalf
        {
            get
            {
                return Mathf.FloatToHalf(BFloat);
            }
        }

        public float BFloat
        {
            get
            {
                return m_B / 65535.0f;
            }
        }
        private ushort m_B;

        internal ushort AHalf
        {
            get
            {
                return Mathf.FloatToHalf(AFloat);
            }
        }

        public float AFloat
        {
            get
            {
                return m_A / 65535.0f;
            }
        }
        private ushort m_A;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)RHalf));
            Marshal.WriteInt16(destPtr, 2, unchecked((short)GHalf));
            Marshal.WriteInt16(destPtr, 4, unchecked((short)BHalf));
            Marshal.WriteInt16(destPtr, 6, unchecked((short)AHalf));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct R16F : IRawData
    {
        public float RFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_R);
            }
        }
        private ushort m_R;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)m_R));
            Marshal.WriteInt16(destPtr, 2, 0);
            Marshal.WriteInt16(destPtr, 4, 0);
            Marshal.WriteInt16(destPtr, 6, RawDataConstants.HalfOne);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RG16F : IRawData
    {
        public float RFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_R);
            }
        }
        private ushort m_R;

        public float GFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_G);
            }
        }
        private ushort m_G;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)m_R));
            Marshal.WriteInt16(destPtr, 2, unchecked((short)m_G));
            Marshal.WriteInt16(destPtr, 4, 0);
            Marshal.WriteInt16(destPtr, 6, RawDataConstants.HalfOne);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGB16F : IRawData
    {
        public float RFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_R);
            }
        }
        private ushort m_R;

        public float GFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_G);
            }
        }
        private ushort m_G;

        public float BFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_B);
            }
        }
        private ushort m_B;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)m_R));
            Marshal.WriteInt16(destPtr, 2, unchecked((short)m_G));
            Marshal.WriteInt16(destPtr, 4, unchecked((short)m_B));
            Marshal.WriteInt16(destPtr, 6, RawDataConstants.HalfOne);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGBA16F : IRawData
    {
        public float RFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_R);
            }
        }
        private ushort m_R;

        public float GFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_G);
            }
        }
        private ushort m_G;

        public float BFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_B);
            }
        }
        private ushort m_B;

        public float AFloat
        {
            get
            {
                return Mathf.HalfToFloat(m_A);
            }
        }
        private ushort m_A;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt16(destPtr, 0, unchecked((short)m_R));
            Marshal.WriteInt16(destPtr, 2, unchecked((short)m_G));
            Marshal.WriteInt16(destPtr, 4, unchecked((short)m_B));
            Marshal.WriteInt16(destPtr, 6, unchecked((short)m_A));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct R32F : IRawData
    {
        public float RFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_R);
            }
        }
        private uint m_R;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, 0, unchecked((int)m_R));
            Marshal.WriteInt32(destPtr, 4, 0);
            Marshal.WriteInt32(destPtr, 8, 0);
            Marshal.WriteInt32(destPtr, 12, RawDataConstants.FloatOneBits);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RG32F : IRawData
    {
        public float RFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_R);
            }
        }
        private uint m_R;

        public float GFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_G);
            }
        }
        private uint m_G;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, 0, unchecked((int)m_R));
            Marshal.WriteInt32(destPtr, 4, unchecked((int)m_G));
            Marshal.WriteInt32(destPtr, 8, 0);
            Marshal.WriteInt32(destPtr, 12, RawDataConstants.FloatOneBits);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGB32F : IRawData
    {
        public float RFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_R);
            }
        }
        private uint m_R;

        public float GFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_G);
            }
        }
        private uint m_G;

        public float BFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_B);
            }
        }
        private uint m_B;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, 0, unchecked((int)m_R));
            Marshal.WriteInt32(destPtr, 4, unchecked((int)m_G));
            Marshal.WriteInt32(destPtr, 8, unchecked((int)m_B));
            Marshal.WriteInt32(destPtr, 12, RawDataConstants.FloatOneBits);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RGBA32F : IRawData
    {
        public float RFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_R);
            }
        }
        private uint m_R;

        public float GFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_G);
            }
        }
        private uint m_G;

        public float BFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_B);
            }
        }
        private uint m_B;

        public float AFloat
        {
            get
            {
                return RawDataConstants.UIntBitsToSingle(m_A);
            }
        }
        private uint m_A;

        public void WriteTo(IntPtr destPtr)
        {
            Marshal.WriteInt32(destPtr, 0, unchecked((int)m_R));
            Marshal.WriteInt32(destPtr, 4, unchecked((int)m_G));
            Marshal.WriteInt32(destPtr, 8, unchecked((int)m_B));
            Marshal.WriteInt32(destPtr, 12, unchecked((int)m_A));
        }
    }
}
