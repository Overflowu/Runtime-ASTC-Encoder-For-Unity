using System.IO;
using System.Threading.Tasks;
using Aperture.Astcenc.Runtime;
using UnityEngine;

namespace Aperture.Astcenc.Samples
{
    /// <summary>
    /// Basic runtime ASTC compression sample demonstrating Auto/GPU/CPU backends,
    /// async encoding, GPU mipmap generation, and persistent KTX v1 disk caching.
    /// </summary>
    public class RuntimeAstcSample : MonoBehaviour
    {
        [Header("Input")]
        [Tooltip("Input texture. CPU backend requires Read/Write enabled; GPU backend also accepts non-readable textures.")]
        public Texture2D inputTexture;

        [Header("Compression Settings")]
        public TextureType textureType = TextureType.Default;
        public BlockSize blockSize = BlockSize.ASTC_6x6;
        public CompressQuality quality = CompressQuality.Fast;
        public EncodeBackend backend = EncodeBackend.Auto;
        public bool generateMipmaps = true;
        public bool useAsync = true;
        public bool useKtxDiskCache = false;

        [Header("Output (Runtime Preview)")]
        public Texture2D outputTexture;

        private bool m_OwnsOutputTexture;

        private async void Start()
        {
            if (inputTexture == null)
            {
                Debug.LogWarning("[RuntimeAstcSample] Assign an inputTexture in the Inspector to run the sample.");
                return;
            }

            if (!Encoder.IsSupport(inputTexture, backend))
            {
                Debug.LogWarning($"[RuntimeAstcSample] Input texture '{inputTexture.name}' is not supported by backend {backend}.");
                return;
            }

            var options = AstcCompressOptions.Default;
            options.TextureType = textureType;
            options.BlockSize = blockSize;
            options.Quality = quality;
            options.Backend = backend;
            options.IncludeMipmaps = generateMipmaps || inputTexture.mipmapCount > 1;
            options.GenerateMipmaps = generateMipmaps;

            if (useKtxDiskCache)
            {
                string cachePath = Path.Combine(Application.persistentDataPath, $"{inputTexture.name}_{blockSize}.ktx");
                outputTexture = await AstcDiskCache.LoadOrCompressAsync(
                    cachePath,
                    () => Task.FromResult<Texture>(inputTexture),
                    options);
            }
            else if (useAsync)
            {
                outputTexture = await inputTexture.CompressToTextureAsync(options);
            }
            else
            {
                outputTexture = inputTexture.CompressToTexture(options);
            }

            m_OwnsOutputTexture = outputTexture != null;
            if (outputTexture != null)
            {
                Debug.Log($"[RuntimeAstcSample] Compressed '{inputTexture.name}' -> {outputTexture.width}x{outputTexture.height}, format={outputTexture.graphicsFormat}, mips={outputTexture.mipmapCount}");
            }
        }

        private void OnDestroy()
        {
            if (m_OwnsOutputTexture && outputTexture != null)
            {
                Destroy(outputTexture);
                outputTexture = null;
                m_OwnsOutputTexture = false;
            }
        }
    }
}
