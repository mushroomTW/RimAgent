using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace RimAgent.UI;

/// <summary>只支援 24／32 位元未壓縮 DIB；輸出 RGBA32、由下往上排列的列，與 Texture2D.LoadRawTextureData 順序相同。</summary>
internal static class DibDecoder
{
    private const int InfoHeaderSize = 40;
    private const int BiRgb = 0;
    private const int BiBitfields = 3;
    private const int MaxColorTableEntries = 65536;

    internal const long MaxPixels = 40_000_000;

    public static bool TryDecode(byte[]? dib, out int width, out int height, [NotNullWhen(true)] out byte[]? rgba)
    {
        width = 0;
        height = 0;
        rgba = null;
        if (dib == null || dib.Length < InfoHeaderSize) return false;
        if (!ValidateDibHeader(dib, out int rawWidth, out int rawHeight, out int bitCount, out int compression, out int headerSize, out int colorsUsed)) return false;

        // Math.Abs(int.MinValue) 會溢位成負數；這種標頭一定是壞資料。
        if (rawHeight == int.MinValue) return false;
        bool bottomUp = rawHeight > 0;
        int rows = Math.Abs(rawHeight);
        if ((long)rawWidth * rows > MaxPixels) return false;

        // 舊式 40 位元組標頭搭配 BI_BITFIELDS 時，標頭後還有三個色彩遮罩；若有調色盤（biClrUsed > 0）亦排在像素資料前。
        int maskBytes = compression == BiBitfields && headerSize == InfoHeaderSize ? 12 : 0;
        long offsetLong = (long)headerSize + maskBytes + (long)colorsUsed * 4;
        if (offsetLong > dib.Length) return false;
        int offset = (int)offsetLong;

        uint rMask = 0x00FF0000u, gMask = 0x0000FF00u, bMask = 0x000000FFu;
        bool customMasks = false;
        if (compression == BiBitfields)
        {
            if (dib.Length < InfoHeaderSize + 12) return false;
            rMask = BitConverter.ToUInt32(dib, InfoHeaderSize);
            gMask = BitConverter.ToUInt32(dib, InfoHeaderSize + 4);
            bMask = BitConverter.ToUInt32(dib, InfoHeaderSize + 8);
            if ((rMask | gMask | bMask) != 0)
            {
                if (rMask == 0 || gMask == 0 || bMask == 0) return false;
                customMasks = rMask != 0x00FF0000u || gMask != 0x0000FF00u || bMask != 0x000000FFu;
            }
        }

        int bytesPerPixel = bitCount / 8;
        int stride = ((rawWidth * bitCount + 31) / 32) * 4;
        if (offset + (long)stride * rows > dib.Length) return false;

        var pixels = new byte[rawWidth * rows * 4];
        for (int row = 0; row < rows; row++)
        {
            // 輸出第 row 列（由下往上）；由上往下的 DIB 要翻轉。
            int source = offset + (bottomUp ? row : rows - 1 - row) * stride;
            int target = row * rawWidth * 4;
            if (customMasks)
                DecodeRowBitfields(dib, source, target, rawWidth, rMask, gMask, bMask, pixels);
            else
                DecodeRow(dib, source, target, rawWidth, bytesPerPixel, pixels);
        }

        width = rawWidth;
        height = rows;
        rgba = pixels;
        return true;
    }

    private static bool ValidateDibHeader(byte[] dib, out int rawWidth, out int rawHeight, out int bitCount, out int compression, out int headerSize, out int colorsUsed)
    {
        headerSize = BitConverter.ToInt32(dib, 0);
        rawWidth = BitConverter.ToInt32(dib, 4);
        rawHeight = BitConverter.ToInt32(dib, 8);
        bitCount = BitConverter.ToInt16(dib, 14);
        compression = BitConverter.ToInt32(dib, 16);
        colorsUsed = BitConverter.ToInt32(dib, 32);
        if (headerSize < InfoHeaderSize || headerSize > dib.Length || rawWidth <= 0 || rawHeight == 0) return false;
        if (colorsUsed < 0 || colorsUsed > MaxColorTableEntries) return false;
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

    private static void DecodeRowBitfields(byte[] dib, int source, int target, int rawWidth, uint rMask, uint gMask, uint bMask, byte[] pixels)
    {
        int rShift = BitOperations.TrailingZeroCount(rMask);
        int gShift = BitOperations.TrailingZeroCount(gMask);
        int bShift = BitOperations.TrailingZeroCount(bMask);
        uint rMax = rMask >> rShift;
        uint gMax = gMask >> gShift;
        uint bMax = bMask >> bShift;
        for (int x = 0; x < rawWidth; x++)
        {
            uint pixel = BitConverter.ToUInt32(dib, source + x * 4);
            int t = target + x * 4;
            pixels[t] = ScaleChannel((pixel & rMask) >> rShift, rMax);
            pixels[t + 1] = ScaleChannel((pixel & gMask) >> gShift, gMax);
            pixels[t + 2] = ScaleChannel((pixel & bMask) >> bShift, bMax);
            pixels[t + 3] = 255;
        }
    }

    private static byte ScaleChannel(uint value, uint max)
    {
        if (max == 255) return (byte)value;
        return (byte)((value * 255u + max / 2u) / max);
    }
}
