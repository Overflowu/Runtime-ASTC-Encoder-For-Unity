using Aperture.Astcenc.Runtime;
using System.IO;
using UnityEngine;

public class Sample : MonoBehaviour
{
    /// <summary>
    /// Input texture (CPU backend requires Read/Write enabled; GPU backend also supports non-readable textures).
    /// </summary>
    public Texture2D inputTexture;
    public Texture2D outputTexture;
    public BlockSize blockSize = BlockSize.ASTC_6x6;
    public CompressQuality quality = CompressQuality.Fast;
    public EncodeBackend backend = EncodeBackend.Auto;

    private bool m_OwnsOutputTexture;

    void Start()
    {
        if (inputTexture == null)
            return;

        outputTexture = inputTexture.CompressToTexture(
            TextureType.Default,
            blockSize,
            quality,
            includeMipmaps: inputTexture.mipmapCount > 1,
            backend: backend);
        m_OwnsOutputTexture = outputTexture != null;

        byte[] fileData = inputTexture.EncodeToASTC(TextureType.Default, blockSize, quality, backend: backend);
        if (fileData != null)
        {
            string outputDir = Application.isEditor ? Application.dataPath : Application.persistentDataPath;
            string filePath = Path.Combine(outputDir, "outputTexture.astc");
            try
            {
                File.WriteAllBytes(filePath, fileData);
            }
            catch (IOException ex)
            {
                Debug.LogError($"Failed to write ASTC file to '{filePath}': {ex.Message}");
            }
        }
    }

    void OnDestroy()
    {
        if (m_OwnsOutputTexture && outputTexture != null)
        {
            Destroy(outputTexture);
            outputTexture = null;
            m_OwnsOutputTexture = false;
        }
    }
}
