using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using RimAgent.Agent;
using RimAgent.Tools;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI
{
    /// <summary>
    /// 底部工具列的「Agent」分頁：對話與行動紀錄、輸入列、能力警告。
    /// 每幀讀 session 的 transcript 快照，不做跨執行緒回呼。
    /// </summary>
    public class MainTabWindow_Agent : MainTabWindow
    {
        private const string InputControlName = "RimAgentInput";
        private const float BottomBarHeight = 32f;
        private const float AutoStripHeight = 26f;
        private const float ButtonWidth = 80f;
        private const float EntryGap = 6f;
        private const int MaxResultChars = 600;

        private static readonly Color UserColor = new Color(0.65f, 0.85f, 1f);
        private static readonly Color ToolColor = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color ErrorColor = new Color(1f, 0.45f, 0.45f);
        private static readonly Color NoticeColor = new Color(1f, 0.85f, 0.4f);

        private string _input = string.Empty;
        private Vector2 _scroll;
        private int _lastEntryCount = -1;
        // 渲染快取：Markdown 轉換與 CalcHeight 只在紀錄數或寬度變時重做，不每幀重跑（OnGUI 一幀會跑多次）。
        private readonly List<(string Text, Color Color, float Height)> _lines = new List<(string, Color, float)>();
        private float _linesTotal;
        private float _linesWidth = -1f;
        private int _linesEntryCount = -1;
        private int _linesTranscriptVersion = -1;
        private string _capabilityWarning;
        private CancellationTokenSource _cts;

        public override Vector2 RequestedTabSize => new Vector2(760f, 580f);

        public MainTabWindow_Agent()
        {
            // Window 預設 Enter 會關閉視窗；輸入列在忙碌或無法送出時 Enter 不會被消費，分頁就會突然消失。
            closeOnAccept = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            ProbeCapabilities();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            RimAgentGameComponent component = RimAgentGameComponent.Instance;
            if (component == null || Find.CurrentMap == null)
            {
                Widgets.Label(inRect, "RimAgent.NoGame".Translate());
                return;
            }

            ColonyAgentSession session = component.Session;
            float top = inRect.y;
            if (_capabilityWarning != null)
            {
                float height = Text.CalcHeight(_capabilityWarning, inRect.width - 8f) + 8f;
                var banner = new Rect(inRect.x, top, inRect.width, height);
                Widgets.DrawBoxSolid(banner, new Color(0.4f, 0.1f, 0.1f, 0.6f));
                GUI.color = ErrorColor;
                Widgets.Label(banner.ContractedBy(4f), _capabilityWarning);
                GUI.color = Color.white;
                top += height + EntryGap;
            }

            var bottom = new Rect(inRect.x, inRect.yMax - BottomBarHeight, inRect.width, BottomBarHeight);
            var autoStrip = new Rect(inRect.x, bottom.y - AutoStripHeight - EntryGap, inRect.width, AutoStripHeight);
            var transcriptRect = new Rect(inRect.x, top, inRect.width, autoStrip.y - top - EntryGap);
            DrawTranscript(transcriptRect, session);
            DrawAutoStrip(autoStrip, component, session);
            DrawBottomBar(bottom, session);
        }

        private static string ModeLabel(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask: return "RimAgent.Settings.Mode.Ask".Translate();
                case AgentMode.Confirm: return "RimAgent.Settings.Mode.Confirm".Translate();
                case AgentMode.Auto: return "RimAgent.Settings.Mode.Auto".Translate();
                default: return "RimAgent.Settings.Mode.Autonomous".Translate();
            }
        }

        /// <summary>模式狀態列：目前模式或自主用量、目標與常駐指令入口。</summary>
        private void DrawAutoStrip(Rect rect, RimAgentGameComponent component, ColonyAgentSession session)
        {
            const float GoalsButtonWidth = 64f;
            const float StandingButtonWidth = 64f;
            var standingRect = new Rect(rect.xMax - StandingButtonWidth, rect.y, StandingButtonWidth, rect.height);
            var buttonRect = new Rect(standingRect.x - GoalsButtonWidth - 4f, rect.y, GoalsButtonWidth, rect.height);
            var labelRect = new Rect(rect.x, rect.y, buttonRect.x - rect.x - 4f, rect.height);

            GUI.color = ToolColor;
            string status = component.AutonomousEnabled
                ? "RimAgent.Auto.StatusOn".Translate(component.AutonomousToolsUsed, component.AutonomousBudget).ToString()
                : "RimAgent.Mode.Status".Translate(ModeLabel(session.Mode)).ToString();
            Widgets.Label(labelRect, status);
            GUI.color = Color.white;

            if (Widgets.ButtonText(buttonRect, "RimAgent.Auto.Goals".Translate(), true, false, !session.IsBusy))
            {
                Find.WindowStack.Add(new Dialog_AutonomousGoals());
            }
            if (Widgets.ButtonText(standingRect, "RimAgent.Standing.Button".Translate(), true, false, !session.IsBusy))
            {
                Find.WindowStack.Add(new Dialog_StandingInstructions());
            }
        }

        private void DrawTranscript(Rect outRect, ColonyAgentSession session)
        {
            float width = outRect.width - 20f;
            int version = session.Transcript.Version;
            if (version != _linesTranscriptVersion || width != _linesWidth)
            {
                RebuildLines(session.Transcript.Snapshot(), width);
                _linesTranscriptVersion = version;
                _linesWidth = width;
            }

            float total = _linesTotal;
            string working = null;
            float workingHeight = 0f;
            if (session.IsBusy)
            {
                working = "RimAgent.Working".Translate();
                workingHeight = Text.CalcHeight(working, width);
                total += workingHeight + EntryGap;
            }

            if (_linesEntryCount != _lastEntryCount)
            {
                _lastEntryCount = _linesEntryCount;
                _scroll.y = float.MaxValue;
            }

            var viewRect = new Rect(0f, 0f, width, Math.Max(total, outRect.height));
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float y = 0f;
            foreach ((string text, Color color, float height) in _lines)
            {
                GUI.color = color;
                Widgets.Label(new Rect(0f, y, width, height), text);
                y += height + EntryGap;
            }
            if (working != null)
            {
                GUI.color = NoticeColor;
                Widgets.Label(new Rect(0f, y, width, workingHeight), working);
            }
            GUI.color = Color.white;
            Widgets.EndScrollView();
        }

        private void RebuildLines(IReadOnlyList<TranscriptEntry> entries, float width)
        {
            _lines.Clear();
            _linesTotal = 0f;
            _linesEntryCount = entries.Count;
            // 戰鬥延遲提醒以指令為單位只顯示一次：記下已經提醒過的指令編號。
            var combatNoted = new HashSet<int>();
            foreach (TranscriptEntry entry in entries)
            {
                foreach ((string text, Color color) in Render(entry, combatNoted))
                {
                    float height = Text.CalcHeight(text, width);
                    _lines.Add((text, color, height));
                    _linesTotal += height + EntryGap;
                }
            }
        }

        private static IEnumerable<(string Text, Color Color)> Render(TranscriptEntry entry, HashSet<int> combatNoted)
        {
            switch (entry.Kind)
            {
                case TranscriptKind.User:
                    // 自主任務的指令是排程器產生的，跟玩家打的字分開標示。
                    yield return ((entry.Autonomous ? "[auto] " : "> ") + entry.Text, entry.Autonomous ? NoticeColor : UserColor);
                    break;
                case TranscriptKind.Assistant:
                    string reply = RenderReply(entry.Text);
                    yield return (reply.Length == 0 ? "RimAgent.EmptyReply".Translate().ToString() : reply, Color.white);
                    if (entry.ToolsStripped) yield return ("RimAgent.ToolsStripped".Translate(), NoticeColor);
                    break;
                case TranscriptKind.Tool:
                    string call = "* " + entry.ToolName + "(" + entry.Arguments + ")";
                    if (entry.RiskCheck.Length > 0) call += " [risk: " + entry.RiskCheck + "]";
                    string result = entry.Result.Length > MaxResultChars ? entry.Result.Substring(0, MaxResultChars) + "…" : entry.Result;
                    yield return (call + "\n  -> " + result, entry.Failed ? ErrorColor : ToolColor);
                    // 同一句指令只提醒一次（舊存檔載入的紀錄 InstructionId 為 0，同樣只提醒一次）。
                    if (entry.ToolCategory == ToolCategory.Combat && combatNoted.Add(entry.InstructionId))
                    {
                        yield return ("RimAgent.CombatLatencyNote".Translate(), NoticeColor);
                    }
                    break;
                case TranscriptKind.Error:
                    yield return ("! " + DescribeError(entry), ErrorColor);
                    break;
                default:
                    yield return (entry.Text, NoticeColor);
                    break;
            }
        }

        private static string DescribeError(TranscriptEntry entry)
        {
            switch (entry.ErrorKind)
            {
                case AgentErrorKind.Cancelled: return "RimAgent.Error.Cancelled".Translate();
                case AgentErrorKind.Configuration: return "RimAgent.Error.Configuration".Translate(entry.Text);
                case AgentErrorKind.Transient: return "RimAgent.Error.Transient".Translate(entry.Text);
                case AgentErrorKind.ContextTooLong: return "RimAgent.Error.ContextTooLong".Translate();
                default: return "RimAgent.Error.Unknown".Translate(entry.Text);
            }
        }

        private void DrawBottomBar(Rect rect, ColonyAgentSession session)
        {
            bool busy = session.IsBusy;
            // 有命令在等確認時，整列換成確認操作：先看預覽再決定，避免誤觸送出。
            if (session.HasPendingConfirmation && !busy)
            {
                DrawConfirmBar(rect, session);
                return;
            }
            bool canSend = !busy && _capabilityWarning == null;
            bool canUndo = !busy && session.Undo.CanUndo;
            var clearRect = new Rect(rect.xMax - ButtonWidth, rect.y, ButtonWidth, rect.height);
            var undoRect = new Rect(clearRect.x - ButtonWidth - 4f, rect.y, ButtonWidth, rect.height);
            var stopRect = new Rect(undoRect.x - ButtonWidth - 4f, rect.y, ButtonWidth, rect.height);
            var sendRect = new Rect(stopRect.x - ButtonWidth - 4f, rect.y, ButtonWidth, rect.height);
            var inputRect = new Rect(rect.x, rect.y, sendRect.x - rect.x - 4f, rect.height);

            // 跟原版 Dialog_Rename 一樣：分頁開著、按 Enter 就送出（GetNameOfFocusedControl 在遊戲內查不到名稱）。
            // 有其他視窗疊在上面時 Window 會把鍵盤事件吃掉，不會誤觸。
            bool enterPressed = canSend &&
                                Event.current.type == EventType.KeyDown &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            GUI.SetNextControlName(InputControlName);
            _input = Widgets.TextField(inputRect, _input);
            if (_input.Length == 0 && GUI.GetNameOfFocusedControl() != InputControlName)
            {
                GUI.color = ToolColor;
                Widgets.Label(inputRect.ContractedBy(4f, 0f), "RimAgent.Placeholder".Translate());
                GUI.color = Color.white;
            }

            if ((Widgets.ButtonText(sendRect, "RimAgent.Send".Translate(), true, true, canSend) || enterPressed) && canSend)
            {
                if (enterPressed) Event.current.Use();
                Send(session);
            }
            if (Widgets.ButtonText(stopRect, "RimAgent.Stop".Translate(), true, true, busy))
            {
                _cts?.Cancel();
                RimAgentGameComponent.Instance?.StopAutonomousRun();
            }
            if (Widgets.ButtonText(undoRect, "RimAgent.Undo".Translate(), true, true, canUndo) && canUndo)
            {
                session.UndoLast();
            }
            if (Widgets.ButtonText(clearRect, "RimAgent.Clear".Translate(), true, true, !busy))
            {
                session.Clear();
                _lastEntryCount = -1;
            }
        }

        private void DrawConfirmBar(Rect rect, ColonyAgentSession session)
        {
            const float WideButtonWidth = 120f;
            var confirmRect = new Rect(rect.x, rect.y, WideButtonWidth, rect.height);
            var discardRect = new Rect(confirmRect.xMax + 4f, rect.y, WideButtonWidth, rect.height);
            var noteRect = new Rect(discardRect.xMax + 8f, rect.y, Mathf.Max(0f, rect.xMax - discardRect.xMax - 8f), rect.height);

            if (Widgets.ButtonText(confirmRect, "RimAgent.Confirm".Translate()))
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                // 結果全部寫進 transcript，這裡不等待；例外也在 session 內部處理完畢。
                _ = session.ConfirmAsync(_cts.Token);
            }
            if (Widgets.ButtonText(discardRect, "RimAgent.ConfirmDiscard".Translate()))
            {
                session.DiscardPendingConfirmation();
            }
            Text.Font = GameFont.Tiny;
            GUI.color = NoticeColor;
            Widgets.Label(noteRect, "RimAgent.ConfirmNote".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void Send(ColonyAgentSession session)
        {
            string text = _input.Trim();
            if (text.Length == 0) return;
            _input = string.Empty;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            // 結果全部寫進 transcript，這裡不等待；例外也在 session 內部處理完畢。
            _ = session.SendAsync(text, _cts.Token);
        }

        private void ProbeCapabilities()
        {
            try
            {
                _capabilityWarning = CheckFunctionCalling() ? null : "RimAgent.NoFunctionCalling".Translate().ToString();
            }
            catch (RimLLMException ex)
            {
                _capabilityWarning = "RimAgent.NoProvider".Translate(ex.Message);
            }
            catch (InvalidOperationException)
            {
                // 框架 mod 在清單裡但 SDK 從未初始化（典型情況：框架 DLL 被 RimWorld 拒載）。
                // 不能讓例外冒到 OnGUI，否則每幀一條紅字且分頁無法使用。
                _capabilityWarning = "RimAgent.FrameworkNotInitialized".Translate();
            }
            catch (Exception ex) when (ex is MissingMethodException || ex is TypeLoadException)
            {
                // 舊版框架沒有 GetEffectiveCapabilities；把 JIT 失敗轉成看得懂的訊息。
                _capabilityWarning = "RimAgent.FrameworkTooOld".Translate();
            }
        }

        /// <summary>獨立成方法，讓舊版框架缺少 API 時的 MissingMethodException 落在呼叫端的 try 內。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CheckFunctionCalling()
        {
            return RimLLMProvider.GetEffectiveCapabilities().SupportsFunctionCalling;
        }

        /// <summary>
        /// 模型回覆的 Markdown 轉 Unity rich text（框架的 RimLLMMarkdown，Markdig 解析）。
        /// 舊版框架沒有此 API 時退回原文顯示，不炸出例外。
        /// </summary>
        private static bool? _markdownAvailable;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string RenderReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (_markdownAvailable == false) return text;
            try
            {
                string rich = RimLLM_Framework.Mod.RimLLMMarkdown.ToRichText(text);
                _markdownAvailable = true;
                return rich ?? text;
            }
            catch (Exception ex) when (ex is MissingMethodException || ex is TypeLoadException)
            {
                // 舊框架沒有渲染 API：永久退回原文。
                _markdownAvailable = false;
                return text;
            }
            catch (Exception)
            {
                // 單句解析失敗只影響這一句：退回原文，渲染保持開啟。
                return text;
            }
        }
    }
}
