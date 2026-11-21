#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Agent;
using RimAgent.Tools;
using Verse;

namespace RimAgent;

public class RimAgentSettings : ModSettings
{
#pragma warning disable S1104 // RimWorld ModSettings and Listing_Standard require public fields passed by ref
    /// <summary>Scribe 鍵沿用舊欄位名，舊存檔與舊設定相容。</summary>
    public bool[] allowByCategory = DefaultAllows();
    public AgentMode mode = AgentMode.Confirm;
    public ReasoningLevel reasoning = ReasoningLevel.Auto;
    public bool reasoningRouting;
    public string agentModel = string.Empty;
    // 模型槽（參照 hermes-agent 的 auxiliary 模型）：Agent 以外的每一種模型呼叫各有一格，
    // 值有三態：<see cref="FollowAgentModel"/>＝跟著 Agent 視窗當前模型（預設）、空白＝框架備援鏈、其餘＝指定模型。
    // 用非空 sentinel 當預設而不是 null：Scribe 讀不到 null 會回填預設值，玩家選的值存檔後會靜靜被換掉。

    public string classifierModel = FollowAgentModel;
    public ReasoningLevel classifierReasoning = ReasoningLevel.Auto;
    public string compactModel = FollowAgentModel;
    public ReasoningLevel compactReasoning = ReasoningLevel.Auto;
    public string subagentModel = FollowAgentModel;
    public ReasoningLevel subagentReasoning = ReasoningLevel.Auto;

    public bool decisionEnabled = true;
    public string decisionProvider = string.Empty;
    public string decisionModel = string.Empty;

    public List<DecisionRouteCategory> decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
    public ReasoningLevel decisionFallbackReasoning = ReasoningLevel.Auto;
    public float decisionRoutingMinConfidence = 0.6f;                // 0 到 1
    public bool decisionAnswerChoices = true;
    public bool decisionTriageUpkeep = true;
    public bool decisionPreloadPacks = true;

    /// <summary>模型槽的「跟隨 Agent」值。不含冒號，真實模型識別碼都是「供應商:模型」，撞不到。</summary>
    public const string FollowAgentModel = "=agent";

    public static bool IsModelSentinel(string value)
    {
        return string.Equals(value, FollowAgentModel, StringComparison.Ordinal);
    }

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

    public string ResolveClassifierModel(string currentAgentModel = null)
    {
        return ResolveModelSlot(classifierModel, currentAgentModel);
    }

    public string ResolveSubagentModel(string currentAgentModel = null)
    {
        return ResolveModelSlot(subagentModel, currentAgentModel);
    }
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
    public bool autonomousCare = false;
    /// <summary>新安裝預設開啟；Scribe 預設值見 <see cref="ExposeData"/>。</summary>
    public bool confirmAutonomousMajor = true;
    public bool autoTimeSpeed = false;
    public bool layouts = true;
    /// <summary>框架查不出模型是否支援圖片輸入（<c>RimLLMProvider.SupportsVision</c> 回傳 null）時才採用的手動設定；不支援圖片的模型收到圖片會直接報錯。</summary>
    public bool imageInput = false;

    // 上下文壓縮：依模型視窗的百分比自動折疊較舊工具輸出（不多花模型呼叫）；
    // 手動 /compact 另走一次 LLM 摘要，不受此開關影響。
    public const int DefaultAutoCompactThreshold = 60;
    public const int MinAutoCompactThreshold = 30;
    // 上限 95：留一點輸出空間給模型，100% 會讓它在回應途中就撞到視窗天花板。
    public const int MaxAutoCompactThreshold = 95;

    public bool autoCompact = true;
    public int autoCompactThreshold = DefaultAutoCompactThreshold;

    public bool recordRuns = false;

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

    private static bool[] DefaultAllows()
    {
        bool[] allows = new bool[CategoryKeys.Length];
        for (int i = 0; i < allows.Length; i++) allows[i] = PresetAllows(PermissionPreset.Recommended, (ToolCategory)i);
        return allows;
    }

    public enum PermissionPreset
    {
        LookOnly,
        Recommended,
        All
    }

    public static bool PresetAllows(PermissionPreset preset, ToolCategory category)
    {
        return preset switch
        {
            PermissionPreset.LookOnly => category == ToolCategory.Observe || category == ToolCategory.Memory || category == ToolCategory.Web,
            PermissionPreset.Recommended => category != ToolCategory.Mcp,
            _ => true,
        };
    }

    public void ApplyPreset(PermissionPreset preset)
    {
        EnsureAllows();
        for (int i = 0; i < allowByCategory.Length; i++) allowByCategory[i] = PresetAllows(preset, (ToolCategory)i);
    }

    public PermissionPreset? MatchPreset()
    {
        EnsureAllows();
        foreach (PermissionPreset preset in Enum.GetValues<PermissionPreset>())
        {
            bool match = true;
            for (int i = 0; i < allowByCategory.Length && match; i++)
                match = allowByCategory[i] == PresetAllows(preset, (ToolCategory)i);
            if (match) return preset;
        }
        return null;
    }

    /// <summary>恢復預設不含 Agent 視窗裡選的模型、思考強度、執行模式與視窗位置，這些不在設定頁。</summary>
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
        layouts = fresh.layouts;
        imageInput = fresh.imageInput;
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

    public bool ClearAgentModelIfMissing(IReadOnlyCollection<string> configuredModels)
    {
        string current = agentModel?.Trim();
        if (string.IsNullOrEmpty(current) || configuredModels == null || configuredModels.Count == 0) return false;
        if (configuredModels.Any(model => string.Equals(model?.Trim(), current, StringComparison.OrdinalIgnoreCase))) return false;
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
            Scribe_Values.Look(ref allowByCategory[i], CategoryKeys[i], defaultValue: true);
        }
        ExposeModelSettings();
        ExposeDecisionSettings();
        Scribe_Values.Look(ref useLocalSearch, "useLocalSearch", defaultValue: false);
        Scribe_Values.Look(ref searxngUrl, "searxngUrl", "http://127.0.0.1:8888");
        Scribe_Values.Look(ref autonomousCooldownMinutes, "autonomousCooldownMinutes", DefaultCooldownMinutes);
        Scribe_Values.Look(ref autonomousCare, "autonomousCare", defaultValue: false);
        // 同上：舊版預設為關，Scribe 預設值維持 false，老玩家升級後不變。
        Scribe_Values.Look(ref confirmAutonomousMajor, "confirmAutonomousMajor", defaultValue: false);
        Scribe_Values.Look(ref autoTimeSpeed, "autoTimeSpeed", defaultValue: false);
        Scribe_Values.Look(ref layouts, "layouts", defaultValue: true);
        Scribe_Values.Look(ref imageInput, "imageInput", defaultValue: false);
        Scribe_Values.Look(ref autoCompact, "autoCompact", defaultValue: true);
        Scribe_Values.Look(ref autoCompactThreshold, "autoCompactPercent", DefaultAutoCompactThreshold);
        Scribe_Values.Look(ref recordRuns, "recordRuns", defaultValue: false);
        Scribe_Values.Look(ref windowRectX, "windowRectX", -1f);
        Scribe_Values.Look(ref windowRectY, "windowRectY", -1f);
        Scribe_Values.Look(ref windowRectWidth, "windowRectWidth", 620f);
        Scribe_Values.Look(ref windowRectHeight, "windowRectHeight", 460f);
        // 壞存檔可能帶著範圍外的值：讀回後夾取，免得自動壓縮永不觸發或過度積極。
        autoCompactThreshold = UnityEngine.Mathf.Clamp(autoCompactThreshold, MinAutoCompactThreshold, MaxAutoCompactThreshold);
    }

    private void ExposeModelSettings()
    {
        // 以名稱存取：已移除的詢問與 YOLO 模式不在列舉中，直接解析會報錯，改回確認。
        string modeName = mode.ToString();
        Scribe_Values.Look(ref modeName, "mode", nameof(AgentMode.Confirm));
        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            mode = Enum.TryParse(modeName, out AgentMode parsed) && Enum.IsDefined<AgentMode>(parsed)
                ? parsed
                : AgentMode.Confirm;
        }
        Scribe_Values.Look(ref reasoning, "reasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref reasoningRouting, "reasoningRouting", defaultValue: false);
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
    }

    private void ExposeDecisionSettings()
    {
        Scribe_Values.Look(ref decisionEnabled, "decisionEnabled", defaultValue: true);
        Scribe_Values.Look(ref decisionProvider, "decisionProvider", string.Empty);
        Scribe_Values.Look(ref decisionModel, "decisionModel", string.Empty);
        Scribe_Collections.Look(ref decisionCategories, "decisionCategories", LookMode.Deep);
        if (decisionCategories == null) decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
        Scribe_Values.Look(ref decisionFallbackReasoning, "decisionFallbackReasoning", ReasoningLevel.Auto);
        Scribe_Values.Look(ref decisionRoutingMinConfidence, "decisionRoutingMinConfidence", 0.6f);
        decisionRoutingMinConfidence = UnityEngine.Mathf.Clamp01(decisionRoutingMinConfidence);
        Scribe_Values.Look(ref decisionAnswerChoices, "decisionAnswerChoices", defaultValue: true);
        Scribe_Values.Look(ref decisionTriageUpkeep, "decisionTriageUpkeep", defaultValue: true);
        Scribe_Values.Look(ref decisionPreloadPacks, "decisionPreloadPacks", defaultValue: true);
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
