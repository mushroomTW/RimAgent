using System;
using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.UI;

namespace RimAgent.Tests;

/// <summary>剪貼簿 CF_DIB 解碼：列補齊、上下方向、BGR 轉 RGBA、不支援的格式拒絕。</summary>
[TestFixture]
public class DibDecoderTests
{
    /// <summary>組一個 BITMAPINFOHEADER + 像素資料；rows 依記憶體順序給（每列 BGR(A) 位元組，不含補齊）。</summary>
    private static byte[] Dib(int width, int height, short bitCount, int compression, params byte[][] rows)
    {
        int stride = ((width * bitCount + 31) / 32) * 4;
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(40));
        data.AddRange(BitConverter.GetBytes(width));
        data.AddRange(BitConverter.GetBytes(height));
        data.AddRange(BitConverter.GetBytes((short)1));
        data.AddRange(BitConverter.GetBytes(bitCount));
        data.AddRange(BitConverter.GetBytes(compression));
        data.AddRange(new byte[20]);
        if (compression == 3) data.AddRange(new byte[12]);
        foreach (byte[] row in rows)
        {
            data.AddRange(row);
            data.AddRange(new byte[stride - row.Length]);
        }
        return data.ToArray();
    }

    [Test]
    public void BottomUp24BitHandlesRowPaddingAndChannelOrder()
    {
        // 2x2、每列 6 位元組補到 8。記憶體第一列是畫面最下方。
        byte[] dib = Dib(2, 2, 24, 0,
            [1, 2, 3, 4, 5, 6],
            [7, 8, 9, 10, 11, 12]);

        Assert.That(DibDecoder.TryDecode(dib, out int w, out int h, out byte[] rgba), Is.True);

        Assert.That((w, h), Is.EqualTo((2, 2)));
        Assert.That(rgba, Is.EqualTo(new byte[]
        {
            3, 2, 1, 255, 6, 5, 4, 255,
            9, 8, 7, 255, 12, 11, 10, 255
        }), "輸出由下往上，與 Unity 原始貼圖資料相同");
    }

    [Test]
    public void TopDown32BitBitfieldsIsFlippedAndOpaque()
    {
        // 高度為負代表由上往下；32 位元截圖的 alpha 常是 0。
        byte[] dib = Dib(1, -2, 32, 3,
            [10, 20, 30, 0],
            [40, 50, 60, 0]);

        Assert.That(DibDecoder.TryDecode(dib, out int w, out int h, out byte[] rgba), Is.True);

        Assert.That((w, h), Is.EqualTo((1, 2)));
        Assert.That(rgba, Is.EqualTo(new byte[] { 60, 50, 40, 255, 30, 20, 10, 255 }));
    }

    [Test]
    public void UnsupportedOrTruncatedDataIsRejected()
    {
        Assert.That(DibDecoder.TryDecode(Dib(2, 2, 8, 0, [1, 2], [3, 4]), out _, out _, out _), Is.False, "調色盤格式不支援");
        Assert.That(DibDecoder.TryDecode(Dib(2, 2, 24, 1, [1, 2, 3, 4, 5, 6], [1, 2, 3, 4, 5, 6]), out _, out _, out _), Is.False, "RLE 壓縮不支援");

        byte[] truncated = Dib(2, 2, 24, 0, [1, 2, 3, 4, 5, 6], [7, 8, 9, 10, 11, 12]);
        Array.Resize(ref truncated, truncated.Length - 4);
        Assert.That(DibDecoder.TryDecode(truncated, out _, out _, out _), Is.False);
        Assert.That(DibDecoder.TryDecode(new byte[10], out _, out _, out _), Is.False);
    }
}
