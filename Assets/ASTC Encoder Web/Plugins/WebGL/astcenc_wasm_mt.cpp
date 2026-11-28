// Emscripten pthreads async job runner for ARM astc-encoder on Unity WebGL.
// Compiled by Unity's bundled Emscripten toolchain when building for WebGL.
// Only active when __EMSCRIPTEN_PTHREADS__ is defined (PlayerSettings.WebGL.threadsSupport = true).

#include <atomic>
#include <cstdint>
#include <cstring>
#include "astcenc.h"

#if defined(__EMSCRIPTEN_PTHREADS__)
#include <algorithm>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

namespace
{
struct SimpleBarrier
{
    std::mutex mtx;
    std::condition_variable cv;
    unsigned int threshold;
    unsigned int count;
    unsigned int generation;

    explicit SimpleBarrier(unsigned int n)
        : threshold(n), count(n), generation(0) {}

    void Wait()
    {
        if (threshold <= 1) return;
        std::unique_lock<std::mutex> lock(mtx);
        unsigned int gen = generation;
        if (--count == 0)
        {
            generation++;
            count = threshold;
            cv.notify_all();
        }
        else
        {
            cv.wait(lock, [this, gen] { return gen != generation; });
        }
    }
};

struct MtJob
{
    std::atomic<int> state{ 0 }; // 0 = running, 1 = success, < 0 = -astcenc_error (or -999)
    std::vector<std::thread> threads;
    std::vector<uint8_t> ownedInput;
    std::vector<uint8_t> ownedOutput;
    astcenc_context* context = nullptr;
};

std::mutex g_jobsMutex;
std::unordered_map<int, std::unique_ptr<MtJob>> g_jobs;
int g_nextJobHandle = 1;
} // namespace
#endif

extern "C"
{

int astcenc_wasm_mt_available()
{
#if defined(__EMSCRIPTEN_PTHREADS__)
    return 1;
#else
    return 0;
#endif
}

int astcenc_wasm_mt_begin_compress(
    astcenc_context* ctx,
    unsigned int thread_count,
    unsigned int block_x,
    unsigned int block_y,
    int swz_r,
    int swz_g,
    int swz_b,
    int swz_a,
    unsigned int width,
    unsigned int height,
    unsigned int mip_count,
    int astc_type_int,
    const uint8_t* pixels_in,
    size_t pixels_in_len,
    size_t expected_out_len)
{
#if defined(__EMSCRIPTEN_PTHREADS__)
    if (!ctx || !pixels_in || pixels_in_len == 0 || expected_out_len == 0 || width == 0 || height == 0 || mip_count == 0)
    {
        return -static_cast<int>(ASTCENC_ERR_BAD_PARAM);
    }

    if (thread_count == 0) thread_count = 1;

    astcenc_type astc_type = static_cast<astcenc_type>(astc_type_int);
    size_t bpp = (astc_type == ASTCENC_TYPE_F32) ? 16u : (astc_type == ASTCENC_TYPE_F16 ? 8u : 4u);

    auto job = std::make_unique<MtJob>();
    job->context = ctx;
    job->ownedInput.assign(pixels_in, pixels_in + pixels_in_len);
    job->ownedOutput.resize(expected_out_len);

    auto barrier = std::make_shared<SimpleBarrier>(thread_count);
    auto sharedErr = std::make_shared<std::atomic<int>>(0);

    astcenc_swizzle swizzle{
        static_cast<astcenc_swz>(swz_r),
        static_cast<astcenc_swz>(swz_g),
        static_cast<astcenc_swz>(swz_b),
        static_cast<astcenc_swz>(swz_a)
    };

    MtJob* rawJob = job.get();
    int handle = 0;
    {
        std::lock_guard<std::mutex> lock(g_jobsMutex);
        handle = g_nextJobHandle++;
        if (g_nextJobHandle <= 0) g_nextJobHandle = 1;
        g_jobs.emplace(handle, std::move(job));
    }

    rawJob->threads.reserve(thread_count);
    for (unsigned int tid = 0; tid < thread_count; ++tid)
    {
        rawJob->threads.emplace_back([rawJob, barrier, sharedErr, ctx, tid, block_x, block_y, swizzle, width, height, mip_count, astc_type, bpp]() {
            size_t in_offset = 0;
            size_t out_offset = 0;

            for (unsigned int mip = 0; mip < mip_count; ++mip)
            {
                unsigned int mw = std::max(1u, width >> mip);
                unsigned int mh = std::max(1u, height >> mip);
                size_t mip_in_bytes = static_cast<size_t>(mw) * static_cast<size_t>(mh) * bpp;
                size_t bx = (mw + block_x - 1u) / block_x;
                size_t by = (mh + block_y - 1u) / block_y;
                size_t mip_out_bytes = bx * by * 16u;

                if (in_offset + mip_in_bytes > rawJob->ownedInput.size() ||
                    out_offset + mip_out_bytes > rawJob->ownedOutput.size())
                {
                    sharedErr->store(-static_cast<int>(ASTCENC_ERR_OUT_OF_MEM), std::memory_order_release);
                    barrier->Wait();
                    break;
                }

                void* slices[1] = { rawJob->ownedInput.data() + in_offset };
                astcenc_image img{ mw, mh, 1u, astc_type, slices };

                astcenc_error err = astcenc_compress_image(
                    ctx,
                    &img,
                    &swizzle,
                    rawJob->ownedOutput.data() + out_offset,
                    mip_out_bytes,
                    tid);

                if (err != ASTCENC_SUCCESS)
                {
                    int expected = 0;
                    sharedErr->compare_exchange_strong(expected, -static_cast<int>(err));
                }

                barrier->Wait();
                if (tid == 0)
                {
                    astcenc_compress_reset(ctx);
                }
                barrier->Wait();

                if (sharedErr->load(std::memory_order_acquire) != 0)
                {
                    break;
                }

                in_offset += mip_in_bytes;
                out_offset += mip_out_bytes;
            }

            if (tid == 0)
            {
                int errVal = sharedErr->load(std::memory_order_acquire);
                rawJob->state.store(errVal == 0 ? 1 : errVal, std::memory_order_release);
            }
        });
    }

    return handle;
#else
    (void)ctx; (void)thread_count; (void)block_x; (void)block_y;
    (void)swz_r; (void)swz_g; (void)swz_b; (void)swz_a;
    (void)width; (void)height; (void)mip_count; (void)astc_type_int;
    (void)pixels_in; (void)pixels_in_len; (void)expected_out_len;
    return -1;
#endif
}

int astcenc_wasm_mt_begin_decompress(
    astcenc_context* ctx,
    unsigned int thread_count,
    unsigned int width,
    unsigned int height,
    int astc_type_int,
    const uint8_t* astc_in,
    size_t astc_in_len,
    size_t expected_out_len)
{
#if defined(__EMSCRIPTEN_PTHREADS__)
    if (!ctx || !astc_in || astc_in_len == 0 || expected_out_len == 0 || width == 0 || height == 0)
    {
        return -static_cast<int>(ASTCENC_ERR_BAD_PARAM);
    }

    if (thread_count == 0) thread_count = 1;
    astcenc_type astc_type = static_cast<astcenc_type>(astc_type_int);

    auto job = std::make_unique<MtJob>();
    job->context = ctx;
    job->ownedInput.assign(astc_in, astc_in + astc_in_len);
    job->ownedOutput.resize(expected_out_len);

    auto barrier = std::make_shared<SimpleBarrier>(thread_count);
    auto sharedErr = std::make_shared<std::atomic<int>>(0);

    MtJob* rawJob = job.get();
    int handle = 0;
    {
        std::lock_guard<std::mutex> lock(g_jobsMutex);
        handle = g_nextJobHandle++;
        if (g_nextJobHandle <= 0) g_nextJobHandle = 1;
        g_jobs.emplace(handle, std::move(job));
    }

    rawJob->threads.reserve(thread_count);
    for (unsigned int tid = 0; tid < thread_count; ++tid)
    {
        rawJob->threads.emplace_back([rawJob, barrier, sharedErr, ctx, tid, width, height, astc_type]() {
            void* slices[1] = { rawJob->ownedOutput.data() };
            astcenc_image img{ width, height, 1u, astc_type, slices };
            astcenc_swizzle swizzle{ ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };

            astcenc_error err = astcenc_decompress_image(
                ctx,
                rawJob->ownedInput.data(),
                rawJob->ownedInput.size(),
                &img,
                &swizzle,
                tid);

            if (err != ASTCENC_SUCCESS)
            {
                int expected = 0;
                sharedErr->compare_exchange_strong(expected, -static_cast<int>(err));
            }

            barrier->Wait();
            if (tid == 0)
            {
                astcenc_decompress_reset(ctx);
                int errVal = sharedErr->load(std::memory_order_acquire);
                rawJob->state.store(errVal == 0 ? 1 : errVal, std::memory_order_release);
            }
        });
    }

    return handle;
#else
    (void)ctx; (void)thread_count; (void)width; (void)height;
    (void)astc_type_int; (void)astc_in; (void)astc_in_len; (void)expected_out_len;
    return -1;
#endif
}

int astcenc_wasm_mt_poll(int handle, uint8_t** out_ptr, size_t* out_len)
{
#if defined(__EMSCRIPTEN_PTHREADS__)
    std::lock_guard<std::mutex> lock(g_jobsMutex);
    auto it = g_jobs.find(handle);
    if (it == g_jobs.end())
    {
        return -static_cast<int>(ASTCENC_ERR_BAD_PARAM);
    }

    int st = it->second->state.load(std::memory_order_acquire);
    if (st == 0)
    {
        return 0; // still running
    }

    if (st == 1 && out_ptr && out_len)
    {
        *out_ptr = it->second->ownedOutput.data();
        *out_len = it->second->ownedOutput.size();
    }
    return st;
#else
    (void)handle; (void)out_ptr; (void)out_len;
    return -1;
#endif
}

void astcenc_wasm_mt_finish(int handle)
{
#if defined(__EMSCRIPTEN_PTHREADS__)
    std::unique_ptr<MtJob> job;
    {
        std::lock_guard<std::mutex> lock(g_jobsMutex);
        auto it = g_jobs.find(handle);
        if (it == g_jobs.end()) return;
        job = std::move(it->second);
        g_jobs.erase(it);
    }
    for (auto& t : job->threads)
    {
        if (t.joinable()) t.join();
    }
#else
    (void)handle;
#endif
}

void astcenc_wasm_mt_cancel(int handle)
{
#if defined(__EMSCRIPTEN_PTHREADS__)
    std::lock_guard<std::mutex> lock(g_jobsMutex);
    auto it = g_jobs.find(handle);
    if (it != g_jobs.end() && it->second->context)
    {
        astcenc_compress_cancel(it->second->context);
    }
#else
    (void)handle;
#endif
}

} // extern "C"
