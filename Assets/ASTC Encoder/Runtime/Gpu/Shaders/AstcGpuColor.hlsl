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

// Exact Khronos ASTC Spec CEM 11 (HDR RGB Direct) hardware unpacker.
// Given 6 unquantized 8-bit endpoint values v[0..5], returns hardware-reconstructed LNS endpoints in [0, 65535].
void AstcUnpackHdrRgb(uint v[6], out float3 outE0Lns, out float3 outE1Lns)
{
    uint v0 = v[0];
    uint v1 = v[1];
    uint v2 = v[2];
    uint v3 = v[3];
    uint v4 = v[4];
    uint v5 = v[5];

    uint majcomp = ((v4 & 0x80u) >> 7u) | (((v5 & 0x80u) >> 7u) << 1u);
    if (majcomp == 3u)
    {
        outE0Lns = float3((float)(v0 << 8u), (float)(v2 << 8u), (float)((v4 & 0x7Fu) << 9u));
        outE1Lns = float3((float)(v1 << 8u), (float)(v3 << 8u), (float)((v5 & 0x7Fu) << 9u));
        return;
    }

    uint modeval = ((v1 & 0x80u) >> 7u) | (((v2 & 0x80u) >> 7u) << 1u) | (((v3 & 0x80u) >> 7u) << 2u);

    uint a  = v0 | ((v1 & 0x40u) << 2u);
    uint b0 = v2 & 0x3Fu;
    uint b1 = v3 & 0x3Fu;
    uint c  = v1 & 0x3Fu;
    uint d0 = v4 & 0x1Fu;
    uint d1 = v5 & 0x1Fu;

    uint dbits = ((modeval == 0u || modeval == 2u) ? 7u : ((modeval == 4u || modeval == 6u) ? 5u : 6u));

    uint bit0 = (v2 >> 6u) & 1u;
    uint bit1 = (v3 >> 6u) & 1u;
    uint bit2 = (v4 >> 6u) & 1u;
    uint bit3 = (v5 >> 6u) & 1u;
    uint bit4 = (v4 >> 5u) & 1u;
    uint bit5 = (v5 >> 5u) & 1u;

    uint ohmod = 1u << modeval;
    if ((ohmod & 0xA4u) != 0u) a |= bit0 << 9u;
    if ((ohmod & 0x08u) != 0u) a |= bit2 << 9u;
    if ((ohmod & 0x50u) != 0u) a |= bit4 << 9u;
    if ((ohmod & 0x50u) != 0u) a |= bit5 << 10u;
    if ((ohmod & 0xA0u) != 0u) a |= bit1 << 10u;
    if ((ohmod & 0xC0u) != 0u) a |= bit2 << 11u;

    if ((ohmod & 0x04u) != 0u) c |= bit1 << 6u;
    if ((ohmod & 0xE8u) != 0u) c |= bit3 << 6u;
    if ((ohmod & 0x20u) != 0u) c |= bit2 << 7u;

    if ((ohmod & 0x5Bu) != 0u) { b0 |= bit0 << 6u; b1 |= bit1 << 6u; }
    if ((ohmod & 0x12u) != 0u) { b0 |= bit2 << 7u; b1 |= bit3 << 7u; }

    if ((ohmod & 0xAFu) != 0u) { d0 |= bit4 << 5u; d1 |= bit5 << 5u; }
    if ((ohmod & 0x05u) != 0u) { d0 |= bit2 << 6u; d1 |= bit3 << 6u; }

    // Sign-extend d0 and d1 from dbits
    uint sxShamt = 32u - dbits;
    int d0s = (int)(d0 << sxShamt) >> (int)sxShamt;
    int d1s = (int)(d1 << sxShamt) >> (int)sxShamt;

    uint valShamt = (modeval >> 1u) ^ 3u;
    int ia  = (int)(a  << valShamt);
    int ib0 = (int)(b0 << valShamt);
    int ib1 = (int)(b1 << valShamt);
    int ic  = (int)(c  << valShamt);
    int id0 = d0s * (int)(1u << valShamt);
    int id1 = d1s * (int)(1u << valShamt);

    int r1 = clamp(ia, 0, 4095);
    int g1 = clamp(ia - ib0, 0, 4095);
    int bl1 = clamp(ia - ib1, 0, 4095);
    int r0 = clamp(ia - ic, 0, 4095);
    int g0 = clamp(ia - ib0 - ic - id0, 0, 4095);
    int bl0 = clamp(ia - ib1 - ic - id1, 0, 4095);

    if (majcomp == 1u)
    {
        int t0 = r0; r0 = g0; g0 = t0;
        int t1 = r1; r1 = g1; g1 = t1;
    }
    else if (majcomp == 2u)
    {
        int t0 = r0; r0 = bl0; bl0 = t0;
        int t1 = r1; r1 = bl1; bl1 = t1;
    }

    outE0Lns = float3((float)(r0 << 4), (float)(g0 << 4), (float)(bl0 << 4));
    outE1Lns = float3((float)(r1 << 4), (float)(g1 << 4), (float)(bl1 << 4));
}

// Packs CEM 11 delta mode fields into 6 8-bit endpoint values.
void AstcPackHdrRgbDeltaMode(uint majcomp, uint modeval, uint a, uint b0, uint b1, uint c, uint d0, uint d1, out uint v[6])
{
    uint v0 = a & 0xFFu;
    uint v1 = ((modeval & 1u) << 7u) | (((a >> 8u) & 1u) << 6u) | (c & 0x3Fu);
    uint v2 = (((modeval >> 1u) & 1u) << 7u) | (b0 & 0x3Fu);
    uint v3 = (((modeval >> 2u) & 1u) << 7u) | (b1 & 0x3Fu);
    uint v4 = ((majcomp & 1u) << 7u) | (d0 & 0x1Fu);
    uint v5 = (((majcomp >> 1u) & 1u) << 7u) | (d1 & 0x1Fu);

    uint bit0 = 0u, bit1 = 0u, bit2 = 0u, bit3 = 0u, bit4 = 0u, bit5 = 0u;
    if (modeval == 0u)
    {
        bit0 = (b0 >> 6u) & 1u; bit1 = (b1 >> 6u) & 1u;
        bit2 = (d0 >> 6u) & 1u; bit3 = (d1 >> 6u) & 1u;
        bit4 = (d0 >> 5u) & 1u; bit5 = (d1 >> 5u) & 1u;
    }
    else if (modeval == 1u)
    {
        bit0 = (b0 >> 6u) & 1u; bit1 = (b1 >> 6u) & 1u;
        bit2 = (b0 >> 7u) & 1u; bit3 = (b1 >> 7u) & 1u;
        bit4 = (d0 >> 5u) & 1u; bit5 = (d1 >> 5u) & 1u;
    }
    else if (modeval == 2u)
    {
        bit0 = (a >> 9u) & 1u;  bit1 = (c >> 6u) & 1u;
        bit2 = (d0 >> 6u) & 1u; bit3 = (d1 >> 6u) & 1u;
        bit4 = (d0 >> 5u) & 1u; bit5 = (d1 >> 5u) & 1u;
    }
    else if (modeval == 3u)
    {
        bit0 = (b0 >> 6u) & 1u; bit1 = (b1 >> 6u) & 1u;
        bit2 = (a >> 9u) & 1u;  bit3 = (c >> 6u) & 1u;
        bit4 = (d0 >> 5u) & 1u; bit5 = (d1 >> 5u) & 1u;
    }
    else if (modeval == 4u)
    {
        bit0 = (b0 >> 6u) & 1u; bit1 = (b1 >> 6u) & 1u;
        bit2 = (b0 >> 7u) & 1u; bit3 = (b1 >> 7u) & 1u;
        bit4 = (a >> 9u) & 1u;  bit5 = (a >> 10u) & 1u;
    }
    else if (modeval == 5u)
    {
        bit0 = (a >> 9u) & 1u;  bit1 = (a >> 10u) & 1u;
        bit2 = (c >> 7u) & 1u;  bit3 = (c >> 6u) & 1u;
        bit4 = (d0 >> 5u) & 1u; bit5 = (d1 >> 5u) & 1u;
    }
    else if (modeval == 6u)
    {
        bit0 = (b0 >> 6u) & 1u; bit1 = (b1 >> 6u) & 1u;
        bit2 = (a >> 11u) & 1u; bit3 = (c >> 6u) & 1u;
        bit4 = (a >> 9u) & 1u;  bit5 = (a >> 10u) & 1u;
    }
    else
    {
        bit0 = (a >> 9u) & 1u;  bit1 = (a >> 10u) & 1u;
        bit2 = (a >> 11u) & 1u; bit3 = (c >> 6u) & 1u;
        bit4 = (d0 >> 5u) & 1u; bit5 = (d1 >> 5u) & 1u;
    }

    v2 |= bit0 << 6u;
    v3 |= bit1 << 6u;
    v4 |= (bit2 << 6u) | (bit4 << 5u);
    v5 |= (bit3 << 6u) | (bit5 << 5u);

    v[0] = v0; v[1] = v1; v[2] = v2; v[3] = v3; v[4] = v4; v[5] = v5;
}

// Encodes HDR RGB endpoints e0, e1 (in LNS [0, 65535]) into 6 BISE endpoint codes
// using CEM_HDR_RGB_DIRECT (11): tries high-precision majcomp 0/1/2 delta modes (up to 12-bit)
// and compares exact hardware-reconstructed LNS MSE against majcomp == 3 direct mode.
void AstcQuantizeHdrRgbEndpoints(
    float3 e0Lns,
    float3 e1Lns,
    uint epQuant,
    out uint codes[6],
    out float3 uq0Lns,
    out float3 uq1Lns)
{
    // Baseline: majcomp == 3 direct mode (always valid)
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

    uq0Lns = float3((float)(uqR0 << 8u), (float)(uqG0 << 8u), (float)((uqB0 & 0x7Fu) << 9u));
    uq1Lns = float3((float)(uqR1 << 8u), (float)(uqG1 << 8u), (float)((uqB1 & 0x7Fu) << 9u));

    float3 d0 = uq0Lns - e0Lns;
    float3 d1 = uq1Lns - e1Lns;
    float bestErr = dot(d0, d0) + dot(d1, d1);

    // High-precision majcomp 0/1/2 delta mode requires e1 to hold the overall major component.
    int3 c0_12 = int3(
        clamp((int)floor(e0Lns.r / 16.0 + 0.5), 0, 4095),
        clamp((int)floor(e0Lns.g / 16.0 + 0.5), 0, 4095),
        clamp((int)floor(e0Lns.b / 16.0 + 0.5), 0, 4095));
    int3 c1_12 = int3(
        clamp((int)floor(e1Lns.r / 16.0 + 0.5), 0, 4095),
        clamp((int)floor(e1Lns.g / 16.0 + 0.5), 0, 4095),
        clamp((int)floor(e1Lns.b / 16.0 + 0.5), 0, 4095));

    int max0 = max(c0_12.r, max(c0_12.g, c0_12.b));
    int max1 = max(c1_12.r, max(c1_12.g, c1_12.b));
    if (max1 < max0)
    {
        return;
    }

    uint majcomp = 0u;
    int R0 = c0_12.r, G0 = c0_12.g, B0 = c0_12.b;
    int R1 = c1_12.r, G1 = c1_12.g, B1 = c1_12.b;
    if (c1_12.g >= c1_12.r && c1_12.g >= c1_12.b)
    {
        majcomp = 1u;
        R0 = c0_12.g; G0 = c0_12.r; B0 = c0_12.b;
        R1 = c1_12.g; G1 = c1_12.r; B1 = c1_12.b;
    }
    else if (c1_12.b > c1_12.r && c1_12.b > c1_12.g)
    {
        majcomp = 2u;
        R0 = c0_12.b; G0 = c0_12.g; B0 = c0_12.r;
        R1 = c1_12.b; G1 = c1_12.g; B1 = c1_12.r;
    }

    int A_12  = R1;
    int B0_12 = R1 - G1;
    int B1_12 = R1 - B1;
    int C_12  = R1 - R0;
    int D0_12 = (R0 - G0) - B0_12;
    int D1_12 = (R0 - B0) - B1_12;

    // Try finest resolution modes first (modeval 7..0) and pick the first that fits.
    for (uint mi = 0u; mi < 8u; ++mi)
    {
        uint mv = 7u - mi;
        uint sh = (mv >> 1u) ^ 3u;
        uint bb = (mv == 1u || mv == 4u) ? 8u : ((mv == 0u || mv == 3u || mv == 6u) ? 7u : 6u);
        uint cb = (mv == 5u) ? 8u : ((mv == 2u || mv == 3u || mv == 6u || mv == 7u) ? 7u : 6u);
        uint db = (mv == 0u || mv == 2u) ? 7u : ((mv == 4u || mv == 6u) ? 5u : 6u);

        int halfStep = (sh > 0u) ? (int)(1u << (sh - 1u)) : 0;
        int aS  = min((int)((1u << (12u - sh)) - 1u), (A_12 + halfStep) >> (int)sh);
        int b0S = (B0_12 + halfStep) >> (int)sh;
        int b1S = (B1_12 + halfStep) >> (int)sh;
        int cS  = (C_12 + halfStep) >> (int)sh;
        float invStep = 1.0 / (float)(1u << sh);
        int d0S = (int)floor((float)D0_12 * invStep + 0.5);
        int d1S = (int)floor((float)D1_12 * invStep + 0.5);

        int dMin = -(int)(1u << (db - 1u));
        int dMax = (int)(1u << (db - 1u)) - 1;
        if (b0S >= 0 && b0S < (int)(1u << bb)
            && b1S >= 0 && b1S < (int)(1u << bb)
            && cS >= 0 && cS < (int)(1u << cb)
            && d0S >= dMin && d0S <= dMax
            && d1S >= dMin && d1S <= dMax)
        {
            uint dMask = (1u << db) - 1u;
            uint rawV[6];
            AstcPackHdrRgbDeltaMode(majcomp, mv, (uint)aS, (uint)b0S, (uint)b1S, (uint)cS, ((uint)d0S) & dMask, ((uint)d1S) & dMask, rawV);

            uint candCodes[6];
            uint uqV[6];
            for (uint ci = 0u; ci < 6u; ++ci)
            {
                candCodes[ci] = AstcQuantizeColor(rawV[ci], epQuant, uqV[ci]);
            }

            float3 cand0, cand1;
            AstcUnpackHdrRgb(uqV, cand0, cand1);
            float3 cd0 = cand0 - e0Lns;
            float3 cd1 = cand1 - e1Lns;
            float candErr = dot(cd0, cd0) + dot(cd1, cd1);
            if (candErr < bestErr)
            {
                for (uint cj = 0u; cj < 6u; ++cj) codes[cj] = candCodes[cj];
                uq0Lns = cand0;
                uq1Lns = cand1;
            }
            break;
        }
    }
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
