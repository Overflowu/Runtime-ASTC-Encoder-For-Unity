#ifndef ASTC_GPU_COLOR_HLSL
#define ASTC_GPU_COLOR_HLSL

#include "AstcGpuBise.hlsl"

// ============================================================================
// Color Space & HDR LNS Conversions
// ============================================================================

float AstcLinearToSRGBScalar(float c)
{
    c = saturate(c);
    return (c <= 0.0031308) ? (c * 12.92) : (1.055 * pow(c, 1.0 / 2.4) - 0.055);
}

float3 AstcLinearToSRGB(float3 rgb)
{
    return float3(
        AstcLinearToSRGBScalar(rgb.r),
        AstcLinearToSRGBScalar(rgb.g),
        AstcLinearToSRGBScalar(rgb.b));
}

// Exact Khronos ASTC float -> 16-bit LNS [0, 65535] conversion.
// Negative values and NaNs clamp to 0; values >= 65536 clamp to 65535.
float AstcFloatToLns(float a)
{
    if (!(a > (1.0 / 67108864.0)))
    {
        return 0.0;
    }
    if (a >= 65536.0)
    {
        return 65535.0;
    }

    uint ai = asuint(a);
    int expVal = (int)((ai >> 23u) & 0xFFu) - 126;
    uint mantBits = (ai & 0x807FFFFFu) | 0x3F000000u;
    float mant = asfloat(mantBits);

    float scaled;
    int lnsExp;
    if (expVal < -13)
    {
        scaled = a * 33554432.0;
        lnsExp = 0;
    }
    else
    {
        scaled = (mant - 0.5) * 4096.0;
        lnsExp = expVal + 14;
    }

    float lnsMant;
    if (scaled < 384.0)
    {
        lnsMant = scaled * (4.0 / 3.0);
    }
    else if (scaled <= 1408.0)
    {
        lnsMant = scaled + 128.0;
    }
    else
    {
        lnsMant = (scaled + 512.0) * (4.0 / 5.0);
    }

    return clamp(lnsMant + (float)lnsExp * 2048.0 + 1.0, 0.0, 65535.0);
}

float4 AstcFloatToLns4(float4 c)
{
    return float4(
        AstcFloatToLns(c.r),
        AstcFloatToLns(c.g),
        AstcFloatToLns(c.b),
        AstcFloatToLns(c.a));
}

uint AstcFloatToHalfU16(float x)
{
    // ASTC HDR UFloat profile encodes non-negative FP16 values in [0, 65504].
    float clamped = (x > 0.0) ? min(x, 65504.0) : 0.0;
    return f32tof16(clamped) & 0xFFFFu;
}

// ============================================================================
// ASTC HDR Endpoint Quantization (CEM 11 / CEM 14 / CEM 15)
// ============================================================================

// Encodes HDR RGB endpoints e0, e1 (in LNS [0, 65535]) into 6 BISE endpoint codes
// using CEM_HDR_RGB_DIRECT (11) direct mode (majcomp == 3), returning exact
// hardware-reconstructed LNS endpoints uq0, uq1 in [0, 65535].
void AstcQuantizeHdrRgbEndpoints(
    float3 e0Lns,
    float3 e1Lns,
    uint epQuant,
    out uint codes[6],
    out float3 uq0Lns,
    out float3 uq1Lns)
{
    uint r0 = (uint)clamp(floor(e0Lns.r / 256.0 + 0.5), 0.0, 255.0);
    uint r1 = (uint)clamp(floor(e1Lns.r / 256.0 + 0.5), 0.0, 255.0);
    uint g0 = (uint)clamp(floor(e0Lns.g / 256.0 + 0.5), 0.0, 255.0);
    uint g1 = (uint)clamp(floor(e1Lns.g / 256.0 + 0.5), 0.0, 255.0);
    uint b0 = 128u + (uint)clamp(floor(e0Lns.b / 512.0 + 0.5), 0.0, 127.0);
    uint b1 = 128u + (uint)clamp(floor(e1Lns.b / 512.0 + 0.5), 0.0, 127.0);

    uint uqR0, uqR1, uqG0, uqG1, uqB0, uqB1;
    codes[0] = AstcQuantizeColor(r0, epQuant, uqR0);
    codes[1] = AstcQuantizeColor(r1, epQuant, uqR1);
    codes[2] = AstcQuantizeColor(g0, epQuant, uqG0);
    codes[3] = AstcQuantizeColor(g1, epQuant, uqG1);
    codes[4] = AstcQuantizeColorMin128(b0, epQuant, uqB0);
    codes[5] = AstcQuantizeColorMin128(b1, epQuant, uqB1);

    // Unpacked by hardware as: R = v << 8, G = v << 8, B = (v & 0x7F) << 9
    uq0Lns = float3((float)(uqR0 << 8u), (float)(uqG0 << 8u), (float)((uqB0 & 0x7Fu) << 9u));
    uq1Lns = float3((float)(uqR1 << 8u), (float)(uqG1 << 8u), (float)((uqB1 & 0x7Fu) << 9u));
}

// Encodes HDR Alpha endpoints a0, a1 (in LNS [0, 65535]) for CEM_HDR_RGBA_DIRECT (15)
// using direct modeval == 3 (bit 7 == 1 on both endpoints).
void AstcQuantizeHdrAlphaEndpoints(
    float a0Lns,
    float a1Lns,
    uint epQuant,
    out uint code0,
    out uint code1,
    out float uqA0Lns,
    out float uqA1Lns)
{
    uint a0 = 128u + (uint)clamp(floor(a0Lns / 512.0 + 0.5), 0.0, 127.0);
    uint a1 = 128u + (uint)clamp(floor(a1Lns / 512.0 + 0.5), 0.0, 127.0);

    uint uq0, uq1;
    code0 = AstcQuantizeColorMin128(a0, epQuant, uq0);
    code1 = AstcQuantizeColorMin128(a1, epQuant, uq1);

    uqA0Lns = (float)((uq0 & 0x7Fu) << 9u);
    uqA1Lns = (float)((uq1 & 0x7Fu) << 9u);
}

// ============================================================================
// ASTC 2-Partition Procedural Hash (Khronos Spec Section 22.21)
// ============================================================================

uint AstcHash52(uint inp)
{
    inp ^= inp >> 15u;
    inp *= 0xEEDE0891u;
    inp ^= inp >> 5u;
    inp += inp << 16u;
    inp ^= inp >> 7u;
    inp ^= inp >> 3u;
    inp ^= inp << 6u;
    inp ^= inp >> 17u;
    return inp;
}

// Returns partition index (0 or 1) for 2-partition ASTC block at texel (x, y).
// smallBlock must be true when (blockX * blockY) < 32 (i.e. 4x4 and 5x5).
uint AstcSelectPartition2(uint seed, uint x, uint y, bool smallBlock)
{
    if (smallBlock)
    {
        x <<= 1u;
        y <<= 1u;
    }

    uint s = seed + 1024u;
    uint rnum = AstcHash52(s);

    uint seed1 = (rnum & 0xFu);        seed1 *= seed1;
    uint seed2 = ((rnum >> 4u) & 0xFu); seed2 *= seed2;
    uint seed3 = ((rnum >> 8u) & 0xFu); seed3 *= seed3;
    uint seed4 = ((rnum >> 12u) & 0xFu); seed4 *= seed4;

    uint sh1, sh2;
    if ((s & 1u) != 0u)
    {
        sh1 = ((s & 2u) != 0u) ? 4u : 5u;
        sh2 = 5u;
    }
    else
    {
        sh1 = 5u;
        sh2 = ((s & 2u) != 0u) ? 4u : 5u;
    }

    seed1 >>= sh1;
    seed2 >>= sh2;
    seed3 >>= sh1;
    seed4 >>= sh2;

    uint a = (seed1 * x + seed2 * y + (rnum >> 14u)) & 0x3Fu;
    uint b = (seed3 * x + seed4 * y + (rnum >> 10u)) & 0x3Fu;

    return (b > a) ? 1u : 0u;
}

// ============================================================================
// ASTC Hardware Bilinear Weight Grid Infill (Khronos Spec Section 22.17)
// ============================================================================

// Computes bilinear grid coordinates (js, jt) and 4 integer weights (w00, w10, w01, w11)
// summing to 16 for texel (x, y) in a (blockX, blockY) block using a (gridW, gridH) weight grid.
void AstcComputeInfillTap(
    uint x, uint y,
    uint blockX, uint blockY,
    uint gridW, uint gridH,
    out uint js, out uint jt,
    out uint4 tapWeights)
{
    uint ds = (1024u + (blockX >> 1u)) / (blockX - 1u);
    uint dt = (1024u + (blockY >> 1u)) / (blockY - 1u);
    uint cs = ds * x;
    uint ct = dt * y;

    uint gs = (cs * (gridW - 1u) + 32u) >> 6u;
    uint gt = (ct * (gridH - 1u) + 32u) >> 6u;

    js = gs >> 4u;
    jt = gt >> 4u;
    uint fs = gs & 15u;
    uint ft = gt & 15u;

    uint w11 = (fs * ft + 8u) >> 4u;
    uint w10 = fs - w11;
    uint w01 = ft - w11;
    uint w00 = 16u - fs - ft + w11;

    tapWeights = uint4(w00, w10, w01, w11);
}

// Evaluates the exact ASTC hardware interpolated weight [0, 64] at texel (x, y)
// from a local unquantized grid array gridUq[36] without passing the array by value.
#define ASTC_EVAL_INFILL(gridUq, tx, ty, bx, by, gw, gh, outWeight64)                  \
    if ((gw) == (bx) && (gh) == (by))                                                  \
    {                                                                                  \
        (outWeight64) = (gridUq)[(ty) * (gw) + (tx)];                                  \
    }                                                                                  \
    else                                                                               \
    {                                                                                  \
        uint _js, _jt;                                                                 \
        uint4 _tw;                                                                     \
        AstcComputeInfillTap((tx), (ty), (bx), (by), (gw), (gh), _js, _jt, _tw);       \
        uint _idx00 = _jt * (gw) + _js;                                                \
        uint _p00 = (gridUq)[_idx00];                                                  \
        uint _p10 = (_tw.y > 0u) ? (gridUq)[_idx00 + 1u] : 0u;                         \
        uint _p01 = (_tw.z > 0u) ? (gridUq)[_idx00 + (gw)] : 0u;                       \
        uint _p11 = (_tw.w > 0u) ? (gridUq)[_idx00 + (gw) + 1u] : 0u;                  \
        (outWeight64) = (_p00 * _tw.x + _p10 * _tw.y + _p01 * _tw.z + _p11 * _tw.w + 8u) >> 4u; \
    }

#endif // ASTC_GPU_COLOR_HLSL
