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
        private const float DropdownWidth = 110f;
        private const float EntryGap = 6f;
        private const float SlashRowHeight = 26f;
        private const float SlashPopupWidth = 480f;
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
        // 斜線指令彈出選單目前反白的項目（隨輸入變動時夾回範圍）。
        private int _slashIndex;

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
            DrawStatusStrip(autoStrip, component, session);
            DrawBottomBar(bottom, session);
            // 最後畫，才能蓋在對話紀錄上面。
            DrawSlashPopup(bottom, session);
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

        private static string ReasoningLabel(ReasoningLevel level)
        {
            return ("RimAgent.Reasoning." + level).Translate();
        }

        /// <summary>狀態列：模式與思考強度兩個下拉（寫回 mod 設定），右側顯示自主用量。</summary>
        private static void DrawStatusStrip(Rect rect, RimAgentGameComponent component, ColonyAgentSession session)
        {
            RimAgentSettings settings = RimAgentMod.Settings;
            var modeRect = new Rect(rect.x, rect.y, DropdownWidth, rect.height);
            var reasoningRect = new Rect(modeRect.xMax + 4f, rect.y, DropdownWidth, rect.height);

            bool enabled = !session.IsBusy && settings != null;
            if (Widgets.ButtonText(modeRect, "RimAgent.Mode.Status".Translate(ModeLabel(session.Mode)), true, true, enabled))
            {
                var options = new List<FloatMenuOption>();
                foreach (AgentMode mode in (AgentMode[])Enum.GetValues(typeof(AgentMode)))
                {
                    options.Add(new FloatMenuOption(ModeLabel(mode), () => SetMode(settings, mode)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            ReasoningLevel current = settings != null ? settings.reasoning : ReasoningLevel.Auto;
            if (Widgets.ButtonText(reasoningRect, "RimAgent.Reasoning.Status".Translate(ReasoningLabel(current)), true, true, enabled))
            {
                var options = new List<FloatMenuOption>();
                foreach (ReasoningLevel level in (ReasoningLevel[])Enum.GetValues(typeof(ReasoningLevel)))
                {
                    options.Add(new FloatMenuOption(ReasoningLabel(level), () => SetReasoning(settings, level)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (RimAgentGameComponent.AutonomousEnabled)
            {
                var usageRect = new Rect(reasoningRect.xMax + 8f, rect.y, rect.xMax - reasoningRect.xMax - 8f, rect.height);
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = ToolColor;
                Widgets.Label(usageRect, "RimAgent.Auto.StatusOn".Translate(component.AutonomousToolsUsed, RimAgentGameComponent.AutonomousBudget));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        // 分頁裡改的設定要自己落盤：ModSettings 只在設定頁關閉時自動寫。
        private static void SetMode(RimAgentSettings settings, AgentMode mode)
        {
            if (settings.mode == mode) return;
            settings.mode = mode;
            settings.Write();
        }

        private static void SetReasoning(RimAgentSettings settings, ReasoningLevel level)
        {
            if (settings.reasoning == level) return;
            settings.reasoning = level;
            settings.Write();
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
            var inputRect = new Rect(rect.x, rect.y, rect.width - ButtonWidth - 4f, rect.height);
            var buttonRect = new Rect(rect.xMax - ButtonWidth, rect.y, ButtonWidth, rect.height);

            // 彈出選單開著時方向鍵／Tab／Enter 歸它用，要在 TextField 處理鍵盤前攔下。
            bool enterPressed = !HandleSlashKeys(session) &&
                                Event.current.type == EventType.KeyDown &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            DrawInputField(inputRect);
            if (DrawSendOrStop(session, buttonRect, canSend, enterPressed) && enterPressed) Event.current.Use();
        }

        /// <summary>
        /// 忙碌時同一顆按鈕變「停止」；斜線指令不看 canSend（本機執行，不需要模型）。
        /// 回傳是否送出了輸入（呼叫端據此吃掉 Enter）。
        /// </summary>
        private bool DrawSendOrStop(ColonyAgentSession session, Rect buttonRect, bool canSend, bool enterPressed)
        {
            bool isSlash = _input.Length > 0 && _input[0] == '/';
            if (session.IsBusy)
            {
                if (Widgets.ButtonText(buttonRect, "RimAgent.Stop".Translate())) StopRun();
                if (!enterPressed || !isSlash) return false;
                Send(session);
                return true;
            }
            bool clicked = Widgets.ButtonText(buttonRect, "RimAgent.Send".Translate(), true, true, canSend || isSlash);
            if (!(clicked || enterPressed) || !(canSend || isSlash)) return false;
            Send(session);
            return true;
        }

        private void StopRun()
        {
            _cts?.Cancel();
            RimAgentGameComponent.Instance?.StopAutonomousRun();
        }

        private void DrawInputField(Rect inputRect)
        {
            GUI.SetNextControlName(InputControlName);
            _input = Widgets.TextField(inputRect, _input);
            if (_input.Length == 0 && GUI.GetNameOfFocusedControl() != InputControlName)
            {
                // 提示文字單行顯示並截斷，避免換行溢出輸入框
                var placeholderRect = inputRect.ContractedBy(4f, 0f);
                GUI.color = ToolColor;
                Text.WordWrap = false;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(placeholderRect, "RimAgent.Placeholder".Translate().ToString().Truncate(placeholderRect.width));
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// 彈出選單的鍵盤操作：↑↓ 移動、Tab／Enter 選取。有處理到就 Use 掉並回傳 true，
        /// 讓後面的 TextField 與送出邏輯不再看到這個按鍵。
        /// </summary>
        private bool HandleSlashKeys(ColonyAgentSession session)
        {
            if (Event.current.type != EventType.KeyDown) return false;
            List<SlashCommand> matches = SlashCommands.Match(_input);
            if (matches.Count == 0) return false;
            _slashIndex = Mathf.Clamp(_slashIndex, 0, matches.Count - 1);
            switch (Event.current.keyCode)
            {
                case KeyCode.UpArrow:
                    _slashIndex = (_slashIndex + matches.Count - 1) % matches.Count;
                    break;
                case KeyCode.DownArrow:
                    _slashIndex = (_slashIndex + 1) % matches.Count;
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    // 名稱已打完整（如 /goal）時 Enter 直接送出執行，不再補成「/goal 」。
                    if (string.Equals(_input, "/" + matches[_slashIndex].Name, StringComparison.OrdinalIgnoreCase)) return false;
                    AcceptSlash(session, matches[_slashIndex]);
                    break;
                case KeyCode.Tab:
                    AcceptSlash(session, matches[_slashIndex]);
                    break;
                default:
                    return false;
            }
            Event.current.Use();
            return true;
        }

        /// <summary>選定指令：要參數的補成「/name 」等玩家接著打，不要參數的直接執行。</summary>
        private void AcceptSlash(ColonyAgentSession session, SlashCommand command)
        {
            if (command.TakesArgs)
            {
                SetInput("/" + command.Name + " ");
                return;
            }
            _input = string.Empty;
            RunSlash(session, command, string.Empty);
        }

        /// <summary>改輸入框內容並把游標移到最後；直接改 _input 的話焦點中的 TextEditor 游標會留在原處。</summary>
        private void SetInput(string text)
        {
            _input = text;
            if (GUI.GetNameOfFocusedControl() != InputControlName) return;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = text;
            editor.MoveTextEnd();
        }

        /// <summary>輸入以 / 開頭且還在打名稱時，在輸入框上方列出符合的指令。</summary>
        private void DrawSlashPopup(Rect bottom, ColonyAgentSession session)
        {
            List<SlashCommand> matches = SlashCommands.Match(_input);
            if (matches.Count == 0) return;
            _slashIndex = Mathf.Clamp(_slashIndex, 0, matches.Count - 1);

            float height = matches.Count * SlashRowHeight + 8f;
            var popup = new Rect(bottom.x, bottom.y - height - 4f, Mathf.Min(SlashPopupWidth, bottom.width), height);
            Widgets.DrawWindowBackground(popup);
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            for (int i = 0; i < matches.Count; i++)
            {
                SlashCommand command = matches[i];
                var row = new Rect(popup.x + 4f, popup.y + 4f + i * SlashRowHeight, popup.width - 8f, SlashRowHeight);
                // 滑鼠只點選、不改反白，否則游標停在選單上時 ↑↓ 會被每幀蓋回去。
                Widgets.DrawHighlightIfMouseover(row);
                if (i == _slashIndex) Widgets.DrawHighlightSelected(row);
                string name = "/" + command.Name;
                float nameWidth = Text.CalcSize(name).x;
                Widgets.Label(new Rect(row.x + 6f, row.y, nameWidth, row.height), name);
                var descRect = new Rect(row.x + 6f + nameWidth + 10f, row.y, row.width - nameWidth - 22f, row.height);
                GUI.color = ToolColor;
                Widgets.Label(descRect, command.DescriptionKey.Translate().ToString().Truncate(descRect.width));
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(row)) AcceptSlash(session, command);
            }
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>執行斜線指令，結果以提示寫進對話；未知指令也在對話裡提示。</summary>
        private void RunSlash(ColonyAgentSession session, SlashCommand command, string args)
        {
            if (command == SlashCommands.Stop)
            {
                StopRun();
                return;
            }
            if (session.IsBusy)
            {
                Notice(session, "RimAgent.Slash.Busy".Translate());
                return;
            }
            if (command == SlashCommands.Undo)
            {
                if (session.Undo.CanUndo) session.UndoLast();
                else Notice(session, "RimAgent.Slash.NothingToUndo".Translate());
            }
            else if (command == SlashCommands.Clear)
            {
                session.Clear();
                _lastEntryCount = -1;
            }
            else if (command == SlashCommands.Goal)
            {
                RunGoal(session, args);
            }
        }

        private static void RunGoal(ColonyAgentSession session, string args)
        {
            RimAgentGameComponent component = RimAgentGameComponent.Instance;
            if (component == null) return;
            IReadOnlyList<string> goals = component.Goals;
            switch (SlashCommands.ParseGoal(args, out int index, out string text))
            {
                case GoalAction.Add:
                    if (component.AddGoal(text)) Notice(session, "RimAgent.Goal.Added".Translate(goals.Count, text));
                    else Notice(session, "RimAgent.Goal.Full".Translate());
                    break;
                case GoalAction.Remove:
                    if (index >= 1 && index <= goals.Count)
                    {
                        string removed = goals[index - 1];
                        component.RemoveGoalAt(index - 1);
                        Notice(session, "RimAgent.Goal.Removed".Translate(index, removed));
                    }
                    else Notice(session, "RimAgent.Goal.NoSuch".Translate(index));
                    break;
                default:
                    Notice(session, goals.Count == 0 ? "RimAgent.Goal.Empty".Translate().ToString() : GoalListText(goals));
                    break;
            }
        }

        private static string GoalListText(IReadOnlyList<string> goals)
        {
            var sb = new System.Text.StringBuilder("RimAgent.Goal.ListHeader".Translate());
            for (int i = 0; i < goals.Count; i++) sb.Append('\n').Append(i + 1).Append(". ").Append(goals[i]);
            return sb.ToString();
        }

        private static void Notice(ColonyAgentSession session, string text)
        {
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = text,
                InstructionId = session.Transcript.CurrentInstructionId
            });
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

            if (SlashCommands.TryParse(text, out SlashCommand command, out string args))
            {
                if (command != null) RunSlash(session, command, args);
                else Notice(session, "RimAgent.Slash.Unknown".Translate(text));
                return;
            }
            if (_capabilityWarning != null) return;

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
