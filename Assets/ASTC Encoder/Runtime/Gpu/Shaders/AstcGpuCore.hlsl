#ifndef ASTC_GPU_CORE_HLSL
#define ASTC_GPU_CORE_HLSL

#include "AstcGpuColor.hlsl"

#define ASTC_MODE_LDR_RGB     0u
#define ASTC_MODE_LDR_RGBA    1u
#define ASTC_MODE_LDR_NORMAL  2u
#define ASTC_MODE_HDR_RGB     3u
#define ASTC_MODE_HDR_RGBA    4u

// Internal effective sub-modes resolved per block (HDR RGBA can auto-select CEM 11/14/15)
#define ASTC_EFF_LDR_RGB           0u // CEM 8  (6 ep)
#define ASTC_EFF_LDR_RGBA          1u // CEM 12 (8 ep)
#define ASTC_EFF_LDR_NORMAL        2u // CEM 4  (4 ep: L=G, A=R)
#define ASTC_EFF_HDR_RGB           3u // CEM 11 (6 ep: HDR RGB)
#define ASTC_EFF_HDR_RGB_LDR_ALPHA 4u // CEM 14 (8 ep: HDR RGB + LDR A)
#define ASTC_EFF_HDR_RGBA          5u // CEM 15 (8 ep: HDR RGB + HDR A)

struct AstcCandidate
{
    uint blockMode;
    uint gridW;
    uint gridH;
    uint wQuant;
    uint epQuant;
    uint isDualPlane;
    uint dualPlaneChannel;
    uint isTwoPart;
    uint partSeed;
    float error;
    float4 e0;
    float4 e1;
    float4 e0_p1;
    float4 e1_p1;
};

#if BLOCK_X <= 4
#define ASTC_MAX_GRID   16
#define ASTC_MAX_GRID_U 16u
#elif BLOCK_X <= 5
#define ASTC_MAX_GRID   25
#define ASTC_MAX_GRID_U 25u
#else
#define ASTC_MAX_GRID   36
#define ASTC_MAX_GRID_U 36u
#endif

// Shared per-thread scratch arrays: eliminates per-call-site local indexableTemp arrays
// and array-by-value copies across functions (prevents FXC/HLSLcc/DXC compile timeouts).
static float g_AstcGrid0[ASTC_MAX_GRID];
static float g_AstcGrid1[ASTC_MAX_GRID];
static float g_AstcDecimNum[ASTC_MAX_GRID];
static float g_AstcDecimDen[ASTC_MAX_GRID];
static uint  g_AstcGridUq0[ASTC_MAX_GRID];
static uint  g_AstcGridUq1[ASTC_MAX_GRID];
static uint  g_AstcEpCodes[16];
static uint  g_AstcCandEpCodes[16];
static uint  g_AstcEpVals[8];
static uint  g_AstcEpCArr[8];
static uint  g_AstcEpUqArr[8];

// On-chip per-thread packed 4x16-bit working-pixel cache for small blocks (4x4, 5x5, 6x6 => 16..36 uint2s).
// Eliminates redundant texture loads, sRGB, and HDR LNS conversions across all 29 fetch call sites
// while keeping VGPR usage bounded for larger 8x8..12x12 blocks.
#if (BLOCK_X * BLOCK_Y) <= 36
#define ASTC_ENABLE_PIXEL_CACHE 1
static uint2 g_AstcPackedBlock[BLOCK_X * BLOCK_Y];

uint2 AstcPackRgba16(float4 v)
{
    uint4 u = (uint4)clamp(floor(v + 0.5), 0.0, 65535.0);
    return uint2(u.x | (u.y << 16u), u.z | (u.w << 16u));
}
#else
#define ASTC_ENABLE_PIXEL_CACHE 0
#endif

// Loads and converts one texel from _AstcSourceTex into working space (single shared sRGB + LNS path).
float4 AstcLoadWorkingPixel(uint baseX, uint baseY, uint x, uint y, uint effMode, out float impWeight)
{
    impWeight = 1.0;

    uint px = min(baseX + x, _AstcSrcWidth - 1u);
    uint py = min(baseY + y, _AstcSrcHeight - 1u);
    float4 raw = _AstcSourceTex.Load(int3(px, py, _AstcMipLevel));

    if (effMode <= ASTC_EFF_LDR_NORMAL)
    {
        float3 rgb = (_AstcIsSRGB != 0u) ? AstcLinearToSRGB(raw.rgb) : saturate(raw.rgb);
        if (effMode == ASTC_EFF_LDR_NORMAL)
        {
            return float4(rgb.g * 255.0, 0.0, 0.0, rgb.r * 255.0);
        }
        float a = (effMode == ASTC_EFF_LDR_RGB) ? 1.0 : saturate(raw.a);
        if (effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u)
        {
            impWeight = max(a, 0.02);
        }
        return float4(rgb * 255.0, a * 255.0);
    }

    // HDR modes: share a single branchless vectorized LNS conversion
    float4 lns = AstcFloatToLns4(raw);
    if (effMode == ASTC_EFF_HDR_RGB)
    {
        return float4(lns.rgb, 65535.0);
    }
    if (effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA)
    {
        // Scale LDR alpha [0,1] into [0, 65535] to balance vector norm with LNS RGB.
        return float4(lns.rgb, saturate(raw.a) * 65535.0);
    }

    // ASTC_EFF_HDR_RGBA
    return lns;
}

// Fetches one texel in the encoder's working space for the current effective mode.
float4 AstcFetchWorkingPixel(uint baseX, uint baseY, uint x, uint y, uint effMode, out float impWeight)
{
#if ASTC_ENABLE_PIXEL_CACHE
    impWeight = 1.0;
    uint2 packed = g_AstcPackedBlock[y * BLOCK_X + x];
    float4 cached = float4(
        (float)(packed.x & 0xFFFFu),
        (float)(packed.x >> 16u),
        (float)(packed.y & 0xFFFFu),
        (float)(packed.y >> 16u));
    if (effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u)
    {
        impWeight = max(cached.a * (1.0 / 255.0), 0.02);
    }
    return cached;
#else
    return AstcLoadWorkingPixel(baseX, baseY, x, y, effMode, impWeight);
#endif
}

// Per-channel importance weight: weights RGB by alpha when UseAlphaWeight is active,
// while always keeping Alpha at full 1.0 weight so transparent edges stay accurate.
float4 AstcGetChannelImp(uint effMode, float imp)
{
    return (effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u)
        ? float4(imp, imp, imp, 1.0)
        : float4(1.0, 1.0, 1.0, 1.0);
}

float4 AstcClampWorkingColor(float4 c, uint effMode)
{
    float maxVal = (effMode >= ASTC_EFF_HDR_RGB) ? 65535.0 : 255.0;
    return clamp(c, 0.0, maxVal);
}

uint AstcGetCemForEffMode(uint effMode)
{
    if (effMode == ASTC_EFF_LDR_NORMAL)        return ASTC_CEM_LDR_LUM_ALPHA_DIRECT;
    if (effMode == ASTC_EFF_LDR_RGB)           return ASTC_CEM_LDR_RGB_DIRECT;
    if (effMode == ASTC_EFF_LDR_RGBA)          return ASTC_CEM_LDR_RGBA_DIRECT;
    if (effMode == ASTC_EFF_HDR_RGB)           return ASTC_CEM_HDR_RGB_DIRECT;
    if (effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA) return ASTC_CEM_HDR_RGB_LDR_ALPHA;
    return ASTC_CEM_HDR_RGBA_DIRECT;
}

uint AstcGetEpCountForEffMode(uint effMode)
{
    if (effMode == ASTC_EFF_LDR_NORMAL) return 4u;
    if (effMode == ASTC_EFF_LDR_RGB || effMode == ASTC_EFF_HDR_RGB) return 6u;
    return 8u;
}

float AstcGetChannel(float4 v, uint ch)
{
    if (ch == 0u) return v.r;
    if (ch == 1u) return v.g;
    if (ch == 2u) return v.b;
    return v.a;
}

float4 AstcSetChannel(float4 v, uint ch, float val)
{
    if (ch == 0u) v.r = val;
    else if (ch == 1u) v.g = val;
    else if (ch == 2u) v.b = val;
    else v.a = val;
    return v;
}

// Channel mask for 1-plane or dual-plane (plane 0) calculations.
// excludedChannel == 4u means no channel is excluded.
float4 AstcChannelMaskEx(uint effMode, uint excludedChannel)
{
    float4 m;
    if (effMode == ASTC_EFF_LDR_NORMAL)
    {
        m = float4(1.0, 0.0, 0.0, 1.0);
    }
    else if (effMode == ASTC_EFF_LDR_RGB || effMode == ASTC_EFF_HDR_RGB)
    {
        m = float4(1.0, 1.0, 1.0, 0.0);
    }
    else
    {
        m = float4(1.0, 1.0, 1.0, 1.0);
    }

    if (excludedChannel < 4u)
    {
        m = AstcSetChannel(m, excludedChannel, 0.0);
    }
    return m;
}

// Quantizes one partition's endpoints (e0, e1) according to effMode and epQuant.
// Writes codes into g_AstcEpCodes[codeOffset..] and returns exact hardware-reconstructed uqE0, uqE1.
void AstcQuantizeEndpoints(
    float4 e0,
    float4 e1,
    uint effMode,
    uint epQuant,
    uint codeOffset,
    out float4 uqE0,
    out float4 uqE1)
{
    uqE0 = float4(0.0, 0.0, 0.0, 255.0);
    uqE1 = float4(0.0, 0.0, 0.0, 255.0);

    if (effMode == ASTC_EFF_LDR_NORMAL)
    {
        g_AstcEpVals[0] = (uint)clamp(floor(e0.x + 0.5), 0.0, 255.0);
        g_AstcEpVals[1] = (uint)clamp(floor(e1.x + 0.5), 0.0, 255.0);
        g_AstcEpVals[2] = (uint)clamp(floor(e0.w + 0.5), 0.0, 255.0);
        g_AstcEpVals[3] = (uint)clamp(floor(e1.w + 0.5), 0.0, 255.0);

        [loop] for (uint i = 0u; i < 4u; ++i)
        {
            uint uqTmp;
            g_AstcEpCodes[codeOffset + i] = AstcQuantizeColor(g_AstcEpVals[i], epQuant, uqTmp);
            g_AstcEpUqArr[i] = uqTmp;
        }

        uqE0 = float4((float)g_AstcEpUqArr[0], 0.0, 0.0, (float)g_AstcEpUqArr[2]);
        uqE1 = float4((float)g_AstcEpUqArr[1], 0.0, 0.0, (float)g_AstcEpUqArr[3]);
        return;
    }

    if (effMode == ASTC_EFF_LDR_RGB || effMode == ASTC_EFF_LDR_RGBA)
    {
        g_AstcEpVals[0] = (uint)clamp(floor(e0.r + 0.5), 0.0, 255.0);
        g_AstcEpVals[1] = (uint)clamp(floor(e1.r + 0.5), 0.0, 255.0);
        g_AstcEpVals[2] = (uint)clamp(floor(e0.g + 0.5), 0.0, 255.0);
        g_AstcEpVals[3] = (uint)clamp(floor(e1.g + 0.5), 0.0, 255.0);
        g_AstcEpVals[4] = (uint)clamp(floor(e0.b + 0.5), 0.0, 255.0);
        g_AstcEpVals[5] = (uint)clamp(floor(e1.b + 0.5), 0.0, 255.0);
        g_AstcEpVals[6] = (uint)clamp(floor(e0.a + 0.5), 0.0, 255.0);
        g_AstcEpVals[7] = (uint)clamp(floor(e1.a + 0.5), 0.0, 255.0);

        [loop] for (uint z = 0u; z < 8u; ++z) { g_AstcEpCArr[z] = 0u; g_AstcEpUqArr[z] = 255u; }

        uint cnt = (effMode == ASTC_EFF_LDR_RGBA) ? 8u : 6u;
        [loop] for (uint i = 0u; i < cnt; ++i)
        {
            uint uqTmp;
            g_AstcEpCArr[i] = AstcQuantizeColor(g_AstcEpVals[i], epQuant, uqTmp);
            g_AstcEpUqArr[i] = uqTmp;
        }

        // Khronos ASTC spec: CEM 8/12 triggers blue-contraction & endpoint swap whenever (r1+g1+b1) < (r0+g0+b0).
        // Guarantee s1 >= s0 so hardware always decodes direct endpoints without unexpected contraction/inversion.
        if ((g_AstcEpUqArr[0] + g_AstcEpUqArr[2] + g_AstcEpUqArr[4]) > (g_AstcEpUqArr[1] + g_AstcEpUqArr[3] + g_AstcEpUqArr[5]))
        {
            [loop] for (uint s = 0u; s < 8u; s += 2u)
            {
                uint tc = g_AstcEpCArr[s]; g_AstcEpCArr[s] = g_AstcEpCArr[s + 1u]; g_AstcEpCArr[s + 1u] = tc;
                uint tu = g_AstcEpUqArr[s]; g_AstcEpUqArr[s] = g_AstcEpUqArr[s + 1u]; g_AstcEpUqArr[s + 1u] = tu;
            }
        }

        [loop] for (uint k = 0u; k < cnt; ++k)
        {
            g_AstcEpCodes[codeOffset + k] = g_AstcEpCArr[k];
        }

        uqE0 = float4((float)g_AstcEpUqArr[0], (float)g_AstcEpUqArr[2], (float)g_AstcEpUqArr[4], (float)g_AstcEpUqArr[6]);
        uqE1 = float4((float)g_AstcEpUqArr[1], (float)g_AstcEpUqArr[3], (float)g_AstcEpUqArr[5], (float)g_AstcEpUqArr[7]);
        return;
    }

    // HDR RGB / HDR RGB + LDR Alpha / HDR RGBA
    // Orient endpoints so e1 holds the major RGB component, enabling CEM 11 high-precision delta modes.
    float maxRgb0 = max(e0.r, max(e0.g, e0.b));
    float maxRgb1 = max(e1.r, max(e1.g, e1.b));
    if (maxRgb0 > maxRgb1)
    {
        float4 tmp = e0; e0 = e1; e1 = tmp;
    }

    float3 uqRgb0, uqRgb1;
    AstcQuantizeHdrRgbEndpoints(e0.rgb, e1.rgb, epQuant, uqRgb0, uqRgb1);
    [loop] for (uint hi = 0u; hi < 6u; ++hi)
    {
        g_AstcEpCodes[codeOffset + hi] = g_AstcHdrCodes[hi];
    }

    uqE0 = float4(uqRgb0, 65535.0);
    uqE1 = float4(uqRgb1, 65535.0);

    if (effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA || effMode == ASTC_EFF_HDR_RGBA)
    {
        bool isHdrA = (effMode == ASTC_EFF_HDR_RGBA);
        uint aIn0 = isHdrA
            ? (128u + (uint)clamp(floor(e0.a / 512.0 + 0.5), 0.0, 127.0))
            : (uint)clamp(floor(e0.a / 257.0 + 0.5), 0.0, 255.0);
        uint aIn1 = isHdrA
            ? (128u + (uint)clamp(floor(e1.a / 512.0 + 0.5), 0.0, 127.0))
            : (uint)clamp(floor(e1.a / 257.0 + 0.5), 0.0, 255.0);
        uint uqA0 = 0u, uqA1 = 0u;
        [loop] for (uint ai = 0u; ai < 2u; ++ai)
        {
            uint uqTmp;
            g_AstcEpCodes[codeOffset + 6u + ai] = AstcQuantizeColorRange((ai == 0u) ? aIn0 : aIn1, epQuant, isHdrA, uqTmp);
            if (ai == 0u) uqA0 = uqTmp; else uqA1 = uqTmp;
        }
        uqE0.a = isHdrA ? (float)((uqA0 & 0x7Fu) << 9u) : (float)(uqA0 * 257u);
        uqE1.a = isHdrA ? (float)((uqA1 & 0x7Fu) << 9u) : (float)(uqA1 * 257u);
    }
}

// Fits principal-axis endpoints for a single subset (partFilter == 2 means all texels).
// excludedChannel == 4u means all active channels participate; 0..3 excludes that channel for Dual-Plane plane 0.
void AstcFitSubsetEndpoints(
    uint baseX, uint baseY,
    uint effMode,
    uint excludedChannel,
    uint partSeed,
    uint partFilter,
    uint refineIters,
    out float4 outE0,
    out float4 outE1)
{
    float4 mask = AstcChannelMaskEx(effMode, excludedChannel);
    bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;

    // Pass 1: per-channel weighted mean & bounds
    float4 sumP = 0.0;
    float4 sumW = 0.0;
    float4 minC = float4(1e9, 1e9, 1e9, 1e9);
    float4 maxC = float4(-1e9, -1e9, -1e9, -1e9);

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            if (partFilter < 2u && AstcSelectPartition2(partSeed, x, y, smallBlock) != partFilter)
                continue;

            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            sumP += p * chImp;
            sumW += chImp;
            minC = min(minC, p);
            maxC = max(maxC, p);
        }
    }

    if (sumW.a <= 1e-6)
    {
        outE0 = 0.0;
        outE1 = 0.0;
        return;
    }

    float4 meanP = sumP / max(sumW, 1e-6);

    // Special fast exact path for 1D channels (Normal plane0 = L only)
    if (effMode == ASTC_EFF_LDR_NORMAL && excludedChannel == 3u)
    {
        outE0 = float4(minC.x, 0.0, 0.0, minC.w);
        outE1 = float4(maxC.x, 0.0, 0.0, maxC.w);
        return;
    }

    // Pass 2: MaxAccum principal axis (ARM astcenc style)
    float4 acc0 = 0.0, acc1 = 0.0, acc2 = 0.0, acc3 = 0.0;
    [loop] for (uint y2 = 0u; y2 < BLOCK_Y; ++y2)
    {
        [loop] for (uint x2 = 0u; x2 < BLOCK_X; ++x2)
        {
            if (partFilter < 2u && AstcSelectPartition2(partSeed, x2, y2, smallBlock) != partFilter)
                continue;

            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x2, y2, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            float4 d = (p - meanP) * mask * chImp;
            acc0 += (d.x >= 0.0) ? d : -d;
            acc1 += (d.y >= 0.0) ? d : -d;
            acc2 += (d.z >= 0.0) ? d : -d;
            acc3 += (d.w >= 0.0) ? d : -d;
        }
    }

    float l0 = dot(acc0, acc0);
    float l1 = dot(acc1, acc1);
    float l2 = dot(acc2, acc2);
    float l3 = dot(acc3, acc3);

    float4 axis = acc0;
    float bestL = l0;
    if (l1 > bestL) { bestL = l1; axis = acc1; }
    if (l2 > bestL) { bestL = l2; axis = acc2; }
    if (l3 > bestL) { bestL = l3; axis = acc3; }

    if (bestL <= 1e-8)
    {
        outE0 = AstcClampWorkingColor(minC, effMode);
        outE1 = AstcClampWorkingColor(maxC, effMode);
        return;
    }

    axis *= rsqrt(bestL);

    // Pass 3: Project onto principal axis for initial [minT, maxT] endpoints
    float minT = 1e9;
    float maxT = -1e9;
    [loop] for (uint y3 = 0u; y3 < BLOCK_Y; ++y3)
    {
        [loop] for (uint x3 = 0u; x3 < BLOCK_X; ++x3)
        {
            if (partFilter < 2u && AstcSelectPartition2(partSeed, x3, y3, smallBlock) != partFilter)
                continue;

            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x3, y3, effMode, imp);
            float t = dot((p - meanP) * mask, axis);
            minT = min(minT, t);
            maxT = max(maxT, t);
        }
    }

    float4 e0 = AstcClampWorkingColor(meanP + axis * minT, effMode);
    float4 e1 = AstcClampWorkingColor(meanP + axis * maxT, effMode);

#if !defined(ASTC_FAST_ONLY)
    // Optional least-squares endpoint refinement with separate RGB (imp) and Alpha (1.0) normal equations
    [loop] for (uint iter = 0u; iter < refineIters; ++iter)
    {
        float4 dir = (e1 - e0) * mask;
        if (dot(dir, dir) <= 1e-6) break;

        float a00_rgb = 0.0, a01_rgb = 0.0, a11_rgb = 0.0;
        float a00_a   = 0.0, a01_a   = 0.0, a11_a   = 0.0;
        float4 b0 = 0.0, b1 = 0.0;

        [loop] for (uint yr = 0u; yr < BLOCK_Y; ++yr)
        {
            [loop] for (uint xr = 0u; xr < BLOCK_X; ++xr)
            {
                if (partFilter < 2u && AstcSelectPartition2(partSeed, xr, yr, smallBlock) != partFilter)
                    continue;

                float imp;
                float4 p = AstcFetchWorkingPixel(baseX, baseY, xr, yr, effMode, imp);
                float4 chImp = AstcGetChannelImp(effMode, imp);
                float4 wDir = dir * chImp;
                float denom = dot(wDir, dir);
                float w = (denom > 1e-6) ? saturate(dot((p - e0) * mask, wDir) / denom) : 0.0;
                float omw = 1.0 - w;

                float wRgb = chImp.x;
                a00_rgb += wRgb * omw * omw;
                a01_rgb += wRgb * w * omw;
                a11_rgb += wRgb * w * w;

                a00_a += omw * omw;
                a01_a += w * omw;
                a11_a += w * w;

                b0 += p * (chImp * omw);
                b1 += p * (chImp * w);
            }
        }

        float detRgb = a00_rgb * a11_rgb - a01_rgb * a01_rgb;
        if (detRgb > 1e-4)
        {
            float invDetRgb = 1.0 / detRgb;
            float3 newE0Rgb = (a11_rgb * b0.rgb - a01_rgb * b1.rgb) * invDetRgb;
            float3 newE1Rgb = (a00_rgb * b1.rgb - a01_rgb * b0.rgb) * invDetRgb;
            e0.rgb = lerp(e0.rgb, AstcClampWorkingColor(float4(newE0Rgb, 0.0), effMode).rgb, mask.rgb);
            e1.rgb = lerp(e1.rgb, AstcClampWorkingColor(float4(newE1Rgb, 0.0), effMode).rgb, mask.rgb);
        }

        float detA = a00_a * a11_a - a01_a * a01_a;
        if (mask.a > 0.0 && detA > 1e-4)
        {
            float invDetA = 1.0 / detA;
            float newE0A = (a11_a * b0.a - a01_a * b1.a) * invDetA;
            float newE1A = (a00_a * b1.a - a01_a * b0.a) * invDetA;
            e0.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, newE0A), effMode).a;
            e1.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, newE1A), effMode).a;
        }
    }
#endif

    if (excludedChannel < 4u)
    {
        e0 = AstcSetChannel(e0, excludedChannel, AstcGetChannel(minC, excludedChannel));
        e1 = AstcSetChannel(e1, excludedChannel, AstcGetChannel(maxC, excludedChannel));
    }

    outE0 = e0;
    outE1 = e1;
}

// Projects block pixels onto (e0, e1) and decimates onto g_AstcGrid0 (gridW x gridH).
void AstcComputeWeightGrid1P(
    uint baseX, uint baseY,
    uint effMode,
    uint excludedChannel,
    float4 e0, float4 e1,
    uint gridW, uint gridH)
{
    uint gridCount = gridW * gridH;
    [loop] for (uint i = 0u; i < ASTC_MAX_GRID_U; ++i)
    {
        g_AstcGrid0[i] = 0.0;
        g_AstcDecimNum[i] = 0.0;
        g_AstcDecimDen[i] = 0.0;
    }

    float4 mask = AstcChannelMaskEx(effMode, excludedChannel);
    float4 dir = (e1 - e0) * mask;
    bool exactGrid = (gridW == BLOCK_X && gridH == BLOCK_Y);
    bool alphaWeightsRgbOnly = (mask.a == 0.0 && effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u);

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            float4 wDir = dir * chImp;
            float denom = dot(wDir, dir);
            float t = (denom > 1e-6) ? saturate(dot((p - e0) * mask, wDir) / denom) : 0.0;

            if (exactGrid)
            {
                g_AstcGrid0[y * gridW + x] = t;
            }
            else
            {
                uint js, jt;
                uint4 tw;
                AstcComputeInfillTap(x, y, BLOCK_X, BLOCK_Y, gridW, gridH, js, jt, tw);
                uint idx00 = jt * gridW + js;
                float decW = alphaWeightsRgbOnly ? imp : 1.0;
                float f00 = (float)tw.x * decW;
                float f10 = (float)tw.y * decW;
                float f01 = (float)tw.z * decW;
                float f11 = (float)tw.w * decW;

                g_AstcDecimNum[idx00] += f00 * t; g_AstcDecimDen[idx00] += f00;
                if (tw.y > 0u) { g_AstcDecimNum[idx00 + 1u] += f10 * t;         g_AstcDecimDen[idx00 + 1u] += f10; }
                if (tw.z > 0u) { g_AstcDecimNum[idx00 + gridW] += f01 * t;      g_AstcDecimDen[idx00 + gridW] += f01; }
                if (tw.w > 0u) { g_AstcDecimNum[idx00 + gridW + 1u] += f11 * t; g_AstcDecimDen[idx00 + gridW + 1u] += f11; }
            }
        }
    }

    if (!exactGrid)
    {
        [loop] for (uint k = 0u; k < gridCount; ++k)
        {
            g_AstcGrid0[k] = (g_AstcDecimDen[k] > 1e-6) ? saturate(g_AstcDecimNum[k] / g_AstcDecimDen[k]) : 0.0;
        }
    }
}

// Projects a single channel (plane 1, CCS in 0..3) onto [c0, c1] and decimates onto g_AstcGrid1 (gridW x gridH).
void AstcComputePlane1WeightGrid(
    uint baseX, uint baseY,
    uint effMode,
    uint plane1Channel,
    float c0, float c1,
    uint gridW, uint gridH)
{
    uint gridCount = gridW * gridH;
    [loop] for (uint i = 0u; i < ASTC_MAX_GRID_U; ++i)
    {
        g_AstcGrid1[i] = 0.0;
        g_AstcDecimNum[i] = 0.0;
        g_AstcDecimDen[i] = 0.0;
    }

    float delta = c1 - c0;
    float invDelta = (abs(delta) > 1e-6) ? (1.0 / delta) : 0.0;
    bool exactGrid = (gridW == BLOCK_X && gridH == BLOCK_Y);
    bool weightByAlpha = (plane1Channel < 3u && effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u);

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float pv = AstcGetChannel(p, plane1Channel);
            float t = (abs(delta) > 1e-6) ? saturate((pv - c0) * invDelta) : 0.0;

            if (exactGrid)
            {
                g_AstcGrid1[y * gridW + x] = t;
            }
            else
            {
                uint js, jt;
                uint4 tw;
                AstcComputeInfillTap(x, y, BLOCK_X, BLOCK_Y, gridW, gridH, js, jt, tw);
                uint idx00 = jt * gridW + js;
                float decW = weightByAlpha ? imp : 1.0;
                float f00 = (float)tw.x * decW;
                float f10 = (float)tw.y * decW;
                float f01 = (float)tw.z * decW;
                float f11 = (float)tw.w * decW;

                g_AstcDecimNum[idx00] += f00 * t; g_AstcDecimDen[idx00] += f00;
                if (tw.y > 0u) { g_AstcDecimNum[idx00 + 1u] += f10 * t;         g_AstcDecimDen[idx00 + 1u] += f10; }
                if (tw.z > 0u) { g_AstcDecimNum[idx00 + gridW] += f01 * t;      g_AstcDecimDen[idx00 + gridW] += f01; }
                if (tw.w > 0u) { g_AstcDecimNum[idx00 + gridW + 1u] += f11 * t; g_AstcDecimDen[idx00 + gridW + 1u] += f11; }
            }
        }
    }

    if (!exactGrid)
    {
        [loop] for (uint k = 0u; k < gridCount; ++k)
        {
            g_AstcGrid1[k] = (g_AstcDecimDen[k] > 1e-6) ? saturate(g_AstcDecimNum[k] / g_AstcDecimDen[k]) : 0.0;
        }
    }
}

#if !defined(ASTC_FAST_ONLY)

// Projects 2-partition block pixels onto partition 0/1 endpoints and decimates onto g_AstcGrid0 (gridW x gridH).
void AstcComputeWeightGrid2P(
    uint baseX, uint baseY,
    uint effMode,
    uint partSeed,
    float4 e0_p0, float4 e1_p0,
    float4 e0_p1, float4 e1_p1,
    uint gridW, uint gridH)
{
    uint gridCount = gridW * gridH;
    [loop] for (uint i = 0u; i < ASTC_MAX_GRID_U; ++i)
    {
        g_AstcGrid0[i] = 0.0;
        g_AstcDecimNum[i] = 0.0;
        g_AstcDecimDen[i] = 0.0;
    }

    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float4 dir0 = (e1_p0 - e0_p0) * mask;
    float4 dir1 = (e1_p1 - e0_p1) * mask;

    bool exactGrid = (gridW == BLOCK_X && gridH == BLOCK_Y);
    bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;
    AstcPart2Params p2 = AstcPreparePartition2(partSeed, smallBlock);

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint part = AstcEvalPartition2(p2, x, y);
            float t;
            if (part == 0u)
            {
                float4 wDir0 = dir0 * chImp;
                float denom0 = dot(wDir0, dir0);
                t = (denom0 > 1e-6) ? saturate(dot((p - e0_p0) * mask, wDir0) / denom0) : 0.0;
            }
            else
            {
                float4 wDir1 = dir1 * chImp;
                float denom1 = dot(wDir1, dir1);
                t = (denom1 > 1e-6) ? saturate(dot((p - e0_p1) * mask, wDir1) / denom1) : 0.0;
            }

            if (exactGrid)
            {
                g_AstcGrid0[y * gridW + x] = t;
            }
            else
            {
                uint js, jt;
                uint4 tw;
                AstcComputeInfillTap(x, y, BLOCK_X, BLOCK_Y, gridW, gridH, js, jt, tw);
                uint idx00 = jt * gridW + js;
                float f00 = (float)tw.x;
                float f10 = (float)tw.y;
                float f01 = (float)tw.z;
                float f11 = (float)tw.w;

                g_AstcDecimNum[idx00] += f00 * t; g_AstcDecimDen[idx00] += f00;
                if (tw.y > 0u) { g_AstcDecimNum[idx00 + 1u] += f10 * t;         g_AstcDecimDen[idx00 + 1u] += f10; }
                if (tw.z > 0u) { g_AstcDecimNum[idx00 + gridW] += f01 * t;      g_AstcDecimDen[idx00 + gridW] += f01; }
                if (tw.w > 0u) { g_AstcDecimNum[idx00 + gridW + 1u] += f11 * t; g_AstcDecimDen[idx00 + gridW + 1u] += f11; }
            }
        }
    }

    if (!exactGrid)
    {
        [loop] for (uint k = 0u; k < gridCount; ++k)
        {
            g_AstcGrid0[k] = (g_AstcDecimDen[k] > 1e-6) ? saturate(g_AstcDecimNum[k] / g_AstcDecimDen[k]) : 0.0;
        }
    }
}

// Unified exact hardware-reconstructed candidate error evaluator for 1P (stage=0), Dual-Plane (stage=1), and 2P (stage=2).
float AstcEvalCandidateError(
    uint baseX, uint baseY,
    uint effMode,
    uint stage,
    uint ccs,
    AstcPart2Params p2,
    float4 e0_p0, float4 e1_p0,
    float4 e0_p1, float4 e1_p1,
    uint gridW, uint gridH,
    uint wQuant, uint epQuant,
    float bestError,
    out float4 outChanErr)
{
    bool isDual = (stage == 1u);
    bool isTwoP = (stage == 2u);
    uint epPerPart = AstcGetEpCountForEffMode(effMode);
    uint partCnt = isTwoP ? 2u : 1u;

    float4 uq0_p0 = 0.0, uq1_p0 = 0.0, uq0_p1 = 0.0, uq1_p1 = 0.0;
    [loop] for (uint pi = 0u; pi < partCnt; ++pi)
    {
        float4 te0 = (pi == 0u) ? e0_p0 : e0_p1;
        float4 te1 = (pi == 0u) ? e1_p0 : e1_p1;
        float4 uq0, uq1;
        AstcQuantizeEndpoints(te0, te1, effMode, epQuant, pi * epPerPart, uq0, uq1);
        if (pi == 0u) { uq0_p0 = uq0; uq1_p0 = uq1; }
        else          { uq0_p1 = uq0; uq1_p1 = uq1; }
    }

    uint gridCount = gridW * gridH;
    [loop] for (uint i = 0u; i < ASTC_MAX_GRID_U; ++i)
    {
        g_AstcGridUq0[i] = 0u;
        if (isDual) g_AstcGridUq1[i] = 0u;
    }
    [loop] for (uint k = 0u; k < gridCount; ++k)
    {
        uint uq0;
        AstcQuantizeWeight(g_AstcGrid0[k], wQuant, uq0);
        g_AstcGridUq0[k] = uq0;
        if (isDual)
        {
            uint uq1;
            AstcQuantizeWeight(g_AstcGrid1[k], wQuant, uq1);
            g_AstcGridUq1[k] = uq1;
        }
    }

    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float totalErr = 0.0;
    outChanErr = 0.0;

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            uint w0;
            ASTC_EVAL_INFILL(g_AstcGridUq0, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w0);

            float4 recon;
            if (isTwoP)
            {
                uint part = AstcEvalPartition2(p2, x, y);
                recon = (part == 0u)
                    ? lerp(uq0_p0, uq1_p0, (float)w0 * (1.0 / 64.0))
                    : lerp(uq0_p1, uq1_p1, (float)w0 * (1.0 / 64.0));
            }
            else
            {
                recon = lerp(uq0_p0, uq1_p0, (float)w0 * (1.0 / 64.0));
                if (isDual)
                {
                    uint w1;
                    ASTC_EVAL_INFILL(g_AstcGridUq1, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w1);
                    float p1Val = lerp(AstcGetChannel(uq0_p0, ccs), AstcGetChannel(uq1_p0, ccs), (float)w1 * (1.0 / 64.0));
                    recon = AstcSetChannel(recon, ccs, p1Val);
                }
            }

            if (isDual && effMode == ASTC_EFF_LDR_NORMAL)
            {
                float2 nOrig = float2(p.w, p.x) * (2.0 / 255.0) - 1.0;
                float zOrig = sqrt(saturate(1.0 - dot(nOrig, nOrig)));
                float2 nRec = float2(recon.a, recon.r) * (2.0 / 255.0) - 1.0;
                float zRec = sqrt(saturate(1.0 - dot(nRec, nRec)));
                totalErr += max(0.0, 1.0 - (dot(nOrig, nRec) + zOrig * zRec));
            }
            else
            {
                float4 diff = (p - recon) * mask;
                float4 chImp = AstcGetChannelImp(effMode, imp);
                float4 sq = diff * diff * chImp;
                if (stage == 0u) outChanErr += sq;
                totalErr += sq.r + sq.g + sq.b + sq.a;
            }
        }
        if (totalErr >= bestError)
            break;
    }
    return totalErr;
}

// Fits both 2-partition subsets simultaneously in 3 shared passes (mean + covariance + projection) instead of 8 separate passes.
void AstcFitBothPartitions2P(
    uint baseX, uint baseY,
    uint effMode,
    AstcPart2Params p2,
    out float4 outE0_p0, out float4 outE1_p0,
    out float4 outE0_p1, out float4 outE1_p1)
{
    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float4 sum0 = 0.0, sum1 = 0.0;
    float4 wSum0 = 0.0, wSum1 = 0.0;

    // Pass 1: means for both partitions
    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            if (AstcEvalPartition2(p2, x, y) == 0u)
            {
                sum0 += p * chImp;
                wSum0 += chImp;
            }
            else
            {
                sum1 += p * chImp;
                wSum1 += chImp;
            }
        }
    }

    float4 mean0 = sum0 / max(wSum0, 1e-6);
    float4 mean1 = sum1 / max(wSum1, 1e-6);

    // Pass 2: covariance + principal axis via power iteration seed for both partitions
    float4 cov0R = 0.0, cov0G = 0.0, cov0B = 0.0, cov0A = 0.0;
    float4 cov1R = 0.0, cov1G = 0.0, cov1B = 0.0, cov1A = 0.0;
    [loop] for (uint y2 = 0u; y2 < BLOCK_Y; ++y2)
    {
        [loop] for (uint x2 = 0u; x2 < BLOCK_X; ++x2)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x2, y2, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            if (AstcEvalPartition2(p2, x2, y2) == 0u)
            {
                float4 d = (p - mean0) * mask;
                float4 dw = d * chImp;
                cov0R += dw.r * d; cov0G += dw.g * d; cov0B += dw.b * d; cov0A += dw.a * d;
            }
            else
            {
                float4 d = (p - mean1) * mask;
                float4 dw = d * chImp;
                cov1R += dw.r * d; cov1G += dw.g * d; cov1B += dw.b * d; cov1A += dw.a * d;
            }
        }
    }

    float4 axis0 = float4(cov0R.r, cov0G.g, cov0B.b, cov0A.a);
    float4 axis1 = float4(cov1R.r, cov1G.g, cov1B.b, cov1A.a);
    [loop] for (uint it = 0u; it < 3u; ++it)
    {
        float4 n0 = float4(dot(cov0R, axis0), dot(cov0G, axis0), dot(cov0B, axis0), dot(cov0A, axis0)) * mask;
        float l0 = length(n0);
        if (l0 > 1e-6) axis0 = n0 / l0;

        float4 n1 = float4(dot(cov1R, axis1), dot(cov1G, axis1), dot(cov1B, axis1), dot(cov1A, axis1)) * mask;
        float l1 = length(n1);
        if (l1 > 1e-6) axis1 = n1 / l1;
    }
    if (length(axis0) <= 1e-6) axis0 = mask;
    if (length(axis1) <= 1e-6) axis1 = mask;
    axis0 = normalize(axis0);
    axis1 = normalize(axis1);

    // Pass 3: min/max projection along each partition's principal axis
    float minT0 = 1e30, maxT0 = -1e30;
    float minT1 = 1e30, maxT1 = -1e30;
    [loop] for (uint y3 = 0u; y3 < BLOCK_Y; ++y3)
    {
        [loop] for (uint x3 = 0u; x3 < BLOCK_X; ++x3)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x3, y3, effMode, imp);
            if (AstcEvalPartition2(p2, x3, y3) == 0u)
            {
                float t = dot((p - mean0) * mask, axis0);
                minT0 = min(minT0, t);
                maxT0 = max(maxT0, t);
            }
            else
            {
                float t = dot((p - mean1) * mask, axis1);
                minT1 = min(minT1, t);
                maxT1 = max(maxT1, t);
            }
        }
    }

    if (minT0 > maxT0) { minT0 = 0.0; maxT0 = 0.0; }
    if (minT1 > maxT1) { minT1 = 0.0; maxT1 = 0.0; }

    outE0_p0 = AstcClampWorkingColor(mean0 + axis0 * minT0, effMode);
    outE1_p0 = AstcClampWorkingColor(mean0 + axis0 * maxT0, effMode);
    outE0_p1 = AstcClampWorkingColor(mean1 + axis1 * minT1, effMode);
    outE1_p1 = AstcClampWorkingColor(mean1 + axis1 * maxT1, effMode);
}

// Unified post-quantization ideal endpoint refit for 1P single-plane and Dual-Plane blocks.
void AstcRefitEndpoints(
    uint baseX, uint baseY,
    uint effMode,
    bool isDualPlane,
    uint ccs,
    uint gridW, uint gridH,
    uint epQuant,
    float4 curUqE0, float4 curUqE1)
{
    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float origErr = 0.0;

    float a00_p0 = 0.0, a01_p0 = 0.0, a11_p0 = 0.0;
    float a00_p0a = 0.0, a01_p0a = 0.0, a11_p0a = 0.0;
    float a00_p1 = 0.0, a01_p1 = 0.0, a11_p1 = 0.0;
    float4 b0_p0 = 0.0, b1_p0 = 0.0;
    float b0_p1 = 0.0, b1_p1 = 0.0;

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w0_64;
            ASTC_EVAL_INFILL(g_AstcGridUq0, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w0_64);
            float w0 = (float)w0_64 * (1.0 / 64.0);
            float omw0 = 1.0 - w0;

            float4 recon = lerp(curUqE0, curUqE1, w0);
            float w1 = w0;
            if (isDualPlane)
            {
                uint w1_64;
                ASTC_EVAL_INFILL(g_AstcGridUq1, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w1_64);
                w1 = (float)w1_64 * (1.0 / 64.0);
                recon = AstcSetChannel(recon, ccs, lerp(AstcGetChannel(curUqE0, ccs), AstcGetChannel(curUqE1, ccs), w1));
            }

            if (isDualPlane && effMode == ASTC_EFF_LDR_NORMAL)
            {
                float2 nOrig = float2(p.w, p.x) * (2.0 / 255.0) - 1.0;
                float zOrig = sqrt(saturate(1.0 - dot(nOrig, nOrig)));
                float2 nRec = float2(recon.a, recon.r) * (2.0 / 255.0) - 1.0;
                float zRec = sqrt(saturate(1.0 - dot(nRec, nRec)));
                origErr += max(0.0, 1.0 - (dot(nOrig, nRec) + zOrig * zRec));
            }
            else
            {
                float4 diff = (p - recon) * mask;
                origErr += dot(diff * diff, chImp);
            }

            float wRgb = chImp.x;
            a00_p0 += wRgb * omw0 * omw0;
            a01_p0 += wRgb * w0 * omw0;
            a11_p0 += wRgb * w0 * w0;

            a00_p0a += omw0 * omw0;
            a01_p0a += w0 * omw0;
            a11_p0a += w0 * w0;

            b0_p0 += p * (chImp * omw0);
            b1_p0 += p * (chImp * w0);

            if (isDualPlane)
            {
                float omw1 = 1.0 - w1;
                float p1Imp = AstcGetChannel(chImp, ccs);
                float p1Val = AstcGetChannel(p, ccs);
                a00_p1 += p1Imp * omw1 * omw1;
                a01_p1 += p1Imp * w1 * omw1;
                a11_p1 += p1Imp * w1 * w1;
                b0_p1 += p1Val * (p1Imp * omw1);
                b1_p1 += p1Val * (p1Imp * w1);
            }
        }
    }

    float4 refitE0 = curUqE0;
    float4 refitE1 = curUqE1;
    bool changed = false;

    float detP0 = a00_p0 * a11_p0 - a01_p0 * a01_p0;
    if (detP0 > 1e-4)
    {
        float invDet = 1.0 / detP0;
        refitE0.rgb = AstcClampWorkingColor(float4((a11_p0 * b0_p0.rgb - a01_p0 * b1_p0.rgb) * invDet, 0.0), effMode).rgb;
        refitE1.rgb = AstcClampWorkingColor(float4((a00_p0 * b1_p0.rgb - a01_p0 * b0_p0.rgb) * invDet, 0.0), effMode).rgb;
        changed = true;
    }

    float detP0A = a00_p0a * a11_p0a - a01_p0a * a01_p0a;
    if ((!isDualPlane || ccs != 3u) && mask.a > 0.0 && detP0A > 1e-4)
    {
        float invDetA = 1.0 / detP0A;
        refitE0.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, (a11_p0a * b0_p0.a - a01_p0a * b1_p0.a) * invDetA), effMode).a;
        refitE1.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, (a00_p0a * b1_p0.a - a01_p0a * b0_p0.a) * invDetA), effMode).a;
        changed = true;
    }

    float detP1 = a00_p1 * a11_p1 - a01_p1 * a01_p1;
    if (isDualPlane && detP1 > 1e-4)
    {
        float invDet1 = 1.0 / detP1;
        float maxVal = (effMode >= ASTC_EFF_HDR_RGB) ? 65535.0 : 255.0;
        float n0 = clamp((a11_p1 * b0_p1 - a01_p1 * b1_p1) * invDet1, 0.0, maxVal);
        float n1 = clamp((a00_p1 * b1_p1 - a01_p1 * b0_p1) * invDet1, 0.0, maxVal);
        refitE0 = AstcSetChannel(refitE0, ccs, n0);
        refitE1 = AstcSetChannel(refitE1, ccs, n1);
        changed = true;
    }

    if (!changed) return;

    [loop] for (uint sv = 0u; sv < 8u; ++sv) g_AstcCandEpCodes[sv] = g_AstcEpCodes[sv];
    float4 uq0, uq1;
    AstcQuantizeEndpoints(refitE0, refitE1, effMode, epQuant, 0u, uq0, uq1);

    float refitErr = 0.0;
    [loop] for (uint y2 = 0u; y2 < BLOCK_Y; ++y2)
    {
        [loop] for (uint x2 = 0u; x2 < BLOCK_X; ++x2)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x2, y2, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w0_64;
            ASTC_EVAL_INFILL(g_AstcGridUq0, x2, y2, BLOCK_X, BLOCK_Y, gridW, gridH, w0_64);
            float4 recon = lerp(uq0, uq1, (float)w0_64 * (1.0 / 64.0));
            if (isDualPlane)
            {
                uint w1_64;
                ASTC_EVAL_INFILL(g_AstcGridUq1, x2, y2, BLOCK_X, BLOCK_Y, gridW, gridH, w1_64);
                recon = AstcSetChannel(recon, ccs, lerp(AstcGetChannel(uq0, ccs), AstcGetChannel(uq1, ccs), (float)w1_64 * (1.0 / 64.0)));
            }

            if (isDualPlane && effMode == ASTC_EFF_LDR_NORMAL)
            {
                float2 nOrig = float2(p.w, p.x) * (2.0 / 255.0) - 1.0;
                float zOrig = sqrt(saturate(1.0 - dot(nOrig, nOrig)));
                float2 nRec = float2(recon.a, recon.r) * (2.0 / 255.0) - 1.0;
                float zRec = sqrt(saturate(1.0 - dot(nRec, nRec)));
                refitErr += max(0.0, 1.0 - (dot(nOrig, nRec) + zOrig * zRec));
            }
            else
            {
                float4 diff = (p - recon) * mask;
                refitErr += dot(diff * diff, chImp);
            }
        }
    }

    if (refitErr >= origErr)
    {
        [loop] for (uint ei = 0u; ei < 8u; ++ei) g_AstcEpCodes[ei] = g_AstcCandEpCodes[ei];
    }
}

#endif // !ASTC_FAST_ONLY

// Assembles the final winning AstcCandidate into a 128-bit physical ASTC block.
uint4 AstcAssembleBlock(uint baseX, uint baseY, uint effMode, AstcCandidate c)
{
    [loop] for (uint e = 0u; e < 16u; ++e) g_AstcEpCodes[e] = 0u;
    [loop] for (uint wi = 0u; wi < 64u; ++wi) g_AstcBiseVals[wi] = 0u;

    uint epPerPart = AstcGetEpCountForEffMode(effMode);
    uint cem = AstcGetCemForEffMode(effMode);

    uint gridCount = c.gridW * c.gridH;
    uint totalWeights = gridCount;

    float4 uqE0 = 0.0, uqE1 = 0.0;
    float4 uq0_p1 = 0.0, uq1_p1 = 0.0;
#if !defined(ASTC_FAST_ONLY)
    uint partCnt = (c.isTwoPart != 0u) ? 2u : 1u;
    [loop] for (uint pi = 0u; pi < partCnt; ++pi)
    {
        float4 te0 = (pi == 0u) ? c.e0 : c.e0_p1;
        float4 te1 = (pi == 0u) ? c.e1 : c.e1_p1;
        float4 tu0, tu1;
        AstcQuantizeEndpoints(te0, te1, effMode, c.epQuant, pi * epPerPart, tu0, tu1);
        if (pi == 0u) { uqE0 = tu0; uqE1 = tu1; }
        else          { uq0_p1 = tu0; uq1_p1 = tu1; }
    }
#else
    AstcQuantizeEndpoints(c.e0, c.e1, effMode, c.epQuant, 0u, uqE0, uqE1);
#endif

#if !defined(ASTC_FAST_ONLY)
    if (c.isTwoPart != 0u)
    {
        AstcComputeWeightGrid2P(baseX, baseY, effMode, c.partSeed, uqE0, uqE1, uq0_p1, uq1_p1, c.gridW, c.gridH);
        [loop] for (uint k = 0u; k < gridCount; ++k)
        {
            uint dummyUq;
            g_AstcBiseVals[k] = AstcQuantizeWeight(g_AstcGrid0[k], c.wQuant, dummyUq);
        }
    }
    else
#endif
    {
        bool isDual = (c.isDualPlane != 0u);
        uint exclCh = isDual ? c.dualPlaneChannel : 4u;
        AstcComputeWeightGrid1P(baseX, baseY, effMode, exclCh, uqE0, uqE1, c.gridW, c.gridH);
        if (isDual)
        {
            AstcComputePlane1WeightGrid(baseX, baseY, effMode, c.dualPlaneChannel, AstcGetChannel(uqE0, c.dualPlaneChannel), AstcGetChannel(uqE1, c.dualPlaneChannel), c.gridW, c.gridH);
        }

#if !defined(ASTC_FAST_ONLY)
        [loop] for (uint zi = 0u; zi < ASTC_MAX_GRID_U; ++zi)
        {
            g_AstcGridUq0[zi] = 0u;
            if (isDual) g_AstcGridUq1[zi] = 0u;
        }
#endif
        [loop] for (uint k = 0u; k < gridCount; ++k)
        {
            uint uq0;
            uint code0 = AstcQuantizeWeight(g_AstcGrid0[k], c.wQuant, uq0);
#if !defined(ASTC_FAST_ONLY)
            g_AstcGridUq0[k] = uq0;
#endif
            if (isDual)
            {
                uint uq1;
                g_AstcBiseVals[2u * k]      = code0;
                g_AstcBiseVals[2u * k + 1u] = AstcQuantizeWeight(g_AstcGrid1[k], c.wQuant, uq1);
#if !defined(ASTC_FAST_ONLY)
                g_AstcGridUq1[k] = uq1;
#endif
            }
            else
            {
                g_AstcBiseVals[k] = code0;
            }
        }
        if (isDual) totalWeights = gridCount * 2u;

#if !defined(ASTC_FAST_ONLY)
        if (_AstcQuality >= 1u)
        {
            AstcRefitEndpoints(baseX, baseY, effMode, isDual, c.dualPlaneChannel, c.gridW, c.gridH, c.epQuant, uqE0, uqE1);
        }
#endif
    }

    // Encode weights at bit 0 into wBuf, then reverse all 128 bits so weights sit at MSB descending.
    uint4 wBuf = uint4(0u, 0u, 0u, 0u);
    AstcEncodeBise(wBuf, 0u, c.wQuant, totalWeights);
    uint4 block = AstcReverse128(wBuf);

    // Header: BlockMode (11 bits) + PartitionCount-1 (2 bits)
    AstcWriteBits128(block, 0u, 11u, c.blockMode);
    uint epStartBit = 17u;
    uint epTotalCount = epPerPart;
#if !defined(ASTC_FAST_ONLY)
    if (c.isTwoPart != 0u)
    {
        AstcWriteBits128(block, 11u, 2u, 1u);
        AstcWriteBits128(block, 13u, 10u, c.partSeed);
        // Matched CEM for both partitions: low 2 bits = 00, upper 4 bits = cem
        AstcWriteBits128(block, 23u, 6u, cem << 2u);
        epStartBit = 29u;
        epTotalCount = epPerPart * 2u;
    }
    else
#endif
    {
        AstcWriteBits128(block, 11u, 2u, 0u);
        AstcWriteBits128(block, 13u, 4u, cem);
        if (c.isDualPlane != 0u)
        {
            uint weightBits = AstcGetBiseBitCount(totalWeights, c.wQuant);
            AstcWriteBits128(block, 128u - weightBits - 2u, 2u, c.dualPlaneChannel);
        }
    }
    [loop] for (uint ei = 0u; ei < epTotalCount; ++ei) g_AstcBiseVals[ei] = g_AstcEpCodes[ei];
    AstcEncodeBise(block, epStartBit, c.epQuant, epTotalCount);

    return block;
}

#if !defined(ASTC_FAST_ONLY)

void AstcTryCandidate(
    uint baseX, uint baseY, uint effMode,
    uint stage, uint ccs, uint partSeed, AstcPart2Params p2,
    float4 e0_p0, float4 e1_p0, float4 e0_p1, float4 e1_p1,
    uint bm, uint gw, uint gh,
    uint wQ, uint epQ,
    inout AstcCandidate best,
    inout float4 bestChanErr)
{
    float4 chanErr;
    float err = AstcEvalCandidateError(baseX, baseY, effMode, stage, ccs, p2, e0_p0, e1_p0, e0_p1, e1_p1, gw, gh, wQ, epQ, best.error, chanErr);
    if (err < best.error)
    {
        best.blockMode = bm;
        best.gridW = gw;
        best.gridH = gh;
        best.wQuant = wQ;
        best.epQuant = epQ;
        best.isDualPlane = (stage == 1u) ? 1u : 0u;
        best.dualPlaneChannel = (stage == 1u) ? ccs : 3u;
        best.isTwoPart = (stage == 2u) ? 1u : 0u;
        best.partSeed = (stage == 2u) ? partSeed : 0u;
        best.error = err;
        best.e0 = e0_p0;
        best.e1 = e1_p0;
        if (stage == 2u)
        {
            best.e0_p1 = e0_p1;
            best.e1_p1 = e1_p1;
        }
        if (stage == 0u)
        {
            bestChanErr = chanErr;
        }
    }
}

// Curated 16 geometrically diverse ASTC 2-partition seeds (vertical, horizontal, diagonals, corners).
uint AstcGetCuratedSeed(uint idx)
{
    if (idx == 0u)  return 16u;
    if (idx == 1u)  return 28u;
    if (idx == 2u)  return 31u;
    if (idx == 3u)  return 78u;
    if (idx == 4u)  return 17u;
    if (idx == 5u)  return 39u;
    if (idx == 6u)  return 116u;
    if (idx == 7u)  return 72u;
    if (idx == 8u)  return 107u;
    if (idx == 9u)  return 43u;
    if (idx == 10u) return 110u;
    if (idx == 11u) return 252u;
    if (idx == 12u) return 61u;
    if (idx == 13u) return 76u;
    if (idx == 14u) return 210u;
    return 137u;
}

// Fast heuristic to select the best 2-partition seed by minimizing per-channel weighted intra-partition variance.
// Evaluates 16 curated seeds in 4 batches of 4 with hoisted AstcPreparePartition2 hash parameters.
uint AstcSelectBestSeed2P(uint baseX, uint baseY, uint effMode)
{
    float4 mask = AstcChannelMaskEx(effMode, 4u);
    bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;

    uint bestSeed = 17u;
    float bestScore = -1.0;

    [loop] for (uint batch = 0u; batch < 16u; batch += 4u)
    {
        uint seed0 = AstcGetCuratedSeed(batch + 0u);
        uint seed1 = AstcGetCuratedSeed(batch + 1u);
        uint seed2 = AstcGetCuratedSeed(batch + 2u);
        uint seed3 = AstcGetCuratedSeed(batch + 3u);

        AstcPart2Params p0 = AstcPreparePartition2(seed0, smallBlock);
        AstcPart2Params p1 = AstcPreparePartition2(seed1, smallBlock);
        AstcPart2Params p2 = AstcPreparePartition2(seed2, smallBlock);
        AstcPart2Params p3 = AstcPreparePartition2(seed3, smallBlock);

        float4 sumAll = 0.0;
        float4 cntAll = 0.0;

        float4 s1_0 = 0.0, s1_1 = 0.0, s1_2 = 0.0, s1_3 = 0.0;
        float4 c1_0 = 0.0, c1_1 = 0.0, c1_2 = 0.0, c1_3 = 0.0;

        [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
        {
            [loop] for (uint x = 0u; x < BLOCK_X; ++x)
            {
                float imp;
                float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
                float4 chImp = AstcGetChannelImp(effMode, imp) * mask;
                float4 pw = p * chImp;
                sumAll += pw;
                cntAll += chImp;

                if (AstcEvalPartition2(p0, x, y) != 0u) { s1_0 += pw; c1_0 += chImp; }
                if (AstcEvalPartition2(p1, x, y) != 0u) { s1_1 += pw; c1_1 += chImp; }
                if (AstcEvalPartition2(p2, x, y) != 0u) { s1_2 += pw; c1_2 += chImp; }
                if (AstcEvalPartition2(p3, x, y) != 0u) { s1_3 += pw; c1_3 += chImp; }
            }
        }

        // Maximizing sum_c (s0_c^2 / c0_c + s1_c^2 / c1_c) minimizes per-channel weighted within-cluster variance.
        float4 c0_0 = cntAll - c1_0;
        if (dot(c0_0, mask) >= 0.5 && dot(c1_0, mask) >= 0.5)
        {
            float4 s0_0 = sumAll - s1_0;
            float score = dot((s0_0 * s0_0) / max(c0_0, 1e-6) + (s1_0 * s1_0) / max(c1_0, 1e-6), mask);
            if (score > bestScore) { bestScore = score; bestSeed = seed0; }
        }

        float4 c0_1 = cntAll - c1_1;
        if (dot(c0_1, mask) >= 0.5 && dot(c1_1, mask) >= 0.5)
        {
            float4 s0_1 = sumAll - s1_1;
            float score = dot((s0_1 * s0_1) / max(c0_1, 1e-6) + (s1_1 * s1_1) / max(c1_1, 1e-6), mask);
            if (score > bestScore) { bestScore = score; bestSeed = seed1; }
        }

        float4 c0_2 = cntAll - c1_2;
        if (dot(c0_2, mask) >= 0.5 && dot(c1_2, mask) >= 0.5)
        {
            float4 s0_2 = sumAll - s1_2;
            float score = dot((s0_2 * s0_2) / max(c0_2, 1e-6) + (s1_2 * s1_2) / max(c1_2, 1e-6), mask);
            if (score > bestScore) { bestScore = score; bestSeed = seed2; }
        }

        float4 c0_3 = cntAll - c1_3;
        if (dot(c0_3, mask) >= 0.5 && dot(c1_3, mask) >= 0.5)
        {
            float4 s0_3 = sumAll - s1_3;
            float score = dot((s0_3 * s0_3) / max(c0_3, 1e-6) + (s1_3 * s1_3) / max(c1_3, 1e-6), mask);
            if (score > bestScore) { bestScore = score; bestSeed = seed3; }
        }
    }

    return bestSeed;
}

#endif // !ASTC_FAST_ONLY

// Main per-block ASTC encoder entrypoint.
uint4 AstcEncodeBlock(uint blockXIdx, uint blockYIdx)
{
    uint baseX = blockXIdx * BLOCK_X;
    uint baseY = blockYIdx * BLOCK_Y;

    bool isHdr = (_AstcMode == ASTC_MODE_HDR_RGB || _AstcMode == ASTC_MODE_HDR_RGBA);

    // ------------------------------------------------------------------------
    // Step 1: Scan raw block stats (Void-Extent check + HDR alpha mode resolution)
    // ------------------------------------------------------------------------
    float4 rawMin = float4(1e30, 1e30, 1e30, 1e30);
    float4 rawMax = float4(-1e30, -1e30, -1e30, -1e30);
    float4 rawFirst = 0.0;

    [loop] for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        [loop] for (uint x = 0u; x < BLOCK_X; ++x)
        {
            uint px = min(baseX + x, _AstcSrcWidth - 1u);
            uint py = min(baseY + y, _AstcSrcHeight - 1u);
            float4 r = _AstcSourceTex.Load(int3(px, py, _AstcMipLevel));
            if (isHdr)
            {
                // Sanitize NaN / Inf / negative values consistently with AstcFloatToLns4 & AstcFloatToHalfU16
                // so a single NaN at (0,0) or +Inf cannot corrupt Void-Extent detection.
                r = float4(
                    (r.r > 0.0) ? min(r.r, 65504.0) : 0.0,
                    (r.g > 0.0) ? min(r.g, 65504.0) : 0.0,
                    (r.b > 0.0) ? min(r.b, 65504.0) : 0.0,
                    (r.a > 0.0) ? min(r.a, 65504.0) : 0.0);
            }
            if (x == 0u && y == 0u) rawFirst = r;
            rawMin = min(rawMin, r);
            rawMax = max(rawMax, r);
        }
    }

    // Resolve effective CEM mode per block
    uint effMode = ASTC_EFF_LDR_RGB;
    if (_AstcMode == ASTC_MODE_LDR_NORMAL)
    {
        effMode = ASTC_EFF_LDR_NORMAL;
    }
    else if (_AstcMode == ASTC_MODE_LDR_RGB)
    {
        effMode = ASTC_EFF_LDR_RGB;
    }
    else if (_AstcMode == ASTC_MODE_LDR_RGBA)
    {
        // Auto-promote opaque blocks to RGB CEM (6 endpoints -> higher precision!)
        effMode = (rawMin.a >= (254.5 / 255.0)) ? ASTC_EFF_LDR_RGB : ASTC_EFF_LDR_RGBA;
    }
    else if (_AstcMode == ASTC_MODE_HDR_RGB)
    {
        effMode = ASTC_EFF_HDR_RGB;
    }
    else // ASTC_MODE_HDR_RGBA
    {
        if (rawMin.a >= 0.999 && rawMax.a <= 1.001)
            effMode = ASTC_EFF_HDR_RGB;
        else if (rawMin.a >= -1e-4 && rawMax.a <= 1.0001)
            effMode = ASTC_EFF_HDR_RGB_LDR_ALPHA;
        else
            effMode = ASTC_EFF_HDR_RGBA;
    }

    // ------------------------------------------------------------------------
    // Step 2: Void-Extent constant color fast path (lossless!)
    // ------------------------------------------------------------------------
    if (!isHdr)
    {
        // AstcLinearToSRGB is monotonic on [0, 1], so converting min/max preserves the exact
        // range in sRGB code space and prevents dark sRGB gradients from being treated as flat.
        float4 cmpMin = saturate(rawMin);
        float4 cmpMax = saturate(rawMax);
        if (_AstcIsSRGB != 0u)
        {
            cmpMin.rgb = AstcLinearToSRGB(rawMin.rgb);
            cmpMax.rgb = AstcLinearToSRGB(rawMax.rgb);
        }

        bool isConst;
        if (effMode == ASTC_EFF_LDR_NORMAL)
            isConst = (cmpMax.r - cmpMin.r < (0.5 / 255.0)) && (cmpMax.g - cmpMin.g < (0.5 / 255.0));
        else if (effMode == ASTC_EFF_LDR_RGB)
            isConst = all(cmpMax.rgb - cmpMin.rgb < (0.5 / 255.0));
        else
            isConst = all(cmpMax - cmpMin < (0.5 / 255.0));

        if (isConst)
        {
            float4 c = rawFirst;
            float3 rgb = (_AstcIsSRGB != 0u) ? AstcLinearToSRGB(c.rgb) : saturate(c.rgb);
            uint4 c8;
            if (effMode == ASTC_EFF_LDR_NORMAL)
            {
                uint g8 = (uint)clamp(floor(rgb.g * 255.0 + 0.5), 0.0, 255.0);
                uint r8 = (uint)clamp(floor(rgb.r * 255.0 + 0.5), 0.0, 255.0);
                c8 = uint4(g8, g8, g8, r8);
            }
            else
            {
                uint a8 = (effMode == ASTC_EFF_LDR_RGB) ? 255u : (uint)clamp(floor(saturate(c.a) * 255.0 + 0.5), 0.0, 255.0);
                c8 = uint4(
                    (uint)clamp(floor(rgb.r * 255.0 + 0.5), 0.0, 255.0),
                    (uint)clamp(floor(rgb.g * 255.0 + 0.5), 0.0, 255.0),
                    (uint)clamp(floor(rgb.b * 255.0 + 0.5), 0.0, 255.0),
                    a8);
            }
            return AstcEncodeVoidExtentLDR(c8 * 257u);
        }
    }
    else
    {
        bool isConstRgb = all(rawMax.rgb - rawMin.rgb <= max(abs(rawMax.rgb) * 1e-4, 1e-5));
        bool isConstA = (effMode == ASTC_EFF_HDR_RGB) || ((rawMax.a - rawMin.a) <= max(abs(rawMax.a) * 1e-4, 1e-5));
        if (isConstRgb && isConstA)
        {
            uint4 f16 = uint4(
                AstcFloatToHalfU16(rawFirst.r),
                AstcFloatToHalfU16(rawFirst.g),
                AstcFloatToHalfU16(rawFirst.b),
                (effMode == ASTC_EFF_HDR_RGB) ? 0x3C00u : AstcFloatToHalfU16(rawFirst.a));
            return AstcEncodeVoidExtentHDR(f16);
        }
    }

#if ASTC_ENABLE_PIXEL_CACHE
    [loop] for (uint cy = 0u; cy < BLOCK_Y; ++cy)
    {
        [loop] for (uint cx = 0u; cx < BLOCK_X; ++cx)
        {
            float dummyImp;
            float4 wp = AstcLoadWorkingPixel(baseX, baseY, cx, cy, effMode, dummyImp);
            g_AstcPackedBlock[cy * BLOCK_X + cx] = AstcPackRgba16(wp);
        }
    }
#endif

    // ------------------------------------------------------------------------
    // Step 3: Normal Map Dedicated Dual-Plane Path (GGGR -> CEM 4 LA + CCS=3)
    // ------------------------------------------------------------------------
    AstcCandidate best;
    best.blockMode = 0x251u;
    best.gridW = 4u;
    best.gridH = 4u;
    best.wQuant = ASTC_QUANT_12;
    best.epQuant = ASTC_QUANT_256;
    best.isDualPlane = 0u;
    best.dualPlaneChannel = 3u;
    best.isTwoPart = 0u;
    best.partSeed = 0u;
    best.error = 1e30;
    best.e0 = 0.0;
    best.e1 = 0.0;
    best.e0_p1 = 0.0;
    best.e1_p1 = 0.0;

    bool isNormal = (effMode == ASTC_EFF_LDR_NORMAL);
    bool hasAlpha = (effMode == ASTC_EFF_LDR_RGBA || effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA || effMode == ASTC_EFF_HDR_RGBA);

#if defined(ASTC_FAST_ONLY)
    uint refineIters = 0u;
#else
    uint refineIters = (!isNormal && _AstcQuality >= 1u) ? 2u : 0u;
#endif
    float4 e0, e1;
    AstcFitSubsetEndpoints(baseX, baseY, effMode, isNormal ? 3u : 4u, 0u, 2u, refineIters, e0, e1);

#if defined(ASTC_FAST_ONLY)
    best.e0 = e0;
    best.e1 = e1;
    if (isNormal)
    {
        best.blockMode = 0x452u; best.gridW = 4u; best.gridH = 4u; best.wQuant = ASTC_QUANT_5; best.epQuant = ASTC_QUANT_256;
        best.isDualPlane = 1u; best.dualPlaneChannel = 3u;
    }
    else if (!hasAlpha)
    {
#if BLOCK_X == 4
        best.blockMode = 0x251u; best.gridW = 4u; best.gridH = 4u; best.wQuant = ASTC_QUANT_12; best.epQuant = ASTC_QUANT_256;
#else
        best.blockMode = 0x0E3u; best.gridW = 5u; best.gridH = 5u; best.wQuant = ASTC_QUANT_6;  best.epQuant = ASTC_QUANT_192;
#endif
    }
    else
    {
#if BLOCK_X == 4
        best.blockMode = 0x053u; best.gridW = 4u; best.gridH = 4u; best.wQuant = ASTC_QUANT_8;  best.epQuant = ASTC_QUANT_192;
#else
        best.blockMode = 0x0E2u; best.gridW = 5u; best.gridH = 5u; best.wQuant = ASTC_QUANT_4;  best.epQuant = ASTC_QUANT_192;
#endif
    }
#else
    if (_AstcQuality == 0u)
    {
        best.e0 = e0;
        best.e1 = e1;
        if (isNormal)
        {
            best.blockMode = 0x452u; best.gridW = 4u; best.gridH = 4u; best.wQuant = ASTC_QUANT_5; best.epQuant = ASTC_QUANT_256;
            best.isDualPlane = 1u; best.dualPlaneChannel = 3u;
        }
        else if (!hasAlpha)
        {
#if BLOCK_X == 4
            best.blockMode = 0x251u; best.gridW = 4u; best.gridH = 4u; best.wQuant = ASTC_QUANT_12; best.epQuant = ASTC_QUANT_256;
#else
            best.blockMode = 0x0E3u; best.gridW = 5u; best.gridH = 5u; best.wQuant = ASTC_QUANT_6;  best.epQuant = ASTC_QUANT_192;
#endif
        }
        else
        {
#if BLOCK_X == 4
            best.blockMode = 0x053u; best.gridW = 4u; best.gridH = 4u; best.wQuant = ASTC_QUANT_8;  best.epQuant = ASTC_QUANT_192;
#else
            best.blockMode = 0x0E2u; best.gridW = 5u; best.gridH = 5u; best.wQuant = ASTC_QUANT_4;  best.epQuant = ASTC_QUANT_192;
#endif
        }
    }
    else
    {
        float4 bestChanErr = 0.0;
        AstcPart2Params p2Params;
        p2Params.seed1 = 0u; p2Params.seed2 = 0u; p2Params.seed3 = 0u; p2Params.seed4 = 0u;
        p2Params.offA = 0u; p2Params.offB = 0u;

        // Unified 3-stage candidate search: stage 0 = 1P, stage 1 = Dual-Plane, stage 2 = 2P (High quality).
        // Consolidates AstcTryCandidate / AstcEvalCandidateError / AstcComputeWeightGrid1P to a single inlined site.
        [loop] for (uint stage = 0u; stage < 3u; ++stage)
        {
            if (stage == 0u && isNormal) continue;
            if (stage == 2u && (isNormal || _AstcQuality < 2u)) continue;

            uint stageCcs = 4u;
            uint stageSeed = 0u;
            float4 sE0_p0 = e0, sE1_p0 = e1;
            float4 sE0_p1 = 0.0, sE1_p1 = 0.0;
            uint candCount = 0u;

            if (stage == 0u)
            {
                if (!hasAlpha)
                {
#if BLOCK_X >= 8
                    candCount = 12u;
#elif BLOCK_X >= 6
                    candCount = 10u;
#elif BLOCK_X >= 5
                    candCount = 6u;
#else
                    candCount = 3u;
#endif
                }
                else
                {
#if BLOCK_X >= 6
                    candCount = 8u;
#elif BLOCK_X >= 5
                    candCount = 5u;
#else
                    candCount = 3u;
#endif
                }
            }
            else if (stage == 1u)
            {
                stageCcs = 3u;
                if (!isNormal)
                {
                    stageCcs = 0u;
                    float maxChErr = bestChanErr.r;
                    if (bestChanErr.g > maxChErr) { maxChErr = bestChanErr.g; stageCcs = 1u; }
                    if (bestChanErr.b > maxChErr) { maxChErr = bestChanErr.b; stageCcs = 2u; }
                    if (hasAlpha && bestChanErr.a > maxChErr) { stageCcs = 3u; }
                    AstcFitSubsetEndpoints(baseX, baseY, effMode, stageCcs, 0u, 2u, 1u, sE0_p0, sE1_p0);
                }
#if BLOCK_X >= 5
                candCount = isNormal ? 4u : 3u;
#else
                candCount = 2u;
#endif
            }
            else
            {
                bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;
                stageSeed = AstcSelectBestSeed2P(baseX, baseY, effMode);
                p2Params = AstcPreparePartition2(stageSeed, smallBlock);
                AstcFitBothPartitions2P(baseX, baseY, effMode, p2Params, sE0_p0, sE1_p0, sE0_p1, sE1_p1);
#if BLOCK_X >= 5
                candCount = 4u;
#else
                candCount = 2u;
#endif
            }

            uint cachedGW = 0u, cachedGH = 0u;
            [loop] for (uint ci = 0u; ci < candCount; ++ci)
            {
                uint bm, gw, gh, wQ, epQ;
                if (stage == 0u)
                {
                    if (!hasAlpha)
                    {
                        if (ci == 0u)      { bm = 0x251u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_12; epQ = ASTC_QUANT_256; }
                        else if (ci == 1u) { bm = 0x242u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_16; epQ = ASTC_QUANT_192; }
                        else if (ci == 2u) { bm = 0x252u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_20; epQ = ASTC_QUANT_96;  }
                        else if (ci == 3u) { bm = 0x0F2u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_5;  epQ = ASTC_QUANT_256; }
                        else if (ci == 4u) { bm = 0x0E3u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_192; }
                        else if (ci == 5u) { bm = 0x0F3u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_8;  epQ = ASTC_QUANT_64;  }
                        else if (ci == 6u) { bm = 0x114u; gw = 6u; gh = 6u; wQ = ASTC_QUANT_3;  epQ = ASTC_QUANT_256; }
                        else if (ci == 7u) { bm = 0x108u; gw = 6u; gh = 6u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_80;  }
                        else if (ci == 8u) { bm = 0x143u; gw = 6u; gh = 4u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_256; }
                        else if (ci == 9u) { bm = 0x04Fu; gw = 4u; gh = 6u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_256; }
                        else if (ci == 10u){ bm = 0x046u; gw = 8u; gh = 4u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
                        else               { bm = 0x04Au; gw = 4u; gh = 8u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
                    }
                    else
                    {
                        if (ci == 0u)      { bm = 0x043u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_256; }
                        else if (ci == 1u) { bm = 0x053u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_8;  epQ = ASTC_QUANT_192; }
                        else if (ci == 2u) { bm = 0x251u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_12; epQ = ASTC_QUANT_96;  }
                        else if (ci == 3u) { bm = 0x0E2u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
                        else if (ci == 4u) { bm = 0x0F2u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_5;  epQ = ASTC_QUANT_80;  }
                        else if (ci == 5u) { bm = 0x114u; gw = 6u; gh = 6u; wQ = ASTC_QUANT_3;  epQ = ASTC_QUANT_96;  }
                        else if (ci == 6u) { bm = 0x142u; gw = 6u; gh = 4u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
                        else               { bm = 0x04Eu; gw = 4u; gh = 6u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
                    }
                }
                else if (stage == 1u)
                {
                    uint4 cfg;
                    if (isNormal)
                    {
                        cfg = (ci == 0u) ? uint4(0x452u, 4u, ASTC_QUANT_5, ASTC_QUANT_256)
                            : (ci == 1u) ? uint4(0x442u, 4u, ASTC_QUANT_4, ASTC_QUANT_256)
                            : (ci == 2u) ? uint4(0x4F1u, 5u, ASTC_QUANT_3, ASTC_QUANT_128)
                                         : uint4(0x4E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_256);
                    }
                    else if (!hasAlpha)
                    {
                        cfg = (ci == 0u) ? uint4(0x451u, 4u, ASTC_QUANT_3, ASTC_QUANT_256)
                            : (ci == 1u) ? uint4(0x442u, 4u, ASTC_QUANT_4, ASTC_QUANT_160)
                                         : uint4(0x4E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_256);
                    }
                    else
                    {
                        cfg = (ci == 0u) ? uint4(0x451u, 4u, ASTC_QUANT_3, ASTC_QUANT_128)
                            : (ci == 1u) ? uint4(0x442u, 4u, ASTC_QUANT_4, ASTC_QUANT_48)
                                         : uint4(0x4E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_160);
                    }
                    bm = cfg.x; gw = cfg.y; gh = cfg.y; wQ = cfg.z; epQ = cfg.w;
                }
                else
                {
                    uint4 cfg;
                    if (!hasAlpha)
                    {
                        if (ci == 0u)      cfg = uint4(0x042u, 4u, ASTC_QUANT_4, ASTC_QUANT_40);
                        else if (ci == 1u) cfg = uint4(0x043u, 4u, ASTC_QUANT_6, ASTC_QUANT_24);
                        else if (ci == 2u) cfg = uint4(0x0F1u, 5u, ASTC_QUANT_3, ASTC_QUANT_24);
                        else               cfg = uint4(0x0E2u, 5u, ASTC_QUANT_4, ASTC_QUANT_16);
                    }
                    else
                    {
                        if (ci == 0u)      cfg = uint4(0x051u, 4u, ASTC_QUANT_3, ASTC_QUANT_20);
                        else if (ci == 1u) cfg = uint4(0x042u, 4u, ASTC_QUANT_4, ASTC_QUANT_16);
                        else if (ci == 2u) cfg = uint4(0x0E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_24);
                        else               cfg = uint4(0x0F1u, 5u, ASTC_QUANT_3, ASTC_QUANT_12);
                    }
                    bm = cfg.x; gw = cfg.y; gh = cfg.y; wQ = cfg.z; epQ = cfg.w;
                }

                if (gw != cachedGW || gh != cachedGH)
                {
                    if (stage == 2u)
                    {
                        AstcComputeWeightGrid2P(baseX, baseY, effMode, stageSeed, sE0_p0, sE1_p0, sE0_p1, sE1_p1, gw, gh);
                    }
                    else
                    {
                        AstcComputeWeightGrid1P(baseX, baseY, effMode, stageCcs, sE0_p0, sE1_p0, gw, gh);
                        if (stage == 1u)
                        {
                            AstcComputePlane1WeightGrid(baseX, baseY, effMode, stageCcs, AstcGetChannel(sE0_p0, stageCcs), AstcGetChannel(sE1_p0, stageCcs), gw, gh);
                        }
                    }
                    cachedGW = gw;
                    cachedGH = gh;
                }

                AstcTryCandidate(baseX, baseY, effMode, stage, stageCcs, stageSeed, p2Params, sE0_p0, sE1_p0, sE0_p1, sE1_p1, bm, gw, gh, wQ, epQ, best, bestChanErr);
            }
        }
    }
#endif // !ASTC_FAST_ONLY

    return AstcAssembleBlock(baseX, baseY, effMode, best);
}

#endif // ASTC_GPU_CORE_HLSL
