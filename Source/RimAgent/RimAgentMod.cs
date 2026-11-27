using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RimAgent.A2A;
using RimAgent.Agent;
using RimAgent.Tools;
using RimAgent.UI;
using RimLLM_Framework;
using RimLLM_Framework.Mod;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

public partial class RimAgentMod : Mod
{
    public static RimAgentSettings? Settings { get; internal set; }

    private const float PermissionRowHeight = 46f;
    private const float PermissionColumnGap = 18f;
    private const float PresetCardHeight = 86f;
    private const float PresetCardGap = 10f;
    private const float OpenFolderButtonWidth = 110f;
    private const float SubIndent = 28f;
    private const string FrameworkPackageId = "GreenMushroom.RimLLMFramework";
    private const string OwnPackageId = "GreenMushroom.RimAgent";

    internal static bool IsOwnOrFramework(string packageId)
    {
        return string.Equals(packageId, OwnPackageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(packageId, FrameworkPackageId, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Color Gold = RimLLMUIStyle.Gold;
    private static readonly Color Green = new(0.44f, 0.75f, 0.45f);
    private static readonly Color Yellow = new(0.88f, 0.72f, 0.31f);
    private static readonly Color Red = new(0.88f, 0.42f, 0.37f);
    private static readonly Color Dim = new(0.66f, 0.67f, 0.69f);
    private static readonly Color PanelFill = new(1f, 1f, 1f, 0.05f);
    private static readonly Color SelectedFill = RimLLMUIStyle.SelectedFill;
    private static readonly Color PanelBorder = RimLLMUIStyle.PanelBorder;

    private static readonly ToolCategory[] Categories = Enum.GetValues<ToolCategory>();

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

    // 設定頁每個 IMGUI 事件都會重畫：權限名稱、說明與提示快取起來，免得每幀翻譯。
    private static string[]? _permissionLabels;
    private static string[]? _permissionDescs;
    private static string[]? _permissionTips;
    private static LoadedLanguage? _permissionLanguage;

    // 權限清單預設收合、分頁停在「一般」；只在本次遊戲期間記住，不寫入設定檔。
    private static bool _showPermissionList;
    private SettingsTab _tab;

    private enum SettingsTab { General, Automation, Connections, Advanced }

    private const float NavWidth = 150f;
    private const float NavRowHeight = 34f;
    private const float FooterHeight = 52f;

    // TextFieldNumeric 需要跨幀保留輸入中的文字。
    private static string? _a2aPortBuffer;

    private Vector2 _settingsScrollPosition = Vector2.zero;
    private float _settingsHeight = 960f;

    public RimAgentMod(ModContentPack content) : base(content)
    {
        RimAgentSettings settings = GetSettings<RimAgentSettings>();
        Settings = settings;
        string token = settings.a2aServer ? settings.EnsureA2AToken() : settings.a2aToken;
        A2AServerController.Apply(content.RootDir, settings.a2aServer, settings.a2aPort, token);
    }

    public override void WriteSettings()
    {
        if (Settings is { } settings && settings.a2aServer) settings.EnsureA2AToken();
        base.WriteSettings();
        if (Settings is { } saved) A2AServerController.Apply(Content.RootDir, saved.a2aServer, saved.a2aPort, saved.a2aToken);
    }

    public override string SettingsCategory()
    {
        return "RimAgent.Settings.Category".CanTranslate()
            ? "RimAgent.Settings.Category".Translate().Resolve()
            : "RimAgent";
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var nav = new Rect(inRect.x, inRect.y, NavWidth, inRect.height - FooterHeight);
        DrawNav(nav);
        var body = new Rect(nav.xMax + 16f, inRect.y, inRect.width - NavWidth - 16f, inRect.height - FooterHeight);
        DrawTab(body);

        var footer = new Listing_Standard();
        footer.Begin(new Rect(inRect.x, inRect.yMax - FooterHeight, inRect.width, FooterHeight));
        DrawFooter(footer);
        footer.End();
    }

    private void DrawNav(Rect nav)
    {
        Widgets.DrawBoxSolid(nav, new Color(1f, 1f, 1f, 0.03f));
        float y = nav.y + 6f;
        foreach (SettingsTab tab in Enum.GetValues<SettingsTab>())
        {
            var row = new Rect(nav.x + 4f, y, nav.width - 8f, NavRowHeight);
            bool selected = tab == _tab;
            RimLLMUIStyle.DrawNavSelection(row, selected);
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = selected ? Gold : Dim;
            Widgets.Label(new Rect(row.x + 10f, row.y, row.width - 10f, row.height), ("RimAgent.Settings.Tab." + tab).Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonInvisible(row) && !selected)
            {
                _tab = tab;
                _settingsScrollPosition = Vector2.zero;
            }
            y += NavRowHeight + 2f;
        }
    }

    private void DrawTab(Rect inRect)
    {
        Rect viewRect = new(0f, 0f, inRect.width - 24f, _settingsHeight);
        Widgets.BeginScrollView(inRect, ref _settingsScrollPosition, viewRect);

        var listing = new Listing_Standard();
        // 捲動檢視裡必須單欄：Listing.GetRect 內容超過檢視高度會自動另起一欄，
        // 超出的內容會跑到檢視寬度外的第二欄（看起來像被截斷且捲不下去），
        // 高度也永遠長不大、捲軸出不來。權限的雙欄是手排的絕對座標，不受影響。
        listing.maxOneColumn = true;
        listing.Begin(viewRect);

        switch (_tab)
        {
            case SettingsTab.General:
                DrawReadyCard(listing);
                DrawSectionHeader(listing, "RimAgent.Settings.Section1Title".Translate(), "RimAgent.Settings.Section1Hint".Translate());
                DrawPresetCards(listing);
                DrawPermissionList(listing);
                DrawModelRoles(listing);
                break;
            case SettingsTab.Automation:
                DrawAutomation(listing);
                break;
            case SettingsTab.Connections:
                DrawA2ASettings(listing);
                DrawWebSearch(listing);
                break;
            case SettingsTab.Advanced:
                DrawAdvanced(listing);
                break;
        }

        listing.End();
        _settingsHeight = Mathf.Max(listing.CurHeight + 24f, inRect.height);
        Widgets.EndScrollView();
    }

    private static void DrawAutomation(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        DrawSectionHeader(listing, "RimAgent.Settings.Section2Title".Translate(), "RimAgent.Settings.Section2Hint".Translate());
        DrawAutonomousCare(listing);

        DrawSectionHeader(listing, "RimAgent.Settings.Section3Title".Translate(), "RimAgent.Settings.Section3Hint".Translate());
        // 決策模型沒設定好時開關變灰、點不動，下方說明原因並給前往按鈕；存檔裡的值不動，設定好後照原值生效。
        DecisionAvailability? decision = DecisionServiceFactory.GetAvailability();
        bool decisionReady = IsDecisionReady(decision);
        bool decisionOn = decisionReady && settings.decisionEnabled;
        DrawToggleRow(listing, ref decisionOn, "RimAgent.Settings.DecisionEnabled".Translate(),
            "RimAgent.Settings.DecisionEnabledDesc".Translate(), "RimAgent.Settings.DecisionEnabledTip".Translate(), 0f, !decisionReady);
        if (decisionReady) settings.decisionEnabled = decisionOn;
        if (!decisionReady || decisionOn) DrawDecisionStatus(listing, decision);
        DrawDecisionSection(listing);
    }

    private static void DrawAdvanced(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        listing.Gap();
        listing.Label("RimAgent.Settings.CompactionHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.AutoCompact".Translate(), ref settings.autoCompact, "RimAgent.Settings.AutoCompactTip".Translate());
        if (settings.autoCompact)
        {
            listing.Label("RimAgent.Settings.AutoCompactThreshold".Translate(settings.autoCompactThreshold), -1f, "RimAgent.Settings.AutoCompactThresholdTip".Translate());
            settings.autoCompactThreshold = Mathf.RoundToInt(listing.Slider(settings.autoCompactThreshold, RimAgentSettings.MinAutoCompactThreshold, RimAgentSettings.MaxAutoCompactThreshold));
        }
        TaggedString tokenLimit = settings.instructionTokenLimitK == 0
            ? "RimAgent.Settings.InstructionTokenLimitNone".Translate()
            : "RimAgent.Settings.InstructionTokenLimit".Translate(settings.instructionTokenLimitK);
        listing.Label(tokenLimit, -1f, "RimAgent.Settings.InstructionTokenLimitTip".Translate());
        settings.instructionTokenLimitK = Mathf.RoundToInt(listing.Slider(settings.instructionTokenLimitK, 0f, RimAgentSettings.MaxInstructionTokenLimitK) / 100f) * 100;

        listing.Gap();
        listing.Label("RimAgent.Settings.LayoutsHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.Layouts".Translate(), ref settings.layouts, "RimAgent.Settings.LayoutsTip".Translate());

        listing.Gap();
        listing.Label("RimAgent.Settings.ImageHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.ImageInput".Translate(), ref settings.imageInput, "RimAgent.Settings.ImageInputTip".Translate());

        DrawRunLogSettings(listing);
    }

    private static void DrawWebSearch(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        listing.Gap();
        listing.Label("RimAgent.Settings.WebSearchHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.UseLocalSearch".Translate(), ref settings.useLocalSearch,
            "RimAgent.Settings.UseLocalSearchTip".Translate());
        if (settings.useLocalSearch)
        {
            listing.Label("RimAgent.Settings.SearxngUrl".Translate());
            settings.searxngUrl = Widgets.TextField(listing.GetRect(28f), settings.searxngUrl ?? string.Empty);
        }
    }

    private static void DrawA2ASettings(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        listing.Gap();
        listing.Label("RimAgent.Settings.A2AHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.A2AServer".Translate(), ref settings.a2aServer, "RimAgent.Settings.A2AServerTip".Translate());
        if (!settings.a2aServer) return;
        string token = settings.EnsureA2AToken();
        Rect portRow = listing.GetRect(28f);
        Widgets.Label(portRow.LeftHalf(), "RimAgent.Settings.A2APort".Translate(settings.a2aPort + 1));
        Widgets.TextFieldNumeric(portRow.RightHalf(), ref settings.a2aPort, ref _a2aPortBuffer, RimAgentSettings.MinA2APort, RimAgentSettings.MaxA2APort);
        Rect tokenRow = listing.GetRect(28f);
        const float RegenWidth = 100f;
        const float CopyWidth = 80f;
        var regenRect = new Rect(tokenRow.xMax - RegenWidth, tokenRow.y, RegenWidth, 26f);
        var copyRect = new Rect(regenRect.x - CopyWidth - 6f, tokenRow.y, CopyWidth, 26f);
        var labelRect = new Rect(tokenRow.x, tokenRow.y + 3f, copyRect.x - tokenRow.x - 8f, 24f);
        string preview = token.Length > 12 ? token[..8] + "…" + token[^4..] : token;
        Text.WordWrap = false;
        Widgets.Label(labelRect, "RimAgent.Settings.A2AToken".Translate(preview).ToString().Truncate(labelRect.width));
        Text.WordWrap = true;
        TooltipHandler.TipRegion(labelRect, "RimAgent.Settings.A2ATokenTip".Translate());
        if (Widgets.ButtonText(copyRect, "RimAgent.Settings.A2ACopyToken".Translate()))
        {
            GUIUtility.systemCopyBuffer = token;
            Messages.Message("RimAgent.Settings.A2ATokenCopied".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
        }
        if (Widgets.ButtonText(regenRect, "RimAgent.Settings.A2ARegenerateToken".Translate()))
        {
            settings.RegenerateA2AToken();
        }
        if (A2AServerController.LastError is { } error)
        {
            listing.Label("RimAgent.Settings.A2AFailed".Translate(error));
        }
        else if (A2AServerController.RunningPort > 0)
        {
            listing.Label("RimAgent.Settings.A2ARunning".Translate(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{A2AServerController.RunningPort}/.well-known/agent-card.json")));
        }
        else
        {
            listing.Label("RimAgent.Settings.A2AApplyOnClose".Translate());
        }
    }

    private static void DrawDecisionSection(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        // 主頁關掉決策模型時，這些細節都沒有作用，整段不顯示；
        // 分流靠決策模型分類，沒有可用的決策模型時細項也不顯示。
        if (settings.decisionEnabled)
        {
            listing.Gap();
            listing.Label("RimAgent.Settings.DecisionModelHeader".Translate());
            listing.GapLine();
        }
        if (settings.decisionEnabled && DrawDecisionSettings(listing))
        {
            listing.CheckboxLabeled("RimAgent.Settings.DecisionTriageUpkeep".Translate(), ref settings.decisionTriageUpkeep,
                "RimAgent.Settings.DecisionTriageUpkeepTip".Translate());
            listing.Gap(6f);
            DrawDecisionRoutingSettings(listing);
        }
    }

    private static void DrawRunLogSettings(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        listing.Gap();
        listing.Label("RimAgent.Settings.RunLogHeader".Translate());
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.RecordRuns".Translate(), ref settings.recordRuns,
            "RimAgent.Settings.RecordRunsTip".Translate());
        if (settings.recordRuns)
        {
            string logFolder = Path.Combine(RunLogWriter.DefaultFolder, RunLogWriter.FolderName);
            string logPath = Path.Combine(logFolder, RunLogWriter.IndexName);
            Rect logRow = listing.GetRect(28f);
            Rect openRect = new(logRow.xMax - OpenFolderButtonWidth, logRow.y, OpenFolderButtonWidth, 26f);
            Rect pathRect = new(logRow.x, logRow.y + 3f, openRect.x - logRow.x - 8f, 24f);
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

    private static void DrawAutonomousCare(Listing_Standard listing)
    {
        if (Settings is not { } settings) return;
        bool careWasEnabled = settings.autonomousCare;
        DrawToggleCard(listing, ref settings.autonomousCare, "RimAgent.Settings.AutonomousCare".Translate(),
            "RimAgent.Settings.AutonomousCareDesc".Translate(), "RimAgent.Settings.AutonomousCareTip".Translate());
        if (careWasEnabled && !settings.autonomousCare) RimAgentGameComponent.Instance?.DisableAutonomousCare();
        if (!settings.autonomousCare) return;

        listing.Gap(4f);
        DrawToggleRow(listing, ref settings.confirmAutonomousMajor, "RimAgent.Settings.ConfirmAutonomousMajor".Translate(),
            "RimAgent.Settings.ConfirmAutonomousMajorDesc".Translate(), "RimAgent.Settings.ConfirmAutonomousMajorTip".Translate(), SubIndent);
        DrawToggleRow(listing, ref settings.autoTimeSpeed, "RimAgent.Settings.AutoTimeSpeed".Translate(),
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
        if (!CooldownChoices.Contains(settings.autonomousCooldownMinutes))
            hint += " " + "RimAgent.Settings.CooldownCustom".Translate(settings.autonomousCooldownMinutes);
        DrawHint(listing, hint, SubIndent, Dim);
    }

    private static void DrawFooter(Listing_Standard listing)
    {
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
        if (Widgets.ButtonText(resetRect, "RimAgent.Settings.ResetDefaults".Translate()) && Settings is { } settings)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RimAgent.Settings.ResetDefaultsConfirm".Translate(), () =>
            {
                bool careWasEnabled = settings.autonomousCare;
                settings.ResetToDefaults();
                if (careWasEnabled && !settings.autonomousCare) RimAgentGameComponent.Instance?.DisableAutonomousCare();
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
