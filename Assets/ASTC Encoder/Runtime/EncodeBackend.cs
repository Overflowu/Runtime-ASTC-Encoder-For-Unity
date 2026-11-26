namespace Aperture.Astcenc.Runtime
{
    /// <summary>
    /// Selects which execution backend is used for runtime ASTC texture compression.
    /// </summary>
    public enum EncodeBackend
    {
        /// <summary>
        /// Native CPU compression via ARM astc-encoder (highest quality presets, requires readable Texture2D).
        /// </summary>
        Cpu = 0,

        /// <summary>
        /// GPU Compute Shader compression (real-time speed, supports RenderTexture and non-readable Texture2D).
        /// </summary>
        Gpu = 1,

        /// <summary>
        /// Prefers GPU Compute Shader when supported on the current device and input texture,
        /// automatically falling back to CPU astc-encoder otherwise.
        /// </summary>
        Auto = 2,
    }
}
