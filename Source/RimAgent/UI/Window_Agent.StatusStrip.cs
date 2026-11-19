using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent
{
    private void DrawStatusStrip(Rect rect, RimAgentGameComponent component, ColonyAgentSession session)
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        var modeRect = new Rect(rect.x, rect.y, DropdownWidth, rect.height);
        // 執行或思考中也能切換：模式、思考強度與模型都是即時同步，下一次呼叫生效。
        bool enabled = settings != null;

        DrawModeButton(modeRect, enabled, settings, session);
        Rect reasoningRect = DrawReasoningButton(new Rect(modeRect.xMax + 6f, rect.y, DropdownWidth, rect.height), enabled, settings);

        var modelRect = new Rect(reasoningRect.xMax + 6f, rect.y, Mathf.Clamp(rect.xMax - reasoningRect.xMax - 6f, 0f, ModelDropdownWidth), rect.height);
        DrawModelButton(modelRect, enabled, settings);

        // 視窗窄到模型按鈕吃滿時，用量字樣就不畫。
        if (RimAgentGameComponent.AutonomousEnabled && rect.xMax - modelRect.xMax > 8f)
        {
            var usageRect = new Rect(modelRect.xMax + 8f, rect.y, rect.xMax - modelRect.xMax - 8f, rect.height);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = ToolTextColor;
            Widgets.Label(usageRect, _autoUsageLabel.Get(component.AutonomousToolsUsed, used => "RimAgent.Auto.StatusUnlimited".Translate(used).ToString()));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    private void DrawModeButton(Rect modeRect, bool enabled, RimAgentSettings settings, ColonyAgentSession session)
    {
        string modeStatus = _modeLabel.Get(session.Mode, mode => "RimAgent.Mode.Status".Translate(ModeLabel(mode)).ToString());
        if (DrawUnifiedButton(modeRect, modeStatus, ControlBorderColor, TitleColor, enabled, GameFont.Tiny))
        {
            var options = new List<FloatMenuOption>();
            foreach (AgentMode mode in new[] { AgentMode.Confirm, AgentMode.Execute, AgentMode.Auto, AgentMode.Autonomous })
            {
                options.Add(new FloatMenuOption(ModeLabel(mode), () => SetMode(settings, mode))
                {
                    tooltip = new TipSignal(ModeDescription(mode))
                });
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        if (Mouse.IsOver(modeRect)) TooltipHandler.TipRegion(modeRect, ModeDescription(session.Mode));
    }

    /// <summary>按鈕寬度貼合文字（「推理：高」很短，固定寬度會留一大片空白）；回傳實際使用的範圍。</summary>
    private Rect DrawReasoningButton(Rect reasoningRect, bool enabled, RimAgentSettings settings)
    {
        ReasoningLevel current = settings != null ? settings.reasoning : ReasoningLevel.Auto;
        bool routing = settings != null && settings.reasoningRouting;
        // 快取鍵：分流時用 -1，其餘用列舉值。文字變了才重新量寬度。
        string reasoningStatus = _reasoningLabel.Get(routing ? -1 : (int)current, key =>
        {
            string label = "RimAgent.Reasoning.Status".Translate(
                key < 0 ? "RimAgent.Reasoning.Routing".Translate().ToString() : ReasoningLabel((ReasoningLevel)key)).ToString();
            GameFont oldFont = Text.Font;
            Text.Font = GameFont.Tiny;
            _reasoningLabelWidth = Mathf.Ceil(Text.CalcSize(label).x) + 24f;
            Text.Font = oldFont;
            return label;
        });
        reasoningRect.width = Mathf.Min(reasoningRect.width, _reasoningLabelWidth);
        DrawReasoningMenu(reasoningRect, reasoningStatus, enabled, settings);
        return reasoningRect;
    }

    private static void DrawReasoningMenu(Rect reasoningRect, string reasoningStatus, bool enabled, RimAgentSettings settings)
    {
        if (DrawUnifiedButton(reasoningRect, reasoningStatus, ControlBorderColor, TitleColor, enabled, GameFont.Tiny))
        {
            var options = new List<FloatMenuOption>();
            if (DecisionServiceFactory.CanUseDecisionModel())
            {
                options.Add(new FloatMenuOption("RimAgent.Reasoning.Routing".Translate(), () => SetReasoningRouting(settings))
                {
                    tooltip = new TipSignal("RimAgent.Reasoning.RoutingTip".Translate())
                });
            }
            foreach (ReasoningLevel level in Enum.GetValues<ReasoningLevel>())
            {
                options.Add(new FloatMenuOption(ReasoningLabel(level), () => SetReasoning(settings, level)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    private void DrawModelButton(Rect modelRect, bool enabled, RimAgentSettings settings)
    {
        string model = settings != null ? settings.agentModel ?? string.Empty : string.Empty;
        if (Mathf.Abs(modelRect.width - _modelLabelWidth) >= 0.5f)
        {
            _modelLabelWidth = modelRect.width;
            _modelLabel = new TextCache<string>();
        }
        string modelStatus = _modelLabel.Get(model, m =>
        {
            Text.Font = GameFont.Tiny;
            string label = "RimAgent.Model.Status".Translate(ModelShortName(m)).ToString().Truncate(_modelLabelWidth - 8f);
            Text.Font = GameFont.Small;
            return label;
        });
        if (DrawUnifiedButton(modelRect, modelStatus, ControlBorderColor, TitleColor, enabled, GameFont.Tiny))
        {
            var options = new List<FloatMenuOption>
            {
                new("RimAgent.Model.Default".Translate(), () => SetModel(settings, string.Empty))
                {
                    tooltip = new TipSignal("RimAgent.Model.DefaultTip".Translate())
                }
            };
            foreach (string entry in ConfiguredModels())
            {
                options.Add(new FloatMenuOption(entry, () => SetModel(settings, entry)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    /// <summary>
    /// 統一的扁平科技風按鈕繪製，消除重複代碼。
    /// </summary>
    private static bool DrawUnifiedButton(Rect rect, string label, Color borderColor, Color textColor, bool enabled, GameFont font = GameFont.Small)
    {
        Color bg = enabled ? ControlBgColor : new Color(0.05f, 0.07f, 0.09f, 0.5f);
        bool hover = Mouse.IsOver(rect) && enabled;
        if (hover) bg = Color.Lerp(bg, Color.white, 0.10f);
        DrawRoundedBox(rect, bg, enabled ? borderColor : ControlBorderColor);

        Text.Anchor = TextAnchor.MiddleCenter;
        Text.Font = font;
        GUI.color = enabled ? textColor : new Color(0.45f, 0.45f, 0.45f);
        // 遊戲字型的中文字形偏上約 2px（截圖量得），文字框下移補正才會視覺置中。
        Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width, rect.height), label);
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;

        return enabled && Widgets.ButtonInvisible(rect);
    }

    private static string ModeLabel(AgentMode mode)
    {
        switch (mode)
        {
            case AgentMode.Confirm: return "RimAgent.Settings.Mode.Confirm".Translate();
            case AgentMode.Execute: return "RimAgent.Settings.Mode.Execute".Translate();
            case AgentMode.Auto: return "RimAgent.Settings.Mode.Auto".Translate();
            default: return "RimAgent.Settings.Mode.Autonomous".Translate();
        }
    }

    /// <summary>模式說明（含自動／完整權限的風險提醒）；設定頁不再列模式，改在下拉選單與按鈕的提示顯示。</summary>
    private static string ModeDescription(AgentMode mode)
    {
        switch (mode)
        {
            case AgentMode.Confirm: return "RimAgent.Settings.Mode.ConfirmDesc".Translate();
            case AgentMode.Execute: return "RimAgent.Settings.Mode.ExecuteDesc".Translate();
            case AgentMode.Auto: return "RimAgent.Settings.Mode.AutoDesc".Translate();
            default: return "RimAgent.Settings.Mode.AutonomousDesc".Translate();
        }
    }

    private static string ReasoningLabel(ReasoningLevel level)
    {
        return RimAgentMod.ReasoningKey(level).Translate();
    }

    private static void SetMode(RimAgentSettings settings, AgentMode mode)
    {
        if (settings == null || settings.mode == mode) return;
        settings.mode = mode;
        settings.Write();
    }

    private static void SetReasoning(RimAgentSettings settings, ReasoningLevel level)
    {
        if (settings == null || (settings.reasoning == level && !settings.reasoningRouting)) return;
        settings.reasoning = level;
        settings.reasoningRouting = false;
        settings.Write();
    }

    private static void SetReasoningRouting(RimAgentSettings settings)
    {
        if (settings == null || settings.reasoningRouting) return;
        settings.reasoningRouting = true;
        settings.Write();
    }

    /// <summary>換模型後重新檢查工具呼叫能力：指定的模型可能不在備援鏈上。</summary>
    private void SetModel(RimAgentSettings settings, string model)
    {
        if (settings == null || string.Equals(settings.agentModel, model, StringComparison.Ordinal)) return;
        settings.agentModel = model;
        settings.Write();
        ProbeCapabilities();
    }

    /// <summary>按鈕上只顯示模型名；"供應商:模型" 取冒號後，純供應商條目（用該供應商預設模型）顯示供應商名。</summary>
    private static string ModelShortName(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "RimAgent.Model.Default".Translate();
        string trimmed = model.Trim();
        int colon = trimmed.IndexOf(':');
        return colon > 0 && colon < trimmed.Length - 1 ? trimmed.Substring(colon + 1) : trimmed;
    }

    /// <summary>
    /// 可切換的模型＝玩家在 RimLLM Framework 備援鏈上設好的條目（已有金鑰與供應商設定）。
    /// 框架未初始化或版本太舊（沒有 GetFallbackChain）就只剩「預設」。設定頁的分類器模型選單也用這份清單。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static List<string> ConfiguredModels()
    {
        var models = new List<string>();
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in RimLLMProvider.GetFallbackChain())
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string trimmed = entry.Trim();
                if (seen.Add(trimmed)) models.Add(trimmed);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is MissingMethodException)
        {
            // 不讓下拉選單拋例外打斷繪製，沿用預設即可。
        }
        return models;
    }
}
