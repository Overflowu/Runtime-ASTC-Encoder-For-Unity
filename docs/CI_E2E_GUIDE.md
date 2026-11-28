# 多平台 Unity 构建与端到端测试指南（GitHub Actions + GameCI）

本项目在 `master` 与 `develop` 分支配置了基于 **GitHub Actions + GameCI** 的自动化测试与多平台端到端构建流水线（[`.github/workflows/unity-multiplatform-e2e.yml`](../.github/workflows/unity-multiplatform-e2e.yml)），覆盖：

| 阶段 / 平台 | 运行环境 | 验证内容 |
| :--- | :--- | :--- |
| **Native Plugins & Linux `.so` Build** | `ubuntu-latest` | 校验 Git LFS 全部 6 个预编译原生库（含 Windows AVX2/SSE4.1）完整性、架构与导出符号；从 `ThirdParty/astc-encoder` 子模块编译 Linux x86_64 `libastcenc-sse4.1-shared.so` |
| **Unity Test Runner (EditMode)** | `ubuntu-latest` (GameCI) | 运行 `Assets/ASTC Encoder/Tests/Editor/AstcEncoderTests.cs` 黄金验证套件（ASTC/KTX 容器、磁盘缓存、队列调度、CPU 压缩/解压缩精度） |
| **Linux x64 Standalone** | `ubuntu-latest` | 构建 Linux Player 并以 `-batchmode -nographics -astc-e2e` 实际运行端到端压缩测试 |
| **Windows x64 Standalone** | `ubuntu-latest` 构建 → `windows-latest` 运行 | 跨平台构建 `.exe`，在真实 Windows Runner 上加载 AVX2/SSE4.1 DLL 执行端到端压缩测试 |
| **macOS Universal Standalone** | `ubuntu-latest` 构建 → `macos-latest` 运行 | 跨平台构建 `.app`，在真实 Apple Silicon macOS Runner 上加载通用二进制 `libastcenc-shared.dylib` 执行端到端压缩测试 |
| **Android ARM64 (IL2CPP)** | `ubuntu-latest` 构建 → `macos-latest` 模拟器运行 | 构建 ARM64 `.apk`，校验 `lib/arm64-v8a/libastcenc-neon-shared.so` 打包，并在 ARM64 Android Emulator 上安装启动校验 `adb logcat` 输出 `ASTC_E2E_PASSED` |
| **iOS ARM64 (IL2CPP)** | `ubuntu-latest` 导出 → `macos-latest` Xcode 编译 | 导出 Xcode 工程，并在 macOS Runner 上执行无签名 `xcodebuild -sdk iphoneos build`，验证 IL2CPP 编译与静态库 `libastcenc-neon-static.a` 链接 |
| **WebGL & WebGL-Threads** | `ubuntu-latest` 构建 → Playwright Chromium 无头浏览器 | 分别构建标准版与开启 `threadsSupport` 的 WebGL Player，由 `serve-webgl.mjs` 提供静态/COOP-COEP 服务，在 SwiftShader WebGL2 Chromium 中验证 Worker、pthreads、RT 回读、`DecompressAsync` 与缓存同步并输出 `ASTC_E2E_PASSED` |

---

## 一、配置 Unity License Secrets

在 GitHub 仓库 **Settings → Secrets and variables → Actions** 中配置：

- **Unity Personal（个人免费版）**：
  - `UNITY_LICENSE`：`Unity_lic.ulf` 完整 XML 内容（注意新版 Unity Hub 需在 *Preferences → Licenses* 手动点击 *Add → Get a free personal license* 才会写入 `Unity_lic.ulf`，不要填入 `UnityEntitlementLicense.xml`）
  - `UNITY_EMAIL`：Unity 账号邮箱
  - `UNITY_PASSWORD`：Unity 账号密码
- **Unity Pro / Plus（序列号版）**：`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`

---

## 二、触发方式

- **手动触发（默认）**：为节省 CI 额度，本流水线不再随 `push` / `pull_request` 自动触发。开发完成需要验证时，在 GitHub 仓库 **Actions → Unity Multi-Platform Build & E2E Tests → Run workflow** 中选择目标分支手动触发，可自定义 `unity_version`（默认 `2021.3.6f1`）和 `run_android_emulator`。
