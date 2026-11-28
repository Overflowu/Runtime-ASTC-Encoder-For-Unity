#!/usr/bin/env python3
"""
Cross-platform zero-dependency native CPU ASTC benchmark runner (ctypes -> astc-encoder v5.7.1).
Works directly against the prebuilt Plugins/ binaries on Windows (SSE4.1 & AVX2), macOS (ARM64 NEON),
and Linux (SSE4.1 / AVX2), outputting structured JSON for CI summaries and README automation.
"""

import argparse
import concurrent.futures
import ctypes
import datetime
import json
import math
import os
import platform
import statistics
import subprocess
import time

ASTCENC_PRF_LDR = 1
ASTCENC_TYPE_U8 = 0
ASTCENC_FLG_DECOMPRESS_ONLY = 1 << 4
ASTCENC_FLG_SELF_DECOMPRESS_ONLY = 1 << 5


class AstcencSwizzle(ctypes.Structure):
    _fields_ = [
        ("r", ctypes.c_int),
        ("g", ctypes.c_int),
        ("b", ctypes.c_int),
        ("a", ctypes.c_int),
    ]


class AstcencImage(ctypes.Structure):
    _fields_ = [
        ("dim_x", ctypes.c_uint32),
        ("dim_y", ctypes.c_uint32),
        ("dim_z", ctypes.c_uint32),
        ("data_type", ctypes.c_int),
        ("data", ctypes.POINTER(ctypes.c_void_p)),
    ]


class AstcNativeLib:
    def __init__(self, path: str):
        self.path = path
        self.lib = ctypes.CDLL(path)

        self.config_init = self.lib.astcenc_config_init
        self.config_init.argtypes = [
            ctypes.c_int, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
            ctypes.c_float, ctypes.c_uint32, ctypes.c_void_p,
        ]
        self.config_init.restype = ctypes.c_int

        self.context_alloc = self.lib.astcenc_context_alloc
        self.context_alloc.argtypes = [
            ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p,
        ]
        self.context_alloc.restype = ctypes.c_int

        self.compress_image = self.lib.astcenc_compress_image
        self.compress_image.argtypes = [
            ctypes.c_void_p, ctypes.POINTER(AstcencImage), ctypes.POINTER(AstcencSwizzle),
            ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32,
        ]
        self.compress_image.restype = ctypes.c_int

        self.compress_reset = self.lib.astcenc_compress_reset
        self.compress_reset.argtypes = [ctypes.c_void_p]
        self.compress_reset.restype = ctypes.c_int

        self.decompress_image = self.lib.astcenc_decompress_image
        self.decompress_image.argtypes = [
            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
            ctypes.POINTER(AstcencImage), ctypes.POINTER(AstcencSwizzle), ctypes.c_uint32,
        ]
        self.decompress_image.restype = ctypes.c_int

        self.decompress_reset = self.lib.astcenc_decompress_reset
        self.decompress_reset.argtypes = [ctypes.c_void_p]
        self.decompress_reset.restype = ctypes.c_int

        self.context_free = self.lib.astcenc_context_free
        self.context_free.argtypes = [ctypes.c_void_p]
        self.context_free.restype = None


def create_procedural_rgba8(width: int, height: int) -> bytearray:
    buf = bytearray(width * height * 4)
    pi = math.pi
    w_den = max(1, width - 1)
    h_den = max(1, height - 1)
    w_q = width // 4
    h_q = height // 4
    idx = 0
    for y in range(height):
        v = y / h_den
        y_chk = y >> 3
        for x in range(width):
            if x < w_q and y < h_q:
                buf[idx] = 96
                buf[idx + 1] = 144
                buf[idx + 2] = 192
                buf[idx + 3] = 255
            else:
                u = x / w_den
                wave = 0.5 + 0.5 * math.sin((u * 18.0 + v * 12.0) * pi)
                chk = 0.15 if (((x >> 3) + y_chk) & 1) == 0 else -0.15
                r = int(round((u * 0.7 + wave * 0.3 + chk) * 255.0))
                g = int(round((v * 0.7 + (1.0 - wave) * 0.3 - chk) * 255.0))
                b = int(round(((u + v) * 0.4 + wave * 0.2) * 255.0))
                a = int(round((0.2 + 0.8 * abs(math.sin((u - v) * 4.0 * pi))) * 255.0))
                buf[idx] = 0 if r < 0 else (255 if r > 255 else r)
                buf[idx + 1] = 0 if g < 0 else (255 if g > 255 else g)
                buf[idx + 2] = 0 if b < 0 else (255 if b > 255 else b)
                buf[idx + 3] = 0 if a < 0 else (255 if a > 255 else a)
            idx += 4
    return buf


def detect_cpu_model() -> str:
    sys_name = platform.system()
    try:
        if sys_name == "Darwin":
            return subprocess.check_output(["sysctl", "-n", "machdep.cpu.brand_string"], text=True).strip()
        if sys_name == "Linux":
            with open("/proc/cpuinfo", "r", encoding="utf-8") as f:
                for line in f:
                    if line.startswith("model name"):
                        return line.split(":", 1)[1].strip()
        if sys_name == "Windows":
            out = subprocess.check_output(
                ["powershell", "-NoProfile", "-Command", "(Get-CimInstance Win32_Processor).Name"],
                text=True,
            ).strip()
            if out:
                return out
    except Exception:
        pass
    return platform.processor() or platform.machine()


def measure_fn(warmup: int, iterations: int, total_pixels: int, fn):
    for _ in range(warmup):
        fn()
    samples = []
    for _ in range(iterations):
        t0 = time.perf_counter()
        fn()
        t1 = time.perf_counter()
        samples.append((t1 - t0) * 1000.0)
    samples.sort()
    median_ms = statistics.median(samples)
    p95_idx = min(len(samples) - 1, max(0, math.ceil(len(samples) * 0.95) - 1))
    mpix_s = ((total_pixels / 1e6) / (median_ms / 1000.0)) if median_ms > 1e-6 else 0.0
    return {
        "MinMs": round(samples[0], 3),
        "MedianMs": round(median_ms, 3),
        "MeanMs": round(sum(samples) / len(samples), 3),
        "P95Ms": round(samples[p95_idx], 3),
        "MaxMs": round(samples[-1], 3),
        "ThroughputMPixPerSec": round(mpix_s, 3),
    }


def run_lib_benchmarks(lib_path: str, isa_label: str, width: int, height: int, rgba_buf: bytearray, threads: int, warmup: int, iterations: int):
    api = AstcNativeLib(lib_path)
    src_arr = (ctypes.c_uint8 * len(rgba_buf)).from_buffer(rgba_buf)
    src_ptr = ctypes.c_void_p(ctypes.addressof(src_arr))
    slices = (ctypes.c_void_p * 1)(src_ptr)
    swz = AstcencSwizzle(0, 1, 2, 3)

    blocks = [("ASTC_4x4", 4), ("ASTC_6x6", 6), ("ASTC_8x8", 8), ("ASTC_12x12", 12)]
    qualities = [("Fast", 10.0), ("Medium", 60.0)]
    results = []

    with concurrent.futures.ThreadPoolExecutor(max_workers=threads) as pool:
        for bs_name, dim in blocks:
            out_len = ((width + dim - 1) // dim) * ((height + dim - 1) // dim) * 16
            out_buf = (ctypes.c_uint8 * out_len)()
            out_ptr = ctypes.c_void_p(ctypes.addressof(out_buf))

            for q_name, q_val in qualities:
                cfg = ctypes.create_string_buffer(256)
                rc = api.config_init(ASTCENC_PRF_LDR, dim, dim, 1, q_val, ASTCENC_FLG_SELF_DECOMPRESS_ONLY, cfg)
                if rc != 0:
                    raise RuntimeError(f"astcenc_config_init failed ({rc}) for {bs_name} {q_name}")

                ctx = ctypes.c_void_p()
                rc = api.context_alloc(cfg, threads, ctypes.byref(ctx), None)
                if rc != 0:
                    raise RuntimeError(f"astcenc_context_alloc failed ({rc}) for {bs_name} {q_name}")

                try:
                    def do_compress():
                        if threads <= 1:
                            img = AstcencImage(width, height, 1, ASTCENC_TYPE_U8, slices)
                            api.compress_image(ctx, ctypes.byref(img), ctypes.byref(swz), out_ptr, out_len, 0)
                        else:
                            futs = []
                            for t in range(threads):
                                def worker(ti=t):
                                    img = AstcencImage(width, height, 1, ASTCENC_TYPE_U8, slices)
                                    api.compress_image(ctx, ctypes.byref(img), ctypes.byref(swz), out_ptr, out_len, ti)
                                futs.append(pool.submit(worker))
                            for f in futs:
                                f.result()
                        api.compress_reset(ctx)

                    st = measure_fn(warmup, iterations, width * height, do_compress)
                    results.append({
                        "Name": f"Native_{isa_label}_{width}_{bs_name}_{q_name}_{threads}T",
                        "Category": "Native_CPU",
                        "ISA": isa_label,
                        "Resolution": f"{width}x{height}",
                        "BlockSize": bs_name,
                        "Quality": q_name,
                        "Threads": threads,
                        "OutputBytes": out_len,
                        **st,
                    })
                finally:
                    api.context_free(ctx)

        # Decompression benchmark on ASTC_6x6
        dim = 6
        out_len = ((width + dim - 1) // dim) * ((height + dim - 1) // dim) * 16
        out_buf = (ctypes.c_uint8 * out_len)()
        out_ptr = ctypes.c_void_p(ctypes.addressof(out_buf))
        dec_buf = (ctypes.c_uint8 * (width * height * 4))()
        dec_slices = (ctypes.c_void_p * 1)(ctypes.c_void_p(ctypes.addressof(dec_buf)))

        cfg = ctypes.create_string_buffer(256)
        api.config_init(ASTCENC_PRF_LDR, dim, dim, 1, 10.0, ASTCENC_FLG_SELF_DECOMPRESS_ONLY, cfg)
        cctx = ctypes.c_void_p()
        api.context_alloc(cfg, threads, ctypes.byref(cctx), None)
        img = AstcencImage(width, height, 1, ASTCENC_TYPE_U8, slices)
        api.compress_image(cctx, ctypes.byref(img), ctypes.byref(swz), out_ptr, out_len, 0)
        api.context_free(cctx)

        dcfg = ctypes.create_string_buffer(256)
        api.config_init(ASTCENC_PRF_LDR, dim, dim, 1, 0.0, ASTCENC_FLG_DECOMPRESS_ONLY, dcfg)
        dctx = ctypes.c_void_p()
        api.context_alloc(dcfg, threads, ctypes.byref(dctx), None)
        try:
            def do_decompress():
                if threads <= 1:
                    dimg = AstcencImage(width, height, 1, ASTCENC_TYPE_U8, dec_slices)
                    api.decompress_image(dctx, out_ptr, out_len, ctypes.byref(dimg), ctypes.byref(swz), 0)
                else:
                    futs = []
                    for t in range(threads):
                        def worker(ti=t):
                            dimg = AstcencImage(width, height, 1, ASTCENC_TYPE_U8, dec_slices)
                            api.decompress_image(dctx, out_ptr, out_len, ctypes.byref(dimg), ctypes.byref(swz), ti)
                        futs.append(pool.submit(worker))
                    for f in futs:
                        f.result()
                api.decompress_reset(dctx)

            st = measure_fn(warmup, max(iterations, 7), width * height, do_decompress)
            results.append({
                "Name": f"Native_{isa_label}_Decompress_{width}_ASTC_6x6_{threads}T",
                "Category": "Native_CPU_Decompress",
                "ISA": isa_label,
                "Resolution": f"{width}x{height}",
                "BlockSize": "ASTC_6x6",
                "Quality": "N/A",
                "Threads": threads,
                "OutputBytes": width * height * 4,
                **st,
            })
        finally:
            api.context_free(dctx)

    return results


def main():
    parser = argparse.ArgumentParser(description="Native CPU astc-encoder benchmark runner")
    parser.add_argument("--lib", action="append", required=True, help="ISA=PATH pair (e.g. AVX2=path/to/dll)")
    parser.add_argument("--size", type=int, default=512, help="Square test texture dimension (default: 512)")
    parser.add_argument("--threads", type=int, default=0, help="Worker thread count (0 = auto min(cores, 4))")
    parser.add_argument("--warmup", type=int, default=1)
    parser.add_argument("--iterations", type=int, default=5)
    parser.add_argument("--out", required=True, help="Output JSON report path")
    args = parser.parse_args()

    cores = os.cpu_count() or 1
    threads = args.threads if args.threads > 0 else max(1, min(cores, 4))
    rgba = create_procedural_rgba8(args.size, args.size)

    report = {
        "SchemaVersion": "1.0",
        "TimestampUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "GitCommit": os.environ.get("GITHUB_SHA", ""),
        "Platform": f"{platform.system()} {platform.machine()}",
        "CpuModel": detect_cpu_model(),
        "CpuCores": cores,
        "ThreadsUsed": threads,
        "Results": [],
    }

    for spec in args.lib:
        if "=" not in spec:
            raise ValueError(f"--lib expects ISA=PATH, got: {spec}")
        isa, path = spec.split("=", 1)
        if not os.path.exists(path):
            raise FileNotFoundError(f"Native library not found: {path}")
        print(f"[bench-native-cpu] Running {isa} ({path}) with {threads} threads on {args.size}x{args.size}...")
        report["Results"].extend(
            run_lib_benchmarks(path, isa, args.size, args.size, rgba, threads, args.warmup, args.iterations)
        )

    out_dir = os.path.dirname(os.path.abspath(args.out))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    print(f"[bench-native-cpu] Wrote {len(report['Results'])} results to {args.out}")


if __name__ == "__main__":
    main()
