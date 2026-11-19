using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Agent;

public sealed partial class ColonyAgentSession
{
    /// <summary>
    /// 標題列計量用的快照：歷史則數、視窗上限與設定百分比。UI 不直接碰歷史與設定。
    /// </summary>
    public readonly struct ContextStatus
    {
        public readonly int Messages;
        public readonly int Threshold;
        public readonly int? ContextWindow;
        public readonly bool AutoCompactEnabled;
        public readonly long? LastInputTokens;

        public ContextStatus(int messages, int threshold, bool autoCompactEnabled, long? lastInputTokens, int? contextWindow)
        {
            Messages = messages;
            Threshold = Math.Max(1, threshold);
            AutoCompactEnabled = autoCompactEnabled;
            LastInputTokens = lastInputTokens;
            ContextWindow = contextWindow;
        }

        /// <summary>最近一次供應商回報用量占模型視窗的比例；任一數值未知時無法計算。</summary>
        public double? UsageFraction => LastInputTokens.HasValue && ContextWindow.HasValue
            ? (double)LastInputTokens.Value / ContextWindow.Value : (double?)null;
    }

    /// <summary>給 GameComponent 的單一同步點：開關與門檻永遠成對更新。</summary>
    public void SyncAutoCompact(bool enabled, int threshold)
    {
        AutoCompactEnabled = enabled;
        AutoCompactThreshold = threshold;
    }

    /// <summary>主模型最近一次回報的輸入 token、歷史則數及框架提供的視窗上限，供 UI 顯示。</summary>
    public ContextStatus GetContextStatus()
    {
        return new ContextStatus(History.Count, AutoCompactThreshold, AutoCompactEnabled,
            _tokenUsage.LastInputTokens, GetContextWindow());
    }

    /// <summary>上下文分布的一個類別；Key 由 UI 翻譯成名稱。</summary>
    public readonly struct ContextPart
    {
        public readonly string Key;
        public readonly long Tokens;
        public readonly int Count;

        public ContextPart(string key, long tokens, int count = 0)
        {
            Key = key;
            Tokens = tokens;
            Count = count;
        }
    }

    /// <summary>
    /// 下一次請求的上下文依類別估算 token，供 UI 分析面板用。
    /// 組法與 <see cref="BuildRunOptions(ReasoningLevel?, string)"/> 相同，但不碰進行中的呼叫狀態。
    /// 估算用字元數換算（CJK 一字約一 token，其餘約四字元一 token），只看比例，與供應商計數會有落差。
    /// </summary>
    public IReadOnlyList<ContextPart> GetContextBreakdown()
    {
        ToolPermissions permissions = _permissions();
        AgentToolRegistry.MainAgentTools selected = _registry.SelectForMainAgent(permissions, LoadedPacks());
        List<AITool> tools = [.. selected.Advertised];
        if (tools.Count > 0) tools.Add(_scriptRunner);
        if (selected.Packs.Count > 0) tools.Add(_toolPackLoader);
        IReadOnlyList<AgentSubagent> subagents = permissions.Allows(ToolCategory.Agents)
            ? _subagents.Available(permissions)
            : Array.Empty<AgentSubagent>();
        if (subagents.Count > 0) tools.Add(_subagents.Function);

        string core = null;
        if (_instructions != null) core = _instructions();
        if (string.IsNullOrWhiteSpace(core)) core = AgentPromptComposer.FallbackPreamble;
        string packIndex = selected.Packs.Count > 0 ? ToolPackTools.Index(selected.Packs) : null;
        string subagentText = subagents.Count > 0 ? SubagentTools.Instructions(subagents) : null;
        string memory = null;
        if (permissions.Allows(ToolCategory.Memory)) memory = MemoryTools.Instructions + "\n\n" + _memory.Index;

        long toolTokens = 0;
        foreach (AITool tool in tools)
        {
            toolTokens += EstimateTokens(tool.Name) + EstimateTokens(tool.Description);
            if (tool is AIFunctionDeclaration function) toolTokens += EstimateTokens(function.JsonSchema.GetRawText());
        }

        long user = 0, assistant = 0, calls = 0, results = 0;
        int userCount = 0, callCount = 0;
        try
        {
            CountHistory(ref user, ref assistant, ref calls, ref results, ref userCount, ref callCount);
        }
        catch (InvalidOperationException)
        {
            // 執行中歷史或訊息內容正被寫入／折疊；這一輪先不算，面板下一次刷新再補。
            user = assistant = calls = results = 0;
            userCount = callCount = 0;
        }

        return
        [
            new ContextPart("SystemPrompt", EstimateTokens(core)),
            new ContextPart("Tools", toolTokens, tools.Count),
            new ContextPart("ToolPacks", EstimateTokens(packIndex), selected.Packs.Count),
            new ContextPart("Subagents", EstimateTokens(subagentText), subagents.Count),
            new ContextPart("Memory", EstimateTokens(memory)),
            new ContextPart("UserMessages", user, userCount),
            new ContextPart("AssistantMessages", assistant),
            new ContextPart("ToolCalls", calls, callCount),
            new ContextPart("ToolResults", results)
        ];
    }

    private void CountHistory(ref long user, ref long assistant, ref long calls, ref long results, ref int userCount, ref int callCount)
    {
        foreach (ChatMessage message in History)
        {
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when message.Role == ChatRole.User:
                        user += EstimateTokens(text.Text);
                        break;
                    case TextContent text:
                        assistant += EstimateTokens(text.Text);
                        break;
                    case TextReasoningContent reasoning:
                        assistant += EstimateTokens(reasoning.Text);
                        break;
                    case FunctionCallContent call:
                        callCount++;
                        calls += EstimateTokens(call.Name) + (call.Arguments == null ? 0 : call.Arguments.Sum(a => EstimateTokens(a.Key) + EstimateTokens(a.Value?.ToString())));
                        break;
                    case FunctionResultContent result:
                        results += EstimateTokens(result.Result?.ToString());
                        break;
                }
            }
            if (message.Role == ChatRole.User) userCount++;
        }
    }

    internal static long EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        long wide = text.Count(c => c >= 0x2E80);
        return wide + (text.Length - wide + 3) / 4;
    }

    /// <summary>
    /// 以 AF 估算的模型歷史 token 數觸發；保留輸出空間，避免等到視窗滿才折疊。
    /// 框架查不到模型視窗時不猜上限，仍可用 /compact 手動摘要。
    /// </summary>
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
    internal bool ShouldAutoCompact(CompactionMessageIndex index)
    {
        if (index == null || !AutoCompactEnabled) return false;
        int? window = GetContextWindow();
        if (!window.HasValue) return false;
        long budget = Math.Min((long)window.Value * Math.Max(1, AutoCompactThreshold) / 100,
            (long)window.Value - MaxOutputTokens);
        // IncludedTokenCount 才是不含排除群組、實際會送進模型的量；TotalTokenCount 會把排除內容也算進去。
        return budget > 0 && index.IncludedTokenCount >= budget;
    }

    private int? GetContextWindow()
    {
        try
        {
            string current = ResolveCurrentModel();
            return current == null ? null : MinimumKnownContextWindow([current], _contextWindowLookup);
        }
        catch (InvalidOperationException)
        {
            // 框架尚未初始化時，視窗上限暫不可用。
            return null;
        }
    }

    /// <summary>
    /// 是否讓模型看圖（截圖結果與玩家貼上的圖片）。框架知道目前模型支不支援圖片輸入就照它，
    /// 不明時（未重新整理模型清單、資料來源沒有該模型）退回玩家的「圖片輸入」設定。
    /// </summary>
    public bool ImageInputEnabled
    {
        get
        {
            bool? known = null;
            try
            {
                string current = ResolveCurrentModel();
                if (current != null) known = _visionLookup(current);
            }
            catch (InvalidOperationException)
            {
                // 框架尚未初始化時，退回玩家設定。
            }
            return known ?? RimAgentMod.Settings?.imageInput == true;
        }
    }

    /// <summary>讀檔在背景執行緒；解碼、縮圖與 JPEG 編碼要用 Unity API，排回主線程。檔案還沒寫完時回傳 null。</summary>
    private static async Task<byte[]> LoadScreenshotForModelAsync(IGameWorld world, string path)
    {
        byte[] png = await Task.Run(() => world.ReadScreenshotFile(path)).ConfigureAwait(false);
        if (png == null) return null;
        return await RimLLM_Framework.Core.RimLLMDispatcher.EnqueueOnMainThreadAsync(() => ModelImageEncoder.FromEncoded(png)).ConfigureAwait(false);
    }

    /// <summary>目前主模型（"供應商:模型"）：有指定就用它，跟隨框架時是備援鏈第一個；都沒有時為 null。</summary>
    private string ResolveCurrentModel()
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        string preferred = PreferredModel;
        string model = settings != null ? settings.ResolveModelSlot(preferred) : preferred;
        if (!string.IsNullOrWhiteSpace(model) && !RimAgentSettings.IsModelSentinel(model)) return model.Trim();
        List<string> candidates;
        try { candidates = RimLLMProvider.GetFallbackChain(); }
        catch (InvalidOperationException) { candidates = []; }
        return candidates.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
    }

    internal static int? MinimumKnownContextWindow(IEnumerable<string> candidates, Func<string, int?> lookup)
    {
        int? minimum = null;
        foreach (string model in candidates.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int? window = lookup(model.Trim());
            if (window > 0 && (!minimum.HasValue || window.Value < minimum.Value)) minimum = window;
        }
        return minimum;
    }

#pragma warning restore MAAI001

    /// <summary>
    /// 更新工具迴圈的迭代上限，供有明確單輪預算的呼叫端使用。
    /// 重設框架屬性不影響 AF session 的歷史。
    /// </summary>
    public void UpdateMaxSteps(int maxSteps)
    {
        int normalized = NormalizeSteps(maxSteps);
        if (Interlocked.Exchange(ref _maxSteps, normalized) == normalized) return;
        _invoking?.MaximumIterationsPerRequest = normalized;
    }

    private static int NormalizeSteps(int maxSteps)
    {
        return Math.Max(1, maxSteps);
    }

    /// <summary>
    /// 手動壓縮（/compact）：用一次 LLM 摘要把較舊的模型歷史折成一則摘要，只保留最近幾組；
    /// 可見的對話紀錄（transcript）、復原堆疊與等待中的確認不受影響。忙碌中不可壓縮。
    /// 沒有歷史可壓、或壓完一則沒少（AF 策略會吞掉摘要失敗並原樣回傳）時寫 Notice 並回傳 null；
    /// 其他例外記一則 Error。任何情況下歷史都不會丟失。
    /// 注意：此處不呼叫 Verse 翻譯（背景續行執行緒不安全），訊息維持英文，
    /// 與 <see cref="ConfirmAsync"/>、<see cref="UndoLast"/> 的 session 層訊息一致。
    /// </summary>
    public async Task<string> CompactAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new AgentBusyException();
        }
        IDisposable compacting = Activity.Begin(AgentActivityKind.Compacting);
        try
        {
            var provider = _agent.ChatHistoryProvider as InMemoryChatHistoryProvider;
            List<ChatMessage> before = provider != null && _session != null
                ? [.. provider.GetMessages(_session)]
                : [];
            if (before.Count == 0)
            {
                AddNotice("Nothing to compact: model history is empty.");
                return null;
            }
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
            var strategy = new SummarizationCompactionStrategy(
                _summarizer,
                CompactionTriggers.Always,
                ManualSummaryPreservedGroups,
                null,
                null);
            IList<ChatMessage> after = [.. (await CompactionProvider.CompactAsync(strategy, before, null, cancellationToken).ConfigureAwait(false))];
#pragma warning restore MAAI001
            // 摘要只會把多組折成一則，不可能變多；一樣多代表沒折到
            // （歷史太短，或策略吞掉了摘要失敗），誠實回報而不是謊稱成功。
            if (after.Count >= before.Count)
            {
                AddNotice($"No compaction: model history still has {before.Count} messages. It may already be compact, or the summarizer failed; the history was not changed.");
                return null;
            }
            provider.SetMessages(_session, [.. after]);
            _tokenUsage.Reset();
            // 展開紀錄多半已被摘要掉，包跟著收回，工具清單回到精簡狀態。
            UnloadToolPacks();
            string summary = $"Context compacted: model history {before.Count} -> {after.Count} messages. Visible log kept.";
            AddNotice(summary);
            return summary;
        }
        catch (Exception ex)
        {
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Error,
                ErrorKind = Classify(ex),
                Text = ex.Message,
                InstructionId = Transcript.CurrentInstructionId
            });
            return null;
        }
        finally
        {
            compacting.Dispose();
            PrepareSaveSnapshot();
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Compact 系 Notice 的單一入口，免得每次重複 new TranscriptEntry。</summary>
    private void AddNotice(string text)
    {
        Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = text,
            InstructionId = Transcript.CurrentInstructionId
        });
    }
}
