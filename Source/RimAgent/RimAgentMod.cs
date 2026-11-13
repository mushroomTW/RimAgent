using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RimAgent.Agent;
using RimAgent.Tools;
using RimAgent.UI;
using RimLLM_Framework;
using RimLLM_Framework.Manager.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

/// <summary>Mod 進入點與設定頁。執行模式由 Agent 視窗切換。</summary>
public class RimAgentMod : Mod
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

    private static readonly ToolCategory[] Categories = (ToolCategory[])Enum.GetValues(typeof(ToolCategory));

    /// <summary>權限清單依影響程度分三組：只看、會改遊戲但改得回來、影響大。</summary>
    private static readonly (string Key, Color Color, ToolCategory[] Members)[] PermissionGroups =
    [
        ("RimAgent.Settings.PermissionGroupGreen", Green, [ToolCategory.Observe, ToolCategory.Memory, ToolCategory.Web]),
        ("RimAgent.Settings.PermissionGroupYellow", Yellow, [ToolCategory.Time, ToolCategory.Work, ToolCategory.Build, ToolCategory.Zone, ToolCategory.Production, ToolCategory.Animal, ToolCategory.Medical]),
        ("RimAgent.Settings.PermissionGroupRed", Red, [ToolCategory.Combat, ToolCategory.Trade, ToolCategory.Prisoner, ToolCategory.Save, ToolCategory.Agents, ToolCategory.Mcp]),
    ];

    private static readonly RimAgentSettings.PermissionPreset[] Presets =
        (RimAgentSettings.PermissionPreset[])Enum.GetValues(typeof(RimAgentSettings.PermissionPreset));

    /// <summary>主動照顧多久可以出手一次（遊戲分鐘）；0＝隨時。</summary>
    private static readonly int[] CooldownChoices = [0, 10, 30, 60];

    // 設定頁每個 IMGUI 事件都會重畫：權限名稱、說明與提示快取起來，免得每幀翻譯。
    private static string[] _permissionLabels;
    private static string[] _permissionDescs;
    private static string[] _permissionTips;
    private static LoadedLanguage _permissionLanguage;

    // 權限清單與進階設定預設收合；只在本次遊戲期間記住，不寫入設定檔。
    private static bool _showPermissionList;
    private static bool _showAdvanced;

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

    /// <summary>區段標題：金色圓號、標題，下方一行白話說明。</summary>
    private static void DrawSectionHeader(Listing_Standard listing, int number, string title, string hint)
    {
        listing.Gap(18f);
        Rect row = listing.GetRect(30f);
        var badge = new Rect(row.x, row.y + 3f, 24f, 24f);
        Widgets.DrawBoxSolid(badge, Gold);
        Text.Anchor = TextAnchor.MiddleCenter;
        GUI.color = new Color(0.12f, 0.12f, 0.12f);
        Widgets.Label(new Rect(badge.x, badge.y + 1f, badge.width, badge.height), number.ToString());
        Text.Font = GameFont.Medium;
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = Gold;
        Widgets.Label(new Rect(badge.xMax + 10f, row.y, row.width - badge.width - 10f, row.height), title);
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
        DrawHint(listing, hint, 0f, Dim);
        listing.Gap(4f);
    }

    /// <summary>小字說明，依寬度自動換行。</summary>
    private static void DrawHint(Listing_Standard listing, string text, float indent, Color color)
    {
        Text.Font = GameFont.Tiny;
        float width = listing.ColumnWidth - indent;
        Rect rect = listing.GetRect(Text.CalcHeight(text, width));
        GUI.color = color;
        Widgets.Label(new Rect(rect.x + indent, rect.y, width, rect.height), text);
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
    }

    /// <summary>三張權限方案卡片；目前權限不符合任何方案時下方提示「自訂」。</summary>
    private static void DrawPresetCards(Listing_Standard listing)
    {
        RimAgentSettings.PermissionPreset? current = Settings.MatchPreset();
        Rect row = listing.GetRect(PresetCardHeight);
        float width = (row.width - PresetCardGap * (Presets.Length - 1)) / Presets.Length;
        for (int i = 0; i < Presets.Length; i++)
        {
            RimAgentSettings.PermissionPreset preset = Presets[i];
            bool on = current == preset;
            var card = new Rect(row.x + i * (width + PresetCardGap), row.y, width, row.height);
            Widgets.DrawBoxSolid(card, on ? SelectedFill : PanelFill);
            GUI.color = on ? Gold : PanelBorder;
            Widgets.DrawBox(card, 2);
            GUI.color = Color.white;
            if (Mouse.IsOver(card)) Widgets.DrawHighlight(card);

            Rect inner = card.ContractedBy(10f);
            string key = PresetKey(preset);
            if (preset == RimAgentSettings.PermissionPreset.Recommended)
            {
                string tag = "RimAgent.Settings.PresetTagRecommended".Translate();
                Text.Font = GameFont.Tiny;
                float tagWidth = Text.CalcSize(tag).x + 10f;
                var tagRect = new Rect(card.xMax - tagWidth - 6f, card.y + 6f, tagWidth, 18f);
                Widgets.DrawBoxSolid(tagRect, Gold);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = new Color(0.12f, 0.12f, 0.12f);
                Widgets.Label(tagRect, tag);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            GUI.color = on ? Gold : Color.white;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), key.Translate());
            GUI.color = Dim;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width, inner.height - 26f), (key + "Desc").Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(card)) Settings.ApplyPreset(preset);
        }
        if (current == null)
        {
            listing.Gap(4f);
            DrawHint(listing, "RimAgent.Settings.PresetCustomNote".Translate(), 0f, Gold);
        }
    }

    /// <summary>可收合的權限清單：依綠／黃／紅分組，每項一行名稱加一行說明，整格可點。</summary>
    private static void DrawPermissionList(Listing_Standard listing)
    {
        listing.Gap(8f);
        if (DrawExpandButton(listing, _showPermissionList, "RimAgent.Settings.PermissionListShow".Translate(), "RimAgent.Settings.PermissionListHide".Translate(), null))
        {
            _showPermissionList = !_showPermissionList;
        }
        if (!_showPermissionList) return;

        EnsurePermissionLabels();
        foreach ((string key, Color color, ToolCategory[] members) in PermissionGroups)
        {
            listing.Gap(8f);
            Rect header = listing.GetRect(24f);
            Widgets.DrawBoxSolid(new Rect(header.x + 2f, header.y + 7f, 10f, 10f), color);
            Widgets.Label(new Rect(header.x + 20f, header.y, header.width - 20f, header.height), key.Translate());

            int rows = (members.Length + 1) / 2;
            Rect grid = listing.GetRect(rows * PermissionRowHeight);
            float columnWidth = (grid.width - PermissionColumnGap) / 2f;
            for (int i = 0; i < members.Length; i++)
            {
                int index = (int)members[i];
                var cell = new Rect(grid.x + (i % 2) * (columnWidth + PermissionColumnGap), grid.y + (i / 2) * PermissionRowHeight, columnWidth, PermissionRowHeight - 4f);
                Widgets.DrawBoxSolid(new Rect(cell.x, cell.y, 3f, cell.height), color);
                bool allowed = Settings.GetAllow(members[i]);
                Widgets.CheckboxDraw(cell.x + 10f, cell.y + 3f, allowed, false);
                float textX = cell.x + 42f;
                float textWidth = cell.xMax - textX;
                Text.WordWrap = false;
                Widgets.Label(new Rect(textX, cell.y, textWidth, 22f), _permissionLabels[index].Truncate(textWidth));
                Text.Font = GameFont.Tiny;
                GUI.color = Dim;
                Widgets.Label(new Rect(textX, cell.y + 21f, textWidth, 18f), _permissionDescs[index].Truncate(textWidth));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                if (Mouse.IsOver(cell))
                {
                    Widgets.DrawHighlight(cell);
                    TooltipHandler.TipRegion(cell, _permissionTips[index]);
                }
                if (Widgets.ButtonInvisible(cell)) Settings.SetAllow(members[i], !allowed);
            }
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
            int minutes = CooldownChoices[i];
            bool on = Settings.autonomousCooldownMinutes == minutes;
            var seg = new Rect(segX + i * segWidth, segRow.y, segWidth - 2f, segRow.height);
            Widgets.DrawBoxSolid(seg, on ? SelectedFill : PanelFill);
            GUI.color = on ? Gold : PanelBorder;
            Widgets.DrawBox(seg);
            GUI.color = on ? Gold : Color.white;
            Text.Anchor = TextAnchor.MiddleCenter;
            string label = minutes == 0 ? "RimAgent.Settings.CooldownNow".Translate() : "RimAgent.Settings.CooldownEvery".Translate(minutes);
            Widgets.Label(seg, label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Mouse.IsOver(seg))
            {
                Widgets.DrawHighlight(seg);
                TooltipHandler.TipRegion(seg, "RimAgent.Settings.AutoCooldownTip".Translate());
            }
            if (Widgets.ButtonInvisible(seg)) Settings.autonomousCooldownMinutes = minutes;
        }
        listing.Gap(2f);
        string hint = "RimAgent.Settings.CooldownHint".Translate();
        if (!CooldownChoices.Contains(Settings.autonomousCooldownMinutes))
            hint += " " + "RimAgent.Settings.CooldownCustom".Translate(Settings.autonomousCooldownMinutes);
        DrawHint(listing, hint, SubIndent, Dim);
    }

    /// <summary>決策模型的連線狀態一行，沒設定時附「前往」按鈕；下方是測試按鈕。</summary>
    private static bool IsDecisionReady(DecisionAvailability availability)
    {
        return availability != null && availability.Enabled && !string.IsNullOrWhiteSpace(availability.Model)
            && (!availability.RequiresKey || availability.HasKey);
    }

    private static void DrawDecisionStatus(Listing_Standard listing, DecisionAvailability availability)
    {
        string statusText;
        bool ok = false;
        if (availability == null || !availability.Enabled)
        {
            statusText = "RimAgent.Settings.DecisionStatusDisabled".Translate();
        }
        else if (availability.RequiresKey && !availability.HasKey)
        {
            statusText = "RimAgent.Settings.DecisionStatusNoKey".Translate(availability.ProviderId);
        }
        else
        {
            string prov = string.IsNullOrEmpty(Settings.decisionProvider) ? availability.ProviderId : Settings.decisionProvider;
            string mdl = string.IsNullOrEmpty(Settings.decisionModel) ? (availability.Model ?? "default") : Settings.decisionModel;
            statusText = "RimAgent.Settings.DecisionStatusEnabled".Translate(prov, mdl);
            ok = true;
        }

        Rect row = listing.GetRect(28f);
        const float ButtonWidth = 110f;
        float textWidth = row.width - SubIndent - (ok ? 0f : ButtonWidth + 8f);
        Text.WordWrap = false;
        GUI.color = ok ? Green : Yellow;
        Widgets.Label(new Rect(row.x + SubIndent, row.y + 3f, textWidth, 24f), statusText.Truncate(textWidth));
        GUI.color = Color.white;
        Text.WordWrap = true;
        TooltipHandler.TipRegion(new Rect(row.x + SubIndent, row.y, textWidth, row.height), statusText);
        if (!ok && Widgets.ButtonText(new Rect(row.xMax - ButtonWidth, row.y, ButtonWidth, 26f), "RimAgent.Settings.GoShort".Translate()))
        {
            OpenFrameworkSettings();
        }
        if (ok) DrawDecisionTest(listing, SubIndent);
    }

    /// <summary>大開關卡片：整張可點，開啟時綠框。</summary>
    private static void DrawToggleCard(Listing_Standard listing, ref bool value, string title, string desc, string tip)
    {
        Rect card = listing.GetRect(58f);
        Widgets.DrawBoxSolid(card, PanelFill);
        GUI.color = value ? Green : PanelBorder;
        Widgets.DrawBox(card, 2);
        GUI.color = Color.white;
        if (Mouse.IsOver(card))
        {
            Widgets.DrawHighlight(card);
            TooltipHandler.TipRegion(card, tip);
        }
        Widgets.CheckboxDraw(card.x + 12f, card.y + (card.height - 24f) / 2f, value, false);
        float textX = card.x + 48f;
        float textWidth = card.xMax - textX - 10f;
        GUI.color = value ? Green : Color.white;
        Widgets.Label(new Rect(textX, card.y + 7f, textWidth, 24f), title);
        GUI.color = Dim;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(textX, card.y + 31f, textWidth, 20f), desc);
        Text.Font = GameFont.Small;
        GUI.color = Color.white;
        if (Widgets.ButtonInvisible(card)) value = !value;
    }

    /// <summary>一列勾選：名稱下面直接一行小字說明，整列可點；完整說明在提示。</summary>
    /// <param name="listing">介面繪製清單控制項。</param>
    /// <param name="value">勾選狀態布林變數參考。</param>
    /// <param name="label">選項名稱標籤。</param>
    /// <param name="desc">選項下方簡短說明文字。</param>
    /// <param name="tip">工具提示文字。</param>
    /// <param name="indent">左側縮排距離。</param>
    /// <param name="disabled">true 時勾選框與文字變灰、點了不會切換。</param>
    private static void DrawToggleRow(Listing_Standard listing, ref bool value, string label, string desc, string tip, float indent, bool disabled = false)
    {
        float textX = indent + 34f;
        float textWidth = listing.ColumnWidth - textX;
        Text.Font = GameFont.Tiny;
        float descHeight = Text.CalcHeight(desc, textWidth);
        Text.Font = GameFont.Small;
        Rect row = listing.GetRect(24f + descHeight + 6f);
        var hit = new Rect(row.x + indent, row.y, row.width - indent, row.height);
        Widgets.CheckboxDraw(hit.x + 4f, row.y + 2f, value, disabled);
        GUI.color = disabled ? Dim : Color.white;
        Widgets.Label(new Rect(row.x + textX, row.y + 1f, textWidth, 24f), label);
        Text.Font = GameFont.Tiny;
        GUI.color = disabled ? new Color(Dim.r, Dim.g, Dim.b, 0.6f) : Dim;
        Widgets.Label(new Rect(row.x + textX, row.y + 24f, textWidth, descHeight), desc);
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
        if (Mouse.IsOver(hit))
        {
            if (!disabled) Widgets.DrawHighlight(hit);
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(hit, tip);
        }
        if (!disabled && Widgets.ButtonInvisible(hit)) value = !value;
        listing.Gap(4f);
    }

    /// <summary>展開／收合按鈕；按下回傳 true，由呼叫端切換狀態。</summary>
    private static bool DrawExpandButton(Listing_Standard listing, bool expanded, string showLabel, string hideLabel, string tip)
    {
        Rect row = listing.GetRect(30f);
        // ButtonText 靠左對齊時文字貼著框線；按鈕只畫底，文字另外留邊距畫，放不下就截斷。
        bool clicked = Widgets.ButtonText(row, string.Empty);
        const float Padding = 10f;
        var labelRect = new Rect(row.x + Padding, row.y, row.width - Padding * 2f, row.height);
        string label = expanded ? "[ − ]  " + hideLabel : "[ + ]  " + showLabel;
        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        Widgets.Label(labelRect, label.Truncate(labelRect.width));
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;
        if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(row, tip);
        return clicked;
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

    /// <summary>
    /// 開 RimLLM Framework 的設定頁。Dialog_ModSettings 一次只允許一個，會換掉目前這頁（關閉時照常存檔）。
    /// </summary>
    private static void OpenFrameworkSettings()
    {
        Mod framework = LoadedModManager.ModHandles.FirstOrDefault(m =>
            string.Equals(m.Content?.PackageId, FrameworkPackageId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(m.SettingsCategory()));
        if (framework == null)
        {
            Messages.Message("RimAgent.Settings.FrameworkNotFound".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Find.WindowStack.Add(new Dialog_ModSettings(framework));
    }

    /// <summary>
    /// 模型分工（參照 hermes-agent 的 auxiliary 模型）：Agent 主模型在 Agent 視窗切換，
    /// 其餘每種模型呼叫各一格，預設跟隨 Agent 模型；所有子代理共用同一格。
    /// </summary>
    private static void DrawModelRoles(Listing_Standard listing)
    {
        listing.Gap();
        listing.Label("RimAgent.Settings.ModelRolesHeader".Translate());
        listing.GapLine();
        DrawModelDropdown(listing, "RimAgent.Settings.SubagentModel".Translate(),
            () => Settings.subagentModel, v => Settings.subagentModel = v,
            "RimAgent.Settings.SubagentModelTip".Translate(),
            () => Settings.subagentReasoning, v => Settings.subagentReasoning = v);
        DrawModelDropdown(listing, "RimAgent.Settings.ClassifierModel".Translate(),
            () => Settings.classifierModel, v => Settings.classifierModel = v,
            "RimAgent.Settings.ClassifierModelTip".Translate(),
            () => Settings.classifierReasoning, v => Settings.classifierReasoning = v);
        DrawModelDropdown(listing, "RimAgent.Settings.CompactModel".Translate(),
            () => Settings.compactModel, v => Settings.compactModel = v,
            "RimAgent.Settings.CompactModelTip".Translate(),
            () => Settings.compactReasoning, v => Settings.compactReasoning = v);
    }

    /// <summary>畫決策模型的供應商與模型選單（狀態與測試在主頁）；回傳框架是否已啟用決策模型。</summary>
    private static bool DrawDecisionSettings(Listing_Standard listing)
    {
        DecisionAvailability availability = DecisionServiceFactory.GetAvailability();

        // 供應商下拉
        Rect provRow = listing.GetRect(28f);
        const float labelWidth = 160f;
        var provLabelRect = new Rect(provRow.x, provRow.y + 3f, labelWidth, 24f);
        var provDropdownRect = new Rect(provLabelRect.xMax + 4f, provRow.y, provRow.xMax - provLabelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(provLabelRect, "RimAgent.Settings.DecisionProvider".Translate().Truncate(labelWidth));
        Text.WordWrap = true;

        string currentProv = Settings.decisionProvider;
        string shownProv = string.IsNullOrEmpty(currentProv)
            ? "RimAgent.Settings.DecisionDefault".Translate().ToString()
            : currentProv;

        if (Widgets.ButtonText(provDropdownRect, shownProv.Truncate(provDropdownRect.width - 12f)))
        {
            // 供應商一律沿用框架啟用的那一個；這裡只讓玩家選擇「本模組不使用決策模型」。
            // 關掉後整段隱藏，可在主頁的決策模型開關重新啟用。
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimAgent.Settings.DecisionDefault".Translate(), () => Settings.decisionProvider = string.Empty),
                new FloatMenuOption("RimAgent.Settings.DecisionProviderNone".Translate(), () => Settings.decisionEnabled = false)
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }
        if (Mouse.IsOver(provDropdownRect) || Mouse.IsOver(provLabelRect))
        {
            TooltipHandler.TipRegion(new Rect(provRow.x, provRow.y, provRow.width, provRow.height), "RimAgent.Settings.DecisionProviderTip".Translate());
        }
        listing.Gap(6f);

        // 模型下拉
        Rect modelRow = listing.GetRect(28f);
        var modelLabelRect = new Rect(modelRow.x, modelRow.y + 3f, labelWidth, 24f);
        var modelDropdownRect = new Rect(modelLabelRect.xMax + 4f, modelRow.y, modelRow.xMax - modelLabelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(modelLabelRect, "RimAgent.Settings.DecisionModel".Translate().Truncate(labelWidth));
        Text.WordWrap = true;

        string currentModel = Settings.decisionModel;
        string shownModel = string.IsNullOrEmpty(currentModel)
            ? "RimAgent.Settings.DecisionDefault".Translate().ToString()
            : currentModel;

        if (Widgets.ButtonText(modelDropdownRect, shownModel.Truncate(modelDropdownRect.width - 12f)))
        {
            // 只列框架給出的模型（玩家在 RimLLM 決策分頁設定的那個），不另外向伺服器拉清單。
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimAgent.Settings.DecisionDefault".Translate(), () => Settings.decisionModel = string.Empty)
            };
            string frameworkModel = availability?.Enabled == true ? availability.Model : null;
            if (!string.IsNullOrEmpty(frameworkModel))
            {
                options.Add(new FloatMenuOption(frameworkModel, () => Settings.decisionModel = frameworkModel));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        if (Mouse.IsOver(modelDropdownRect) || Mouse.IsOver(modelLabelRect))
        {
            TooltipHandler.TipRegion(new Rect(modelRow.x, modelRow.y, modelRow.width, modelRow.height), "RimAgent.Settings.DecisionModelTip".Translate());
        }
        listing.Gap(6f);
        return availability != null && availability.Enabled;
    }

    /// <summary>測試按鈕與最近一次結果；送出的是設定頁目前選的供應商與模型。</summary>
    private static void DrawDecisionTest(Listing_Standard listing, float indent)
    {
        Rect row = listing.GetRect(28f);
        const float buttonWidth = 130f;
        var buttonRect = new Rect(row.x + indent, row.y, buttonWidth, 26f);
        bool running = DecisionModelTester.Running;
        string buttonLabel = running
            ? "RimAgent.Settings.DecisionTestRunning".Translate().ToString()
            : "RimAgent.Settings.DecisionTest".Translate().ToString();
        if (Widgets.ButtonText(buttonRect, buttonLabel) && !running)
        {
            DecisionModelTester.Start();
        }
        TooltipHandler.TipRegion(buttonRect, "RimAgent.Settings.DecisionTestTip".Translate());

        string result;
        switch (DecisionModelTester.Outcome)
        {
            case DecisionModelTester.TestOutcome.Ok:
                result = "RimAgent.Settings.DecisionTestOk".Translate(DecisionModelTester.Detail);
                break;
            case DecisionModelTester.TestOutcome.Unavailable:
                result = "RimAgent.Settings.DecisionTestUnavailable".Translate();
                break;
            case DecisionModelTester.TestOutcome.NoAnswer:
                result = "RimAgent.Settings.DecisionTestNoAnswer".Translate();
                break;
            case DecisionModelTester.TestOutcome.Failed:
                result = "RimAgent.Settings.DecisionTestFailed".Translate(DecisionModelTester.Detail);
                break;
            default:
                result = string.Empty;
                break;
        }
        var resultRect = new Rect(buttonRect.xMax + 8f, row.y + 3f, row.xMax - buttonRect.xMax - 8f, 24f);
        Text.WordWrap = false;
        Widgets.Label(resultRect, result.Truncate(resultRect.width));
        Text.WordWrap = true;
        TooltipHandler.TipRegion(resultRect, result);
        listing.Gap(6f);
    }

    /// <summary>自訂分類的識別碼必須唯一；刪除中間項目後不能再用清單長度 + 1。</summary>
    internal static string NextCustomCategoryId(IReadOnlyList<DecisionRouteCategory> categories)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (categories != null)
        {
            foreach (DecisionRouteCategory category in categories)
            {
                if (category != null && !string.IsNullOrWhiteSpace(category.id)) used.Add(category.id.Trim());
            }
        }
        int number = 1;
        while (used.Contains("custom_" + number)) number++;
        return "custom_" + number;
    }

    private static void DrawDecisionRoutingSettings(Listing_Standard listing)
    {
        // 備援思考強度
        DrawReasoningRow(
            listing,
            "RimAgent.Settings.DecisionFallbackReasoning".Translate(),
            () => Settings.decisionFallbackReasoning,
            v => Settings.decisionFallbackReasoning = v,
            "RimAgent.Settings.DecisionFallbackReasoningTip".Translate());

        // 最小信心門檻
        listing.Label(
            "RimAgent.Settings.DecisionMinConfidence".Translate(Settings.decisionRoutingMinConfidence.ToString("P0")),
            -1f,
            "RimAgent.Settings.DecisionMinConfidenceTip".Translate());
        Settings.decisionRoutingMinConfidence = listing.Slider(Settings.decisionRoutingMinConfidence, 0f, 1f);

        listing.Gap(6f);

        // 類別清單
        Rect catHeader = listing.GetRect(28f);
        const float ActionButtonWidth = 90f;
        Rect resetRect = new Rect(catHeader.xMax - ActionButtonWidth, catHeader.y, ActionButtonWidth, 26f);
        Rect addRect = new Rect(resetRect.x - ActionButtonWidth - 6f, catHeader.y, ActionButtonWidth, 26f);
        Widgets.Label(new Rect(catHeader.x, catHeader.y + 3f, addRect.x - catHeader.x - 8f, 24f), "RimAgent.Settings.DecisionCategoriesHeader".Translate());

        if (Widgets.ButtonText(addRect, "RimAgent.Settings.DecisionCategoryAdd".Translate()))
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimAgent.Settings.DecisionCategoryAddCustom".Translate(), () =>
                {
                    string id = NextCustomCategoryId(Settings.decisionCategories);
                    int nextNum = 1;
                    if (id.Length > "custom_".Length) int.TryParse(id.Substring("custom_".Length), out nextNum);
                    Settings.decisionCategories.Add(new DecisionRouteCategory(
                        id,
                        "RimAgent.Settings.DecisionCategoryCustomName".Translate(nextNum),
                        "Custom category description"));
                })
            };

            foreach (DecisionRouteCategory builtIn in DecisionRouteCategory.CreateDefaultCategories())
            {
                if (!Settings.decisionCategories.Any(c => string.Equals(c.id, builtIn.id, StringComparison.OrdinalIgnoreCase)))
                {
                    DecisionRouteCategory captured = builtIn;
                    options.Add(new FloatMenuOption("RimAgent.Settings.DecisionCategoryRestore".Translate(captured.name, captured.id), () =>
                    {
                        Settings.decisionCategories.Add(captured);
                    }));
                }
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (Widgets.ButtonText(resetRect, "RimAgent.Settings.DecisionCategoriesReset".Translate()))
        {
            Settings.decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
        }

        listing.Gap(4f);

        DecisionRouteCategory toDelete = null;
        for (int i = 0; i < Settings.decisionCategories.Count; i++)
        {
            DecisionRouteCategory cat = Settings.decisionCategories[i];
            Rect row = listing.GetRect(24f);
            const float DeleteBtnWidth = 50f;
            Rect delRect = new Rect(row.xMax - DeleteBtnWidth, row.y, DeleteBtnWidth, 22f);
            Rect titleRect = new Rect(row.x, row.y + 2f, row.width - DeleteBtnWidth - 6f, 20f);

            Text.Font = GameFont.Tiny;
            string displayName = string.IsNullOrEmpty(cat.name) ? cat.id : cat.name;
            Widgets.Label(titleRect, $"{displayName} ({cat.id})");
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(delRect, "RimAgent.Settings.DecisionCategoryDelete".Translate()))
            {
                toDelete = cat;
            }

            DrawReasoningRow(
                listing,
                "  " + "RimAgent.Settings.CategoryReasoning".Translate(),
                () => cat.reasoning,
                v => cat.reasoning = v,
                cat.description);
        }

        if (toDelete != null)
        {
            Settings.decisionCategories.Remove(toDelete);
        }
    }

    /// <summary>
    /// 一格模型槽：模型下拉（跟隨 Agent／Fallback／備援鏈條目，選項與 Agent 視窗的模型選單相同）
    /// 加上選用的思考強度下拉。
    /// </summary>
    /// <param name="listing">介面繪製清單控制項。</param>
    /// <param name="label">模型槽標籤文字。</param>
    /// <param name="current">取得當前選定模型識別碼的委派。</param>
    /// <param name="set">設定選定模型識別碼的回呼。</param>
    /// <param name="tip">工具提示文字。</param>
    /// <param name="reasoning">思考強度的讀取；null 表示這列不顯示思考強度。</param>
    /// <param name="setReasoning">設定思考強度的回呼；null 表示不提供修改。</param>
    private static void DrawModelDropdown(
        Listing_Standard listing, string label, Func<string> current, Action<string> set, string tip,
        Func<ReasoningLevel> reasoning = null, Action<ReasoningLevel> setReasoning = null)
    {
        Rect row = listing.GetRect(28f);
        const float labelWidth = 160f;
        const float reasoningWidth = 90f;
        var labelRect = new Rect(row.x, row.y + 3f, labelWidth, 24f);
        float dropdownRight = reasoning != null ? row.xMax - reasoningWidth - 6f : row.xMax;
        var dropdownRect = new Rect(labelRect.xMax + 4f, row.y, dropdownRight - labelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(labelRect, label.Truncate(labelWidth));
        Text.WordWrap = true;
        string raw = current();
        string shown = GetModelDropdownDisplayText(raw);
        if (Widgets.ButtonText(dropdownRect, shown.Truncate(dropdownRect.width - 12f)))
        {
            ShowModelDropdownMenu(set);
        }
        if (Mouse.IsOver(dropdownRect) || Mouse.IsOver(labelRect))
        {
            TooltipHandler.TipRegion(new Rect(row.x, row.y, dropdownRect.xMax - row.x, row.height), tip);
        }
        if (reasoning != null)
        {
            var reasoningRect = new Rect(row.xMax - reasoningWidth, row.y, reasoningWidth, 26f);
            if (Widgets.ButtonText(reasoningRect, ReasoningKey(reasoning()).Translate()))
            {
                ShowReasoningDropdownMenu(setReasoning);
            }
            TooltipHandler.TipRegion(reasoningRect, "RimAgent.Settings.SlotReasoningTip".Translate());
        }
        listing.Gap(6f);
    }

    private static string GetModelDropdownDisplayText(string raw)
    {
        string value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "RimAgent.Model.Default".Translate().ToString();
        }
        if (string.Equals(value, RimAgentSettings.FollowAgentModel, StringComparison.Ordinal))
        {
            return "RimAgent.Model.FollowAgent".Translate().ToString();
        }
        return value;
    }

    private static void ShowModelDropdownMenu(Action<string> set)
    {
        var options = new List<FloatMenuOption>();
        options.Add(new FloatMenuOption("RimAgent.Model.FollowAgent".Translate(), () => set(RimAgentSettings.FollowAgentModel))
        {
            tooltip = new TipSignal("RimAgent.Model.FollowAgentTip".Translate())
        });
        options.Add(new FloatMenuOption("RimAgent.Model.Default".Translate(), () => set(string.Empty))
        {
            tooltip = new TipSignal("RimAgent.Model.DefaultTip".Translate())
        });
        foreach (string entry in Window_Agent.ConfiguredModels())
        {
            string captured = entry;
            options.Add(new FloatMenuOption(captured, () => set(captured)));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>只有思考強度下拉的一列：分流類別與備援思考強度用。</summary>
    private static void DrawReasoningRow(
        Listing_Standard listing,
        string label,
        Func<ReasoningLevel> reasoning,
        Action<ReasoningLevel> setReasoning,
        string tip)
    {
        Rect row = listing.GetRect(28f);
        const float ReasoningWidth = 90f;
        var labelRect = new Rect(row.x, row.y + 3f, row.width - ReasoningWidth - 8f, 24f);
        var reasoningRect = new Rect(row.xMax - ReasoningWidth, row.y, ReasoningWidth, 26f);
        Text.WordWrap = false;
        Widgets.Label(labelRect, label.Truncate(labelRect.width));
        Text.WordWrap = true;
        if (Widgets.ButtonText(reasoningRect, ReasoningKey(reasoning()).Translate()))
        {
            ShowReasoningDropdownMenu(setReasoning);
        }
        if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, tip);
        listing.Gap(6f);
    }

    private static void ShowReasoningDropdownMenu(Action<ReasoningLevel> setReasoning)
    {
        var options = new List<FloatMenuOption>();
        foreach (ReasoningLevel level in (ReasoningLevel[])Enum.GetValues(typeof(ReasoningLevel)))
        {
            ReasoningLevel captured = level;
            options.Add(new FloatMenuOption(ReasoningKey(level).Translate(), () => setReasoning(captured)));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>名稱、一行說明與完整提示只在語言變了才重組；提示最前面放名稱與說明，截斷時仍看得到全文。</summary>
    private static void EnsurePermissionLabels()
    {
        LoadedLanguage language = LanguageDatabase.activeLanguage;
        if (_permissionLabels != null && language == _permissionLanguage) return;
        _permissionLanguage = language;
        _permissionLabels = new string[Categories.Length];
        _permissionDescs = new string[Categories.Length];
        _permissionTips = new string[Categories.Length];
        for (int i = 0; i < Categories.Length; i++)
        {
            string key = PermissionKey(Categories[i]);
            _permissionLabels[i] = key.Translate();
            _permissionDescs[i] = (key + "Desc").Translate();
            _permissionTips[i] = _permissionLabels[i] + "\n" + _permissionDescs[i] + "\n\n" + (key + "Tip").Translate();
        }
    }

    /// <summary>工具類別權限的翻譯鍵（提示另加 "Tip" 後綴）。設定鍵以類別英文命名，只有 MCP 全大寫。</summary>
    internal static string PermissionKey(ToolCategory category)
    {
        return "RimAgent.Settings." + (category == ToolCategory.Mcp ? "MCP" : category.ToString());
    }

    /// <summary>權限方案卡片的翻譯鍵（說明另加 "Desc" 後綴）。</summary>
    internal static string PresetKey(RimAgentSettings.PermissionPreset preset)
    {
        return "RimAgent.Settings.Preset." + preset;
    }

    /// <summary>思考強度的翻譯鍵。</summary>
    internal static string ReasoningKey(ReasoningLevel level)
    {
        return "RimAgent.Reasoning." + level;
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
