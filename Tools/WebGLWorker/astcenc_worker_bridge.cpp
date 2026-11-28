// Standalone Emscripten Web Worker bridge for ARM astc-encoder.
// Compiles to both WASM SIMD128 (-msimd128 -msse4.1) and WASM Scalar bundles.
// Caches astcenc_context* instances per configuration inside the worker to amortize setup cost.

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <map>
#include <tuple>
#include "astcenc.h"

namespace
{
struct ContextKey
{
    int profile;
    unsigned int block_x;
    unsigned int block_y;
    int quality_bits;
    unsigned int flags;
    unsigned int alpha_scale_radius;

    bool operator<(const ContextKey& o) const
    {
        return std::tie(profile, block_x, block_y, quality_bits, flags, alpha_scale_radius)
             < std::tie(o.profile, o.block_x, o.block_y, o.quality_bits, o.flags, o.alpha_scale_radius);
    }
};

constexpr size_t kMaxCachedContexts = 8;
std::map<ContextKey, astcenc_context*> g_contextCache;

astcenc_context* GetOrCreateContext(
    astcenc_profile profile,
    unsigned int block_x,
    unsigned int block_y,
    float quality,
    unsigned int flags,
    unsigned int alpha_scale_radius,
    astcenc_error* out_err)
{
    int quality_bits = 0;
    std::memcpy(&quality_bits, &quality, sizeof(float));

    ContextKey key{
        static_cast<int>(profile),
        block_x,
        block_y,
        quality_bits,
        flags,
        alpha_scale_radius
    };

    auto it = g_contextCache.find(key);
    if (it != g_contextCache.end())
    {
        *out_err = ASTCENC_SUCCESS;
        return it->second;
    }

    if (g_contextCache.size() >= kMaxCachedContexts)
    {
        auto oldest = g_contextCache.begin();
        astcenc_context_free(oldest->second);
        g_contextCache.erase(oldest);
    }

    astcenc_config cfg{};
    astcenc_error err = astcenc_config_init(profile, block_x, block_y, 1, quality, flags, &cfg);
    if (err != ASTCENC_SUCCESS)
    {
        *out_err = err;
        return nullptr;
    }

    if ((flags & ASTCENC_FLG_USE_ALPHA_WEIGHT) != 0)
    {
        cfg.a_scale_radius = alpha_scale_radius;
    }

    astcenc_context* ctx = nullptr;
    err = astcenc_context_alloc(&cfg, 1, &ctx, nullptr);
    if (err != ASTCENC_SUCCESS || ctx == nullptr)
    {
        *out_err = (err != ASTCENC_SUCCESS) ? err : ASTCENC_ERR_OUT_OF_MEM;
        return nullptr;
    }

    g_contextCache.emplace(key, ctx);
    *out_err = ASTCENC_SUCCESS;
    return ctx;
}
} // namespace

extern "C"
{

int astcenc_worker_is_simd()
{
#if defined(__wasm_simd128__)
    return 1;
#else
    return 0;
#endif
}

void astcenc_worker_clear_cache()
{
    for (auto& kv : g_contextCache)
    {
        astcenc_context_free(kv.second);
    }
    g_contextCache.clear();
}

int astcenc_worker_compress(
    int profile,
    unsigned int block_x,
    unsigned int block_y,
    float quality,
    unsigned int flags,
    unsigned int alpha_scale_radius,
    int swz_r,
    int swz_g,
    int swz_b,
    int swz_a,
    unsigned int width,
    unsigned int height,
    unsigned int mip_count,
    int data_type,
    uint8_t* rgba_in,
    uint8_t* data_out,
    size_t data_out_len)
{
    if (!rgba_in || !data_out || width == 0 || height == 0 || mip_count == 0)
    {
        return static_cast<int>(ASTCENC_ERR_BAD_PARAM);
    }

    size_t bytes_per_pixel = 0;
    astcenc_type astc_type = ASTCENC_TYPE_U8;
    if (data_type == 0)
    {
        astc_type = ASTCENC_TYPE_U8;
        bytes_per_pixel = 4;
    }
    else if (data_type == 1)
    {
        astc_type = ASTCENC_TYPE_F16;
        bytes_per_pixel = 8;
    }
    else if (data_type == 2)
    {
        astc_type = ASTCENC_TYPE_F32;
        bytes_per_pixel = 16;
    }
    else
    {
        return static_cast<int>(ASTCENC_ERR_BAD_PARAM);
    }

    astcenc_error err = ASTCENC_SUCCESS;
    astcenc_context* ctx = GetOrCreateContext(
        static_cast<astcenc_profile>(profile),
        block_x,
        block_y,
        quality,
        flags,
        alpha_scale_radius,
        &err);
    if (!ctx)
    {
        return static_cast<int>(err);
    }

    astcenc_swizzle swizzle{
        static_cast<astcenc_swz>(swz_r),
        static_cast<astcenc_swz>(swz_g),
        static_cast<astcenc_swz>(swz_b),
        static_cast<astcenc_swz>(swz_a)
    };

    size_t in_offset = 0;
    size_t out_offset = 0;

    for (unsigned int mip = 0; mip < mip_count; ++mip)
    {
        unsigned int mip_w = std::max(1u, width >> mip);
        unsigned int mip_h = std::max(1u, height >> mip);
        size_t mip_in_bytes = static_cast<size_t>(mip_w) * static_cast<size_t>(mip_h) * bytes_per_pixel;
        size_t bx = (mip_w + block_x - 1u) / block_x;
        size_t by = (mip_h + block_y - 1u) / block_y;
        size_t mip_out_bytes = bx * by * 16u;

        if (out_offset + mip_out_bytes > data_out_len)
        {
            return static_cast<int>(ASTCENC_ERR_OUT_OF_MEM);
        }

        void* slices[1] = { rgba_in + in_offset };
        astcenc_image img{ mip_w, mip_h, 1u, astc_type, slices };

        err = astcenc_compress_image(ctx, &img, &swizzle, data_out + out_offset, mip_out_bytes, 0u);
        astcenc_compress_reset(ctx);
        if (err != ASTCENC_SUCCESS)
        {
            return static_cast<int>(err);
        }

        in_offset += mip_in_bytes;
        out_offset += mip_out_bytes;
    }

    return static_cast<int>(ASTCENC_SUCCESS);
}

} // extern "C"
