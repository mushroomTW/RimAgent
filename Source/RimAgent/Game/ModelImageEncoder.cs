using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RimAgent.Game;

/// <summary>
/// 把要送給模型的圖片縮到長邊上限並轉成 JPEG：原尺寸 PNG 動輒數 MB，請求會很大，
/// 而看圖模型會再縮圖，太高解析度只多花傳輸與 token。只能在 Unity 主線程呼叫。
/// </summary>
internal static class ModelImageEncoder
{
    public const int MaxEdge = 1280;

    private const int JpegQuality = 80;

    public static byte[] FromEncoded(byte[] data, int maxEdge = MaxEdge)
    {
        if (data == null || data.Length == 0) return [];
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
        try
        {
            return source.LoadImage(data) ? FromTexture(source, maxEdge) : [];
        }
        finally
        {
            Object.Destroy(source);
        }
    }

    public static byte[] FromTexture(Texture2D source, int maxEdge = MaxEdge)
    {
        int width = source.width;
        int height = source.height;
        float scale = Math.Min(1f, (float)maxEdge / Math.Max(width, height));
        int targetWidth = Math.Max(1, Mathf.RoundToInt(width * scale));
        int targetHeight = Math.Max(1, Mathf.RoundToInt(height * scale));
        if (targetWidth == width && targetHeight == height) return source.EncodeToJPG(JpegQuality);

        RenderTexture target = RenderTexture.GetTemporary(targetWidth, targetHeight, 0);
        RenderTexture previous = RenderTexture.active;
        var scaled = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, mipChain: false);
        try
        {
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            scaled.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            scaled.Apply();
            return scaled.EncodeToJPG(JpegQuality);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.Destroy(scaled);
        }
    }
}
