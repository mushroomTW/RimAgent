using System;

namespace RimAgent.UI;

/// <summary>
/// 解碼剪貼簿的 CF_DIB（BITMAPINFOHEADER + 像素）。只支援螢幕截圖常見的 24／32 位元未壓縮格式；
/// 輸出 RGBA32、由下往上排列的列，與 Unity Texture2D.LoadRawTextureData 的順序相同。
/// </summary>
internal static class DibDecoder
{
    private const int InfoHeaderSize = 40;
    private const int BiRgb = 0;
    private const int BiBitfields = 3;

    /// <summary>超過這個像素數就不解碼，避免異常大小的剪貼簿資料吃光記憶體。</summary>
    internal const long MaxPixels = 40_000_000;

    public static bool TryDecode(byte[] dib, out int width, out int height, out byte[] rgba)
    {
        width = 0;
        height = 0;
        rgba = null;
        if (dib == null || dib.Length < InfoHeaderSize) return false;
        if (!ValidateDibHeader(dib, out int rawWidth, out int rawHeight, out int bitCount, out int compression, out int headerSize)) return false;

        // Math.Abs(int.MinValue) 會溢位成負數；這種標頭一定是壞資料。
        if (rawHeight == int.MinValue) return false;
        bool bottomUp = rawHeight > 0;
        int rows = Math.Abs(rawHeight);
        if ((long)rawWidth * rows > MaxPixels) return false;

        // 舊式 40 位元組標頭搭配 BI_BITFIELDS 時，標頭後還有三個色彩遮罩；32 位元不會有調色盤。
        int offset = headerSize + (compression == BiBitfields && headerSize == InfoHeaderSize ? 12 : 0);
        int bytesPerPixel = bitCount / 8;
        int stride = ((rawWidth * bitCount + 31) / 32) * 4;
        if (offset + (long)stride * rows > dib.Length) return false;

        var pixels = new byte[rawWidth * rows * 4];
        for (int row = 0; row < rows; row++)
        {
            // 輸出第 row 列（由下往上）；由上往下的 DIB 要翻轉。
            int source = offset + (bottomUp ? row : rows - 1 - row) * stride;
            int target = row * rawWidth * 4;
            DecodeRow(dib, source, target, rawWidth, bytesPerPixel, pixels);
        }

        width = rawWidth;
        height = rows;
        rgba = pixels;
        return true;
    }

    private static bool ValidateDibHeader(byte[] dib, out int rawWidth, out int rawHeight, out int bitCount, out int compression, out int headerSize)
    {
        headerSize = BitConverter.ToInt32(dib, 0);
        rawWidth = BitConverter.ToInt32(dib, 4);
        rawHeight = BitConverter.ToInt32(dib, 8);
        bitCount = BitConverter.ToInt16(dib, 14);
        compression = BitConverter.ToInt32(dib, 16);
        if (headerSize < InfoHeaderSize || rawWidth <= 0 || rawHeight == 0) return false;
        if (bitCount != 24 && bitCount != 32) return false;
        if (compression != BiRgb && !(compression == BiBitfields && bitCount == 32)) return false;
        return true;
    }

    private static void DecodeRow(byte[] dib, int source, int target, int rawWidth, int bytesPerPixel, byte[] pixels)
    {
        for (int x = 0; x < rawWidth; x++)
        {
            int s = source + x * bytesPerPixel;
            int t = target + x * 4;
            pixels[t] = dib[s + 2];
            pixels[t + 1] = dib[s + 1];
            pixels[t + 2] = dib[s];
            // 截圖的 32 位元 DIB 常把 alpha 填 0，一律視為不透明。
            pixels[t + 3] = 255;
        }
    }
}
