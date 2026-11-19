using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RimAgent.Agent;
using RimAgent.Tools;
using RimAgent.UI;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

/// <summary>Mod 進入點與設定頁。執行模式由 Agent 視窗切換。</summary>
public partial class RimAgentMod : Mod
{
    public static RimAgentSettings Settings { get; internal set; }

    private const float PermissionRowHeight = 46f;
    private const float PermissionColumnGap = 18f;
    private const float PresetCardHeight = 86f;
    private const float PresetCardGap = 10f;
    private const float OpenFolderButtonWidth = 110f;
    private const float SubIndent = 28f;
    private const string FrameworkPackageId = "GreenMushroom.RimLLMFramework";

    private static readonly Color Gold = new Color(0.94f, 0.78f, 0.45f);
    private static readonly Color Green = new Color(0.44f, 0.75f, 0.45f);
    private static readonly Color Yellow = new Color(0.88f, 0.72f, 0.31f);
    private static readonly Color Red = new Color(0.88f, 0.42f, 0.37f);
    private static readonly Color Dim = new Color(0.66f, 0.67f, 0.69f);
    private static readonly Color PanelFill = new Color(1f, 1f, 1f, 0.05f);
    private static readonly Color SelectedFill = new Color(0.94f, 0.78f, 0.45f, 0.16f);
    private static readonly Color PanelBorder = new Color(0.4f, 0.41f, 0.43f);

    private static readonly ToolCategory[] Categories = Enum.GetValues<ToolCategory>();

    /// <summary>權限清單依影響程度分三組：只看、會改遊戲但改得回來、影響大。</summary>
    private static readonly (string Key, Color Color, ToolCategory[] Members)[] PermissionGroups =
    [
        ("RimAgent.Settings.PermissionGroupGreen", Green, [ToolCategory.Observe, ToolCategory.Memory, ToolCategory.Web]),
        ("RimAgent.Settings.PermissionGroupYellow", Yellow, [ToolCategory.Time, ToolCategory.Work, ToolCategory.Build, ToolCategory.Zone, ToolCategory.Production, ToolCategory.Animal, ToolCategory.Medical]),
        ("RimAgent.Settings.PermissionGroupRed", Red, [ToolCategory.Combat, ToolCategory.Trade, ToolCategory.Prisoner, ToolCategory.Save, ToolCategory.Agents, ToolCategory.Mcp]),
    ];

    private static readonly RimAgentSettings.PermissionPreset[] Presets =
        Enum.GetValues<RimAgentSettings.PermissionPreset>();

    /// <summary>主動照顧多久可以出手一次（遊戲分鐘）；0＝隨時。</summary>
    private static readonly int[] CooldownChoices = [0, 10, 30, 60];
    private const string DecisionDefaultKey = "RimAgent.Settings.DecisionDefault";
    private const string CustomIdPrefix = "custom_";

    // 設定頁每個 IMGUI 事件都會重畫：權限名稱、說明與提示快取起來，免得每幀翻譯。
    private static string[] _permissionLabels;
    private static string[] _permissionDescs;
    private static string[] _permissionTips;
    private static LoadedLanguage _permissionLanguage;

    // 權限清單與進階設定預設收合；只在本次遊戲期間記住，不寫入設定檔。
    private static bool _showPermissionList;
    private bool _showAdvanced;

    private Vector2 _settingsScrollPosition = Vector2.zero;
    private float _settingsHeight = 960f;

    public RimAgentMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<RimAgentSettings>();
    }

    public override string SettingsCategory()
    {
        return "RimAgent.Settings.Category".CanTranslate()
            ? "RimAgent.Settings.Category".Translate().Resolve()
            : "RimAgent";
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        Rect viewRect = new Rect(0f, 0f, inRect.width - 24f, _settingsHeight);
        Widgets.BeginScrollView(inRect, ref _settingsScrollPosition, viewRect);

        var listing = new Listing_Standard();
        // 捲動檢視裡必須單欄：Listing.GetRect 內容超過檢視高度會自動另起一欄，
        // 超出的內容會跑到檢視寬度外的第二欄（看起來像被截斷且捲不下去），
        // 高度也永遠長不大、捲軸出不來。權限的雙欄是手排的絕對座標，不受影響。
        listing.maxOneColumn = true;
        listing.Begin(viewRect);

        // 主頁只問三個問題；說明直接寫在選項下面，不靠滑鼠停留的提示。
        DrawReadyCard(listing);

        DrawSectionHeader(listing, 1, "RimAgent.Settings.Section1Title".Translate(), "RimAgent.Settings.Section1Hint".Translate());
        DrawPresetCards(listing);
        DrawPermissionList(listing);

        DrawSectionHeader(listing, 2, "RimAgent.Settings.Section2Title".Translate(), "RimAgent.Settings.Section2Hint".Translate());
        DrawAutonomousCare(listing);

        // 總開關與連線狀態放在主頁；供應商、模型與各項細節留在進階設定。
        DrawSectionHeader(listing, 3, "RimAgent.Settings.Section3Title".Translate(), "RimAgent.Settings.Section3Hint".Translate());
        // 決策模型沒設定好時開關變灰、點不動，下方說明原因並給前往按鈕；存檔裡的值不動，設定好後照原值生效。
        DecisionAvailability decision = DecisionServiceFactory.GetAvailability();
        bool decisionReady = IsDecisionReady(decision);
        bool decisionOn = decisionReady && Settings.decisionEnabled;
        DrawToggleRow(listing, ref decisionOn, "RimAgent.Settings.DecisionEnabled".Translate(),
            "RimAgent.Settings.DecisionEnabledDesc".Translate(), "RimAgent.Settings.DecisionEnabledTip".Translate(), 0f, !decisionReady);
        if (decisionReady) Settings.decisionEnabled = decisionOn;
        if (!decisionReady || decisionOn) DrawDecisionStatus(listing, decision);

        listing.Gap(20f);
        if (DrawExpandButton(listing, _showAdvanced, "RimAgent.Settings.ShowAdvanced".Translate(), "RimAgent.Settings.HideAdvanced".Translate(),
            "RimAgent.Settings.ShowAdvancedTip".Translate()))
        {
            _showAdvanced = !_showAdvanced;
        }
        if (_showAdvanced) DrawAdvanced(listing);

        DrawFooter(listing);

        listing.End();
        _settingsHeight = Mathf.Max(listing.CurHeight + 24f, inRect.height);
        Widgets.EndScrollView();
    }

    /// <summary>進階設定：模型分工、決策模型（含分流）、上下文壓縮、網路搜尋來源與執行紀錄。</summary>
    private static void DrawAdvanced(Listing_Standard listing)
    {
        DrawModelRoles(listing);

        // 主頁關掉決策模型時，這些細節都沒有作用，整段不顯示；
        // 分流靠決策模型分類，沒有可用的決策模型時細項也不顯示。
        if (Settings.decisionEnabled)
        {
            listing.Gap();
            listing.Label("RimAgent.Settings.DecisionModelHeader".Translate());
            listing.GapLine();
        }
        if (Settings.decisionEnabled && DrawDecisionSettings(listing))
        {
            listing.CheckboxLabeled("RimAgent.Settings.DecisionAnswerChoices".Translate(), ref Settings.decisionAnswerChoices,
                "RimAgent.Settings.DecisionAnswerChoicesTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.DecisionTriageUpkeep".Translate(), ref Settings.decisionTriageUpkeep,
                "RimAgent.Settings.DecisionTriageUpkeepTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.DecisionPreloadPacks".Translate(), ref Settings.decisionPreloadPacks,
                "RimAgent.Settings.DecisionPreloadPacksTip".Translate());
            listing.Gap(6f);
            DrawDecisionRoutingSettings(listing);
        }

        listing.Gap();
        listing.Label("RimAgent.Settings.CompactionHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.AutoCompact".Translate(), ref Settings.autoCompact, "RimAgent.Settings.AutoCompactTip".Translate());
        if (Settings.autoCompact)
        {
            listing.Label("RimAgent.Settings.AutoCompactThreshold".Translate(Settings.autoCompactThreshold), -1f, "RimAgent.Settings.AutoCompactThresholdTip".Translate());
            Settings.autoCompactThreshold = Mathf.RoundToInt(listing.Slider(Settings.autoCompactThreshold, RimAgentSettings.MinAutoCompactThreshold, RimAgentSettings.MaxAutoCompactThreshold));
        }

        listing.Gap();
        listing.Label("RimAgent.Settings.LayoutsHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.Layouts".Translate(), ref Settings.layouts, "RimAgent.Settings.LayoutsTip".Translate());

        listing.Gap();
        listing.Label("RimAgent.Settings.ImageHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.ImageInput".Translate(), ref Settings.imageInput, "RimAgent.Settings.ImageInputTip".Translate());

        listing.Gap();
        listing.Label("RimAgent.Settings.WebSearchHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.UseLocalSearch".Translate(), ref Settings.useLocalSearch,
            "RimAgent.Settings.UseLocalSearchTip".Translate());
        if (Settings.useLocalSearch)
        {
            listing.Label("RimAgent.Settings.SearxngUrl".Translate());
            Settings.searxngUrl = Widgets.TextField(listing.GetRect(28f), Settings.searxngUrl ?? string.Empty);
        }

        listing.Gap();
        listing.Label("RimAgent.Settings.RunLogHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.RecordRuns".Translate(), ref Settings.recordRuns,
            "RimAgent.Settings.RecordRunsTip".Translate());
        if (Settings.recordRuns)
        {
            string logFolder = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent");
            string logPath = Path.Combine(logFolder, "RimAgent-runs.jsonl");
            Rect logRow = listing.GetRect(28f);
            Rect openRect = new Rect(logRow.xMax - OpenFolderButtonWidth, logRow.y, OpenFolderButtonWidth, 26f);
            Rect pathRect = new Rect(logRow.x, logRow.y + 3f, openRect.x - logRow.x - 8f, 24f);
            // 路徑很長，單行截斷，完整路徑放在提示。
            Text.WordWrap = false;
            Widgets.Label(pathRect, "RimAgent.Settings.RunLogPath".Translate(logPath).ToString().Truncate(pathRect.width));
            Text.WordWrap = true;
            TooltipHandler.TipRegion(pathRect, logPath);
            if (Widgets.ButtonText(openRect, "RimAgent.Settings.OpenLogFolder".Translate()))
            {
                OpenFolder(logFolder);
            }
        }
    }

    /// <summary>最上面的準備狀態：框架有沒有可用的 AI 模型；沒有就給一顆直接前往框架設定的按鈕。</summary>
    private static void DrawReadyCard(Listing_Standard listing)
    {
        // 框架沒有公開「主供應商有沒有金鑰」的 API，只能以備援鏈裡有沒有模型判斷。
        List<string> models = Window_Agent.ConfiguredModels();
        bool ready = models.Count > 0;
        Color accent = ready ? Green : Yellow;

        Rect card = listing.GetRect(64f);
        Widgets.DrawBoxSolid(card, new Color(accent.r, accent.g, accent.b, 0.10f));
        GUI.color = accent;
        Widgets.DrawBox(card, 2);
        GUI.color = Color.white;

        Rect inner = card.ContractedBy(10f);
        const float ButtonWidth = 230f;
        float textWidth = ready ? inner.width : inner.width - ButtonWidth - 10f;
        string title = ready ? "RimAgent.Settings.ReadyTitle".Translate() : "RimAgent.Settings.NotReadyTitle".Translate();
        string detail = ready
            ? "RimAgent.Settings.ReadyDetail".Translate(models[0]).ToString()
            : "RimAgent.Settings.NotReadyDetail".Translate().ToString();

        Text.WordWrap = false;
        GUI.color = accent;
        Widgets.Label(new Rect(inner.x, inner.y, textWidth, 24f), title.Truncate(textWidth));
        Text.Font = GameFont.Tiny;
        GUI.color = Dim;
        Widgets.Label(new Rect(inner.x, inner.y + 24f, textWidth, 20f), detail.Truncate(textWidth));
        Text.Font = GameFont.Small;
        GUI.color = Color.white;
        Text.WordWrap = true;

        if (!ready)
        {
            var buttonRect = new Rect(inner.xMax - ButtonWidth, inner.y + (inner.height - 32f) / 2f, ButtonWidth, 32f);
            if (Widgets.ButtonText(buttonRect, "RimAgent.Settings.GoToFramework".Translate())) OpenFrameworkSettings();
        }
    }

    /// <summary>主動照顧：一個大開關，開啟後才顯示子選項與出手頻率。</summary>
    private static void DrawAutonomousCare(Listing_Standard listing)
    {
        bool careWasEnabled = Settings.autonomousCare;
        DrawToggleCard(listing, ref Settings.autonomousCare, "RimAgent.Settings.AutonomousCare".Translate(),
            "RimAgent.Settings.AutonomousCareDesc".Translate(), "RimAgent.Settings.AutonomousCareTip".Translate());
        if (careWasEnabled && !Settings.autonomousCare) RimAgentGameComponent.Instance?.DisableAutonomousCare();
        if (!Settings.autonomousCare) return;

        listing.Gap(4f);
        DrawToggleRow(listing, ref Settings.confirmAutonomousMajor, "RimAgent.Settings.ConfirmAutonomousMajor".Translate(),
            "RimAgent.Settings.ConfirmAutonomousMajorDesc".Translate(), "RimAgent.Settings.ConfirmAutonomousMajorTip".Translate(), SubIndent);
        DrawToggleRow(listing, ref Settings.autoTimeSpeed, "RimAgent.Settings.AutoTimeSpeed".Translate(),
            "RimAgent.Settings.AutoTimeSpeedDesc".Translate(), "RimAgent.Settings.AutoTimeSpeedTip".Translate(), SubIndent);

        listing.Gap(6f);
        Rect question = listing.GetRect(24f);
        Widgets.Label(new Rect(question.x + SubIndent, question.y, question.width - SubIndent, question.height), "RimAgent.Settings.CooldownQuestion".Translate());
        Rect segRow = listing.GetRect(32f);
        float segX = segRow.x + SubIndent;
        float segWidth = (segRow.width - SubIndent) / CooldownChoices.Length;
        for (int i = 0; i < CooldownChoices.Length; i++)
        {
            DrawCooldownSegment(segRow, segX + i * segWidth, segWidth, CooldownChoices[i]);
        }
        listing.Gap(2f);
        string hint = "RimAgent.Settings.CooldownHint".Translate();
        if (!CooldownChoices.Contains(Settings.autonomousCooldownMinutes))
            hint += " " + "RimAgent.Settings.CooldownCustom".Translate(Settings.autonomousCooldownMinutes);
        DrawHint(listing, hint, SubIndent, Dim);
    }

    /// <summary>頁尾：框架設定的位置與前往按鈕，右側恢復預設（先確認）。</summary>
    private static void DrawFooter(Listing_Standard listing)
    {
        listing.Gap(16f);
        listing.GapLine();
        Rect row = listing.GetRect(30f);
        const float ResetWidth = 130f;
        const float GoWidth = 110f;
        var resetRect = new Rect(row.xMax - ResetWidth, row.y, ResetWidth, 28f);
        var goRect = new Rect(resetRect.x - GoWidth - 16f, row.y, GoWidth, 28f);
        float noteWidth = goRect.x - row.x - 8f;
        Text.WordWrap = false;
        GUI.color = Dim;
        string note = "RimAgent.Settings.FrameworkNote".Translate();
        Widgets.Label(new Rect(row.x, row.y + 4f, noteWidth, 24f), note.Truncate(noteWidth));
        GUI.color = Color.white;
        Text.WordWrap = true;
        TooltipHandler.TipRegion(new Rect(row.x, row.y, noteWidth, row.height), note);
        if (Widgets.ButtonText(goRect, "RimAgent.Settings.GoShort".Translate())) OpenFrameworkSettings();
        if (Widgets.ButtonText(resetRect, "RimAgent.Settings.ResetDefaults".Translate()))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RimAgent.Settings.ResetDefaultsConfirm".Translate(), () =>
            {
                bool careWasEnabled = Settings.autonomousCare;
                Settings.ResetToDefaults();
                if (careWasEnabled && !Settings.autonomousCare) RimAgentGameComponent.Instance?.DisableAutonomousCare();
            }, destructive: true));
        }
    }

    /// <summary>還沒寫過紀錄時資料夾可能不存在，先建立再開；建不了（唯讀、權限不足）就提示玩家，不讓例外打斷設定頁繪製。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Messages.Message("RimAgent.Settings.OpenLogFolderFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Application.OpenURL(folder);
    }
}
