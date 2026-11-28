#!/usr/bin/env python3
"""
Aggregates Unity Player (GPU + CPU) and Native CPU benchmark JSON reports, computes
regression/improvement deltas against docs/benchmarks/baseline.json, generates docs/Benchmarks.md,
writes GitHub Actions step summary, and updates <!-- BENCHMARK_START/END --> blocks in README.md / README_CN.md.
"""

import argparse
import datetime
import json
import os
import re
from typing import Any, Dict, List, Optional, Tuple


MARKER_START = "<!-- BENCHMARK_START -->"
MARKER_END = "<!-- BENCHMARK_END -->"


def load_json(path: Optional[str]) -> Optional[Dict[str, Any]]:
    if not path or not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def fmt_delta(curr_ms: float, base_ms: Optional[float]) -> str:
    if base_ms is None or base_ms <= 1e-6:
        return "new"
    pct = (curr_ms - base_ms) / base_ms * 100.0
    if pct <= -5.0:
        return f"🚀 {pct:+.1f}%"
    if pct >= 15.0:
        return f"⚠️ {pct:+.1f}%"
    return f"{pct:+.1f}%"


def vram_saved_pct(compressed_bytes: int, uncompressed_bytes: int) -> str:
    if uncompressed_bytes <= 0 or compressed_bytes <= 0:
        return "—"
    saved = (1.0 - compressed_bytes / uncompressed_bytes) * 100.0
    return f"-{saved:.1f}%"


def index_results(report: Optional[Dict[str, Any]]) -> Dict[str, Dict[str, Any]]:
    if not report:
        return {}
    return {r["Name"]: r for r in report.get("Results", []) if "Name" in r}


def build_readme_block(
    lang: str,
    unity_report: Optional[Dict[str, Any]],
    native_reports: List[Dict[str, Any]],
    commit_sha: str,
    run_url: str,
) -> str:
    is_cn = lang == "cn"
    lines: List[str] = []
    short_sha = commit_sha[:7] if commit_sha else "local"
    date_str = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
    u_idx = index_results(unity_report)

    if unity_report and u_idx:
        gpu_model = unity_report.get("GpuModel", "Unknown GPU")
        gfx_api = unity_report.get("GraphicsDeviceType", "")
        cpu_model = unity_report.get("CpuModel", "Unknown CPU")
        cpu_cores = unity_report.get("CpuCores", "?")
        unity_ver = unity_report.get("UnityVersion", "")

        if is_cn:
            lines.append(
                f"以下数据由 GitHub Actions 在 **`{gpu_model}`（{gfx_api}）+ `{cpu_model}`（{cpu_cores} 核） / Unity `{unity_ver}`** 上自动实测生成（无 Editor 开销的 Standalone Player，预热 + 多次采样中位数）："
            )
            lines.append("")
            lines.append("#### GPU Compute vs. CPU 编码对比（`1024×1024` RGBA8）")
            lines.append("")
            lines.append("| 块尺寸 | 质量 | GPU 耗时 (ms) | CPU 耗时 (ms) | GPU 加速比 | GPU / CPU PSNR | 显存节省 |")
            lines.append("| :--- | :--- | ---: | ---: | ---: | ---: | ---: |")
        else:
            lines.append(
                f"Measured automatically via GitHub Actions on **`{gpu_model}` ({gfx_api}) + `{cpu_model}` ({cpu_cores} cores) / Unity `{unity_ver}`** (Release Standalone Player, warmup + multi-iteration median):"
            )
            lines.append("")
            lines.append("#### GPU Compute vs. CPU Encoding (`1024×1024` RGBA8)")
            lines.append("")
            lines.append("| Block Size | Quality | GPU Median (ms) | CPU Median (ms) | GPU Speedup | GPU / CPU PSNR | VRAM Saved |")
            lines.append("| :--- | :--- | ---: | ---: | ---: | ---: | ---: |")

        for bs in ["ASTC_4x4", "ASTC_6x6", "ASTC_8x8", "ASTC_12x12"]:
            for q in ["Fast", "Medium"]:
                g = u_idx.get(f"GPU_RGBA8_1024_{bs}_{q}")
                c = u_idx.get(f"CPU_RGBA8_1024_{bs}_{q}")
                if not g and not c:
                    continue
                g_ms = f"{g['MedianMs']:.2f}" if g else "—"
                c_ms = f"{c['MedianMs']:.2f}" if c else "—"
                spd = f"**{c['MedianMs'] / g['MedianMs']:.1f}×**" if (g and c and g["MedianMs"] > 1e-6) else "—"
                g_psnr = f"{g['RgbPsnrDb']:.1f}" if (g and g.get("RgbPsnrDb", 0) > 0) else "—"
                c_psnr = f"{c['RgbPsnrDb']:.1f}" if (c and c.get("RgbPsnrDb", 0) > 0) else "—"
                psnr_col = f"{g_psnr} / {c_psnr} dB" if (g_psnr != "—" or c_psnr != "—") else "—"
                ref = g or c
                saved = vram_saved_pct(ref.get("OutputBytes", 0), ref.get("UncompressedBytes", 0))
                bs_label = bs.replace("ASTC_", "ASTC ")
                lines.append(f"| **{bs_label}** | `{q}` | {g_ms} | {c_ms} | {spd} | {psnr_col} | {saved} |")

        # Key Runtime Highlights table
        highlights = []
        zr = u_idx.get("GPU_ZeroReadback_1024_6x6_Fast")
        if zr:
            highlights.append((
                "GPU 零回读 `CompressToTexture` (`1024×1024 6x6 Fast`)" if is_cn else "GPU Zero-Readback `CompressToTexture` (`1024×1024 6x6 Fast`)",
                f"**{zr['MedianMs']:.2f} ms** ({zr['ThroughputMPixPerSec']:.1f} MP/s)",
                f"{zr['GcAllocBytesPerOp'] / 1024.0:.1f} KB",
                "直拷硬件 ASTC 纹理，无 CPU 回读" if is_cn else "Direct `CopyTexture`, zero CPU readback",
            ))
        zrm = u_idx.get("GPU_ZeroReadback_MipGen_1024_6x6_Fast")
        if zrm:
            highlights.append((
                "GPU 单 CommandBuffer MipGen + 编码 (`1024×1024+Mips`)" if is_cn else "Single-`CommandBuffer` GPU MipGen + Encode (`1024×1024+Mips`)",
                f"**{zrm['MedianMs']:.2f} ms** ({zrm['ThroughputMPixPerSec']:.1f} MP/s)",
                f"{zrm['GcAllocBytesPerOp'] / 1024.0:.1f} KB",
                "11 级 Mip 生成与编码一体化完成" if is_cn else "Full 11-mip chain generated & encoded on GPU",
            ))
        gn = u_idx.get("GPU_Normal_512_6x6_Medium")
        cn = u_idx.get("CPU_Normal_512_6x6_Medium")
        if gn:
            err_txt = f"GPU {gn.get('MeanNormalDeg', 0):.2f}°" + (f" vs CPU {cn.get('MeanNormalDeg', 0):.2f}°" if cn else "")
            highlights.append((
                "法线贴图 GGGR 编码 (`512×512 6x6 Medium`)" if is_cn else "Normal Map GGGR Encoding (`512×512 6x6 Medium`)",
                f"GPU **{gn['MedianMs']:.2f} ms**" + (f" vs CPU {cn['MedianMs']:.2f} ms" if cn else ""),
                f"{gn['GcAllocBytesPerOp'] / 1024.0:.1f} KB",
                f"平均角度误差：{err_txt}" if is_cn else f"Mean angular error: {err_txt}",
            ))
        rp = u_idx.get("CPU_CompressRawPixelsInto_512_6x6_Fast")
        if rp:
            highlights.append((
                "原始像素直压 `CompressRawPixelsInto` (`512×512 6x6 Fast`)" if is_cn else "Zero-Alloc `CompressRawPixelsInto` (`512×512 6x6 Fast`)",
                f"**{rp['MedianMs']:.2f} ms** ({rp['ThroughputMPixPerSec']:.1f} MP/s)",
                f"**{rp['GcAllocBytesPerOp']} B**",
                "复用 NativeArray + `AstcContextPool`" if is_cn else "Caller-owned `NativeArray` + `AstcContextPool`",
            ))
        dc = u_idx.get("DiskCache_TryLoad_512_6x6_FullMips")
        if dc:
            highlights.append((
                "KTX 磁盘缓存秒开 `AstcDiskCache.TryLoad` (`512×512+Mips`)" if is_cn else "KTX Disk Cache `AstcDiskCache.TryLoad` (`512×512+Mips`)",
                f"**{dc['MedianMs']:.2f} ms**",
                f"{dc['GcAllocBytesPerOp'] / 1024.0:.1f} KB",
                "一次压缩，后续启动直传 GPU" if is_cn else "Compress once, instant load on subsequent launches",
            ))

        if highlights:
            lines.append("")
            if is_cn:
                lines.append("#### 关键运行时特性实测")
                lines.append("")
                lines.append("| 场景 | 中位数耗时 / 吞吐 | GC 分配 | 说明 |")
                lines.append("| :--- | :--- | ---: | :--- |")
            else:
                lines.append("#### Key Runtime Highlights")
                lines.append("")
                lines.append("| Scenario | Median / Throughput | GC Alloc | Notes |")
                lines.append("| :--- | :--- | ---: | :--- |")
            for sc, tm, gc, nt in highlights:
                lines.append(f"| {sc} | {tm} | {gc} | {nt} |")

    # Cross-platform Native CPU SIMD table (Windows AVX2 vs SSE4.1, macOS NEON, Linux SSE4.1)
    if native_reports:
        lines.append("")
        if is_cn:
            lines.append("#### 跨平台原生 CPU 指令集实测（`astc-encoder v5.7.1`，`512×512` RGBA8 `ASTC_6x6`）")
            lines.append("")
            lines.append("| 平台 / CPU | 指令集（ISA） | 线程 | `Fast` (ms) | `Medium` (ms) | `Fast` 吞吐 | CPU 软解 (ms) |")
            lines.append("| :--- | :--- | ---: | ---: | ---: | ---: | ---: |")
        else:
            lines.append("#### Cross-Platform Native CPU SIMD (`astc-encoder v5.7.1`, `512×512` RGBA8 `ASTC_6x6`)")
            lines.append("")
            lines.append("| Platform / CPU | ISA | Threads | `Fast` (ms) | `Medium` (ms) | `Fast` MP/s | Decompress (ms) |")
            lines.append("| :--- | :--- | ---: | ---: | ---: | ---: | ---: |")

        for nr in native_reports:
            plat = nr.get("Platform", "Unknown")
            cpu = nr.get("CpuModel", "")
            threads = nr.get("ThreadsUsed", "?")
            by_isa: Dict[str, Dict[str, Dict[str, Any]]] = {}
            for r in nr.get("Results", []):
                isa = r.get("ISA", "Default")
                by_isa.setdefault(isa, {})
                if r.get("Category") == "Native_CPU_Decompress":
                    by_isa[isa]["DECOMPRESS"] = r
                elif r.get("BlockSize") == "ASTC_6x6":
                    by_isa[isa][r.get("Quality", "")] = r

            for isa, m in by_isa.items():
                fast = m.get("Fast")
                med = m.get("Medium")
                dec = m.get("DECOMPRESS")
                f_ms = f"{fast['MedianMs']:.2f}" if fast else "—"
                m_ms = f"{med['MedianMs']:.2f}" if med else "—"
                f_mp = f"{fast['ThroughputMPixPerSec']:.2f}" if fast else "—"
                d_ms = f"{dec['MedianMs']:.2f}" if dec else "—"
                label = f"{plat} (`{cpu}`)" if cpu else plat
                lines.append(f"| {label} | `{isa}` | {threads} | {f_ms} | {m_ms} | {f_mp} | {d_ms} |")

    lines.append("")
    run_link = f" · [Actions Run]({run_url})" if run_url else ""
    if is_cn:
        lines.append(
            f"> 基准溯源：Commit `{short_sha}` · {date_str} · [查看完整全矩阵与基线对比（docs/Benchmarks.md）](docs/Benchmarks.md){run_link}"
        )
    else:
        lines.append(
            f"> Provenance: Commit `{short_sha}` · {date_str} · [Full Benchmark Matrix & Regression Baseline (docs/Benchmarks.md)](docs/Benchmarks.md){run_link}"
        )

    return "\n".join(lines)


def build_full_docs_markdown(
    unity_report: Optional[Dict[str, Any]],
    native_reports: List[Dict[str, Any]],
    baseline: Optional[Dict[str, Any]],
    commit_sha: str,
    run_url: str,
) -> str:
    base_unity = index_results((baseline or {}).get("unity"))
    base_native: Dict[str, Dict[str, Any]] = {}
    for nr in (baseline or {}).get("native", []):
        plat = nr.get("Platform", "")
        for r in nr.get("Results", []):
            base_native[f"{plat}::{r['Name']}"] = r

    date_str = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    short_sha = commit_sha[:7] if commit_sha else "local"

    lines = [
        "# Runtime ASTC Encoder — Performance Benchmarks & Regression Baseline",
        "",
        f"- **Updated**: `{date_str}` | **Commit**: `{short_sha}`" + (f" | [GitHub Actions Run]({run_url})" if run_url else ""),
        "- **Baseline Comparison**: Deltas (`Δ vs Baseline`) compare each benchmark's median latency against the previous saved snapshot in `docs/benchmarks/baseline.json` (`🚀` ≥ 5% faster, `⚠️` ≥ 15% slower).",
        "",
    ]

    if unity_report:
        lines.extend([
            "## 1. Unity Standalone Player (Real Hardware GPU + CPU)",
            "",
            f"- **GPU**: `{unity_report.get('GpuModel')}` (`{unity_report.get('GraphicsDeviceType')}`)",
            f"- **CPU**: `{unity_report.get('CpuModel')}` ({unity_report.get('CpuCores')} cores)",
            f"- **Unity**: `{unity_report.get('UnityVersion')}` | **Platform**: `{unity_report.get('Platform')}`",
            f"- **GPU Direct CopyTexture**: `4x4={unity_report.get('SupportsDirectCopy4x4')}`, `6x6={unity_report.get('SupportsDirectCopy6x6')}`",
            "",
            "| Benchmark | Backend | Res | Block | Quality | Min (ms) | Median (ms) | P95 (ms) | MP/s | GC (KB) | RGB PSNR | Normal Err | Δ vs Baseline |",
            "| :--- | :--- | :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        ])

        for r in unity_report.get("Results", []):
            prev = base_unity.get(r["Name"])
            delta = fmt_delta(r["MedianMs"], prev["MedianMs"] if prev else None)
            psnr = f"{r['RgbPsnrDb']:.2f} dB" if r.get("RgbPsnrDb", 0) > 0 else "—"
            norm = f"{r['MeanNormalDeg']:.2f}°" if r.get("MeanNormalDeg", 0) > 0 else "—"
            lines.append(
                f"| `{r['Name']}` | {r['Backend']} | {r['Resolution']} | {r['BlockSize']} | {r['Quality']} "
                f"| {r['MinMs']:.2f} | **{r['MedianMs']:.2f}** | {r['P95Ms']:.2f} | {r['ThroughputMPixPerSec']:.2f} "
                f"| {r['GcAllocBytesPerOp'] / 1024.0:.1f} | {psnr} | {norm} | {delta} |"
            )
        lines.append("")

    if native_reports:
        lines.extend([
            "## 2. Cross-Platform Native CPU (`astc-encoder v5.7.1`)",
            "",
            "| Platform / CPU | Benchmark | ISA | Threads | Min (ms) | Median (ms) | P95 (ms) | MP/s | Δ vs Baseline |",
            "| :--- | :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |",
        ])
        for nr in native_reports:
            plat = nr.get("Platform", "")
            cpu = nr.get("CpuModel", "")
            plat_label = f"{plat} (`{cpu}`)" if cpu else plat
            for r in nr.get("Results", []):
                prev = base_native.get(f"{plat}::{r['Name']}")
                delta = fmt_delta(r["MedianMs"], prev["MedianMs"] if prev else None)
                lines.append(
                    f"| {plat_label} | `{r['Name']}` | `{r.get('ISA', '')}` | {r.get('Threads', '')} "
                    f"| {r['MinMs']:.2f} | **{r['MedianMs']:.2f}** | {r['P95Ms']:.2f} | {r['ThroughputMPixPerSec']:.2f} | {delta} |"
                )
        lines.append("")

    return "\n".join(lines)


def replace_marker_block(file_path: str, new_content: str) -> bool:
    if not os.path.exists(file_path):
        return False
    with open(file_path, "r", encoding="utf-8") as f:
        text = f.read()

    pattern = re.compile(
        re.escape(MARKER_START) + r".*?" + re.escape(MARKER_END),
        flags=re.DOTALL,
    )
    replacement = f"{MARKER_START}\n{new_content}\n{MARKER_END}"
    if not pattern.search(text):
        print(f"[update-benchmark-docs] WARNING: markers not found in {file_path}, skipping.")
        return False

    updated = pattern.sub(replacement, text, count=1)
    with open(file_path, "w", encoding="utf-8") as f:
        f.write(updated)
    return True


def main():
    parser = argparse.ArgumentParser(description="Generate benchmark docs, regression diff, and update READMEs")
    parser.add_argument("--unity-report", help="Path to Unity Player benchmark JSON")
    parser.add_argument("--native-report", action="append", default=[], help="Path(s) to native CPU benchmark JSON")
    parser.add_argument("--baseline", default="docs/benchmarks/baseline.json", help="Previous baseline JSON path")
    parser.add_argument("--write-baseline", default="docs/benchmarks/baseline.json", help="Output baseline JSON path")
    parser.add_argument("--docs-out", default="docs/Benchmarks.md", help="Full benchmark markdown report path")
    parser.add_argument("--readme-en", default="README.md")
    parser.add_argument("--readme-cn", default="README_CN.md")
    parser.add_argument("--step-summary", help="Optional path to append GitHub Actions step summary")
    parser.add_argument("--commit", default=os.environ.get("GITHUB_SHA", ""))
    parser.add_argument("--run-url", default="")
    args = parser.parse_args()

    unity_report = load_json(args.unity_report)
    native_reports = [r for r in (load_json(p) for p in args.native_report) if r is not None]
    baseline = load_json(args.baseline)

    if not unity_report and not native_reports:
        raise SystemExit("No benchmark reports provided.")

    full_md = build_full_docs_markdown(unity_report, native_reports, baseline, args.commit, args.run_url)
    docs_dir = os.path.dirname(os.path.abspath(args.docs_out))
    if docs_dir:
        os.makedirs(docs_dir, exist_ok=True)
    with open(args.docs_out, "w", encoding="utf-8") as f:
        f.write(full_md)
    print(f"[update-benchmark-docs] Wrote {args.docs_out}")

    en_block = build_readme_block("en", unity_report, native_reports, args.commit, args.run_url)
    cn_block = build_readme_block("cn", unity_report, native_reports, args.commit, args.run_url)
    if replace_marker_block(args.readme_en, en_block):
        print(f"[update-benchmark-docs] Updated {args.readme_en}")
    if replace_marker_block(args.readme_cn, cn_block):
        print(f"[update-benchmark-docs] Updated {args.readme_cn}")

    if args.write_baseline:
        base_dir = os.path.dirname(os.path.abspath(args.write_baseline))
        if base_dir:
            os.makedirs(base_dir, exist_ok=True)
        new_baseline = {
            "SchemaVersion": "1.0",
            "TimestampUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "GitCommit": args.commit,
            "unity": unity_report,
            "native": native_reports,
        }
        with open(args.write_baseline, "w", encoding="utf-8") as f:
            json.dump(new_baseline, f, indent=2)
        print(f"[update-benchmark-docs] Saved baseline to {args.write_baseline}")

    if args.step_summary:
        with open(args.step_summary, "a", encoding="utf-8") as f:
            f.write(full_md + "\n")
        print(f"[update-benchmark-docs] Appended summary to {args.step_summary}")


if __name__ == "__main__":
    main()
