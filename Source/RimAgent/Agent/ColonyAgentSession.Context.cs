#nullable disable

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
    public readonly struct ContextStatus
    {
        public readonly int Messages;
        public readonly int Threshold;
        public readonly int? ContextWindow;
        public readonly bool AutoCompactEnabled;
        public readonly long? LastInputTokens;
        public readonly long? CompactBudget;
        /// <summary>上次真實用量加上之後新增、還沒送出的內容；與自動壓縮觸發時比的是同一個量。</summary>
        public readonly long? ProjectedInputTokens;

        public ContextStatus(int messages, int threshold, bool autoCompactEnabled, long? lastInputTokens, int? contextWindow, long? pendingTokens = null)
        {
            Messages = messages;
            Threshold = Math.Max(1, threshold);
            AutoCompactEnabled = autoCompactEnabled;
            LastInputTokens = lastInputTokens;
            ContextWindow = contextWindow;
            CompactBudget = CompactionBudget(contextWindow, Threshold);
            ProjectedInputTokens = lastInputTokens + (pendingTokens ?? 0);
        }

        /// <summary>最近一次供應商回報用量占模型視窗的比例；任一數值未知時無法計算。</summary>
        public double? UsageFraction => LastInputTokens.HasValue && ContextWindow.HasValue
            ? (double)LastInputTokens.Value / ContextWindow.Value : (double?)null;
    }

    public void SyncAutoCompact(bool enabled, int threshold)
    {
        AutoCompactEnabled = enabled;
        AutoCompactThreshold = threshold;
    }

    public ContextStatus GetContextStatus()
    {
        long? pending;
        try
        {
            pending = _tokenUsage.Meter.Inspect(History).Pending;
        }
        catch (InvalidOperationException)
        {
            // 執行中歷史正被寫入；這一輪只顯示上次真實值。
            pending = null;
        }
        return new ContextStatus(History.Count, AutoCompactThreshold, AutoCompactEnabled,
            _tokenUsage.LastInputTokens, GetContextWindow(), pending);
    }

    public readonly struct ContextPart
    {
        public readonly string Key;
        public readonly long Tokens;
        public readonly int Count;
        /// <summary>數字由供應商回報的用量推回；否則為字元數估算。</summary>
        public readonly bool Exact;
        /// <summary>還沒載入的工具包：不在這一輪送出的上下文裡，只顯示載入後會佔多少。</summary>
        public readonly bool Deferred;

        public ContextPart(string key, long tokens, int count = 0, bool exact = false, bool deferred = false)
        {
            Key = key;
            Tokens = tokens;
            Count = count;
            Exact = exact;
            Deferred = deferred;
        }
    }

    /// <summary>組法與 <see cref="CreateRunOptions(ReasoningLevel?, string)"/> 相同但不碰進行中的呼叫狀態。歷史用供應商用量推回的大小；其餘以字元數估算（CJK 一字約一 token，其餘約四字元一 token）。</summary>
    public IReadOnlyList<ContextPart> GetContextBreakdown()
    {
        ToolPermissions permissions = _permissions();
        AgentToolRegistry.MainAgentTools selected = _registry.SelectForMainAgent(permissions, LoadedPacks());
        List<AITool> tools = [.. selected.Advertised];
        if (tools.Count > 0) tools.Add(_scriptRunner);
        if (selected.Packs.Count > 0) tools.Add(_toolPackLoader);
        IReadOnlyList<AgentSubagent> subagents = permissions.Allows(ToolCategory.Agents)
            ? _subagents.Available(permissions)
            : [];
        if (subagents.Count > 0) tools.Add(_subagents.Function);

        string core = null;
        if (_instructions != null) core = _instructions();
        if (string.IsNullOrWhiteSpace(core)) core = AgentPromptComposer.FallbackPreamble;
        string packIndex = selected.Packs.Count > 0 ? ToolPackTools.Index(selected.Packs) : null;
        string subagentText = subagents.Count > 0 ? SubagentTools.Instructions(subagents) : null;
        string memory = null;
        if (permissions.Allows(ToolCategory.Memory)) memory = MemoryTools.Instructions + "\n\n" + _memory.Index;

        long toolTokens = tools.Sum(EstimateToolTokens);
        var loaded = new HashSet<AITool>(tools);
        var deferred = new HashSet<AITool>();
        int deferredPacks = 0;
        foreach (var pack in selected.PackTools.Values)
        {
            bool unloaded = false;
            foreach (AITool tool in pack)
            {
                if (loaded.Contains(tool)) continue;
                unloaded = true;
                deferred.Add(tool);
            }
            if (unloaded) deferredPacks++;
        }

        HistoryTally user = new(), assistant = new(), calls = new(), results = new();
        int userCount = 0;
        try
        {
            CountHistory(user, assistant, calls, results, ref userCount);
        }
        catch (InvalidOperationException)
        {
            // 執行中歷史或訊息內容正被寫入／折疊；這一輪先不算，面板下一次刷新再補。
            user = new(); assistant = new(); calls = new(); results = new();
            userCount = 0;
        }

        return
        [
            new ContextPart("SystemPrompt", EstimateTokens(core)),
            new ContextPart("Tools", toolTokens, tools.Count),
            new ContextPart("ToolPacks", EstimateTokens(packIndex), selected.Packs.Count),
            new ContextPart("Subagents", EstimateTokens(subagentText), subagents.Count),
            new ContextPart("Memory", EstimateTokens(memory)),
            user.ToPart("UserMessages", userCount),
            assistant.ToPart("AssistantMessages", 0),
            calls.ToPart("ToolCalls", calls.Items),
            results.ToPart("ToolResults", 0),
            new ContextPart("DeferredPacks", deferred.Sum(EstimateToolTokens), deferredPacks, deferred: true)
        ];
    }

    private static long EstimateToolTokens(AITool tool)
    {
        long tokens = EstimateTokens(tool.Name) + EstimateTokens(tool.Description);
        if (tool is AIFunctionDeclaration function) tokens += EstimateTokens(function.JsonSchema.GetRawText());
        return tokens;
    }

    /// <summary>精確的部分照原值；真實總數與精確部分的差額，按比例分給估算的部分。</summary>
    internal static IReadOnlyList<ContextPart> ReconcileParts(IReadOnlyList<ContextPart> parts, long? realTotal)
    {
        if (!realTotal.HasValue) return parts;
        long exact = parts.Where(p => p.Exact && !p.Deferred).Sum(p => p.Tokens);
        long estimated = parts.Where(p => !p.Exact && !p.Deferred).Sum(p => p.Tokens);
        if (estimated <= 0) return parts;
        double scale = Math.Max(0, realTotal.Value - exact) / (double)estimated;
        return [.. parts.Select(p => p.Exact || p.Deferred ? p : new ContextPart(p.Key, (long)Math.Round(p.Tokens * scale, MidpointRounding.ToEven), p.Count))];
    }

    private sealed class HistoryTally
    {
        public long Tokens;
        public int Items;
        private bool _allMeasured = true;

        public void Add(long? measured, long estimate)
        {
            Items++;
            Tokens += measured ?? estimate;
            if (!measured.HasValue) _allMeasured = false;
        }

        public ContextPart ToPart(string key, int count) => new(key, Tokens, count, exact: _allMeasured);
    }

    private void CountHistory(HistoryTally user, HistoryTally assistant, HistoryTally calls, HistoryTally results, ref int userCount)
    {
        ContextMeter meter = _tokenUsage.Meter;
        HashSet<AIContent> folded = meter.Inspect(History).Folded;
        foreach (ChatMessage message in History)
        {
            foreach (AIContent content in message.Contents)
            {
                if (folded.Contains(content)) continue;
                HistoryTally target = content switch
                {
                    TextContent when message.Role == ChatRole.User => user,
                    TextContent or TextReasoningContent => assistant,
                    FunctionCallContent => calls,
                    FunctionResultContent => results,
                    _ => null
                };
                target?.Add(meter.Measured(content), ContextMeter.Estimate(content));
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

    private const double ReserveShare = 0.15;
    private const long ReserveMin = 16000;

    /// <summary>預留量照 opencode（`packages/core/src/session/compaction.ts` 的 calculateCeiling）：視窗的 15%，且至少 16k；視窗不到 32k 時只留 15%。框架查不到模型視窗時不猜上限。</summary>
    internal static long? CompactionBudget(int? window, int threshold)
    {
        if (!window.HasValue || window.Value <= 0) return null;
        long size = window.Value;
        long budget = Math.Min(size * Math.Max(1, threshold) / 100, size - Reserve(size));
        return budget > 0 ? budget : null;
    }

    private static long Reserve(long window) => Math.Max((long)Math.Floor(window * ReserveShare), window >= 2 * ReserveMin ? ReserveMin : 0);

    /// <summary>不超過模型的輸出上限（超過會被部分供應商拒絕），也不超過壓縮預留量，輸入與輸出才放得進視窗；輸出上限不明時不放寬預設值。</summary>
    internal static int OutputBudget(int? window, int? outputLimit)
    {
        long cap = outputLimit is > 0 ? outputLimit.Value : DefaultMaxOutputTokens;
        if (window is > 0) cap = Math.Min(cap, Reserve(window.Value));
        return (int)Math.Max(1, cap);
    }

    /// <summary><paramref name="model"/> 為空或哨兵值（跟隨主模型）時查目前的主模型。</summary>
    internal int MaxOutputFor(string model)
    {
        string resolved = string.IsNullOrWhiteSpace(model)
            || RimAgentSettings.IsModelSentinel(model.Trim())
            || string.Equals(model, RimAgentSettings.FollowAgentModel, StringComparison.Ordinal)
            ? ResolveCurrentModel()
            : model.Trim();
        if (resolved == null) return DefaultMaxOutputTokens;
        List<string> chain;
        try { chain = RimLLMProvider.GetFallbackChain(); }
        catch (InvalidOperationException) { chain = []; }
        try
        {
            return OutputBudgetFor([resolved, .. chain], _contextWindowLookup, _outputLimitLookup);
        }
        catch (InvalidOperationException)
        {
            // 框架尚未初始化時，上限暫不可用。
            return DefaultMaxOutputTokens;
        }
    }

    /// <summary>框架失敗時會沿備援鏈換模型重送同一份 options，取所有候選中最小的預算，換到誰都不會超過上限。</summary>
    internal static int OutputBudgetFor(IEnumerable<string> candidates, Func<string, int?> windowLookup, Func<string, int?> limitLookup)
    {
        int? minimum = null;
        foreach (string model in candidates.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int budget = OutputBudget(windowLookup(model), limitLookup(model));
            if (!minimum.HasValue || budget < minimum.Value) minimum = budget;
        }
        return minimum ?? DefaultMaxOutputTokens;
    }

    /// <summary>有供應商回報的用量時以它為錨點推算這次請求的輸入 token；沒有時退回 AF 估算的訊息量。</summary>
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
    internal bool ShouldAutoCompact(CompactionMessageIndex index)
    {
        if (index == null || !AutoCompactEnabled) return false;
        long? budget = CompactionBudget(GetContextWindow(), AutoCompactThreshold);
        if (!budget.HasValue) return false;
        // IncludedTokenCount 才是不含排除群組、實際會送進模型的量；TotalTokenCount 會把排除內容也算進去。
        long measure = _tokenUsage.Meter.Project(index.GetIncludedMessages()) ?? index.IncludedTokenCount;
        return measure >= budget.Value;
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

    /// <summary>框架知道目前模型支不支援圖片輸入就照它，不明時（未重新整理模型清單、資料來源沒有該模型）退回玩家的「圖片輸入」設定。</summary>
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
                summarizationPrompt: null,
                target: null);
            IList<ChatMessage> after = [.. (await CompactionProvider.CompactAsync(strategy, before, logger: null, cancellationToken).ConfigureAwait(false))];
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
