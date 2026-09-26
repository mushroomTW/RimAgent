using RimAgent.Agent;
using RimAgent.Tools;
using Verse;

namespace RimAgent
{
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
        /// <summary>Agent 優先使用的模型（"供應商:模型"，在 Agent 視窗切換）；留空則沿用框架備援鏈。</summary>
        public string agentModel = string.Empty;
        /// <summary>Auto 模式風險分類器模型識別碼（留空則沿用框架預設）。</summary>
        public string classifierModel = string.Empty;
        /// <summary>選用本機 SearXNG；預設改用免設定的 Parallel 匿名搜尋。</summary>
        public bool useLocalSearch = false;
        public string searxngUrl = "http://127.0.0.1:8888";

        // 自主模式（排程器）的節奏與配額在設定頁；持續目標另由獨立流程執行並跟著存檔走。
        public const int DefaultAutoIntervalHours = 6;
        public const int MinAutoIntervalHours = 1;
        public const int MaxAutoIntervalHours = 24;
        public const int DefaultCooldownMinutes = 0;
        public const int MinCooldownMinutes = 0;
        public const int MaxCooldownMinutes = 60;

        public int autonomousIntervalHours = DefaultAutoIntervalHours;
        public int autonomousCooldownMinutes = DefaultCooldownMinutes;
        public bool autoOnRaid = true;
        public bool autoOnMentalBreak = true;
        public bool autoOnFoodShortage = true;
        public bool autoOnEmergencyMedical = true;
        public bool autoOnFire = true;

        // 上下文壓縮：依模型視窗的百分比自動折疊較舊工具輸出（不多花模型呼叫）；
        // 手動 /compact 另走一次 LLM 摘要，不受此開關影響。
        public const int DefaultAutoCompactThreshold = 90;
        public const int MinAutoCompactThreshold = 30;
        public const int MaxAutoCompactThreshold = 90;

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
        {
            "allowObserve", "allowWork", "allowBuild", "allowCombat", "allowTrade", "allowMedical",
            "allowPrisoner", "allowAnimal", "allowZone", "allowProduction", "allowTime", "allowMCP",
            "allowWeb", "allowMemory"
        };

        private static bool[] DefaultAllows()
        {
            return new[]
            {
                true, true, true, true, true, true,
                true, true, true, true, true, true,
                true, true
            };
        }

        private void EnsureAllows()
        {
            if (allowByCategory == null || allowByCategory.Length != CategoryKeys.Length)
            {
                allowByCategory = DefaultAllows();
            }
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

        public UnityEngine.Vector2 WindowSize => new UnityEngine.Vector2(
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
            for (int i = 0; i < CategoryKeys.Length; i++)
            {
                Scribe_Values.Look(ref allowByCategory[i], CategoryKeys[i], true);
            }
            Scribe_Values.Look(ref mode, "mode", AgentMode.Confirm);
            Scribe_Values.Look(ref reasoning, "reasoning", ReasoningLevel.Auto);
            Scribe_Values.Look(ref agentModel, "agentModel", string.Empty);
            Scribe_Values.Look(ref classifierModel, "classifierModel", string.Empty);
            Scribe_Values.Look(ref useLocalSearch, "useLocalSearch", false);
            Scribe_Values.Look(ref searxngUrl, "searxngUrl", "http://127.0.0.1:8888");
            Scribe_Values.Look(ref autonomousIntervalHours, "autonomousIntervalHours", DefaultAutoIntervalHours);
            Scribe_Values.Look(ref autonomousCooldownMinutes, "autonomousCooldownMinutes", DefaultCooldownMinutes);
            Scribe_Values.Look(ref autoOnRaid, "autoOnRaid", true);
            Scribe_Values.Look(ref autoOnMentalBreak, "autoOnMentalBreak", true);
            Scribe_Values.Look(ref autoOnFoodShortage, "autoOnFoodShortage", true);
            Scribe_Values.Look(ref autoOnEmergencyMedical, "autoOnEmergencyMedical", true);
            Scribe_Values.Look(ref autoOnFire, "autoOnFire", true);
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
}
