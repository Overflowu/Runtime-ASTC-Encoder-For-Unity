# 多平台 Unity 构建与端到端测试指南（GitHub Actions + GameCI）

本项目已配置基于 **GitHub Actions + GameCI** 的自动化测试与多平台构建流水线（[`.github/workflows/unity-multiplatform-e2e.yml`](../.github/workflows/unity-multiplatform-e2e.yml)），覆盖：

| 阶段 / 平台 | 运行环境 | 验证内容 |
| :--- | :--- | :--- |
| **Native Plugins & Linux `.so` Build** | `ubuntu-latest` | 校验 Git LFS 原生库完整性、架构与导出符号；从 ARM `astc-encoder` v4.4.0 编译 Linux x86_64 `libastcenc-sse4.1-shared.so`（无需 Unity License） |
| **Unity Test Runner** | `ubuntu-latest` (GameCI) | 运行全部 **EditMode** 与 **PlayMode** Unity Test Framework 测试用例 |
| **Linux x64 Standalone** | `ubuntu-latest` | 构建 Linux Player 并以 `-batchmode -nographics -astc-e2e` 实际运行端到端压缩测试 |
| **Windows x64 Standalone** | `ubuntu-latest` 构建 → `windows-latest` 运行 | 跨平台构建 `.exe`，在真实 Windows Runner 上加载 `astcenc-sse4.1-shared.dll` 执行端到端压缩测试 |
| **macOS Universal Standalone** | `ubuntu-latest` 构建 → `macos-latest` 运行 | 跨平台构建 `.app`，在真实 Apple Silicon macOS Runner 上加载通用二进制 `libastcenc-shared.dylib` 执行端到端压缩测试 |
| **Android ARM64 (IL2CPP)** | `ubuntu-latest` 构建 → `macos-latest` 模拟器运行 | 构建 ARM64 `.apk`，校验 `lib/arm64-v8a/libastcenc-neon-shared.so` 打包，并在 ARM64 Android Emulator 上安装启动校验 `adb logcat` 输出 `ASTC_E2E_PASSED` |
| **iOS ARM64 (IL2CPP)** | `ubuntu-latest` 导出 → `macos-latest` Xcode 编译 | 导出 Xcode 工程，并在 macOS Runner 上执行无签名 `xcodebuild -sdk iphoneos build`，验证 IL2CPP 编译与静态库 `libastcenc-neon-static.a` 链接 |

---

## 一、配置 Unity License Secrets（必需，仅需配置一次）

在 GitHub 仓库页面进入 **Settings → Secrets and variables → Actions → New repository secret**：

### 方式 1：Unity Personal（个人免费版）
1. 在本机已激活 Unity 的机器上获取 `.ulf` 激活文件：
   - **Windows**：`C:\ProgramData\Unity\Unity_lic.ulf`
   - **macOS**：`/Library/Application Support/Unity/Unity_lic.ulf`
   - **Linux**：`~/.local/share/unity3d/Unity/Unity_lic.ulf`
   （或参考 [GameCI Activation 文档](https://game.ci/docs/github/activation) 生成）
2. 将 `.ulf` 文件的完整 XML 内容保存为 Secret：
   - `UNITY_LICENSE`：`.ulf` 文件全文

### 方式 2：Unity Pro / Plus（序列号版）
添加以下 3 个 Secrets：
- `UNITY_EMAIL`：Unity 账号邮箱
- `UNITY_PASSWORD`：Unity 账号密码
- `UNITY_SERIAL`：Unity Pro/Plus 序列号

> **注意**：未配置上述 Secret 时，流水线仍会自动执行 `native-abi-check`（校验各平台原生库与 ABI），但会跳过需要 Unity License 的测试与构建 Job。

---

## 二、触发方式

- **自动触发**：向 `master` / `main` 分支推送代码或提交 Pull Request 时自动运行。
- **手动触发**：在 GitHub 仓库 **Actions → Unity Multi-Platform Build & E2E Tests → Run workflow** 中触发，可自定义：
  - `unity_version`：默认 `2021.3.6f1`（工程 [`ProjectVersion.txt`](../ProjectSettings/ProjectVersion.txt) 中的 `2021.3.6f1c1` 为中国特供版，GameCI 镜像使用国际版 `2021.3.6f1` 即可兼容打开）。
  - `run_android_emulator`：是否启用 Android ARM64 模拟器真机级运行校验（默认开启）。

---

## 三、本地运行测试与构建

1. **拉取 Git LFS 原生库与示例贴图**：
   ```bash
   git lfs install
   git lfs pull
   ./scripts/verify-native-plugins.sh
   ```
2. **如在 Linux 下开发/测试**：先运行 `./scripts/build-linux-astcenc.sh` 生成 `Assets/ASTC Encoder/Plugins/Linux/x86_64/libastcenc-sse4.1-shared.so`。
3. **在 Unity Editor 中运行测试**：打开 **Window → General → Test Runner**，分别运行 **EditMode**（`Aperture.Astcenc.Tests.EditMode`）和 **PlayMode**（`Aperture.Astcenc.Tests.PlayMode`）。
4. **在 Unity Editor 中一键构建**：通过菜单 **Build → CI → ...** 选择目标平台，或使用命令行调用 `Aperture.Astcenc.Editor.CIBuildScript.Build`。
5. **在已构建的桌面 Player 上手动触发 E2E 冒烟测试**：
   ```bash
   ./ASTCEncoder.x86_64 -batchmode -nographics -logfile - -astc-e2e
   ```
