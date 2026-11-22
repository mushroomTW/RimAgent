#nullable disable

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Tools;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;

namespace RimAgent.UI;

#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent
{
    // 圓角貼圖（白色，用 GUI.color 上色）：鍵是半徑像素。Unity 內建的圓角 DrawTexture 在遊戲裡不生效，只能自己產生。
    private static readonly Dictionary<int, Texture2D> RoundedFillTex = [];

    private static readonly Dictionary<int, Texture2D> RoundedRingTex = [];

    private static void DrawRoundedBox(Rect rect, Color fill, Color border, float radius = ControlRadius)
    {
        if (Event.current.type != EventType.Repaint) return;
        int r = Mathf.Max(1, Mathf.RoundToInt(radius));
        Color oldColor = GUI.color;
        if (fill.a > 0f)
        {
            GUI.color = fill;
            DrawNineSlice(rect, GetRoundedTex(RoundedFillTex, r, ring: false), r);
        }
        if (border.a > 0f)
        {
            GUI.color = border;
            DrawNineSlice(rect, GetRoundedTex(RoundedRingTex, r, ring: true), r);
        }
        GUI.color = oldColor;
    }

    private static Texture2D _shieldTex;

    /// <summary>盾牌外框：上緣平、兩側直到一半，再收成底部尖端。程式產生，免得多出貨一張圖。</summary>
    private static Texture2D ShieldTex => _shieldTex ??= BuildShieldTex(32);

    private static Texture2D BuildShieldTex(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var pixels = new Color[size * size];
        const float Stroke = 0.13f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = 1f - (y + 0.5f) / size;
                bool outer = InShield(u, v, 0f);
                bool inner = InShield(u, v, Stroke);
                pixels[y * size + x] = new Color(1f, 1f, 1f, outer && !inner ? 1f : 0f);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    private static bool InShield(float u, float v, float inset)
    {
        const float Top = 0.06f;
        if (v < Top + inset || v > 0.98f - inset * 1.4f) return false;
        float t = Mathf.InverseLerp(0.45f, 0.98f - inset * 1.4f, v);
        float halfWidth = (0.82f - inset) * (1f - t * t);
        return Mathf.Abs(u) <= halfWidth;
    }

    private static Texture2D GetRoundedTex(Dictionary<int, Texture2D> cache, int r, bool ring)
    {
        if (cache.TryGetValue(r, out Texture2D cached)) return cached;
        int size = r * 2 + 2;
        float half = size / 2f;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
            float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - half) - (half - r));
            float dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - half) - (half - r));
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float alpha = Mathf.Clamp01(r + 0.5f - distance);
            // 外框：以 1px 線條中心算覆蓋率再開根號；弧線像素的覆蓋天生較低，弧段稍加粗補亮，直邊仍是完整 1px。
            if (ring)
            {
                float reach = dx > 0f && dy > 0f ? 1.3f : 1f;
                alpha = Mathf.Sqrt(Mathf.Clamp01(reach - Mathf.Abs(distance - (r - 0.5f))));
            }
            pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
        }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        cache[r] = texture;
        return texture;
    }

    /// <summary>
    /// 手動九宮格，顏色取自 GUI.color。GUIStyle 的 border 九宮格在接縫處會重疊約 1px，
    /// 半透明底色因此在離邊 r 像素處多疊一條暗線，所以改成九塊互不重疊的貼圖。
    /// UV 的 v 由下往上：畫面上排對應 v 靠 1 的那段、下排對應靠 0 的那段，圓角方向才不會反。
    /// </summary>
    private static void DrawNineSlice(Rect rect, Texture2D texture, int r)
    {
        float c = Mathf.Min(r, Mathf.Floor(Mathf.Min(rect.width, rect.height) / 2f));
        float k = (float)r / texture.width;
        float m = 2f / texture.width;
        // UI 縮放不是 1 時切線落在半像素上，Unity 各自把每塊對齊像素，相鄰兩塊會重疊 1px（半透明底色疊兩層變暗線）。
        // 先把每條切線對齊到裝置像素，相鄰兩塊共用同一個座標。
        // 換算參數整個九宮格共用一份，不必每條切線各算一次。
        Vector2 origin = GUIUtility.GUIToScreenPoint(Vector2.zero);
        Vector2 unit = GUIUtility.GUIToScreenPoint(Vector2.one) - origin;
        float sx = PixelScale(unit.x, GUI.matrix.m00), sy = PixelScale(unit.y, GUI.matrix.m11);
        float x0 = AlignToPixel(rect.x, origin.x, unit.x, sx), x1 = AlignToPixel(rect.x + c, origin.x, unit.x, sx);
        float x2 = AlignToPixel(rect.xMax - c, origin.x, unit.x, sx), x3 = AlignToPixel(rect.xMax, origin.x, unit.x, sx);
        float y0 = AlignToPixel(rect.y, origin.y, unit.y, sy), y1 = AlignToPixel(rect.y + c, origin.y, unit.y, sy);
        float y2 = AlignToPixel(rect.yMax - c, origin.y, unit.y, sy), y3 = AlignToPixel(rect.yMax, origin.y, unit.y, sy);
        float midW = x2 - x1, midH = y2 - y1;
        GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x0, y0, x1, y1), texture, new Rect(0f, 1f - k, k, k));
        GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x2, y0, x3, y1), texture, new Rect(1f - k, 1f - k, k, k));
        GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x0, y2, x1, y3), texture, new Rect(0f, 0f, k, k));
        GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x2, y2, x3, y3), texture, new Rect(1f - k, 0f, k, k));
        if (midW > 0f)
        {
            GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x1, y0, x2, y1), texture, new Rect(k, 1f - k, m, k));
            GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x1, y2, x2, y3), texture, new Rect(k, 0f, m, k));
        }
        if (midH > 0f)
        {
            GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x0, y1, x1, y2), texture, new Rect(0f, k, k, m));
            GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x2, y1, x3, y2), texture, new Rect(1f - k, k, k, m));
        }
        if (midW > 0f && midH > 0f)
            GUI.DrawTextureWithTexCoords(Rect.MinMaxRect(x1, y1, x2, y2), texture, new Rect(k, k, m, m));
    }

    /// <summary>
    /// GUIToScreenPoint 是否已含 GUI.matrix 的縮放不確定：量得 1 個 GUI 單位剛好 1 螢幕單位時，
    /// 縮放還沒算進去，用當下 GUI.matrix 的縮放（而非全域 UI 縮放，子矩陣另外縮放時才不會錯）。
    /// </summary>
    private static float PixelScale(float perUnit, float matrixScale)
    {
        return Mathf.Abs(perUnit - 1f) < 0.001f && matrixScale > 0f ? matrixScale : 1f;
    }

    private static float AlignToPixel(float v, float origin, float perUnit, float scale)
    {
        float pixel = Mathf.Round((origin + v * perUnit) * scale);
        return (pixel / scale - origin) / perUnit;
    }
}
