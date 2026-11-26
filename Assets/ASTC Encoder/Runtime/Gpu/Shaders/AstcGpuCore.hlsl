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

// Fetches one texel in the encoder's working space for the current effective mode.
float4 AstcFetchWorkingPixel(uint baseX, uint baseY, uint x, uint y, uint effMode, out float impWeight)
{
    uint px = min(baseX + x, _AstcSrcWidth - 1u);
    uint py = min(baseY + y, _AstcSrcHeight - 1u);
    float4 raw = _AstcSourceTex.Load(int3(px, py, _AstcMipLevel));

    impWeight = 1.0;

    if (effMode == ASTC_EFF_LDR_NORMAL)
    {
        // _AstcIsSRGB means the source view decodes sRGB on load; re-encode to recover the stored bytes
        // because normal data is always emitted to a linear (UNorm) ASTC target.
        float nr = (_AstcIsSRGB != 0u) ? AstcLinearToSRGBScalar(raw.r) : saturate(raw.r);
        float ng = (_AstcIsSRGB != 0u) ? AstcLinearToSRGBScalar(raw.g) : saturate(raw.g);
        return float4(ng * 255.0, 0.0, 0.0, nr * 255.0);
    }

    if (effMode == ASTC_EFF_LDR_RGB)
    {
        float3 rgb = (_AstcIsSRGB != 0u) ? AstcLinearToSRGB(raw.rgb) : saturate(raw.rgb);
        return float4(rgb * 255.0, 255.0);
    }

    if (effMode == ASTC_EFF_LDR_RGBA)
    {
        float3 rgb = (_AstcIsSRGB != 0u) ? AstcLinearToSRGB(raw.rgb) : saturate(raw.rgb);
        float a = saturate(raw.a);
        if (_AstcUseAlphaWeight != 0u)
        {
            impWeight = max(a, 0.02);
        }
        return float4(rgb * 255.0, a * 255.0);
    }

    if (effMode == ASTC_EFF_HDR_RGB)
    {
        return float4(AstcFloatToLns(raw.r), AstcFloatToLns(raw.g), AstcFloatToLns(raw.b), 65535.0);
    }

    if (effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA)
    {
        // Scale LDR alpha [0,1] into [0, 65535] to balance vector norm with LNS RGB.
        return float4(AstcFloatToLns(raw.r), AstcFloatToLns(raw.g), AstcFloatToLns(raw.b), saturate(raw.a) * 65535.0);
    }

    // ASTC_EFF_HDR_RGBA
    return AstcFloatToLns4(raw);
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
// Writes codes into codesOut[codeOffset..] and returns exact hardware-reconstructed uqE0, uqE1.
void AstcQuantizeEndpoints(
    float4 e0,
    float4 e1,
    uint effMode,
    uint epQuant,
    inout uint codesOut[16],
    uint codeOffset,
    out float4 uqE0,
    out float4 uqE1)
{
    uqE0 = float4(0.0, 0.0, 0.0, 255.0);
    uqE1 = float4(0.0, 0.0, 0.0, 255.0);

    if (effMode == ASTC_EFF_LDR_NORMAL)
    {
        uint l0 = (uint)clamp(floor(e0.x + 0.5), 0.0, 255.0);
        uint l1 = (uint)clamp(floor(e1.x + 0.5), 0.0, 255.0);
        uint a0 = (uint)clamp(floor(e0.w + 0.5), 0.0, 255.0);
        uint a1 = (uint)clamp(floor(e1.w + 0.5), 0.0, 255.0);

        uint uqL0, uqL1, uqA0, uqA1;
        codesOut[codeOffset + 0u] = AstcQuantizeColor(l0, epQuant, uqL0);
        codesOut[codeOffset + 1u] = AstcQuantizeColor(l1, epQuant, uqL1);
        codesOut[codeOffset + 2u] = AstcQuantizeColor(a0, epQuant, uqA0);
        codesOut[codeOffset + 3u] = AstcQuantizeColor(a1, epQuant, uqA1);

        uqE0 = float4((float)uqL0, 0.0, 0.0, (float)uqA0);
        uqE1 = float4((float)uqL1, 0.0, 0.0, (float)uqA1);
        return;
    }

    if (effMode == ASTC_EFF_LDR_RGB || effMode == ASTC_EFF_LDR_RGBA)
    {
        uint r0 = (uint)clamp(floor(e0.r + 0.5), 0.0, 255.0);
        uint r1 = (uint)clamp(floor(e1.r + 0.5), 0.0, 255.0);
        uint g0 = (uint)clamp(floor(e0.g + 0.5), 0.0, 255.0);
        uint g1 = (uint)clamp(floor(e1.g + 0.5), 0.0, 255.0);
        uint b0 = (uint)clamp(floor(e0.b + 0.5), 0.0, 255.0);
        uint b1 = (uint)clamp(floor(e1.b + 0.5), 0.0, 255.0);

        uint uqR0, uqR1, uqG0, uqG1, uqB0, uqB1;
        uint cR0 = AstcQuantizeColor(r0, epQuant, uqR0);
        uint cR1 = AstcQuantizeColor(r1, epQuant, uqR1);
        uint cG0 = AstcQuantizeColor(g0, epQuant, uqG0);
        uint cG1 = AstcQuantizeColor(g1, epQuant, uqG1);
        uint cB0 = AstcQuantizeColor(b0, epQuant, uqB0);
        uint cB1 = AstcQuantizeColor(b1, epQuant, uqB1);

        uint uqA0 = 255u, uqA1 = 255u;
        uint cA0 = 0u, cA1 = 0u;
        if (effMode == ASTC_EFF_LDR_RGBA)
        {
            uint a0 = (uint)clamp(floor(e0.a + 0.5), 0.0, 255.0);
            uint a1 = (uint)clamp(floor(e1.a + 0.5), 0.0, 255.0);
            cA0 = AstcQuantizeColor(a0, epQuant, uqA0);
            cA1 = AstcQuantizeColor(a1, epQuant, uqA1);
        }

        // Khronos ASTC spec: CEM 8/12 triggers blue-contraction & endpoint swap whenever (r1+g1+b1) < (r0+g0+b0).
        // Guarantee s1 >= s0 so hardware always decodes direct endpoints without unexpected contraction/inversion.
        if ((uqR0 + uqG0 + uqB0) > (uqR1 + uqG1 + uqB1))
        {
            uint t;
            t = cR0; cR0 = cR1; cR1 = t;
            t = cG0; cG0 = cG1; cG1 = t;
            t = cB0; cB0 = cB1; cB1 = t;
            t = cA0; cA0 = cA1; cA1 = t;
            t = uqR0; uqR0 = uqR1; uqR1 = t;
            t = uqG0; uqG0 = uqG1; uqG1 = t;
            t = uqB0; uqB0 = uqB1; uqB1 = t;
            t = uqA0; uqA0 = uqA1; uqA1 = t;
        }

        codesOut[codeOffset + 0u] = cR0;
        codesOut[codeOffset + 1u] = cR1;
        codesOut[codeOffset + 2u] = cG0;
        codesOut[codeOffset + 3u] = cG1;
        codesOut[codeOffset + 4u] = cB0;
        codesOut[codeOffset + 5u] = cB1;

        uqE0 = float4((float)uqR0, (float)uqG0, (float)uqB0, (float)uqA0);
        uqE1 = float4((float)uqR1, (float)uqG1, (float)uqB1, (float)uqA1);

        if (effMode == ASTC_EFF_LDR_RGBA)
        {
            codesOut[codeOffset + 6u] = cA0;
            codesOut[codeOffset + 7u] = cA1;
        }
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

    uint rgbCodes[6];
    float3 uqRgb0, uqRgb1;
    AstcQuantizeHdrRgbEndpoints(e0.rgb, e1.rgb, epQuant, rgbCodes, uqRgb0, uqRgb1);
    codesOut[codeOffset + 0u] = rgbCodes[0];
    codesOut[codeOffset + 1u] = rgbCodes[1];
    codesOut[codeOffset + 2u] = rgbCodes[2];
    codesOut[codeOffset + 3u] = rgbCodes[3];
    codesOut[codeOffset + 4u] = rgbCodes[4];
    codesOut[codeOffset + 5u] = rgbCodes[5];

    uqE0 = float4(uqRgb0, 65535.0);
    uqE1 = float4(uqRgb1, 65535.0);

    if (effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA)
    {
        uint a0 = (uint)clamp(floor(e0.a / 257.0 + 0.5), 0.0, 255.0);
        uint a1 = (uint)clamp(floor(e1.a / 257.0 + 0.5), 0.0, 255.0);
        uint uqA0, uqA1;
        codesOut[codeOffset + 6u] = AstcQuantizeColor(a0, epQuant, uqA0);
        codesOut[codeOffset + 7u] = AstcQuantizeColor(a1, epQuant, uqA1);
        uqE0.a = (float)(uqA0 * 257u);
        uqE1.a = (float)(uqA1 * 257u);
    }
    else if (effMode == ASTC_EFF_HDR_RGBA)
    {
        uint cA0, cA1;
        float uqA0, uqA1;
        AstcQuantizeHdrAlphaEndpoints(e0.a, e1.a, epQuant, cA0, cA1, uqA0, uqA1);
        codesOut[codeOffset + 6u] = cA0;
        codesOut[codeOffset + 7u] = cA1;
        uqE0.a = uqA0;
        uqE1.a = uqA1;
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

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
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
    for (uint y2 = 0u; y2 < BLOCK_Y; ++y2)
    {
        for (uint x2 = 0u; x2 < BLOCK_X; ++x2)
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
    for (uint y3 = 0u; y3 < BLOCK_Y; ++y3)
    {
        for (uint x3 = 0u; x3 < BLOCK_X; ++x3)
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
    for (uint iter = 0u; iter < refineIters; ++iter)
    {
        float4 dir = (e1 - e0) * mask;
        if (dot(dir, dir) <= 1e-6) break;

        float a00_rgb = 0.0, a01_rgb = 0.0, a11_rgb = 0.0;
        float a00_a   = 0.0, a01_a   = 0.0, a11_a   = 0.0;
        float4 b0 = 0.0, b1 = 0.0;

        for (uint yr = 0u; yr < BLOCK_Y; ++yr)
        {
            for (uint xr = 0u; xr < BLOCK_X; ++xr)
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

// Projects block pixels onto (e0, e1) and decimates onto a (gridW, gridH) weight grid.
void AstcComputeWeightGrid1P(
    uint baseX, uint baseY,
    uint effMode,
    uint excludedChannel,
    float4 e0, float4 e1,
    uint gridW, uint gridH,
    out float gridW01[36])
{
    uint gridCount = gridW * gridH;
    float num[36];
    float den[36];
    for (uint i = 0u; i < 36u; ++i)
    {
        gridW01[i] = 0.0;
        num[i] = 0.0;
        den[i] = 0.0;
    }

    float4 mask = AstcChannelMaskEx(effMode, excludedChannel);
    float4 dir = (e1 - e0) * mask;
    bool exactGrid = (gridW == BLOCK_X && gridH == BLOCK_Y);
    bool alphaWeightsRgbOnly = (mask.a == 0.0 && effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u);

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            float4 wDir = dir * chImp;
            float denom = dot(wDir, dir);
            float t = (denom > 1e-6) ? saturate(dot((p - e0) * mask, wDir) / denom) : 0.0;

            if (exactGrid)
            {
                gridW01[y * gridW + x] = t;
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

                num[idx00] += f00 * t; den[idx00] += f00;
                if (tw.y > 0u) { num[idx00 + 1u] += f10 * t;         den[idx00 + 1u] += f10; }
                if (tw.z > 0u) { num[idx00 + gridW] += f01 * t;      den[idx00 + gridW] += f01; }
                if (tw.w > 0u) { num[idx00 + gridW + 1u] += f11 * t; den[idx00 + gridW + 1u] += f11; }
            }
        }
    }

    if (!exactGrid)
    {
        for (uint k = 0u; k < gridCount; ++k)
        {
            gridW01[k] = (den[k] > 1e-6) ? saturate(num[k] / den[k]) : 0.0;
        }
    }
}

// Projects a single channel (plane 1, CCS in 0..3) onto [c0, c1] and decimates onto a (gridW, gridH) weight grid.
void AstcComputePlane1WeightGrid(
    uint baseX, uint baseY,
    uint effMode,
    uint plane1Channel,
    float c0, float c1,
    uint gridW, uint gridH,
    out float gridW01[36])
{
    uint gridCount = gridW * gridH;
    float num[36];
    float den[36];
    for (uint i = 0u; i < 36u; ++i)
    {
        gridW01[i] = 0.0;
        num[i] = 0.0;
        den[i] = 0.0;
    }

    float delta = c1 - c0;
    float invDelta = (abs(delta) > 1e-6) ? (1.0 / delta) : 0.0;
    bool exactGrid = (gridW == BLOCK_X && gridH == BLOCK_Y);
    bool weightByAlpha = (plane1Channel < 3u && effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u);

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float pv = AstcGetChannel(p, plane1Channel);
            float t = (abs(delta) > 1e-6) ? saturate((pv - c0) * invDelta) : 0.0;

            if (exactGrid)
            {
                gridW01[y * gridW + x] = t;
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

                num[idx00] += f00 * t; den[idx00] += f00;
                if (tw.y > 0u) { num[idx00 + 1u] += f10 * t;         den[idx00 + 1u] += f10; }
                if (tw.z > 0u) { num[idx00 + gridW] += f01 * t;      den[idx00 + gridW] += f01; }
                if (tw.w > 0u) { num[idx00 + gridW + 1u] += f11 * t; den[idx00 + gridW + 1u] += f11; }
            }
        }
    }

    if (!exactGrid)
    {
        for (uint k = 0u; k < gridCount; ++k)
        {
            gridW01[k] = (den[k] > 1e-6) ? saturate(num[k] / den[k]) : 0.0;
        }
    }
}

#if !defined(ASTC_FAST_ONLY)

// Projects 2-partition block pixels onto partition 0/1 endpoints and decimates onto (gridW, gridH).
void AstcComputeWeightGrid2P(
    uint baseX, uint baseY,
    uint effMode,
    uint partSeed,
    float4 e0_p0, float4 e1_p0,
    float4 e0_p1, float4 e1_p1,
    uint gridW, uint gridH,
    out float gridW01[36])
{
    uint gridCount = gridW * gridH;
    float num[36];
    float den[36];
    for (uint i = 0u; i < 36u; ++i)
    {
        gridW01[i] = 0.0;
        num[i] = 0.0;
        den[i] = 0.0;
    }

    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float4 dir0 = (e1_p0 - e0_p0) * mask;
    float4 dir1 = (e1_p1 - e0_p1) * mask;

    bool exactGrid = (gridW == BLOCK_X && gridH == BLOCK_Y);
    bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint part = AstcSelectPartition2(partSeed, x, y, smallBlock);
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
                gridW01[y * gridW + x] = t;
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

                num[idx00] += f00 * t; den[idx00] += f00;
                if (tw.y > 0u) { num[idx00 + 1u] += f10 * t;         den[idx00 + 1u] += f10; }
                if (tw.z > 0u) { num[idx00 + gridW] += f01 * t;      den[idx00 + gridW] += f01; }
                if (tw.w > 0u) { num[idx00 + gridW + 1u] += f11 * t; den[idx00 + gridW + 1u] += f11; }
            }
        }
    }

    if (!exactGrid)
    {
        for (uint k = 0u; k < gridCount; ++k)
        {
            gridW01[k] = (den[k] > 1e-6) ? saturate(num[k] / den[k]) : 0.0;
        }
    }
}

// Evaluates exact hardware-reconstructed MSE for a 1-partition single-plane candidate.
float AstcEvalError1P(
    uint baseX, uint baseY,
    uint effMode,
    float4 e0, float4 e1,
    uint gridW, uint gridH,
    uint wQuant, uint epQuant)
{
    uint epCodes[16];
    for (uint e = 0u; e < 16u; ++e) epCodes[e] = 0u;
    float4 uqE0, uqE1;
    AstcQuantizeEndpoints(e0, e1, effMode, epQuant, epCodes, 0u, uqE0, uqE1);

    float gridW01[36];
    AstcComputeWeightGrid1P(baseX, baseY, effMode, 4u, uqE0, uqE1, gridW, gridH, gridW01);

    uint gridUq[36];
    uint gridCount = gridW * gridH;
    for (uint i = 0u; i < 36u; ++i) gridUq[i] = 0u;
    for (uint k = 0u; k < gridCount; ++k)
    {
        uint uqW;
        AstcQuantizeWeight(gridW01[k], wQuant, uqW);
        gridUq[k] = uqW;
    }

    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float totalErr = 0.0;
    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            uint w64;
            ASTC_EVAL_INFILL(gridUq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w64);
            float4 recon = lerp(uqE0, uqE1, (float)w64 * (1.0 / 64.0));
            float4 diff = (p - recon) * mask;
            if (effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u)
            {
                totalErr += dot(diff.rgb, diff.rgb) * imp + diff.a * diff.a;
            }
            else
            {
                totalErr += dot(diff, diff);
            }
        }
    }
    return totalErr;
}

// Finds which active channel (0..2 for RGB, 0..3 for RGBA) has the largest residual error under the winning 1P candidate.
uint AstcFindWorstChannel1P(
    uint baseX, uint baseY,
    uint effMode,
    AstcCandidate best1P,
    bool hasAlpha)
{
    uint epCodes[16];
    for (uint e = 0u; e < 16u; ++e) epCodes[e] = 0u;
    float4 uqE0, uqE1;
    AstcQuantizeEndpoints(best1P.e0, best1P.e1, effMode, best1P.epQuant, epCodes, 0u, uqE0, uqE1);

    float gridW01[36];
    AstcComputeWeightGrid1P(baseX, baseY, effMode, 4u, uqE0, uqE1, best1P.gridW, best1P.gridH, gridW01);

    uint gridUq[36];
    uint gridCount = best1P.gridW * best1P.gridH;
    for (uint i = 0u; i < 36u; ++i) gridUq[i] = 0u;
    for (uint k = 0u; k < gridCount; ++k)
    {
        uint uqW;
        AstcQuantizeWeight(gridW01[k], best1P.wQuant, uqW);
        gridUq[k] = uqW;
    }

    float4 chanErr = 0.0;
    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w64;
            ASTC_EVAL_INFILL(gridUq, x, y, BLOCK_X, BLOCK_Y, best1P.gridW, best1P.gridH, w64);
            float4 recon = lerp(uqE0, uqE1, (float)w64 * (1.0 / 64.0));
            float4 diff = p - recon;
            chanErr += diff * diff * chImp;
        }
    }

    uint worstCh = 0u;
    float maxErr = chanErr.r;
    if (chanErr.g > maxErr) { maxErr = chanErr.g; worstCh = 1u; }
    if (chanErr.b > maxErr) { maxErr = chanErr.b; worstCh = 2u; }
    if (hasAlpha && chanErr.a > maxErr) { worstCh = 3u; }
    return worstCh;
}

// Evaluates exact hardware-reconstructed error for a 1-partition dual-plane (CCS in 0..3) candidate.
// Uses tangent-space angular error for Normal maps and alpha-weighted MSE for RGBA.
float AstcEvalErrorDualPlane(
    uint baseX, uint baseY,
    uint effMode,
    uint ccs,
    float4 e0, float4 e1,
    uint gridW, uint gridH,
    uint wQuant, uint epQuant)
{
    uint epCodes[16];
    for (uint e = 0u; e < 16u; ++e) epCodes[e] = 0u;
    float4 uqE0, uqE1;
    AstcQuantizeEndpoints(e0, e1, effMode, epQuant, epCodes, 0u, uqE0, uqE1);

    float grid0W01[36];
    float grid1W01[36];
    AstcComputeWeightGrid1P(baseX, baseY, effMode, ccs, uqE0, uqE1, gridW, gridH, grid0W01);
    AstcComputePlane1WeightGrid(baseX, baseY, effMode, ccs, AstcGetChannel(uqE0, ccs), AstcGetChannel(uqE1, ccs), gridW, gridH, grid1W01);

    uint grid0Uq[36];
    uint grid1Uq[36];
    uint gridCount = gridW * gridH;
    for (uint i = 0u; i < 36u; ++i) { grid0Uq[i] = 0u; grid1Uq[i] = 0u; }
    for (uint k = 0u; k < gridCount; ++k)
    {
        uint uq0, uq1;
        AstcQuantizeWeight(grid0W01[k], wQuant, uq0);
        AstcQuantizeWeight(grid1W01[k], wQuant, uq1);
        grid0Uq[k] = uq0;
        grid1Uq[k] = uq1;
    }

    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float totalErr = 0.0;
    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            uint w0;
            uint w1;
            ASTC_EVAL_INFILL(grid0Uq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w0);
            ASTC_EVAL_INFILL(grid1Uq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w1);
            float4 recon = lerp(uqE0, uqE1, (float)w0 * (1.0 / 64.0));
            float p1Val = lerp(AstcGetChannel(uqE0, ccs), AstcGetChannel(uqE1, ccs), (float)w1 * (1.0 / 64.0));
            recon = AstcSetChannel(recon, ccs, p1Val);

            if (effMode == ASTC_EFF_LDR_NORMAL)
            {
                // Optimize tangent-space angular error: 1 - dot(n_orig, n_recon) with reconstructed Z.
                float2 nOrig = float2(p.w, p.x) * (2.0 / 255.0) - 1.0;
                float zOrig = sqrt(saturate(1.0 - dot(nOrig, nOrig)));
                float2 nRec = float2(recon.a, recon.r) * (2.0 / 255.0) - 1.0;
                float zRec = sqrt(saturate(1.0 - dot(nRec, nRec)));
                totalErr += max(0.0, 1.0 - (dot(nOrig, nRec) + zOrig * zRec));
            }
            else
            {
                float4 diff = (p - recon) * mask;
                if (effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u)
                {
                    totalErr += dot(diff.rgb, diff.rgb) * imp + diff.a * diff.a;
                }
                else
                {
                    totalErr += dot(diff, diff);
                }
            }
        }
    }
    return totalErr;
}

// Evaluates exact hardware-reconstructed MSE for a 2-partition candidate.
float AstcEvalError2P(
    uint baseX, uint baseY,
    uint effMode,
    uint partSeed,
    float4 e0_p0, float4 e1_p0,
    float4 e0_p1, float4 e1_p1,
    uint gridW, uint gridH,
    uint wQuant, uint epQuant)
{
    uint epCodes[16];
    for (uint e = 0u; e < 16u; ++e) epCodes[e] = 0u;
    uint epPerPart = AstcGetEpCountForEffMode(effMode);
    float4 uq0_p0, uq1_p0, uq0_p1, uq1_p1;
    AstcQuantizeEndpoints(e0_p0, e1_p0, effMode, epQuant, epCodes, 0u, uq0_p0, uq1_p0);
    AstcQuantizeEndpoints(e0_p1, e1_p1, effMode, epQuant, epCodes, epPerPart, uq0_p1, uq1_p1);

    float gridW01[36];
    AstcComputeWeightGrid2P(baseX, baseY, effMode, partSeed, uq0_p0, uq1_p0, uq0_p1, uq1_p1, gridW, gridH, gridW01);

    uint gridUq[36];
    uint gridCount = gridW * gridH;
    for (uint i = 0u; i < 36u; ++i) gridUq[i] = 0u;
    for (uint k = 0u; k < gridCount; ++k)
    {
        uint uqW;
        AstcQuantizeWeight(gridW01[k], wQuant, uqW);
        gridUq[k] = uqW;
    }

    float4 mask = AstcChannelMaskEx(effMode, 4u);
    bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;
    float totalErr = 0.0;
    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            uint part = AstcSelectPartition2(partSeed, x, y, smallBlock);
            uint w64;
            ASTC_EVAL_INFILL(gridUq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w64);
            float4 recon = (part == 0u)
                ? lerp(uq0_p0, uq1_p0, (float)w64 * (1.0 / 64.0))
                : lerp(uq0_p1, uq1_p1, (float)w64 * (1.0 / 64.0));
            float4 diff = (p - recon) * mask;
            if (effMode == ASTC_EFF_LDR_RGBA && _AstcUseAlphaWeight != 0u)
            {
                totalErr += dot(diff.rgb, diff.rgb) * imp + diff.a * diff.a;
            }
            else
            {
                totalErr += dot(diff, diff);
            }
        }
    }
    return totalErr;
}

// Post-quantization ideal endpoint refit for 1P single-plane blocks:
// Solves closed-form 2x2 least squares against the exact hardware-interpolated quantized weights,
// adopting the refitted endpoints only if they strictly reduce exact reconstruction error.
void AstcRefitEndpoints1P(
    uint baseX, uint baseY,
    uint effMode,
    uint gridW, uint gridH,
    uint gridUq[36],
    uint epQuant,
    float4 curUqE0, float4 curUqE1,
    inout uint epCodes[16])
{
    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float origErr = 0.0;
    float a00_rgb = 0.0, a01_rgb = 0.0, a11_rgb = 0.0;
    float a00_a   = 0.0, a01_a   = 0.0, a11_a   = 0.0;
    float4 b0 = 0.0, b1 = 0.0;

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w64;
            ASTC_EVAL_INFILL(gridUq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w64);
            float w = (float)w64 * (1.0 / 64.0);
            float omw = 1.0 - w;

            float4 recon = lerp(curUqE0, curUqE1, w);
            float4 diff = (p - recon) * mask;
            origErr += dot(diff * diff, chImp);

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

    float4 refitE0 = curUqE0;
    float4 refitE1 = curUqE1;
    bool changed = false;

    float detRgb = a00_rgb * a11_rgb - a01_rgb * a01_rgb;
    if (detRgb > 1e-4)
    {
        float invDet = 1.0 / detRgb;
        refitE0.rgb = AstcClampWorkingColor(float4((a11_rgb * b0.rgb - a01_rgb * b1.rgb) * invDet, 0.0), effMode).rgb;
        refitE1.rgb = AstcClampWorkingColor(float4((a00_rgb * b1.rgb - a01_rgb * b0.rgb) * invDet, 0.0), effMode).rgb;
        changed = true;
    }

    float detA = a00_a * a11_a - a01_a * a01_a;
    if (mask.a > 0.0 && detA > 1e-4)
    {
        float invDetA = 1.0 / detA;
        refitE0.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, (a11_a * b0.a - a01_a * b1.a) * invDetA), effMode).a;
        refitE1.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, (a00_a * b1.a - a01_a * b0.a) * invDetA), effMode).a;
        changed = true;
    }

    if (!changed) return;

    uint candCodes[16];
    for (uint e = 0u; e < 16u; ++e) candCodes[e] = 0u;
    float4 uq0, uq1;
    AstcQuantizeEndpoints(refitE0, refitE1, effMode, epQuant, candCodes, 0u, uq0, uq1);

    float refitErr = 0.0;
    for (uint y2 = 0u; y2 < BLOCK_Y; ++y2)
    {
        for (uint x2 = 0u; x2 < BLOCK_X; ++x2)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x2, y2, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w64;
            ASTC_EVAL_INFILL(gridUq, x2, y2, BLOCK_X, BLOCK_Y, gridW, gridH, w64);
            float4 recon = lerp(uq0, uq1, (float)w64 * (1.0 / 64.0));
            float4 diff = (p - recon) * mask;
            refitErr += dot(diff * diff, chImp);
        }
    }

    if (refitErr < origErr)
    {
        for (uint ei = 0u; ei < 8u; ++ei) epCodes[ei] = candCodes[ei];
    }
}

// Post-quantization ideal endpoint refit for Dual-Plane blocks (independent 2x2 LS per weight plane).
void AstcRefitEndpointsDualPlane(
    uint baseX, uint baseY,
    uint effMode,
    uint ccs,
    uint gridW, uint gridH,
    uint grid0Uq[36],
    uint grid1Uq[36],
    uint epQuant,
    float4 curUqE0, float4 curUqE1,
    inout uint epCodes[16])
{
    float4 mask = AstcChannelMaskEx(effMode, 4u);
    float origErr = 0.0;

    float a00_p0 = 0.0, a01_p0 = 0.0, a11_p0 = 0.0;
    float a00_p0a = 0.0, a01_p0a = 0.0, a11_p0a = 0.0;
    float a00_p1 = 0.0, a01_p1 = 0.0, a11_p1 = 0.0;
    float4 b0_p0 = 0.0, b1_p0 = 0.0;
    float b0_p1 = 0.0, b1_p1 = 0.0;

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w0_64, w1_64;
            ASTC_EVAL_INFILL(grid0Uq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w0_64);
            ASTC_EVAL_INFILL(grid1Uq, x, y, BLOCK_X, BLOCK_Y, gridW, gridH, w1_64);
            float w0 = (float)w0_64 * (1.0 / 64.0);
            float w1 = (float)w1_64 * (1.0 / 64.0);
            float omw0 = 1.0 - w0;
            float omw1 = 1.0 - w1;

            float4 recon = lerp(curUqE0, curUqE1, w0);
            recon = AstcSetChannel(recon, ccs, lerp(AstcGetChannel(curUqE0, ccs), AstcGetChannel(curUqE1, ccs), w1));

            if (effMode == ASTC_EFF_LDR_NORMAL)
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

            float p1Imp = AstcGetChannel(chImp, ccs);
            float p1Val = AstcGetChannel(p, ccs);
            a00_p1 += p1Imp * omw1 * omw1;
            a01_p1 += p1Imp * w1 * omw1;
            a11_p1 += p1Imp * w1 * w1;
            b0_p1 += p1Val * (p1Imp * omw1);
            b1_p1 += p1Val * (p1Imp * w1);
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
    if (ccs != 3u && mask.a > 0.0 && detP0A > 1e-4)
    {
        float invDetA = 1.0 / detP0A;
        refitE0.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, (a11_p0a * b0_p0.a - a01_p0a * b1_p0.a) * invDetA), effMode).a;
        refitE1.a = AstcClampWorkingColor(float4(0.0, 0.0, 0.0, (a00_p0a * b1_p0.a - a01_p0a * b0_p0.a) * invDetA), effMode).a;
        changed = true;
    }

    float detP1 = a00_p1 * a11_p1 - a01_p1 * a01_p1;
    if (detP1 > 1e-4)
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

    uint candCodes[16];
    for (uint e = 0u; e < 16u; ++e) candCodes[e] = 0u;
    float4 uq0, uq1;
    AstcQuantizeEndpoints(refitE0, refitE1, effMode, epQuant, candCodes, 0u, uq0, uq1);

    float refitErr = 0.0;
    for (uint y2 = 0u; y2 < BLOCK_Y; ++y2)
    {
        for (uint x2 = 0u; x2 < BLOCK_X; ++x2)
        {
            float imp;
            float4 p = AstcFetchWorkingPixel(baseX, baseY, x2, y2, effMode, imp);
            float4 chImp = AstcGetChannelImp(effMode, imp);
            uint w0_64, w1_64;
            ASTC_EVAL_INFILL(grid0Uq, x2, y2, BLOCK_X, BLOCK_Y, gridW, gridH, w0_64);
            ASTC_EVAL_INFILL(grid1Uq, x2, y2, BLOCK_X, BLOCK_Y, gridW, gridH, w1_64);
            float4 recon = lerp(uq0, uq1, (float)w0_64 * (1.0 / 64.0));
            recon = AstcSetChannel(recon, ccs, lerp(AstcGetChannel(uq0, ccs), AstcGetChannel(uq1, ccs), (float)w1_64 * (1.0 / 64.0)));

            if (effMode == ASTC_EFF_LDR_NORMAL)
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

    if (refitErr < origErr)
    {
        for (uint ei = 0u; ei < 8u; ++ei) epCodes[ei] = candCodes[ei];
    }
}

#endif // !ASTC_FAST_ONLY

// Assembles the final winning AstcCandidate into a 128-bit physical ASTC block.
uint4 AstcAssembleBlock(uint baseX, uint baseY, uint effMode, AstcCandidate c)
{
    uint epCodes[16];
    for (uint e = 0u; e < 16u; ++e) epCodes[e] = 0u;

    uint epPerPart = AstcGetEpCountForEffMode(effMode);
    uint cem = AstcGetCemForEffMode(effMode);
    uint wCodes[64];
    for (uint wi = 0u; wi < 64u; ++wi) wCodes[wi] = 0u;

    uint gridCount = c.gridW * c.gridH;
    uint totalWeights = gridCount;

#if !defined(ASTC_FAST_ONLY)
    if (c.isTwoPart != 0u)
    {
        float4 uq0_p0, uq1_p0, uq0_p1, uq1_p1;
        AstcQuantizeEndpoints(c.e0, c.e1, effMode, c.epQuant, epCodes, 0u, uq0_p0, uq1_p0);
        AstcQuantizeEndpoints(c.e0_p1, c.e1_p1, effMode, c.epQuant, epCodes, epPerPart, uq0_p1, uq1_p1);

        float gridW01[36];
        AstcComputeWeightGrid2P(baseX, baseY, effMode, c.partSeed, uq0_p0, uq1_p0, uq0_p1, uq1_p1, c.gridW, c.gridH, gridW01);
        for (uint k = 0u; k < gridCount; ++k)
        {
            uint dummyUq;
            wCodes[k] = AstcQuantizeWeight(gridW01[k], c.wQuant, dummyUq);
        }
    }
    else
#endif
    if (c.isDualPlane != 0u)
    {
        float4 uqE0, uqE1;
        AstcQuantizeEndpoints(c.e0, c.e1, effMode, c.epQuant, epCodes, 0u, uqE0, uqE1);

        float grid0W01[36];
        float grid1W01[36];
        AstcComputeWeightGrid1P(baseX, baseY, effMode, c.dualPlaneChannel, uqE0, uqE1, c.gridW, c.gridH, grid0W01);
        AstcComputePlane1WeightGrid(baseX, baseY, effMode, c.dualPlaneChannel, AstcGetChannel(uqE0, c.dualPlaneChannel), AstcGetChannel(uqE1, c.dualPlaneChannel), c.gridW, c.gridH, grid1W01);

#if !defined(ASTC_FAST_ONLY)
        uint grid0Uq[36];
        uint grid1Uq[36];
        for (uint zi = 0u; zi < 36u; ++zi) { grid0Uq[zi] = 0u; grid1Uq[zi] = 0u; }
#endif
        for (uint k = 0u; k < gridCount; ++k)
        {
            uint uq0, uq1;
            wCodes[2u * k]      = AstcQuantizeWeight(grid0W01[k], c.wQuant, uq0);
            wCodes[2u * k + 1u] = AstcQuantizeWeight(grid1W01[k], c.wQuant, uq1);
#if !defined(ASTC_FAST_ONLY)
            grid0Uq[k] = uq0;
            grid1Uq[k] = uq1;
#endif
        }
        totalWeights = gridCount * 2u;

#if !defined(ASTC_FAST_ONLY)
        if (_AstcQuality >= 1u)
        {
            AstcRefitEndpointsDualPlane(baseX, baseY, effMode, c.dualPlaneChannel, c.gridW, c.gridH, grid0Uq, grid1Uq, c.epQuant, uqE0, uqE1, epCodes);
        }
#endif
    }
    else
    {
        float4 uqE0, uqE1;
        AstcQuantizeEndpoints(c.e0, c.e1, effMode, c.epQuant, epCodes, 0u, uqE0, uqE1);

        float gridW01[36];
        AstcComputeWeightGrid1P(baseX, baseY, effMode, 4u, uqE0, uqE1, c.gridW, c.gridH, gridW01);

#if !defined(ASTC_FAST_ONLY)
        uint gridUq[36];
        for (uint zi = 0u; zi < 36u; ++zi) gridUq[zi] = 0u;
#endif
        for (uint k = 0u; k < gridCount; ++k)
        {
            uint uqW;
            wCodes[k] = AstcQuantizeWeight(gridW01[k], c.wQuant, uqW);
#if !defined(ASTC_FAST_ONLY)
            gridUq[k] = uqW;
#endif
        }

#if !defined(ASTC_FAST_ONLY)
        if (_AstcQuality >= 1u)
        {
            AstcRefitEndpoints1P(baseX, baseY, effMode, c.gridW, c.gridH, gridUq, c.epQuant, uqE0, uqE1, epCodes);
        }
#endif
    }

    // Encode weights at bit 0 into wBuf, then reverse all 128 bits so weights sit at MSB descending.
    uint4 wBuf = uint4(0u, 0u, 0u, 0u);
    AstcEncodeBise64(wBuf, 0u, c.wQuant, totalWeights, wCodes);
    uint4 block = AstcReverse128(wBuf);

    // Header: BlockMode (11 bits) + PartitionCount-1 (2 bits)
    AstcWriteBits128(block, 0u, 11u, c.blockMode);
#if !defined(ASTC_FAST_ONLY)
    if (c.isTwoPart != 0u)
    {
        AstcWriteBits128(block, 11u, 2u, 1u);
        AstcWriteBits128(block, 13u, 10u, c.partSeed);
        // Matched CEM for both partitions: low 2 bits = 00, upper 4 bits = cem
        AstcWriteBits128(block, 23u, 6u, cem << 2u);
        AstcEncodeBise16(block, 29u, c.epQuant, epPerPart * 2u, epCodes);
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
        AstcEncodeBise16(block, 17u, c.epQuant, epPerPart, epCodes);
    }

    return block;
}

#if !defined(ASTC_FAST_ONLY)

void AstcTryCandidate1P(
    uint baseX, uint baseY, uint effMode,
    float4 e0, float4 e1,
    uint bm, uint gw, uint gh, uint wQ, uint epQ,
    inout AstcCandidate best)
{
    float err = AstcEvalError1P(baseX, baseY, effMode, e0, e1, gw, gh, wQ, epQ);
    if (err < best.error)
    {
        best.blockMode = bm;
        best.gridW = gw;
        best.gridH = gh;
        best.wQuant = wQ;
        best.epQuant = epQ;
        best.isDualPlane = 0u;
        best.dualPlaneChannel = 3u;
        best.isTwoPart = 0u;
        best.partSeed = 0u;
        best.error = err;
        best.e0 = e0;
        best.e1 = e1;
    }
}

void AstcTryCandidateDualPlane(
    uint baseX, uint baseY, uint effMode,
    uint ccs,
    float4 e0, float4 e1,
    uint bm, uint gw, uint gh, uint wQ, uint epQ,
    inout AstcCandidate best)
{
    float err = AstcEvalErrorDualPlane(baseX, baseY, effMode, ccs, e0, e1, gw, gh, wQ, epQ);
    if (err < best.error)
    {
        best.blockMode = bm;
        best.gridW = gw;
        best.gridH = gh;
        best.wQuant = wQ;
        best.epQuant = epQ;
        best.isDualPlane = 1u;
        best.dualPlaneChannel = ccs;
        best.isTwoPart = 0u;
        best.partSeed = 0u;
        best.error = err;
        best.e0 = e0;
        best.e1 = e1;
    }
}

void AstcTryCandidate2P(
    uint baseX, uint baseY, uint effMode,
    uint partSeed,
    float4 e0_p0, float4 e1_p0,
    float4 e0_p1, float4 e1_p1,
    uint bm, uint gw, uint gh, uint wQ, uint epQ,
    inout AstcCandidate best)
{
    float err = AstcEvalError2P(baseX, baseY, effMode, partSeed, e0_p0, e1_p0, e0_p1, e1_p1, gw, gh, wQ, epQ);
    if (err < best.error)
    {
        best.blockMode = bm;
        best.gridW = gw;
        best.gridH = gh;
        best.wQuant = wQ;
        best.epQuant = epQ;
        best.isDualPlane = 0u;
        best.dualPlaneChannel = 3u;
        best.isTwoPart = 1u;
        best.partSeed = partSeed;
        best.error = err;
        best.e0 = e0_p0;
        best.e1 = e1_p0;
        best.e0_p1 = e0_p1;
        best.e1_p1 = e1_p1;
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
// Evaluates 16 curated seeds in 4 batches of 4 to cut texture fetches by 4x without dynamic array indexing.
uint AstcSelectBestSeed2P(uint baseX, uint baseY, uint effMode)
{
    float4 mask = AstcChannelMaskEx(effMode, 4u);
    bool smallBlock = (BLOCK_X * BLOCK_Y) < 32u;

    uint bestSeed = 17u;
    float bestScore = -1.0;

    for (uint batch = 0u; batch < 16u; batch += 4u)
    {
        uint seed0 = AstcGetCuratedSeed(batch + 0u);
        uint seed1 = AstcGetCuratedSeed(batch + 1u);
        uint seed2 = AstcGetCuratedSeed(batch + 2u);
        uint seed3 = AstcGetCuratedSeed(batch + 3u);

        float4 sumAll = 0.0;
        float4 cntAll = 0.0;

        float4 s1_0 = 0.0, s1_1 = 0.0, s1_2 = 0.0, s1_3 = 0.0;
        float4 c1_0 = 0.0, c1_1 = 0.0, c1_2 = 0.0, c1_3 = 0.0;

        for (uint y = 0u; y < BLOCK_Y; ++y)
        {
            for (uint x = 0u; x < BLOCK_X; ++x)
            {
                float imp;
                float4 p = AstcFetchWorkingPixel(baseX, baseY, x, y, effMode, imp);
                float4 chImp = AstcGetChannelImp(effMode, imp) * mask;
                float4 pw = p * chImp;
                sumAll += pw;
                cntAll += chImp;

                if (AstcSelectPartition2(seed0, x, y, smallBlock) != 0u) { s1_0 += pw; c1_0 += chImp; }
                if (AstcSelectPartition2(seed1, x, y, smallBlock) != 0u) { s1_1 += pw; c1_1 += chImp; }
                if (AstcSelectPartition2(seed2, x, y, smallBlock) != 0u) { s1_2 += pw; c1_2 += chImp; }
                if (AstcSelectPartition2(seed3, x, y, smallBlock) != 0u) { s1_3 += pw; c1_3 += chImp; }
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

    for (uint y = 0u; y < BLOCK_Y; ++y)
    {
        for (uint x = 0u; x < BLOCK_X; ++x)
        {
            uint px = min(baseX + x, _AstcSrcWidth - 1u);
            uint py = min(baseY + y, _AstcSrcHeight - 1u);
            float4 r = _AstcSourceTex.Load(int3(px, py, _AstcMipLevel));
            if (isHdr)
            {
                // Sanitize NaN / Inf / negative values consistently with AstcFloatToLns & AstcFloatToHalfU16
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
            uint4 c8;
            if (effMode == ASTC_EFF_LDR_NORMAL)
            {
                float nr = (_AstcIsSRGB != 0u) ? AstcLinearToSRGBScalar(c.r) : saturate(c.r);
                float ng = (_AstcIsSRGB != 0u) ? AstcLinearToSRGBScalar(c.g) : saturate(c.g);
                uint g8 = (uint)clamp(floor(ng * 255.0 + 0.5), 0.0, 255.0);
                uint r8 = (uint)clamp(floor(nr * 255.0 + 0.5), 0.0, 255.0);
                c8 = uint4(g8, g8, g8, r8);
            }
            else
            {
                float3 rgb = (_AstcIsSRGB != 0u) ? AstcLinearToSRGB(c.rgb) : saturate(c.rgb);
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

    if (effMode == ASTC_EFF_LDR_NORMAL)
    {
        float4 nE0, nE1;
        AstcFitSubsetEndpoints(baseX, baseY, effMode, 3u, 0u, 2u, 0u, nE0, nE1);

#if defined(ASTC_FAST_ONLY)
        best.blockMode = 0x452u;
        best.gridW = 4u;
        best.gridH = 4u;
        best.wQuant = ASTC_QUANT_5;
        best.epQuant = ASTC_QUANT_256;
        best.isDualPlane = 1u;
        best.dualPlaneChannel = 3u;
        best.e0 = nE0;
        best.e1 = nE1;
        return AstcAssembleBlock(baseX, baseY, effMode, best);
#else
        if (_AstcQuality == 0u)
        {
            best.blockMode = 0x452u;
            best.gridW = 4u;
            best.gridH = 4u;
            best.wQuant = ASTC_QUANT_5;
            best.epQuant = ASTC_QUANT_256;
            best.isDualPlane = 1u;
            best.dualPlaneChannel = 3u;
            best.e0 = nE0;
            best.e1 = nE1;
            return AstcAssembleBlock(baseX, baseY, effMode, best);
        }

        // Balanced / High: compare Dual-Plane 4x4 (and 5x5 on >=5x5 blocks) using tangent-space angular error
#if BLOCK_X >= 5
        uint normCandCount = 4u;
#else
        uint normCandCount = 2u;
#endif
        for (uint ni = 0u; ni < normCandCount; ++ni)
        {
            uint4 cfg = (ni == 0u) ? uint4(0x452u, 4u, ASTC_QUANT_5, ASTC_QUANT_256)
                      : (ni == 1u) ? uint4(0x442u, 4u, ASTC_QUANT_4, ASTC_QUANT_256)
                      : (ni == 2u) ? uint4(0x4F1u, 5u, ASTC_QUANT_3, ASTC_QUANT_128)
                                   : uint4(0x4E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_256);
            AstcTryCandidateDualPlane(baseX, baseY, effMode, 3u, nE0, nE1, cfg.x, cfg.y, cfg.y, cfg.z, cfg.w, best);
        }
        return AstcAssembleBlock(baseX, baseY, effMode, best);
#endif
    }

    // ------------------------------------------------------------------------
    // Step 4: Color / HDR 1-Partition Endpoint Fitting
    // ------------------------------------------------------------------------
#if defined(ASTC_FAST_ONLY)
    uint refineIters = 0u;
#else
    uint refineIters = (_AstcQuality >= 1u) ? 2u : 0u;
#endif
    float4 e0, e1;
    AstcFitSubsetEndpoints(baseX, baseY, effMode, 4u, 0u, 2u, refineIters, e0, e1);

    bool hasAlpha = (effMode == ASTC_EFF_LDR_RGBA || effMode == ASTC_EFF_HDR_RGB_LDR_ALPHA || effMode == ASTC_EFF_HDR_RGBA);

    // ------------------------------------------------------------------------
    // Step 5: Fast Path (ASTC_FAST_ONLY or Quality == 0) - Single optimal mode
    // ------------------------------------------------------------------------
#if defined(ASTC_FAST_ONLY)
    best.e0 = e0;
    best.e1 = e1;
    if (!hasAlpha)
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
    return AstcAssembleBlock(baseX, baseY, effMode, best);
#else
    if (_AstcQuality == 0u)
    {
        best.e0 = e0;
        best.e1 = e1;
        if (!hasAlpha)
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
        return AstcAssembleBlock(baseX, baseY, effMode, best);
    }

    // ------------------------------------------------------------------------
    // Step 6: Balanced / High Path - Multi-BlockMode Candidate Search (including anisotropic grids)
    // ------------------------------------------------------------------------
    uint cand1PCount;
    if (!hasAlpha)
    {
#if BLOCK_X >= 8
        cand1PCount = 12u;
#elif BLOCK_X >= 6
        cand1PCount = 10u;
#elif BLOCK_X >= 5
        cand1PCount = 6u;
#else
        cand1PCount = 3u;
#endif
    }
    else
    {
#if BLOCK_X >= 6
        cand1PCount = 8u;
#elif BLOCK_X >= 5
        cand1PCount = 5u;
#else
        cand1PCount = 3u;
#endif
    }

    for (uint c1 = 0u; c1 < cand1PCount; ++c1)
    {
        uint bm, gw, gh, wQ, epQ;
        if (!hasAlpha)
        {
            if (c1 == 0u)      { bm = 0x251u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_12; epQ = ASTC_QUANT_256; }
            else if (c1 == 1u) { bm = 0x242u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_16; epQ = ASTC_QUANT_192; }
            else if (c1 == 2u) { bm = 0x252u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_20; epQ = ASTC_QUANT_96;  }
            else if (c1 == 3u) { bm = 0x0F2u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_5;  epQ = ASTC_QUANT_256; }
            else if (c1 == 4u) { bm = 0x0E3u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_192; }
            else if (c1 == 5u) { bm = 0x0F3u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_8;  epQ = ASTC_QUANT_64;  }
            else if (c1 == 6u) { bm = 0x114u; gw = 6u; gh = 6u; wQ = ASTC_QUANT_3;  epQ = ASTC_QUANT_256; }
            else if (c1 == 7u) { bm = 0x108u; gw = 6u; gh = 6u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_80;  }
            else if (c1 == 8u) { bm = 0x143u; gw = 6u; gh = 4u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_256; }
            else if (c1 == 9u) { bm = 0x04Fu; gw = 4u; gh = 6u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_256; }
            else if (c1 == 10u){ bm = 0x046u; gw = 8u; gh = 4u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
            else               { bm = 0x04Au; gw = 4u; gh = 8u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
        }
        else
        {
            if (c1 == 0u)      { bm = 0x043u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_6;  epQ = ASTC_QUANT_256; }
            else if (c1 == 1u) { bm = 0x053u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_8;  epQ = ASTC_QUANT_192; }
            else if (c1 == 2u) { bm = 0x251u; gw = 4u; gh = 4u; wQ = ASTC_QUANT_12; epQ = ASTC_QUANT_96;  }
            else if (c1 == 3u) { bm = 0x0E2u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
            else if (c1 == 4u) { bm = 0x0F2u; gw = 5u; gh = 5u; wQ = ASTC_QUANT_5;  epQ = ASTC_QUANT_80;  }
            else if (c1 == 5u) { bm = 0x114u; gw = 6u; gh = 6u; wQ = ASTC_QUANT_3;  epQ = ASTC_QUANT_96;  }
            else if (c1 == 6u) { bm = 0x142u; gw = 6u; gh = 4u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
            else               { bm = 0x04Eu; gw = 4u; gh = 6u; wQ = ASTC_QUANT_4;  epQ = ASTC_QUANT_192; }
        }
        AstcTryCandidate1P(baseX, baseY, effMode, e0, e1, bm, gw, gh, wQ, epQ, best);
    }

    // Dual-Plane with dynamic CCS selection (0=R, 1=G, 2=B, 3=A) for both opaque RGB (ORM/MRAO) and RGBA!
    {
        uint bestCcs = AstcFindWorstChannel1P(baseX, baseY, effMode, best, hasAlpha);
        float4 dpE0, dpE1;
        AstcFitSubsetEndpoints(baseX, baseY, effMode, bestCcs, 0u, 2u, 1u, dpE0, dpE1);
#if BLOCK_X >= 5
        uint dpCount = 3u;
#else
        uint dpCount = 2u;
#endif
        for (uint di = 0u; di < dpCount; ++di)
        {
            uint4 cfg;
            if (!hasAlpha)
            {
                cfg = (di == 0u) ? uint4(0x451u, 4u, ASTC_QUANT_3, ASTC_QUANT_256)
                    : (di == 1u) ? uint4(0x442u, 4u, ASTC_QUANT_4, ASTC_QUANT_160)
                                 : uint4(0x4E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_256);
            }
            else
            {
                cfg = (di == 0u) ? uint4(0x451u, 4u, ASTC_QUANT_3, ASTC_QUANT_128)
                    : (di == 1u) ? uint4(0x442u, 4u, ASTC_QUANT_4, ASTC_QUANT_48)
                                 : uint4(0x4E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_160);
            }
            AstcTryCandidateDualPlane(baseX, baseY, effMode, bestCcs, dpE0, dpE1, cfg.x, cfg.y, cfg.y, cfg.z, cfg.w, best);
        }
    }

    // ------------------------------------------------------------------------
    // Step 7: High Quality Path (Quality >= 2) - 2-Partition Directional Search
    // ------------------------------------------------------------------------
    if (_AstcQuality >= 2u)
    {
        uint bestSeed = AstcSelectBestSeed2P(baseX, baseY, effMode);
        float4 e0_p0, e1_p0, e0_p1, e1_p1;
        AstcFitSubsetEndpoints(baseX, baseY, effMode, 4u, bestSeed, 0u, 1u, e0_p0, e1_p0);
        AstcFitSubsetEndpoints(baseX, baseY, effMode, 4u, bestSeed, 1u, 1u, e0_p1, e1_p1);

#if BLOCK_X >= 5
        uint p2Count = 4u;
#else
        uint p2Count = 2u;
#endif
        for (uint p2 = 0u; p2 < p2Count; ++p2)
        {
            uint4 cfg;
            if (!hasAlpha)
            {
                if (p2 == 0u)      cfg = uint4(0x042u, 4u, ASTC_QUANT_4, ASTC_QUANT_40);
                else if (p2 == 1u) cfg = uint4(0x043u, 4u, ASTC_QUANT_6, ASTC_QUANT_24);
                else if (p2 == 2u) cfg = uint4(0x0F1u, 5u, ASTC_QUANT_3, ASTC_QUANT_24);
                else               cfg = uint4(0x0E2u, 5u, ASTC_QUANT_4, ASTC_QUANT_16);
            }
            else
            {
                if (p2 == 0u)      cfg = uint4(0x051u, 4u, ASTC_QUANT_3, ASTC_QUANT_20);
                else if (p2 == 1u) cfg = uint4(0x042u, 4u, ASTC_QUANT_4, ASTC_QUANT_16);
                else if (p2 == 2u) cfg = uint4(0x0E1u, 5u, ASTC_QUANT_2, ASTC_QUANT_24);
                else               cfg = uint4(0x0F1u, 5u, ASTC_QUANT_3, ASTC_QUANT_12);
            }
            AstcTryCandidate2P(baseX, baseY, effMode, bestSeed, e0_p0, e1_p0, e0_p1, e1_p1, cfg.x, cfg.y, cfg.y, cfg.z, cfg.w, best);
        }
    }

    return AstcAssembleBlock(baseX, baseY, effMode, best);
#endif // !ASTC_FAST_ONLY
}

#endif // ASTC_GPU_CORE_HLSL
