using System;

namespace RimAgent.Game;

/// <summary>
/// 地圖俯視截圖的版面：每格幾像素、座標格線間距與四周留白。
/// 圖片長邊不超過 <see cref="MaxEdge"/>，送給模型時就不必再縮圖，格線與數字不會被糊掉。
/// 不碰 Unity，可單元測試；世界層照它渲染，工具層照它寫說明文字。
/// </summary>
public sealed class MapShotLayout
{
    /// <summary>圖片長邊上限，與送模型前的縮圖上限一致。</summary>
    public const int MaxEdge = 1280;

    /// <summary>每格像素上限：小範圍放到這麼大已足夠看清，再大只是浪費 token。</summary>
    public const int MaxPixelsPerCell = 32;

    /// <summary>格線間距至少要有這麼多像素，標籤才放得下、線也不會密到蓋住地圖。</summary>
    private const int MinGridSpacingPixels = 36;

    private static readonly int[] GridSteps = [5, 10, 25, 50, 100];

    private MapShotLayout(CellRectSpec rect, int pixelsPerCell, int gridStep, int leftMargin, int rightMargin, int bottomMargin, int topMargin)
    {
        Rect = rect;
        PixelsPerCell = pixelsPerCell;
        GridStep = gridStep;
        LeftMargin = leftMargin;
        RightMargin = rightMargin;
        BottomMargin = bottomMargin;
        TopMargin = topMargin;
    }

    /// <summary>截圖涵蓋的格子範圍（含頭尾）。</summary>
    public CellRectSpec Rect { get; }
    public int CellsWide => Rect.MaxX - Rect.MinX + 1;
    public int CellsHigh => Rect.MaxZ - Rect.MinZ + 1;
    public int PixelsPerCell { get; }
    /// <summary>格線畫在座標為此數倍數的格子的西緣與南緣。</summary>
    public int GridStep { get; }
    public int LeftMargin { get; }
    public int RightMargin { get; }
    public int BottomMargin { get; }
    public int TopMargin { get; }
    public int MapWidthPx => CellsWide * PixelsPerCell;
    public int MapHeightPx => CellsHigh * PixelsPerCell;
    public int ImageWidth => LeftMargin + MapWidthPx + RightMargin;
    public int ImageHeight => BottomMargin + MapHeightPx + TopMargin;

    /// <summary>依範圍算出版面；範圍需已裁進地圖內。</summary>
    public static MapShotLayout For(CellRectSpec rect)
    {
        int left = DigitFont.TextWidth(Math.Max(rect.MaxZ, 0).ToString()) + 2 * DigitFont.Padding;
        int right = DigitFont.TextWidth(Math.Max(rect.MaxX, 0).ToString()) + DigitFont.Padding;
        int bottom = DigitFont.GlyphHeight + 2 * DigitFont.Padding;
        int top = DigitFont.Padding;

        int cellsWide = rect.MaxX - rect.MinX + 1;
        int cellsHigh = rect.MaxZ - rect.MinZ + 1;
        int byWidth = (MaxEdge - left - right) / cellsWide;
        int byHeight = (MaxEdge - bottom - top) / cellsHigh;
        int ppc = Math.Max(1, Math.Min(MaxPixelsPerCell, Math.Min(byWidth, byHeight)));

        int step = GridSteps[GridSteps.Length - 1];
        foreach (int candidate in GridSteps)
        {
            if (candidate * ppc >= MinGridSpacingPixels)
            {
                step = candidate;
                break;
            }
        }
        return new MapShotLayout(rect, ppc, step, left, right, bottom, top);
    }
}

/// <summary>
/// 把渲染好的地圖像素加上座標格線與邊緣數字標籤。像素為 RGBA32、第 0 列在最下面（Unity 貼圖慣例），
/// 所以圖上 z 越大越靠上，正好是北方朝上。
/// </summary>
public static class MapShotOverlay
{
    private static readonly byte[] Background = [28, 28, 28];
    private static readonly byte[] LineColor = [255, 225, 0];
    private static readonly byte[] LabelColor = [255, 255, 255];

    /// <summary>格線與地圖混色的比例（0-255）；太實會蓋住格子內容，太淡在雪地上看不到。</summary>
    private const int LineAlpha = 150;

    /// <summary>格線延伸進留白區的長度（像素），讓數字與線對得起來。</summary>
    private const int TickLength = 3;

    /// <summary>
    /// 組出最終圖片：深灰底、地圖貼在留白內側、每 <see cref="MapShotLayout.GridStep"/> 格一條黃線、
    /// 左緣標 z、下緣標 x。地圖像素的 alpha 一律補成不透明。
    /// </summary>
    /// <param name="mapPixels">渲染結果，大小須為 MapWidthPx x MapHeightPx。</param>
    public static byte[] Compose(byte[] mapPixels, MapShotLayout layout)
    {
        if (mapPixels == null) throw new ArgumentNullException(nameof(mapPixels));
        if (layout == null) throw new ArgumentNullException(nameof(layout));
        int mapW = layout.MapWidthPx;
        int mapH = layout.MapHeightPx;
        if (mapPixels.Length != mapW * mapH * 4) throw new ArgumentException($"expected {mapW}x{mapH} RGBA pixels", nameof(mapPixels));

        int width = layout.ImageWidth;
        int height = layout.ImageHeight;
        var image = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++) Set(image, i * 4, Background, 255);

        for (int y = 0; y < mapH; y++)
        {
            int src = y * mapW * 4;
            int dst = ((layout.BottomMargin + y) * width + layout.LeftMargin) * 4;
            Buffer.BlockCopy(mapPixels, src, image, dst, mapW * 4);
            for (int x = 0; x < mapW; x++) image[dst + x * 4 + 3] = 255;
        }

        CellRectSpec rect = layout.Rect;
        int ppc = layout.PixelsPerCell;
        int step = layout.GridStep;

        // 直線：x 為 step 倍數的格子西緣。
        for (int cx = FirstMultiple(rect.MinX, step); cx <= rect.MaxX; cx += step)
        {
            int px = layout.LeftMargin + (cx - rect.MinX) * ppc;
            for (int y = 0; y < mapH; y++) Blend(image, width, px, layout.BottomMargin + y, LineColor, LineAlpha);
            for (int t = 1; t <= TickLength; t++) Blend(image, width, px, layout.BottomMargin - t, LineColor, 255);
            string label = cx.ToString();
            int labelX = Math.Min(px + 1, width - DigitFont.TextWidth(label) - 1);
            DigitFont.Draw(image, width, height, label, labelX, layout.BottomMargin - DigitFont.Padding - DigitFont.GlyphHeight, LabelColor);
        }

        // 橫線：z 為 step 倍數的格子南緣。
        for (int cz = FirstMultiple(rect.MinZ, step); cz <= rect.MaxZ; cz += step)
        {
            int py = layout.BottomMargin + (cz - rect.MinZ) * ppc;
            for (int x = 0; x < mapW; x++) Blend(image, width, layout.LeftMargin + x, py, LineColor, LineAlpha);
            for (int t = 1; t <= TickLength; t++) Blend(image, width, layout.LeftMargin - t, py, LineColor, 255);
            string label = cz.ToString();
            int labelY = Math.Min(py + 1, height - DigitFont.GlyphHeight - 1);
            DigitFont.Draw(image, width, height, label, layout.LeftMargin - DigitFont.Padding - DigitFont.TextWidth(label), labelY, LabelColor);
        }
        return image;
    }

    private static int FirstMultiple(int min, int step)
    {
        int r = min % step;
        return r == 0 ? min : min + (step - r);
    }

    private static void Blend(byte[] image, int width, int x, int y, byte[] color, int alpha)
    {
        if (x < 0 || y < 0 || x >= width) return;
        int i = (y * width + x) * 4;
        if (i + 3 >= image.Length) return;
        for (int c = 0; c < 3; c++) image[i + c] = (byte)((color[c] * alpha + image[i + c] * (255 - alpha)) / 255);
        image[i + 3] = 255;
    }

    internal static void Set(byte[] image, int i, byte[] color, byte alpha)
    {
        image[i] = color[0];
        image[i + 1] = color[1];
        image[i + 2] = color[2];
        image[i + 3] = alpha;
    }
}

/// <summary>3x5 點陣數字，放大 <see cref="Scale"/> 倍；不依賴 Unity 字型，直接寫進像素陣列。</summary>
internal static class DigitFont
{
    public const int Scale = 2;
    public const int GlyphWidth = 3 * Scale;
    public const int GlyphHeight = 5 * Scale;
    public const int Gap = Scale;
    public const int Padding = 3;

    // 每個數字 5 列（由上而下），每列 3 位元（最高位在左）。
    private static readonly int[][] Glyphs =
    [
        [7, 5, 5, 5, 7], [2, 6, 2, 2, 7], [7, 1, 7, 4, 7], [7, 1, 7, 1, 7], [5, 5, 7, 1, 1],
        [7, 4, 7, 1, 7], [7, 4, 7, 5, 7], [7, 1, 1, 1, 1], [7, 5, 7, 5, 7], [7, 5, 7, 1, 7]
    ];

    public static int TextWidth(string text) => text.Length * GlyphWidth + Math.Max(0, text.Length - 1) * Gap;

    /// <summary>以 (x, y) 為左下角畫字；y 往上增加。超出圖片的像素略過。</summary>
    public static void Draw(byte[] image, int width, int height, string text, int x, int y, byte[] color)
    {
        for (int n = 0; n < text.Length; n++)
        {
            int digit = text[n] - '0';
            if (digit < 0 || digit > 9) continue;
            int gx = x + n * (GlyphWidth + Gap);
            for (int row = 0; row < 5; row++)
            {
                int bits = Glyphs[digit][row];
                for (int col = 0; col < 3; col++)
                {
                    if ((bits & (4 >> col)) == 0) continue;
                    for (int sy = 0; sy < Scale; sy++)
                    {
                        for (int sx = 0; sx < Scale; sx++)
                        {
                            int px = gx + col * Scale + sx;
                            int py = y + (4 - row) * Scale + sy;
                            if (px < 0 || py < 0 || px >= width || py >= height) continue;
                            MapShotOverlay.Set(image, (py * width + px) * 4, color, 255);
                        }
                    }
                }
            }
        }
    }
}
