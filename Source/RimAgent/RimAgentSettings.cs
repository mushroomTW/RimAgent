using System;
using System.Collections.Generic;
using RimAgent.Agent;
using RimAgent.Tools;
using Verse;

namespace RimAgent;

/// <summary>玩家可調的設定：十二類工具權限、執行模式與背景排程選項。</summary>
public class RimAgentSettings : ModSettings
{
#pragma warning disable S1104 // RimWorld ModSettings and Listing_Standard require public fields passed by ref
    /// <summary>十二類工具開關，Taxi <see cref="ToolCategory"/> 順序存放；Scribe 鍵沿用舊欄位名，舊存檔與舊設定相容。</summary>
    public bool[] allowByCategory = DefaultAllows();
    /// <summary>執行模式：詢問／確認／自動／自主，見 <see cref="AgentMode"/>。</summary>
    public AgentMode mode = AgentMode.Confirm;
    /// <summary>模型思考強度，見 <see cref="ReasoningLevel"/>；Auto 沿用框架預設。</summary>
    public ReasoningLevel reasoning = ReasoningLevel.Auto;
    /// <summary>思考強度自動分流：開啟時每句指令由決策模型分類決定思考強度，<see cref="reasoning"/> 暫不套用。</summary>
    public bool reasoningRouting;
    /// <summary>Agent 優先使用的模型（"供應商:模型"，在 Agent 視窗切換）；留空則沿用框架備援鏈。</summary>
    public string agentModel = string.Empty;
    // 模型槽（參照 hermes-agent 的 auxiliary 模型）：Agent 以外的每一種模型呼叫各有一格，
    // 值有三態：<see cref="FollowAgentModel"/>＝跟著 Agent 視窗當前模型（預設）、空白＝框架備援鏈、其餘＝指定模型。
    // 用非空 sentinel 當預設而不是 null：Scribe 讀不到 null 會回填預設值，玩家選的值存檔後會靜靜被換掉。

    /// <summary>Auto 模式風險分類器的模型槽。</summary>
    public string classifierModel = FollowAgentModel;
    public ReasoningLevel classifierReasoning = ReasoningLevel.Auto;
    /// <summary>手動 /compact 摘要的模型槽。</summary>
    public string compactModel = FollowAgentModel;
    public ReasoningLevel compactReasoning = ReasoningLevel.Auto;
    /// <summary>所有子代理共用的模型槽。</summary>
    public string subagentModel = FollowAgentModel;
    public ReasoningLevel subagentReasoning = ReasoningLevel.Auto;

    /// <summary>總開關：關掉時所有決策模型功能都不呼叫，一律走主 LLM／規則的備援路徑。</summary>
    public bool decisionEnabled = true;
    /// <summary>決策模型供應商 id（留空＝沿用框架設定）。</summary>
    public string decisionProvider = string.Empty;
    /// <summary>決策模型名稱（留空＝沿用該供應商設定的模型）。</summary>
    public string decisionModel = string.Empty;

    public List<DecisionRouteCategory> decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
    public ReasoningLevel decisionFallbackReasoning = ReasoningLevel.Auto;
    public float decisionRoutingMinConfidence = 0.6f;                // 0 到 1
    /// <summary>System One 代答抉擇信件與對話框（背景照顧開啟時）；信心不足或要額外輸入的仍交給主 LLM。</summary>
    public bool decisionAnswerChoices = true;
    /// <summary>System One 先篩基地狀況觸發，判定不必處理就不叫主 LLM。</summary>
    public bool decisionTriageUpkeep = true;
    /// <summary>System One 每句指令預載用得到的工具包。</summary>
    public bool decisionPreloadPacks = true;

    /// <summary>模型槽的「跟隨 Agent」值。不含冒號，真實模型識別碼都是「供應商:模型」，撞不到。</summary>
    public const string FollowAgentModel = "=agent";

    /// <summary>模型槽的哨兵值「跟隨 Agent」不是真正的模型識別碼。</summary>
    public static bool IsModelSentinel(string value)
    {
        return string.Equals(value, FollowAgentModel, StringComparison.Ordinal);
    }

    /// <summary>把模型槽值解成實際模型識別碼；空白表示交給框架備援鏈。</summary>
    public string ResolveModelSlot(string slot, string currentAgentModel = null)
    {
        string value = (slot ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (string.Equals(value, FollowAgentModel, StringComparison.Ordinal))
        {
            value = (currentAgentModel ?? agentModel ?? string.Empty).Trim();
        }
        return value;
    }

    /// <summary>分類器實際該用的模型識別碼；空白表示交給框架備援鏈。</summary>
    public string ResolveClassifierModel(string currentAgentModel = null)
    {
        return ResolveModelSlot(classifierModel, currentAgentModel);
    }

    /// <summary>子代理實際該用的模型識別碼。</summary>
    public string ResolveSubagentModel(string currentAgentModel = null)
    {
        return ResolveModelSlot(subagentModel, currentAgentModel);
    }
    /// <summary>選用本機 SearXNG；預設改用免設定的 Parallel 匿名搜尋。</summary>
    public bool useLocalSearch = false;
#pragma warning disable S1075 // Default URL for local SearXNG service setting
    public string searxngUrl = "http://127.0.0.1:8888";
#pragma warning restore S1075

    // 自主模式（排程器）的節奏與配額在設定頁；持續目標另由獨立流程執行並跟著存檔走。
    // 定期巡檢的節奏刻意不在這裡（見 AutonomousScheduler.HeartbeatTicks）：那是 agent 的運轉節奏，
    // 開放給玩家調只會變成一個看不出在做什麼的旋鈕，按到底就是關掉主動巡視。
    public const int DefaultCooldownMinutes = 0;
    public const int MinCooldownMinutes = 0;
    public const int MaxCooldownMinutes = 60;

    public int autonomousCooldownMinutes = DefaultCooldownMinutes;
    /// <summary>是否主動照顧殖民地；獨立於工具執行模式。</summary>
    public bool autonomousCare = false;
    /// <summary>主動照顧時重大操作是否需要確認；完整權限模式不受影響。新安裝預設開啟（Scribe 預設值見 <see cref="ExposeData"/>）。</summary>
    public bool confirmAutonomousMajor = true;
    /// <summary>日常自動調整遊戲速度：安全時加速，出事降回正常；預設關閉。</summary>
    public bool autoTimeSpeed = false;
    /// <summary>讓 agent 能整組放置預先設計好的 13x13 模組化房型；預設開啟。</summary>
    public bool layouts = true;
    /// <summary>
    /// 框架查不出目前模型是否支援圖片輸入時（<c>RimLLMProvider.SupportsVision</c> 回傳 null）才採用的手動設定：
    /// 開啟時截圖會附給模型看、玩家可在輸入欄貼圖。預設關閉，不支援圖片的模型收到圖片會直接報錯。
    /// </summary>
    public bool imageInput = false;

    // 上下文壓縮：依模型視窗的百分比自動折疊較舊工具輸出（不多花模型呼叫）；
    // 手動 /compact 另走一次 LLM 摘要，不受此開關影響。
    public const int DefaultAutoCompactThreshold = 60;
    public const int MinAutoCompactThreshold = 30;
    // 上限 95：留一點輸出空間給模型，100% 會讓它在回應途中就撞到視窗天花板。
    public const int MaxAutoCompactThreshold = 95;

    public bool autoCompact = true;
    public int autoCompactThreshold = DefaultAutoCompactThreshold;

    /// <summary>將指令、模型回應與工具結果寫入有上限的獨立 JSONL 日誌；預設關閉。</summary>
    public bool recordRuns = false;

    // 懸浮視窗的位置與大小持久化
    public float windowRectX = -1f;
    public float windowRectY = -1f;
    public float windowRectWidth = 620f;
    public float windowRectHeight = 460f;
#pragma warning restore S1104

    public const float DefaultWindowWidth = 620f;
    public const float DefaultWindowHeight = 460f;
    public const float MinWindowWidth = 360f;
    public const float MinWindowHeight = 320f;

    private static readonly string[] CategoryKeys =
    [
        "allowObserve", "allowWork", "allowBuild", "allowCombat", "allowTrade", "allowMedical",
        "allowPrisoner", "allowAnimal", "allowZone", "allowProduction", "allowTime", "allowMCP",
        "allowWeb", "allowMemory", "allowAgents", "allowSave"
    ];

    /// <summary>新安裝的權限＝「推薦」方案：除了 MCP 全開。</summary>
    private static bool[] DefaultAllows()
    {
        bool[] allows = new bool[CategoryKeys.Length];
        for (int i = 0; i < allows.Length; i++) allows[i] = PresetAllows(PermissionPreset.Recommended, (ToolCategory)i);
        return allows;
    }

    /// <summary>設定頁的三種權限方案。</summary>
    public enum PermissionPreset
    {
        /// <summary>只看不動：只能查看、記筆記與上網查資料。</summary>
        LookOnly,
        /// <summary>幫我管家：除了外部程式（MCP）全開。</summary>
        Recommended,
        /// <summary>全部交給它。</summary>
        All
    }

    public static bool PresetAllows(PermissionPreset preset, ToolCategory category)
    {
        switch (preset)
        {
            case PermissionPreset.LookOnly:
                return category == ToolCategory.Observe || category == ToolCategory.Memory || category == ToolCategory.Web;
            case PermissionPreset.Recommended:
                return category != ToolCategory.Mcp;
            default:
                return true;
        }
    }

    public void ApplyPreset(PermissionPreset preset)
    {
        EnsureAllows();
        for (int i = 0; i < allowByCategory.Length; i++) allowByCategory[i] = PresetAllows(preset, (ToolCategory)i);
    }

    /// <summary>目前的權限符合哪個方案；手動調過不符合任何方案時回 null（自訂）。</summary>
    public PermissionPreset? MatchPreset()
    {
        EnsureAllows();
        foreach (PermissionPreset preset in (PermissionPreset[])Enum.GetValues(typeof(PermissionPreset)))
        {
            bool match = true;
            for (int i = 0; i < allowByCategory.Length && match; i++)
                match = allowByCategory[i] == PresetAllows(preset, (ToolCategory)i);
            if (match) return preset;
        }
        return null;
    }

    /// <summary>
    /// 設定頁的「恢復預設」：權限、主動照顧、決策模型、模型分工、壓縮、搜尋與紀錄回到新安裝的值。
    /// Agent 視窗裡選的模型、思考強度、執行模式與視窗位置不在設定頁，保留不動。
    /// </summary>
    public void ResetToDefaults()
    {
        var fresh = new RimAgentSettings();
        allowByCategory = fresh.allowByCategory;
        classifierModel = fresh.classifierModel;
        classifierReasoning = fresh.classifierReasoning;
        compactModel = fresh.compactModel;
        compactReasoning = fresh.compactReasoning;
        subagentModel = fresh.subagentModel;
        subagentReasoning = fresh.subagentReasoning;
        decisionEnabled = fresh.decisionEnabled;
        decisionProvider = fresh.decisionProvider;
        decisionModel = fresh.decisionModel;
        decisionCategories = fresh.decisionCategories;
        decisionFallbackReasoning = fresh.decisionFallbackReasoning;
        decisionRoutingMinConfidence = fresh.decisionRoutingMinConfidence;
        decisionAnswerChoices = fresh.decisionAnswerChoices;
        decisionTriageUpkeep = fresh.decisionTriageUpkeep;
        decisionPreloadPacks = fresh.decisionPreloadPacks;
        useLocalSearch = fresh.useLocalSearch;
        searxngUrl = fresh.searxngUrl;
        autonomousCooldownMinutes = fresh.autonomousCooldownMinutes;
        autonomousCare = fresh.autonomousCare;
        confirmAutonomousMajor = fresh.confirmAutonomousMajor;
        autoTimeSpeed = fresh.autoTimeSpeed;
        autoCompact = fresh.autoCompact;
        autoCompactThreshold = fresh.autoCompactThreshold;
        recordRuns = fresh.recordRuns;
    }

    private void EnsureAllows()
    {
        if (allowByCategory == null)
        {
            allowByCategory = DefaultAllows();
            return;
        }
        if (allowByCategory.Length == CategoryKeys.Length) return;

        // 舊存檔的分類數較少：保留玩家已關掉的權限，只把新加入的類別補成預設開啟。
        // 直接重置會讓升級後所有類別悄悄變回開啟。
        bool[] old = allowByCategory;
        allowByCategory = DefaultAllows();
        Array.Copy(old, allowByCategory, Math.Min(old.Length, allowByCategory.Length));
    }

    public bool GetAllow(ToolCategory category)
    {
        EnsureAllows();
        return allowByCategory[(int)category];
    }

    public void SetAllow(ToolCategory category, bool value)
    {
        EnsureAllows();
        allowByCategory[(int)category] = value;
    }

    /// <summary>
    /// 指定的 Agent 模型已不在框架的模型清單（備援鏈）時清回預設，改為跟隨框架。
    /// 玩家在框架換掉模型後，RimAgent 不該還留著舊模型字串繼續用。清單為空（框架未就緒）時不動。
    /// </summary>
    /// <returns>有清掉時回 true，呼叫端負責寫回設定檔。</returns>
    public bool ClearAgentModelIfMissing(IReadOnlyCollection<string> configuredModels)
    {
        string current = agentModel?.Trim();
        if (string.IsNullOrEmpty(current) || configuredModels == null || configuredModels.Count == 0) return false;
        foreach (string model in configuredModels)
        {
            if (string.Equals(model?.Trim(), current, StringComparison.OrdinalIgnoreCase)) return false;
        }
        agentModel = string.Empty;
        return true;
    }

    public UnityEngine.Vector2 WindowSize => new(
        UnityEngine.Mathf.Max(MinWindowWidth, windowRectWidth),
        UnityEngine.Mathf.Max(MinWindowHeight, windowRectHeight));

    public bool HasSavedWindowPosition => windowRectX >= 0f && windowRectY >= 0f;

    public UnityEngine.Rect GetWindowBounds(float screenWidth, float screenHeight)
    {
        float w = WindowSize.x;
        float h = WindowSize.y;
        float x = windowRectX;
        float y = windowRectY;

        if (!HasSavedWindowPosition || x > screenWidth - 60f || y > screenHeight - 40f)
        {
            x = UnityEngine.Mathf.Max(10f, screenWidth - w - 24f);
            y = UnityEngine.Mathf.Max(40f, screenHeight - h - 90f);
        }

        return new UnityEngine.Rect(x, y, w, h);
    }

    public void UpdateBounds(UnityEngine.Rect rect)
    {
        bool changed = UnityEngine.Mathf.Abs(windowRectX - rect.x) > 1f ||
                       UnityEngine.Mathf.Abs(windowRectY - rect.y) > 1f ||
                       UnityEngine.Mathf.Abs(windowRectWidth - rect.width) > 1f ||
                       UnityEngine.Mathf.Abs(windowRectHeight - rect.height) > 1f;
        if (changed)
        {
            windowRectX = rect.x;
            windowRectY = rect.y;
            windowRectWidth = rect.width;
            windowRectHeight = rect.height;
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        EnsureAllows();
        // Scribe 預設值刻意維持舊版的「全部開啟」：設定檔不寫入等於預設值的欄位，
        // 若跟著新的推薦預設改成關，老玩家沒寫進檔的 MCP 會在升級後悄悄被關掉。
        // 新安裝沒有設定檔、不經過這裡，直接用欄位初始值（推薦方案）。
        for (int i = 0; i < CategoryKeys.Length; i++)
        {
            Scribe_Values.Look(ref allowByCategory[i], CategoryKeys[i], true);
        }
        // 以名稱存取：已移除的詢問與 YOLO 模式不在列舉中，直接解析會報錯，改回確認。
        string modeName = mode.ToString();
        Scribe_Values.Look(ref modeName, "mode", nameof(AgentMode.Confirm));
        if (Scribe.mode == LoadSaveMode.LoadingVars)
            mode = Enum.TryParse(modeName, out AgentMode parsed) && Enum.IsDefined(typeof(AgentMode), parsed)
                ? parsed
                : AgentMode.Confirm;
        Scribe_Values.Look(ref reasoning, "reasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref reasoningRouting, "reasoningRouting", false);
        Scribe_Values.Look(ref agentModel, "agentModel", string.Empty);
        // 舊版的「自動分流」是模型選單裡的 "=auto" 哨兵；改為思考強度分流後，模型回到預設並開啟思考強度分流。
        if (Scribe.mode == LoadSaveMode.LoadingVars && string.Equals(agentModel, "=auto", StringComparison.Ordinal))
        {
            agentModel = string.Empty;
            reasoningRouting = true;
        }
        Scribe_Values.Look(ref classifierModel, "classifierModel", FollowAgentModel);
        Scribe_Values.Look(ref classifierReasoning, "classifierReasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref compactModel, "compactModel", FollowAgentModel);
        Scribe_Values.Look(ref compactReasoning, "compactReasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref subagentModel, "subagentModel", FollowAgentModel);
        Scribe_Values.Look(ref subagentReasoning, "subagentReasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref decisionEnabled, "decisionEnabled", true);
        Scribe_Values.Look(ref decisionProvider, "decisionProvider", string.Empty);
        Scribe_Values.Look(ref decisionModel, "decisionModel", string.Empty);
        Scribe_Collections.Look(ref decisionCategories, "decisionCategories", LookMode.Deep);
        if (decisionCategories == null) decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
        Scribe_Values.Look(ref decisionFallbackReasoning, "decisionFallbackReasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref decisionRoutingMinConfidence, "decisionRoutingMinConfidence", 0.6f);
        decisionRoutingMinConfidence = UnityEngine.Mathf.Clamp01(decisionRoutingMinConfidence);
        Scribe_Values.Look(ref decisionAnswerChoices, "decisionAnswerChoices", true);
        Scribe_Values.Look(ref decisionTriageUpkeep, "decisionTriageUpkeep", true);
        Scribe_Values.Look(ref decisionPreloadPacks, "decisionPreloadPacks", true);
        Scribe_Values.Look(ref useLocalSearch, "useLocalSearch", false);
        Scribe_Values.Look(ref searxngUrl, "searxngUrl", "http://127.0.0.1:8888");
        Scribe_Values.Look(ref autonomousCooldownMinutes, "autonomousCooldownMinutes", DefaultCooldownMinutes);
        Scribe_Values.Look(ref autonomousCare, "autonomousCare", false);
        // 同上：舊版預設為關，Scribe 預設值維持 false，老玩家升級後不變。
        Scribe_Values.Look(ref confirmAutonomousMajor, "confirmAutonomousMajor", false);
        Scribe_Values.Look(ref autoTimeSpeed, "autoTimeSpeed", false);
        Scribe_Values.Look(ref layouts, "layouts", true);
        Scribe_Values.Look(ref imageInput, "imageInput", false);
        Scribe_Values.Look(ref autoCompact, "autoCompact", true);
        Scribe_Values.Look(ref autoCompactThreshold, "autoCompactPercent", DefaultAutoCompactThreshold);
        Scribe_Values.Look(ref recordRuns, "recordRuns", false);
        Scribe_Values.Look(ref windowRectX, "windowRectX", -1f);
        Scribe_Values.Look(ref windowRectY, "windowRectY", -1f);
        Scribe_Values.Look(ref windowRectWidth, "windowRectWidth", 620f);
        Scribe_Values.Look(ref windowRectHeight, "windowRectHeight", 460f);
        // 壞存檔可能帶著範圍外的值：讀回後夾取，免得自動壓縮永不觸發或過度積極。
        autoCompactThreshold = UnityEngine.Mathf.Clamp(autoCompactThreshold, MinAutoCompactThreshold, MaxAutoCompactThreshold);
    }

    public ToolPermissions ToPermissions()
    {
        EnsureAllows();
        var permissions = new ToolPermissions();
        for (int i = 0; i < CategoryKeys.Length; i++)
        {
            permissions.Set((ToolCategory)i, allowByCategory[i]);
        }
        return permissions;
    }
}
