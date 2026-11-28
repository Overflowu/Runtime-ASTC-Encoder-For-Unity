using System;
using System.IO;
using Aperture.Astcenc.Runtime;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Built-player end-to-end smoke test runner.
/// Activates in player builds when launched with the <c>-astc-e2e</c> command-line flag
/// or when built with the <c>ASTC_CI_E2E</c> scripting define symbol.
/// </summary>
public static class AstcE2ESmokeRunner
{
    private const string LogPrefix = "[ASTC_E2E]";

#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RunIfRequested()
    {
        if (!ShouldRun())
        {
            return;
        }

        Debug.Log($"{LogPrefix} Starting runtime end-to-end verification on platform={Application.platform}, arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, gfx={SystemInfo.graphicsDeviceType}");

        try
        {
            VerifyIsSupport();
            VerifyAllBlockSizes();
            VerifyEncodeToAstcAndKtxRoundTrip();
            VerifyNonSquareMipmaps();
            VerifyNormalAndMultiFormats();
            VerifyInPlaceCompressIfSupported();

            Debug.Log($"{LogPrefix} ASTC_E2E_PASSED");
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} ASTC_E2E_FAILED: {ex}");
            Application.Quit(1);
        }
    }

    private static bool ShouldRun()
    {
#if ASTC_CI_E2E
        return true;
#else
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "-astc-e2e", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
#endif
    }
#endif

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }

    private static Texture2D CreateProceduralTexture(int width, int height, TextureFormat format, bool mipChain, bool linear = false)
    {
        Texture2D tex = new Texture2D(width, height, format, mipChain, linear);
        Color[] pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Color(
                    (float)x / Math.Max(1, width - 1),
                    (float)y / Math.Max(1, height - 1),
                    ((x ^ y) & 0xFF) / 255.0f,
                    0.75f);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(mipChain, false);
        return tex;
    }

    private static int ExpectedCompressedBytes(int width, int height, int mipCount, int blockDim)
    {
        int total = 0;
        for (int i = 0; i < mipCount; i++)
        {
            int mw = Math.Max(1, width >> i);
            int mh = Math.Max(1, height >> i);
            int bx = (mw + blockDim - 1) / blockDim;
            int by = (mh + blockDim - 1) / blockDim;
            total += bx * by * 16;
        }
        return total;
    }

    private static void VerifyIsSupport()
    {
        AssertTrue(!Encoder.IsSupport(null), "Null texture must not be supported.");

        Texture2D readable = CreateProceduralTexture(16, 16, TextureFormat.RGBA32, false);
        try
        {
            AssertTrue(Encoder.IsSupport(readable, EncodeBackend.Cpu), "Readable RGBA32 texture must be supported on CPU backend.");
            readable.Apply(false, true);
            AssertTrue(!Encoder.IsSupport(readable, EncodeBackend.Cpu), "Non-readable texture must not be supported on CPU backend.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(readable);
        }
    }

    private static void VerifyAllBlockSizes()
    {
        (BlockSize size, int dim)[] cases =
        {
            (BlockSize.ASTC_4x4, 4),
            (BlockSize.ASTC_5x5, 5),
            (BlockSize.ASTC_6x6, 6),
            (BlockSize.ASTC_8x8, 8),
            (BlockSize.ASTC_10x10, 10),
            (BlockSize.ASTC_12x12, 12),
        };

        Texture2D tex = CreateProceduralTexture(32, 32, TextureFormat.RGBA32, false);
        try
        {
            foreach (var (blockSize, dim) in cases)
            {
                int expected = ExpectedCompressedBytes(32, 32, 1, dim);
                using (AstcRaw raw = tex.CompressTexture(TextureType.Default, blockSize, CompressQuality.Fastest, backend: EncodeBackend.Cpu))
                {
                    AssertTrue(raw != null, $"CompressTexture returned null for {blockSize}.");
                    AssertTrue(raw.Length == expected, $"{blockSize}: expected {expected} bytes, got {raw.Length}.");
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    private static void VerifyEncodeToAstcAndKtxRoundTrip()
    {
        const int width = 37;
        const int height = 23;
        Texture2D tex = CreateProceduralTexture(width, height, TextureFormat.RGBA32, false);
        try
        {
            byte[] encoded = tex.EncodeToASTC(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast, backend: EncodeBackend.Cpu);
            int expectedPayload = ExpectedCompressedBytes(width, height, 1, 6);
            AssertTrue(encoded != null, "EncodeToASTC returned null.");
            AssertTrue(encoded.Length == AstcHeader.HeaderSize + expectedPayload, $"Expected {AstcHeader.HeaderSize + expectedPayload} bytes, got {encoded.Length}.");

            AssertTrue(AstcHeader.TryParse(encoded, 0, out uint bx, out uint by, out uint bz, out int w, out int h, out int d), "AstcHeader.TryParse failed.");
            AssertTrue(bx == 6 && by == 6 && bz == 1, $"Invalid header block dims: {bx}x{by}x{bz}.");
            AssertTrue(w == width && h == height && d == 1, $"Invalid header image dims: {w}x{h}x{d}.");

            string outPath = Path.Combine(Application.persistentDataPath, "astc_e2e_test.astc");
            File.WriteAllBytes(outPath, encoded);
            byte[] readBack = File.ReadAllBytes(outPath);
            AssertTrue(readBack.Length == encoded.Length, "Written .astc file length mismatch.");
            File.Delete(outPath);

            using (AstcRaw raw = tex.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest, backend: EncodeBackend.Cpu))
            {
                byte[] ktx = AstcKtxContainer.Encode(raw, width, height, 1, GraphicsFormat.RGBA_ASTC6X6_UNorm);
                AssertTrue(ktx != null, "KTX container encode returned null.");
                AssertTrue(AstcKtxContainer.TryDecode(ktx, out KtxAstcData decoded), "KTX container decode failed.");
                AssertTrue(decoded.Width == width && decoded.Height == height && decoded.MipCount == 1, "KTX decoded metadata mismatch.");
                AssertTrue(decoded.RawAstcPayload.Length == raw.Length, "KTX decoded payload length mismatch.");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    private static void VerifyNonSquareMipmaps()
    {
        Texture2D tex = CreateProceduralTexture(64, 32, TextureFormat.RGBA32, true);
        try
        {
            AssertTrue(tex.mipmapCount > 1, "Expected mipmapCount > 1 for mipmapped texture.");
            int expected = ExpectedCompressedBytes(64, 32, tex.mipmapCount, 6);
            using (AstcRaw raw = tex.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest, backend: EncodeBackend.Cpu))
            {
                AssertTrue(raw != null, "Mipmapped non-square CompressTexture returned null.");
                AssertTrue(raw.Length == expected, $"Mipmapped non-square expected {expected} bytes, got {raw.Length}.");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    private static void VerifyNormalAndMultiFormats()
    {
        (TextureFormat fmt, TextureType type, bool linear)[] cases =
        {
            (TextureFormat.RGBA32, TextureType.Normal, true),
            (TextureFormat.RGB24, TextureType.Default, false),
            (TextureFormat.R8, TextureType.Default, true),
            (TextureFormat.RGBAHalf, TextureType.Default, true),
            (TextureFormat.RGBAFloat, TextureType.Default, true),
        };

        foreach (var (fmt, type, linear) in cases)
        {
            Texture2D tex = CreateProceduralTexture(16, 16, fmt, false, linear);
            try
            {
                AssertTrue(Encoder.IsSupport(tex, EncodeBackend.Cpu), $"Expected format {fmt} ({tex.graphicsFormat}) to be supported.");
                using (AstcRaw raw = tex.CompressTexture(type, BlockSize.ASTC_4x4, CompressQuality.Fastest, backend: EncodeBackend.Cpu))
                {
                    int expected = ExpectedCompressedBytes(16, 16, 1, 4);
                    AssertTrue(raw != null && raw.Length == expected, $"Format {fmt} ({type}) failed compression.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }

    private static void VerifyInPlaceCompressIfSupported()
    {
        if (!SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_6x6) ||
            !SystemInfo.IsFormatSupported(GraphicsFormat.RGBA_ASTC6X6_UNorm, FormatUsage.Sample))
        {
            Debug.Log($"{LogPrefix} Skipping GPU in-place Compress check (ASTC GPU sampling unsupported in current graphics mode: {SystemInfo.graphicsDeviceType}).");
            return;
        }

        Texture2D tex = CreateProceduralTexture(32, 32, TextureFormat.RGBA32, false, linear: true);
        try
        {
            bool ok = tex.Compress(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fastest, makeNoLongerReadable: true, backend: EncodeBackend.Auto);
            AssertTrue(ok, "In-place Texture2D.Compress returned false.");
            AssertTrue(tex.graphicsFormat == GraphicsFormat.RGBA_ASTC6X6_UNorm, $"Expected RGBA_ASTC6X6_UNorm, got {tex.graphicsFormat}.");
            AssertTrue(!tex.isReadable, "Expected texture to be non-readable after makeNoLongerReadable=true.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
