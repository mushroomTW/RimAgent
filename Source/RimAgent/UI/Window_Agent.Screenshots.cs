#nullable disable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Tools;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent
{
    private void DrawScreenshotPreview(Rect rect, string path)
    {
        DrawRoundedBox(rect, ControlBgColor, ControlBorderColor);
        Texture2D texture = GetScreenshotTexture(path);
        if (texture != null)
        {
            GUI.color = Color.white;
            GUI.DrawTexture(rect.ContractedBy(4f), texture, ScaleMode.ScaleToFit);
        }
        else
        {
            GUI.color = ToolTextColor;
            Widgets.Label(rect.ContractedBy(8f), "RimAgent.Screenshot.Pending".Translate());
        }
        GUI.color = Color.white;
    }

    /// <summary>繪製時只查快取；還沒載入的記下來，交給 <see cref="LoadPendingScreenshot"/> 在 WindowUpdate 載入。</summary>
    private Texture2D GetScreenshotTexture(string path)
    {
        if (_screenshotTextures.TryGetValue(path, out Texture2D cached))
        {
            // 用到的移到最後：看得到的截圖永遠是最近用過的，淘汰不會輪到它們。
            // 比較方式要與字典一致（不分大小寫），否則同一張圖會在清單裡重複。
            int last = _screenshotOrder.Count - 1;
            if (!string.Equals(_screenshotOrder[last], path, StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < last; i++)
                {
                    if (!string.Equals(_screenshotOrder[i], path, StringComparison.OrdinalIgnoreCase)) continue;
                    string key = _screenshotOrder[i];
                    _screenshotOrder.RemoveAt(i);
                    _screenshotOrder.Add(key);
                    break;
                }
            }
            return cached;
        }
        if (!_screenshotFailedAt.TryGetValue(path, out int failedAt)
            || unchecked(Environment.TickCount - failedAt) >= ScreenshotRetryMs)
        {
            _pendingScreenshot = path;
        }
        return null;
    }

    /// <summary>
    /// 每幀最多載入一張截圖（在 WindowUpdate，不在 OnGUI 裡解碼）：先在背景讀檔，讀完才在主線程解碼，
    /// 再用 GPU 逐次減半縮到 <see cref="MaxScreenshotWidth"/>（一次大倍率縮會嚴重鋸齒），
    /// 只留縮圖且丟掉 CPU 端副本，原圖立即釋放。
    /// 檔案還不在或讀不了就隔 <see cref="ScreenshotRetryMs"/> 再試，不每幀打磁碟。
    /// Unity 原生呼叫集中在這一個方法（RuntimeCompatibilityTests 逐一登記這類方法）。
    /// </summary>
    private void LoadPendingScreenshot()
    {
        if (_screenshotRead == null)
        {
            string pending = _pendingScreenshot;
            _pendingScreenshot = null;
            if (pending == null || _screenshotTextures.ContainsKey(pending)) return;
            _readingScreenshot = pending;
            _screenshotRead = Task.Run(() => File.Exists(pending) ? File.ReadAllBytes(pending) : null, CancellationToken.None);
            return;
        }
        if (!_screenshotRead.IsCompleted) return;
        string path = _readingScreenshot;
        // 讀檔失敗（IO、權限）時 Task 為 faulted，當作讀不到。
        #pragma warning disable VSTHRD002 // 上一行已確認 Task 完成，此處不會阻塞
        byte[] bytes = _screenshotRead.Status == TaskStatus.RanToCompletion ? _screenshotRead.Result : null;
        #pragma warning restore VSTHRD002
        _screenshotRead = null;
        _readingScreenshot = null;

        Texture2D preview = TryCreatePreviewTexture(bytes);
        if (preview == null)
        {
            _screenshotFailedAt[path] = Environment.TickCount;
            return;
        }
        _screenshotFailedAt.Remove(path);
        _screenshotTextures[path] = preview;
        _screenshotOrder.Add(path);
        while (_screenshotOrder.Count > MaxCachedScreenshots)
        {
            string oldest = _screenshotOrder[0];
            _screenshotOrder.RemoveAt(0);
            UnityEngine.Object.Destroy(_screenshotTextures[oldest]);
            _screenshotTextures.Remove(oldest);
        }
    }

    private static Texture2D TryCreatePreviewTexture(byte[] bytes)
    {
        if (bytes == null) return null;
        Texture2D full = null;
        try
        {
            full = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!full.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(full);
                return null;
            }
            return DownscaleTexture(full);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is UnityException)
        {
            if (full != null) UnityEngine.Object.Destroy(full);
            return null;
        }
    }

    private static Texture2D DownscaleTexture(Texture2D full)
    {
        if (full.width <= MaxScreenshotWidth)
        {
            full.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return full;
        }

        RenderTexture previous = RenderTexture.active;
        RenderTexture step = null;
        try
        {
            Texture source = full;
            int width = full.width;
            do
            {
                width = Mathf.Max(MaxScreenshotWidth, width / 2);
                int height = Mathf.Max(1, Mathf.RoundToInt(full.height * (width / (float)full.width)));
                RenderTexture next = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, next);
                if (step != null) RenderTexture.ReleaseTemporary(step);
                step = next;
                source = next;
            }
            while (width > MaxScreenshotWidth);

            RenderTexture.active = step;
            var preview = new Texture2D(step.width, step.height, TextureFormat.RGBA32, mipChain: false);
            try
            {
                preview.ReadPixels(new Rect(0f, 0f, step.width, step.height), 0, 0);
                preview.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            }
            catch
            {
                // 失敗時 preview 沒人會接手，要自己銷毀，免得洩漏貼圖。
                UnityEngine.Object.Destroy(preview);
                throw;
            }
            return preview;
        }
        finally
        {
            RenderTexture.active = previous;
            if (step != null) RenderTexture.ReleaseTemporary(step);
            UnityEngine.Object.Destroy(full);
        }
    }
}
