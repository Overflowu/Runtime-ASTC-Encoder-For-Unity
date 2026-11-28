#ifndef ASTC_GPU_BISE_HLSL
#define ASTC_GPU_BISE_HLSL

// Pure-ALU ASTC Bounded Integer Sequence Encoding (BISE), Quantization/Unquantization,
// and 128-bit physical block assembly.
// Zero large static const tables to guarantee mobile GPU (Android Vulkan/GLES3.1 & iOS Metal) compatibility.

#define ASTC_QUANT_2   0u
#define ASTC_QUANT_3   1u
#define ASTC_QUANT_4   2u
#define ASTC_QUANT_5   3u
#define ASTC_QUANT_6   4u
#define ASTC_QUANT_8   5u
#define ASTC_QUANT_10  6u
#define ASTC_QUANT_12  7u
#define ASTC_QUANT_16  8u
#define ASTC_QUANT_20  9u
#define ASTC_QUANT_24  10u
#define ASTC_QUANT_32  11u
#define ASTC_QUANT_40  12u
#define ASTC_QUANT_48  13u
#define ASTC_QUANT_64  14u
#define ASTC_QUANT_80  15u
#define ASTC_QUANT_96  16u
#define ASTC_QUANT_128 17u
#define ASTC_QUANT_160 18u
#define ASTC_QUANT_192 19u
#define ASTC_QUANT_256 20u

// Color Endpoint Modes (CEM)
#define ASTC_CEM_LDR_LUM_ALPHA_DIRECT  4u  // 4 endpoint values (used for GGGR Normal Maps)
#define ASTC_CEM_LDR_RGB_DIRECT        8u  // 6 endpoint values
#define ASTC_CEM_HDR_RGB_DIRECT        11u // 6 endpoint values (HDR RGB, A = 1.0)
#define ASTC_CEM_LDR_RGBA_DIRECT       12u // 8 endpoint values
#define ASTC_CEM_HDR_RGB_LDR_ALPHA     14u // 8 endpoint values (HDR RGB + LDR A)
#define ASTC_CEM_HDR_RGBA_DIRECT       15u // 8 endpoint values (HDR RGB + HDR A)

// Compact 21-entry packed LUT: rangeVal (bits 0..8) | bits (9..12) | trits (13) | quints (14)
static const uint kAstcQuantTable[21] =
{
      2u | (1u << 9u),
      3u | (0u << 9u) | (1u << 13u),
      4u | (2u << 9u),
      5u | (0u << 9u) | (1u << 14u),
      6u | (1u << 9u) | (1u << 13u),
      8u | (3u << 9u),
     10u | (1u << 9u) | (1u << 14u),
     12u | (2u << 9u) | (1u << 13u),
     16u | (4u << 9u),
     20u | (2u << 9u) | (1u << 14u),
     24u | (3u << 9u) | (1u << 13u),
     32u | (5u << 9u),
     40u | (3u << 9u) | (1u << 14u),
     48u | (4u << 9u) | (1u << 13u),
     64u | (6u << 9u),
     80u | (4u << 9u) | (1u << 14u),
     96u | (5u << 9u) | (1u << 13u),
    128u | (7u << 9u),
    160u | (5u << 9u) | (1u << 14u),
    192u | (6u << 9u) | (1u << 13u),
    256u | (8u << 9u)
};

// Returns (range, bits, trits, quints) for a quantization mode 0..20.
void AstcGetQuantParams(uint q, out uint rangeVal, out uint bits, out uint trits, out uint quints)
{
    uint packed = kAstcQuantTable[min(q, 20u)];
    rangeVal = packed & 0x1FFu;
    bits     = (packed >> 9u) & 0xFu;
    trits    = (packed >> 13u) & 1u;
    quints   = (packed >> 14u) & 1u;
}

uint AstcGetBiseBitCount(uint count, uint q)
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);
    uint total = count * b;
    if (t != 0u)
    {
        total += (count * 8u + 4u) / 5u;
    }
    else if (qu != 0u)
    {
        total += (count * 7u + 2u) / 3u;
    }
    return total;
}

// Pure-ALU BISE 5-trit packer -> 8-bit integer (exact match to Khronos ASTC spec).
uint AstcPack5Trits(uint t0, uint t1, uint t2, uint t3, uint t4)
{
    uint C;
    if (t2 == 2u)
    {
        C = (t1 == 2u) ? ((3u << 2u) | t0) : ((t1 << 4u) | (t0 << 2u) | 3u);
    }
    else
    {
        C = (t2 << 4u) | (t1 << 2u) | t0;
    }

    if (t4 == 2u)
    {
        return (t3 == 2u)
            ? (((C >> 2u) << 5u) | (7u << 2u) | (C & 3u))
            : ((t3 << 7u) | (3u << 5u) | C);
    }
    return (t4 << 7u) | (t3 << 5u) | C;
}

// Pure-ALU BISE 3-quint packer -> 7-bit integer (exact match to Khronos ASTC spec).
uint AstcPack3Quints(uint q0, uint q1, uint q2)
{
    if (q2 == 4u)
    {
        if (q1 == 4u)
        {
            return (q0 == 4u) ? 7u : ((1u << 5u) | (q0 << 3u) | 7u);
        }
        if (q0 == 4u)
        {
            return (1u << 5u) | (q1 << 3u) | 6u;
        }
        return ((3u - (q0 >> 1u)) << 5u) | (q1 << 3u) | 6u | (q0 & 1u);
    }
    if (q1 == 4u)
    {
        return (q0 == 4u) ? ((q2 << 3u) | 6u) : ((q2 << 5u) | (q0 << 3u) | 5u);
    }
    return (q2 << 5u) | (q1 << 3u) | q0;
}

// Maps a monotonically ordered quantized level k in [0, range-1] to the ASTC BISE code.
uint AstcLinearToCode(uint k, uint q)
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);
    if ((t == 0u && qu == 0u) || b == 0u)
    {
        return k;
    }

    uint halfR = rangeVal >> 1u;
    uint baseVal = (t != 0u) ? 3u : 5u;
    if (k < halfR)
    {
        uint d = k % baseVal;
        uint m = k / baseVal;
        return (d << b) | (m << 1u);
    }
    else
    {
        uint rk = (rangeVal - 1u) - k;
        uint d = rk % baseVal;
        uint m = rk / baseVal;
        return (d << b) | (m << 1u) | 1u;
    }
}

// Exact Khronos ASTC color unquantization: BISE code v -> [0, 255].
uint AstcUnquantizeColor(uint v, uint q)
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);

    if (t == 0u && qu == 0u)
    {
        if (b == 8u) return v;
        if (b == 7u) return (v << 1u) | (v >> 6u);
        if (b == 6u) return (v << 2u) | (v >> 4u);
        if (b == 5u) return (v << 3u) | (v >> 2u);
        if (b == 4u) return (v << 4u) | v;
        if (b == 3u) return (v << 5u) | (v << 2u) | (v >> 1u);
        if (b == 2u) return v * 85u;
        return v * 255u;
    }

    uint D = v >> b;
    uint low = v & ((1u << b) - 1u);
    uint A = ((low & 1u) != 0u) ? 0x1FFu : 0u;
    uint B = 0u;
    uint C = 0u;
    uint m = low >> 1u;

    if (t != 0u)
    {
        if (b == 1u)      { C = 204u; B = 0u; }
        else if (b == 2u) { C = 93u;  B = (m << 8u) | (m << 4u) | (m << 2u) | (m << 1u); }
        else if (b == 3u) { C = 44u;  B = (m << 7u) | (m << 2u) | m; }
        else if (b == 4u) { C = 22u;  B = (m << 6u) | m; }
        else if (b == 5u) { C = 11u;  B = (m << 5u) | (m >> 2u); }
        else              { C = 5u;   B = (m << 4u) | (m >> 4u); }
    }
    else
    {
        if (b == 1u)      { C = 113u; B = 0u; }
        else if (b == 2u) { C = 54u;  B = (m << 8u) | (m << 3u) | (m << 2u); }
        else if (b == 3u) { C = 26u;  B = (m << 7u) | (m << 1u) | (m >> 1u); }
        else if (b == 4u) { C = 13u;  B = (m << 6u) | (m >> 1u); }
        else              { C = 6u;   B = (m << 5u) | (m >> 3u); }
    }

    uint T = (D * C + B) ^ A;
    return (A & 0x80u) | (T >> 2u);
}

// Exact Khronos ASTC weight unquantization: BISE code v -> [0, 64].
uint AstcUnquantizeWeight(uint v, uint q)
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);
    uint T = 0u;

    if (t == 0u && qu == 0u)
    {
        if (b == 1u)      T = (v != 0u) ? 63u : 0u;
        else if (b == 2u) T = v * 21u;
        else if (b == 3u) T = (v << 3u) | v;
        else if (b == 4u) T = (v << 2u) | (v >> 2u);
        else              T = (v << 1u) | (v >> 4u);
    }
    else if (t != 0u)
    {
        uint D = v >> b;
        uint low = v & ((1u << b) - 1u);
        if (b == 0u)
        {
            T = (D == 0u) ? 0u : ((D == 1u) ? 32u : 63u);
        }
        else
        {
            uint A = ((low & 1u) != 0u) ? 0x7Fu : 0u;
            uint m = low >> 1u;
            uint B = 0u;
            uint C = 50u;
            if (b == 2u)      { C = 23u; B = (m << 6u) | (m << 2u) | m; }
            else if (b == 3u) { C = 11u; B = (m << 5u) | m; }
            T = (D * C + B) ^ A;
            T = (A & 0x20u) | (T >> 2u);
        }
    }
    else
    {
        uint D = v >> b;
        uint low = v & ((1u << b) - 1u);
        if (b == 0u)
        {
            if (D == 0u) T = 0u;
            else if (D == 1u) T = 16u;
            else if (D == 2u) T = 32u;
            else if (D == 3u) T = 47u;
            else T = 63u;
        }
        else
        {
            uint A = ((low & 1u) != 0u) ? 0x7Fu : 0u;
            uint m = low >> 1u;
            uint B = 0u;
            uint C = 28u;
            if (b == 2u) { C = 13u; B = (m << 6u) | (m << 1u); }
            T = (D * C + B) ^ A;
            T = (A & 0x20u) | (T >> 2u);
        }
    }

    if (T > 32u) T += 1u;
    return T;
}

// Quantizes an 8-bit endpoint value c8 in [0, 255] to the exact nearest BISE code for quant mode q.
// When min128 is true, constrains the unquantized value to >= 128 (HDR modeval/majcomp == 3 direct endpoints).
uint AstcQuantizeColorRange(uint c8, uint q, bool min128, out uint unquantOut)
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);
    if (min128) c8 = max(c8, 128u);
    if (q == ASTC_QUANT_256)
    {
        unquantOut = c8;
        return c8;
    }

    int minK = min128 ? (int)(rangeVal >> 1u) : 0;
    int maxK = (int)rangeVal - 1;
    int kEst = clamp((int)floor((float)c8 * (float)maxK / 255.0 + 0.5), minK, maxK);

    uint bestCode = 0u;
    uint bestUq = 0u;
    int bestErr = 10000;

    [loop] for (int step = 0; step < 3; ++step)
    {
        int dk = (step == 0) ? 0 : ((step == 1) ? -1 : 1);
        int k = kEst + dk;
        if (k < minK || k > maxK) continue;
        uint code = AstcLinearToCode((uint)k, q);
        uint uq = AstcUnquantizeColor(code, q);
        int err = abs((int)uq - (int)c8);
        if (err < bestErr) { bestErr = err; bestCode = code; bestUq = uq; }
    }

    unquantOut = bestUq;
    return bestCode;
}

uint AstcQuantizeColor(uint c8, uint q, out uint unquantOut)
{
    return AstcQuantizeColorRange(c8, q, false, unquantOut);
}

uint AstcQuantizeColorMin128(uint c8, uint q, out uint unquantOut)
{
    return AstcQuantizeColorRange(c8, q, true, unquantOut);
}

// Quantizes a normalized weight w01 in [0, 1] to the exact nearest BISE code and returns unquantized [0, 64].
uint AstcQuantizeWeight(float w01, uint q, out uint unquantOut)
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);

    float w64 = saturate(w01) * 64.0;
    int maxK = (int)rangeVal - 1;
    int kEst = clamp((int)floor(saturate(w01) * (float)maxK + 0.5), 0, maxK);

    uint bestCode = 0u;
    uint bestUq = 0u;
    float bestErr = 1e30;

    [loop] for (int step = 0; step < 3; ++step)
    {
        int dk = (step == 0) ? 0 : ((step == 1) ? -1 : 1);
        int k = kEst + dk;
        if (k < 0 || k > maxK) continue;
        uint code = AstcLinearToCode((uint)k, q);
        uint uq = AstcUnquantizeWeight(code, q);
        float err = abs((float)uq - w64);
        if (err < bestErr) { bestErr = err; bestCode = code; bestUq = uq; }
    }

    unquantOut = bestUq;
    return bestCode;
}

// Writes up to 24 bits into a 128-bit block (little-endian uint4).
void AstcWriteBits128(inout uint4 blk, uint bitPos, uint numBits, uint val)
{
    if (numBits == 0u) return;
    uint mask = (numBits >= 32u) ? 0xFFFFFFFFu : ((1u << numBits) - 1u);
    val &= mask;

    uint wordIdx = bitPos >> 5u;
    uint bitOff = bitPos & 31u;

    uint lo = val << bitOff;
    uint hi = (bitOff > 0u) ? (val >> (32u - bitOff)) : 0u;

    if (wordIdx == 0u)      { blk.x |= lo; if (bitOff + numBits > 32u) blk.y |= hi; }
    else if (wordIdx == 1u) { blk.y |= lo; if (bitOff + numBits > 32u) blk.z |= hi; }
    else if (wordIdx == 2u) { blk.z |= lo; if (bitOff + numBits > 32u) blk.w |= hi; }
    else                    { blk.w |= lo; }
}

// Reverses all 128 bits of a uint4 (places BISE weight stream at MSB descending).
uint4 AstcReverse128(uint4 v)
{
    return uint4(reversebits(v.w), reversebits(v.z), reversebits(v.y), reversebits(v.x));
}

// Encodes up to 64 BISE symbols into a 128-bit block starting at startBit.
void AstcEncodeBise64(inout uint4 dst, uint startBit, uint q, uint count, uint vals[64])
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);
    uint bitPos = startBit;

    if (t == 0u && qu == 0u)
    {
        [loop] for (uint i = 0u; i < count; ++i)
        {
            AstcWriteBits128(dst, bitPos, b, vals[i]);
            bitPos += b;
        }
    }
    else if (t != 0u)
    {
        uint lowMask = (b > 0u) ? ((1u << b) - 1u) : 0u;
        [loop] for (uint i = 0u; i < count; i += 5u)
        {
            uint v0 = vals[i];
            uint v1 = (i + 1u < count) ? vals[i + 1u] : 0u;
            uint v2 = (i + 2u < count) ? vals[i + 2u] : 0u;
            uint v3 = (i + 3u < count) ? vals[i + 3u] : 0u;
            uint v4 = (i + 4u < count) ? vals[i + 4u] : 0u;

            uint T = AstcPack5Trits(v0 >> b, v1 >> b, v2 >> b, v3 >> b, v4 >> b);

            AstcWriteBits128(dst, bitPos, b, v0 & lowMask); bitPos += b;
            AstcWriteBits128(dst, bitPos, 2u, T & 3u);       bitPos += 2u;

            if (i + 1u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v1 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (T >> 2u) & 3u); bitPos += 2u;
            }
            if (i + 2u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v2 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 1u, (T >> 4u) & 1u); bitPos += 1u;
            }
            if (i + 3u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v3 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (T >> 5u) & 3u); bitPos += 2u;
            }
            if (i + 4u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v4 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 1u, (T >> 7u) & 1u); bitPos += 1u;
            }
        }
    }
    else
    {
        uint lowMask = (b > 0u) ? ((1u << b) - 1u) : 0u;
        [loop] for (uint i = 0u; i < count; i += 3u)
        {
            uint v0 = vals[i];
            uint v1 = (i + 1u < count) ? vals[i + 1u] : 0u;
            uint v2 = (i + 2u < count) ? vals[i + 2u] : 0u;

            uint Q = AstcPack3Quints(v0 >> b, v1 >> b, v2 >> b);

            AstcWriteBits128(dst, bitPos, b, v0 & lowMask); bitPos += b;
            AstcWriteBits128(dst, bitPos, 3u, Q & 7u);       bitPos += 3u;

            if (i + 1u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v1 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (Q >> 3u) & 3u); bitPos += 2u;
            }
            if (i + 2u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v2 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (Q >> 5u) & 3u); bitPos += 2u;
            }
        }
    }
}

// Encodes up to 16 endpoint BISE symbols into a 128-bit block starting at startBit.
void AstcEncodeBise16(inout uint4 dst, uint startBit, uint q, uint count, uint vals[16])
{
    uint rangeVal, b, t, qu;
    AstcGetQuantParams(q, rangeVal, b, t, qu);
    uint bitPos = startBit;

    if (t == 0u && qu == 0u)
    {
        [loop] for (uint i = 0u; i < count; ++i)
        {
            AstcWriteBits128(dst, bitPos, b, vals[i]);
            bitPos += b;
        }
    }
    else if (t != 0u)
    {
        uint lowMask = (b > 0u) ? ((1u << b) - 1u) : 0u;
        [loop] for (uint i = 0u; i < count; i += 5u)
        {
            uint v0 = vals[i];
            uint v1 = (i + 1u < count) ? vals[i + 1u] : 0u;
            uint v2 = (i + 2u < count) ? vals[i + 2u] : 0u;
            uint v3 = (i + 3u < count) ? vals[i + 3u] : 0u;
            uint v4 = (i + 4u < count) ? vals[i + 4u] : 0u;

            uint T = AstcPack5Trits(v0 >> b, v1 >> b, v2 >> b, v3 >> b, v4 >> b);

            AstcWriteBits128(dst, bitPos, b, v0 & lowMask); bitPos += b;
            AstcWriteBits128(dst, bitPos, 2u, T & 3u);       bitPos += 2u;

            if (i + 1u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v1 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (T >> 2u) & 3u); bitPos += 2u;
            }
            if (i + 2u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v2 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 1u, (T >> 4u) & 1u); bitPos += 1u;
            }
            if (i + 3u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v3 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (T >> 5u) & 3u); bitPos += 2u;
            }
            if (i + 4u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v4 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 1u, (T >> 7u) & 1u); bitPos += 1u;
            }
        }
    }
    else
    {
        uint lowMask = (b > 0u) ? ((1u << b) - 1u) : 0u;
        [loop] for (uint i = 0u; i < count; i += 3u)
        {
            uint v0 = vals[i];
            uint v1 = (i + 1u < count) ? vals[i + 1u] : 0u;
            uint v2 = (i + 2u < count) ? vals[i + 2u] : 0u;

            uint Q = AstcPack3Quints(v0 >> b, v1 >> b, v2 >> b);

            AstcWriteBits128(dst, bitPos, b, v0 & lowMask); bitPos += b;
            AstcWriteBits128(dst, bitPos, 3u, Q & 7u);       bitPos += 3u;

            if (i + 1u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v1 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (Q >> 3u) & 3u); bitPos += 2u;
            }
            if (i + 2u < count)
            {
                AstcWriteBits128(dst, bitPos, b, v2 & lowMask); bitPos += b;
                AstcWriteBits128(dst, bitPos, 2u, (Q >> 5u) & 3u); bitPos += 2u;
            }
        }
    }
}

// Encodes 2D LDR Void-Extent constant color block (UNorm16 per channel).
uint4 AstcEncodeVoidExtentLDR(uint4 rgba16)
{
    uint w2 = (rgba16.r & 0xFFFFu) | ((rgba16.g & 0xFFFFu) << 16u);
    uint w3 = (rgba16.b & 0xFFFFu) | ((rgba16.a & 0xFFFFu) << 16u);
    return uint4(0xFFFFFDFCu, 0xFFFFFFFFu, w2, w3);
}

// Encodes 2D HDR Void-Extent constant color block (FP16 per channel).
uint4 AstcEncodeVoidExtentHDR(uint4 rgbaF16)
{
    uint w2 = (rgbaF16.r & 0xFFFFu) | ((rgbaF16.g & 0xFFFFu) << 16u);
    uint w3 = (rgbaF16.b & 0xFFFFu) | ((rgbaF16.a & 0xFFFFu) << 16u);
    return uint4(0xFFFFFFFCu, 0xFFFFFFFFu, w2, w3);
}

#endif // ASTC_GPU_BISE_HLSL
