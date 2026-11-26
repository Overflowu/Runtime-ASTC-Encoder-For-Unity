using System;

namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Configuration options for runtime ASTC texture compression.
    /// Provides a single extensible entry point for basic and advanced encoder settings.
    /// </summary>
    [Serializable]
    public struct AstcCompressOptions
    {
        /// <summary>Semantic texture type (Default color/data map or Normal map).</summary>
        public TextureType TextureType;

        /// <summary>ASTC block footprint (larger blocks yield higher compression ratio).</summary>
        public BlockSize BlockSize;

        /// <summary>Encoder search quality preset.</summary>
        public CompressQuality Quality;

        /// <summary>Execution backend (Auto prefers GPU with automatic CPU fallback).</summary>
        public EncodeBackend Backend;

        /// <summary>
        /// Worker thread count for the CPU backend.
        /// 1 = single-threaded, &lt;= 0 = auto-select based on CPU cores.
        /// </summary>
        public int ThreadCount;

        /// <summary>
        /// Weight RGB error by alpha transparency (recommended only for true transparency textures;
        /// leave false for channel-packed maps such as ORM/MRAO).
        /// </summary>
        public bool UseAlphaWeight;

        /// <summary>
        /// Radius for alpha-weight scaling in the CPU backend (defaults to 1 when <see cref="UseAlphaWeight"/> is true
        /// to minimize color bleed under bilinear filtering).
        /// </summary>
        public uint AlphaScaleRadius;

        /// <summary>
        /// Enable perceptual error metrics in the CPU backend (optimizes for visual quality rather than pure PSNR).
        /// </summary>
        public bool UsePerceptual;

        /// <summary>Whether to encode all mipmap levels present on the input texture (ignored by single-level .astc export).</summary>
        public bool IncludeMipmaps;

        /// <summary>
        /// When true and using the GPU backend on a single-mip source texture, automatically generates a full
        /// sRGB-aware / normal-renormalizing mipmap chain on the GPU and compresses all levels in one pass.
        /// </summary>
        public bool GenerateMipmaps;

        /// <summary>For in-place <see cref="Encoder.Compress(UnityEngine.Texture2D, AstcCompressOptions)"/>, whether to release CPU pixel memory after uploading.</summary>
        public bool MakeNoLongerReadable;

        /// <summary>Default balanced runtime compression options (Default color, ASTC 6x6, Fast, Auto backend).</summary>
        public static AstcCompressOptions Default => new AstcCompressOptions
        {
            TextureType = TextureType.Default,
            BlockSize = BlockSize.ASTC_6x6,
            Quality = CompressQuality.Fast,
            Backend = EncodeBackend.Auto,
            ThreadCount = 0,
            UseAlphaWeight = false,
            AlphaScaleRadius = 1u,
            UsePerceptual = false,
            IncludeMipmaps = true,
            GenerateMipmaps = false,
            MakeNoLongerReadable = true,
        };

        /// <summary>Preset configured for tangent-space normal maps (linear GGGR encoding).</summary>
        public static AstcCompressOptions NormalMap => new AstcCompressOptions
        {
            TextureType = TextureType.Normal,
            BlockSize = BlockSize.ASTC_6x6,
            Quality = CompressQuality.Fast,
            Backend = EncodeBackend.Auto,
            ThreadCount = 0,
            UseAlphaWeight = false,
            AlphaScaleRadius = 0u,
            UsePerceptual = false,
            IncludeMipmaps = true,
            GenerateMipmaps = false,
            MakeNoLongerReadable = true,
        };

        /// <summary>Preset configured for alpha-blended transparent textures (UI, foliage, decals).</summary>
        public static AstcCompressOptions Transparent => new AstcCompressOptions
        {
            TextureType = TextureType.Default,
            BlockSize = BlockSize.ASTC_6x6,
            Quality = CompressQuality.Fast,
            Backend = EncodeBackend.Auto,
            ThreadCount = 0,
            UseAlphaWeight = true,
            AlphaScaleRadius = 1u,
            UsePerceptual = false,
            IncludeMipmaps = true,
            GenerateMipmaps = false,
            MakeNoLongerReadable = true,
        };
    }
}
