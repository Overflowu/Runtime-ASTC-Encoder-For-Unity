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

        byte[] fileData = inputTexture.EncodeToASTC(TextureType.Default, blockSize, quality, backend: backend);
        if (fileData != null)
        {
            string filePath = Path.Combine(Application.dataPath, "outputTexture.astc");
            File.WriteAllBytes(filePath, fileData);
        }
    }
}
