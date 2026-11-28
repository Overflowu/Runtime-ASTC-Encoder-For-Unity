using Aperture.Astcenc.Runtime;
using System.IO;
using UnityEngine;

public class Sample : MonoBehaviour
{
    /// <summary>
    /// input texture should be read/write enabled and uncompressed
    /// </summary>
    public Texture2D inputTexture;
    public Texture2D outputTexture;

    void Start()
    {
        if (inputTexture == null)
        {
            Debug.LogWarning("Sample: inputTexture is not assigned.");
            return;
        }

        using (AstcRaw raw = inputTexture.CompressTexture(TextureType.Default, BlockSize.ASTC_6x6, CompressQuality.Fast))
        {
            if (raw != null)
            {
                raw.ThrowIfDisposed();

                if (SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_6x6))
                {
                    outputTexture = new Texture2D(inputTexture.width, inputTexture.height, TextureFormat.ASTC_6x6, inputTexture.mipmapCount > 1, false);
                    outputTexture.name = inputTexture.name;
                    outputTexture.LoadRawTextureData(raw);
                    outputTexture.Apply(true, true);
                }
            }
        }

        byte[] fileData = inputTexture.EncodeToASTC();
        if (fileData != null)
        {
            string baseDir = Application.isEditor ? Application.dataPath : Application.persistentDataPath;
            string filePath = Path.Combine(baseDir, "outputTexture.astc");
            File.WriteAllBytes(filePath, fileData);
        }
    }
}
