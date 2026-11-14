using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Tools;
using RimAgent.UI;

namespace RimAgent.Agent;

/// <summary>
/// 送出請求前附上圖片，讓模型看得到：
/// <list type="bullet">
/// <item>玩家在輸入欄貼上的圖：附在本句指令（最後一則使用者訊息）上。</item>
/// <item>take_screenshot 的截圖：在本輪最新一次截圖結果的工具訊息後面，插入一則附圖的使用者訊息。
/// 工具訊息本身只能放文字（OpenAI 相容端點的限制），所以圖片另起一則。</item>
/// </list>
/// 只改送出的請求，不寫回對話歷史（也就不進存檔）；下一句指令後就不再重送，避免每輪都付圖片的 token。
/// </summary>
internal sealed class ImageAttachingChatClient : DelegatingChatClient
{
    internal const string ScreenshotToolName = "take_screenshot";

    /// <summary>截圖在該幀渲染完才寫檔，讀不到時每隔一小段重試，最多約兩秒。</summary>
    internal const int MaxLoadAttempts = 20;

    private const int MaxCachedImages = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Func<bool> _enabled;
    private readonly Func<string, Task<byte[]>> _load;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<byte[]> _playerImages;

    /// <summary>設定本句指令要附的玩家圖片（JPEG）；指令結束時以 null 清除。</summary>
    public void SetPlayerImages(IReadOnlyList<byte[]> images)
    {
        _playerImages = images != null && images.Count > 0 ? images : null;
    }

    /// <param name="innerClient">下一層 client。</param>
    /// <param name="enabled">設定「模型支援圖片輸入」是否開啟。</param>
    /// <param name="load">讀取並縮圖（須排到主線程）；尚未寫完回傳 null。</param>
    public ImageAttachingChatClient(IChatClient innerClient, Func<bool> enabled, Func<string, Task<byte[]>> load)
        : base(innerClient)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _load = load ?? throw new ArgumentNullException(nameof(load));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> prepared = await AttachAsync(messages, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(prepared, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> prepared = await AttachAsync(messages, cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(prepared, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    internal async Task<IEnumerable<ChatMessage>> AttachAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (messages == null || !_enabled()) return messages;
        List<ChatMessage> list = messages as List<ChatMessage> ?? [.. messages];
        int lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
        List<ChatMessage> copy = null;

        IReadOnlyList<byte[]> playerImages = _playerImages;
        if (playerImages != null && lastUser >= 0)
        {
            copy = [.. list];
            copy[lastUser] = WithImages(list[lastUser], playerImages);
        }

        if (TryFindLatestScreenshot(list, lastUser + 1, out int toolIndex, out string path))
        {
            byte[] image = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (image != null)
            {
                // 插在同一批工具訊息之後：工具訊息必須緊接著發出呼叫的助理訊息。
                int insertAt = toolIndex + 1;
                while (insertAt < list.Count && list[insertAt].Role == ChatRole.Tool) insertAt++;
                copy ??= [.. list];
                copy.Insert(insertAt, new ChatMessage(ChatRole.User, new List<AIContent>
                {
                    new TextContent("Image from " + ScreenshotToolName + " (" + Path.GetFileName(path) + "):"),
                    new DataContent(image, "image/jpeg")
                }));
            }
        }
        return copy ?? messages;
    }

    /// <summary>複製一則訊息並在內容後面加上圖片；原訊息屬於對話歷史，不能直接改。</summary>
    private static ChatMessage WithImages(ChatMessage original, IReadOnlyList<byte[]> images)
    {
        var contents = new List<AIContent>(original.Contents);
        contents.AddRange(images.Select(i => (AIContent)new DataContent(i, "image/jpeg")));
        return new ChatMessage(original.Role, contents)
        {
            AuthorName = original.AuthorName,
            MessageId = original.MessageId,
            AdditionalProperties = original.AdditionalProperties
        };
    }

    /// <summary>從後往前找本輪最新一次成功的截圖結果；run_script 裡的截圖步驟也算。</summary>
    private static bool TryFindLatestScreenshot(List<ChatMessage> list, int start, out int toolIndex, out string path)
    {
        var screenshotCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FunctionCallContent call in list.Skip(start)
                     .Where(m => m.Role == ChatRole.Assistant)
                     .SelectMany(m => m.Contents)
                     .OfType<FunctionCallContent>()
                     .Where(c => (c.Name == ScreenshotToolName || c.Name == ScriptTools.ToolName) && c.CallId != null))
        {
            screenshotCalls[call.CallId] = call.Name;
        }

        for (int i = list.Count - 1; i >= start && screenshotCalls.Count > 0; i--)
        {
            if (list[i].Role != ChatRole.Tool) continue;
            foreach (FunctionResultContent result in list[i].Contents.OfType<FunctionResultContent>().Reverse())
            {
                if (result.CallId == null || !screenshotCalls.TryGetValue(result.CallId, out string tool)) continue;
                string text = result.Result?.ToString();
                path = tool == ScreenshotToolName
                    ? TranscriptScreenshot.TryParseSavedPath(text)
                    : TranscriptScreenshot.TryParseScriptSavedPath(text);
                if (path == null) continue;
                toolIndex = i;
                return true;
            }
        }
        toolIndex = -1;
        path = null;
        return false;
    }

    private async Task<byte[]> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(path, out byte[] cached)) return cached;
        for (int attempt = 0; attempt < MaxLoadAttempts; attempt++)
        {
            byte[] image = await _load(path).ConfigureAwait(false);
            if (image != null)
            {
                if (_cache.Count >= MaxCachedImages) _cache.Clear();
                _cache[path] = image;
                return image;
            }
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
