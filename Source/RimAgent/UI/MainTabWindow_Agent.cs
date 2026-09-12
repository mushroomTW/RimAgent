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
        private string _capabilityWarning;
        private CancellationTokenSource _cts;

        public override Vector2 RequestedTabSize => new Vector2(760f, 580f);

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

            AgentSession session = component.Session;
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
            var transcriptRect = new Rect(inRect.x, top, inRect.width, bottom.y - top - EntryGap);
            DrawTranscript(transcriptRect, session);
            DrawBottomBar(bottom, session);
        }

        private void DrawTranscript(Rect outRect, AgentSession session)
        {
            IReadOnlyList<TranscriptEntry> entries = session.Transcript.Snapshot();
            float width = outRect.width - 20f;

            var lines = new List<(string Text, Color Color, float Height)>(entries.Count + 1);
            float total = 0f;
            foreach (TranscriptEntry entry in entries)
            {
                foreach ((string text, Color color) in Render(entry))
                {
                    float height = Text.CalcHeight(text, width);
                    lines.Add((text, color, height));
                    total += height + EntryGap;
                }
            }
            if (session.IsBusy)
            {
                string working = "RimAgent.Working".Translate();
                lines.Add((working, NoticeColor, Text.CalcHeight(working, width)));
                total += lines[lines.Count - 1].Height + EntryGap;
            }

            if (entries.Count != _lastEntryCount)
            {
                _lastEntryCount = entries.Count;
                _scroll.y = float.MaxValue;
            }

            var viewRect = new Rect(0f, 0f, width, Math.Max(total, outRect.height));
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float y = 0f;
            foreach ((string text, Color color, float height) in lines)
            {
                GUI.color = color;
                Widgets.Label(new Rect(0f, y, width, height), text);
                y += height + EntryGap;
            }
            GUI.color = Color.white;
            Widgets.EndScrollView();
        }

        private static IEnumerable<(string Text, Color Color)> Render(TranscriptEntry entry)
        {
            switch (entry.Kind)
            {
                case TranscriptKind.User:
                    yield return ("> " + entry.Text, UserColor);
                    break;
                case TranscriptKind.Assistant:
                    yield return (entry.Text.Length == 0 ? "RimAgent.EmptyReply".Translate().ToString() : entry.Text, Color.white);
                    if (entry.ToolsStripped) yield return ("RimAgent.ToolsStripped".Translate(), NoticeColor);
                    break;
                case TranscriptKind.Tool:
                    string call = "* " + entry.ToolName + "(" + entry.Arguments + ")";
                    string result = entry.Result.Length > MaxResultChars ? entry.Result.Substring(0, MaxResultChars) + "…" : entry.Result;
                    yield return (call + "\n  -> " + result, entry.Failed ? ErrorColor : ToolColor);
                    if (entry.ToolCategory == ToolCategory.Combat) yield return ("RimAgent.CombatLatencyNote".Translate(), NoticeColor);
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

        private void DrawBottomBar(Rect rect, AgentSession session)
        {
            bool busy = session.IsBusy;
            bool canSend = !busy && _capabilityWarning == null;
            var clearRect = new Rect(rect.xMax - ButtonWidth, rect.y, ButtonWidth, rect.height);
            var stopRect = new Rect(clearRect.x - ButtonWidth - 4f, rect.y, ButtonWidth, rect.height);
            var sendRect = new Rect(stopRect.x - ButtonWidth - 4f, rect.y, ButtonWidth, rect.height);
            var inputRect = new Rect(rect.x, rect.y, sendRect.x - rect.x - 4f, rect.height);

            bool enterPressed = canSend &&
                                Event.current.type == EventType.KeyDown &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                                GUI.GetNameOfFocusedControl() == InputControlName;

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
            }
            if (Widgets.ButtonText(clearRect, "RimAgent.Clear".Translate(), true, true, !busy))
            {
                session.Clear();
                _lastEntryCount = -1;
            }
        }

        private void Send(AgentSession session)
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
    }
}
