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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "S101:TypesShouldBeNamedInPascalCase", Justification = "RimWorld MainTabWindow naming convention referenced in XML Defs")]
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
        // 渲染快取：Markdown 轉換與 CalcHeight 只在紀錄變動或寬度變時重做，不每幀重跑（OnGUI 一幀會跑多次）。
        // 紀錄加入後不會再改，所以同一 Epoch 內只渲染新增的尾段；Clear／裁掉舊紀錄才全量重建。
        private readonly List<(string Text, Color Color, float Height)> _lines = new List<(string, Color, float)>();
        private float _linesTotal;
        private float _linesWidth = -1f;
        private int _linesEntryCount = -1;
        private int _linesTranscriptVersion = -1;
        private int _linesTranscriptEpoch = -1;
        // 分頁視窗跨讀檔存活，新局的 transcript 是另一個物件（Epoch 又從 0 起算），要認物件不能只認數字。
        private AgentTranscript _linesTranscript;
        // 戰鬥延遲提醒以指令為單位只顯示一次：跨增量渲染保留已提醒過的指令編號。
        private readonly HashSet<int> _combatNoted = new HashSet<int>();
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
        private static void DrawAutoStrip(Rect rect, RimAgentGameComponent component, ColonyAgentSession session)
        {
            const float GoalsButtonWidth = 64f;
            const float StandingButtonWidth = 64f;
            var standingRect = new Rect(rect.xMax - StandingButtonWidth, rect.y, StandingButtonWidth, rect.height);
            var buttonRect = new Rect(standingRect.x - GoalsButtonWidth - 4f, rect.y, GoalsButtonWidth, rect.height);
            var labelRect = new Rect(rect.x, rect.y, buttonRect.x - rect.x - 4f, rect.height);

            GUI.color = ToolColor;
            string status = RimAgentGameComponent.AutonomousEnabled
                ? "RimAgent.Auto.StatusOn".Translate(component.AutonomousToolsUsed, RimAgentGameComponent.AutonomousBudget).ToString()
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
            AgentTranscript transcript = session.Transcript;
            bool sameTranscript = ReferenceEquals(transcript, _linesTranscript);
            if (!sameTranscript || transcript.Version != _linesTranscriptVersion || Mathf.Abs(width - _linesWidth) > 0.001f)
            {
                // 快照與版本號一次上鎖取得，否則背景執行緒的 Add 夾在中間裁切時 Epoch 會跟清單對不上。
                IReadOnlyList<TranscriptEntry> entries = transcript.Snapshot(out int version, out int epoch);
                bool appendOnly = sameTranscript
                                  && epoch == _linesTranscriptEpoch
                                  && Mathf.Abs(width - _linesWidth) <= 0.001f
                                  && entries.Count >= _linesEntryCount;
                if (appendOnly) AppendLines(entries, _linesEntryCount, width);
                else RebuildLines(entries, width);
                _linesTranscript = transcript;
                _linesTranscriptVersion = version;
                _linesTranscriptEpoch = epoch;
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
            // 只畫捲動視窗看得到的行：富文字排版即使被裁掉也照算，長對話會每幀白做。
            // _scroll 可能是還沒被 BeginScrollView 夾住的 MaxValue，這裡自己夾一次。
            float visibleTop = Mathf.Clamp(_scroll.y, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
            float visibleBottom = visibleTop + outRect.height;
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float y = 0f;
            foreach ((string text, Color color, float height) in _lines)
            {
                if (y + height >= visibleTop && y <= visibleBottom)
                {
                    GUI.color = color;
                    Widgets.Label(new Rect(0f, y, width, height), text);
                }
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
            _combatNoted.Clear();
            AppendLines(entries, 0, width);
        }

        /// <summary>只渲染 <paramref name="from"/> 起的新紀錄，接在既有快取後面。</summary>
        private void AppendLines(IReadOnlyList<TranscriptEntry> entries, int from, float width)
        {
            for (int i = Math.Max(0, from); i < entries.Count; i++)
            {
                foreach ((string text, Color color) in Render(entries[i], _combatNoted))
                {
                    float height = Text.CalcHeight(text, width);
                    _lines.Add((text, color, height));
                    _linesTotal += height + EntryGap;
                }
            }
            _linesEntryCount = entries.Count;
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
                    foreach (var line in RenderAssistant(entry)) yield return line;
                    break;
                case TranscriptKind.Tool:
                    foreach (var line in RenderTool(entry, combatNoted)) yield return line;
                    break;
                case TranscriptKind.Error:
                    yield return ("! " + DescribeError(entry), ErrorColor);
                    break;
                default:
                    yield return (entry.Text, NoticeColor);
                    break;
            }
        }

        private static IEnumerable<(string Text, Color Color)> RenderAssistant(TranscriptEntry entry)
        {
            string reply = RenderReply(entry.Text);
            yield return (reply.Length == 0 ? "RimAgent.EmptyReply".Translate().ToString() : reply, Color.white);
            if (entry.ToolsStripped) yield return ("RimAgent.ToolsStripped".Translate(), NoticeColor);
        }

        private static IEnumerable<(string Text, Color Color)> RenderTool(TranscriptEntry entry, HashSet<int> combatNoted)
        {
            string call = "* " + entry.ToolName + "(" + entry.Arguments + ")";
            if (entry.RiskCheck.Length > 0) call += " [risk: " + entry.RiskCheck + "]";
            string result = entry.Result.Length > MaxResultChars ? entry.Result.Substring(0, MaxResultChars) + "…" : entry.Result;
            yield return (call + "\n  -> " + result, entry.Failed ? ErrorColor : ToolColor);
            // 同一句指令只提醒一次（舊存檔載入的紀錄 InstructionId 為 0，同樣只提醒一次）。
            if (entry.ToolCategory == ToolCategory.Combat && combatNoted.Add(entry.InstructionId))
            {
                yield return ("RimAgent.CombatLatencyNote".Translate(), NoticeColor);
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
            if (session.HasPendingConfirmation && !session.IsBusy)
            {
                DrawConfirmBar(rect, session);
                return;
            }
            bool canSend = !session.IsBusy && _capabilityWarning == null;
            float buttonsTotalWidth = (ButtonWidth + 4f) * 4f;
            var inputRect = new Rect(rect.x, rect.y, rect.width - buttonsTotalWidth, rect.height);

            bool enterPressed = canSend &&
                                Event.current.type == EventType.KeyDown &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            DrawInputField(inputRect);
            DrawActionButtons(session, rect, canSend, enterPressed);
        }

        private void DrawInputField(Rect inputRect)
        {
            GUI.SetNextControlName(InputControlName);
            _input = Widgets.TextField(inputRect, _input);
            if (_input.Length == 0 && GUI.GetNameOfFocusedControl() != InputControlName)
            {
                GUI.color = ToolColor;
                Widgets.Label(inputRect.ContractedBy(4f, 0f), "RimAgent.Placeholder".Translate());
                GUI.color = Color.white;
            }
        }

        private void DrawActionButtons(ColonyAgentSession session, Rect barRect, bool canSend, bool enterPressed)
        {
            bool busy = session.IsBusy;
            bool canUndo = !busy && session.Undo.CanUndo;
            var clearRect = new Rect(barRect.xMax - ButtonWidth, barRect.y, ButtonWidth, barRect.height);
            var undoRect = new Rect(clearRect.x - ButtonWidth - 4f, barRect.y, ButtonWidth, barRect.height);
            var stopRect = new Rect(undoRect.x - ButtonWidth - 4f, barRect.y, ButtonWidth, barRect.height);
            var sendRect = new Rect(stopRect.x - ButtonWidth - 4f, barRect.y, ButtonWidth, barRect.height);

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
