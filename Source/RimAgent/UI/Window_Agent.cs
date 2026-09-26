using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Tools;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI
{
    /// <summary>
    /// 懸浮可移動的 Agent 視窗，支援隨意拖曳、自由縮放、位置記憶。
    /// 包含雙色標籤決策日誌串流、模式/思考切換、輸入列與斜線指令。
    /// </summary>
    public class Window_Agent : Window
    {
        private const string InputControlName = "RimAgentFloatingInput";
        private const float TitleBarHeight = 30f;
        private const float BottomBarHeight = 32f;
        private const float AutoStripHeight = 24f;
        private const float ButtonWidth = 72f;
        private const float DropdownWidth = 110f;
        /// <summary>模型名稱較長，按鈕放寬；視窗窄時縮到剩餘寬度。</summary>
        private const float ModelDropdownWidth = 170f;
        private const float EntryGap = 6f;
        /// <summary>每回合（一句 User 紀錄）開頭多留的間距，分隔線畫在正中間。</summary>
        private const float TurnGap = 14f;
        /// <summary>玩家氣泡最寬佔對話區的比例。</summary>
        private const float BubbleMaxRatio = 0.8f;
        private const float BubblePadX = 8f;
        private const float BubblePadY = 4f;
        private const float BubbleRadius = 8f;
        /// <summary>捲動位置離底部多近仍算「停在底部」，新內容來時自動跟隨。</summary>
        private const float StickToBottomSlack = 4f;
        private const float JumpButtonWidth = 130f;
        private const float JumpButtonHeight = 22f;
        /// <summary>輸入歷史保留筆數。</summary>
        private const int MaxInputHistory = 50;
        private const float SlashRowHeight = 26f;
        private const float SlashPopupWidth = 460f;
        private const int MaxResultChars = 600;
        /// <summary>截圖預覽快取張數；要大於一個視窗同時看得到的張數，否則會每幀互相擠掉重載。</summary>
        private const int MaxCachedScreenshots = 12;
        /// <summary>截圖預覽縮到的最大寬度（像素）；原圖全解析度一張就是數十 MB。</summary>
        private const int MaxScreenshotWidth = 800;
        /// <summary>截圖檔還不在（剛拍、寫入中）或讀不了時，隔多久再試（毫秒）。</summary>
        private const int ScreenshotRetryMs = 1000;
        /// <summary>視窗寬度要停住多久才重排對話（毫秒）。</summary>
        private const int ReflowDelayMs = 150;
        /// <summary>上下文用量到達幾成時標題列計量變黃預警。</summary>

        // 圖片風格配色
        private static readonly Color BgColor = new Color(0.04f, 0.06f, 0.08f, 0.88f);
        private static readonly Color BorderColor = new Color(0.62f, 0.68f, 0.76f, 0.70f);
        private static readonly Color TitleColor = new Color(0.80f, 0.88f, 0.96f);
        private static readonly Color CloseButHoverColor = new Color(1f, 0.35f, 0.35f, 0.3f);

        // Rich text 標籤色彩 Hex（用於雙色分離排版）
        private const string EmergencyHex = "FF5959"; // 亮紅橙：[危險急停]
        private const string PauseHex = "FFC846";     // 金黃：[自動暫停]
        private const string AutoActionHex = "73CCFF"; // 冷青藍：[自動處理]
        private const string TextHex = "E6EDF5";       // 柔和淺白：內文敘述
        private const string SubTextHex = "A0B0C0";    // 淺灰藍：次要細節 / 結果
        private const string NoticeHex = "FFD670";     // 提示黃

        private static readonly Color ControlBgColor = new Color(0.08f, 0.11f, 0.15f, 0.75f);
        private static readonly Color ControlBorderColor = new Color(0.45f, 0.52f, 0.60f, 0.65f);
        private static readonly Color ToolActionColor = new Color(0.48f, 0.82f, 1f);
        private static readonly Color ToolTextColor = new Color(0.75f, 0.78f, 0.82f);
        private static readonly Color EmergencyColor = new Color(1f, 0.35f, 0.35f);
        private static readonly Color PauseColor = new Color(1f, 0.80f, 0.28f);
        private static readonly Color NoticeColor = new Color(0.95f, 0.85f, 0.45f);
        private static readonly Color BubbleColor = new Color(0.16f, 0.30f, 0.44f, 0.55f);
        private static readonly Color BubbleBorderColor = new Color(0.66f, 0.86f, 1f, 0.45f);
        private static readonly Color DividerColor = new Color(0.45f, 0.52f, 0.60f, 0.30f);

        private enum LineKind
        {
            Text,
            /// <summary>玩家親自輸入的話：靠右、包在氣泡裡。</summary>
            PlayerBubble,
            /// <summary>工具呼叫摘要列：點一下展開或收合參數與結果。</summary>
            ToolHeader,
            /// <summary>每句指令一次的戰鬥延遲提醒；畫法同 Text，重排該筆時要讓它重新出現。</summary>
            CombatNote,
            Screenshot
        }

        private struct TranscriptLine
        {
            public LineKind Kind;
            public string Text;
            /// <summary>去標籤純文字：透明框選層用，複製時拿不到顏色標籤。</summary>
            public string PlainText;
            public float Height;
            /// <summary>PlayerBubble 專用：氣泡外框寬度。</summary>
            public float BubbleWidth;
            /// <summary>Screenshot 專用。</summary>
            public string ImagePath;
            /// <summary>這行屬於哪筆紀錄；ToolHeader 點擊時靠它找出同一筆的所有行，只重排那一筆。</summary>
            public TranscriptEntry Entry;
            /// <summary>回合開頭：上方多留 <see cref="TurnGap"/> 並畫分隔線。</summary>
            public bool TurnStart;
        }

        /// <summary>整局共用、關掉視窗也保留；最新的在最後。</summary>
        private static readonly List<string> InputHistory = new List<string>();

        private string _input = string.Empty;
        private Vector2 _scroll;
        /// <summary>上一次繪製時的最大捲動量；捲動位置在它附近就算停在底部。</summary>
        private float _lastMaxScroll;
        /// <summary>下一次繪製強制捲到底（送出指令、清除對話後）。</summary>
        private bool _scrollToBottom = true;
        /// <summary>最後一次停在底部時的逐字稿 Version（單調遞增，裁切舊紀錄也不會倒退）；之後變了就顯示「有新訊息」。</summary>
        private int _seenVersion;
        /// <summary>正在瀏覽的歷史索引；-1 代表沒在瀏覽。</summary>
        private int _historyIndex = -1;
        /// <summary>開始瀏覽歷史前輸入框裡的草稿，↓ 回到底時還原。</summary>
        private string _historyDraft = string.Empty;
        /// <summary>玩家親手設定過的工具展開狀態（true＝展開）；以參考比對，沒設定的走預設。</summary>
        private readonly Dictionary<TranscriptEntry, bool> _toolExpanded = new Dictionary<TranscriptEntry, bool>();

        // 渲染快取
        private readonly List<TranscriptLine> _lines = new List<TranscriptLine>();
        private readonly Dictionary<string, Texture2D> _screenshotTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        /// <summary>最近用到的排在最後；繪製時會把用到的移到最後，淘汰從最前面開始。</summary>
        private readonly List<string> _screenshotOrder = new List<string>();
        /// <summary>載入失敗的截圖與失敗時的 Environment.TickCount。</summary>
        private readonly Dictionary<string, int> _screenshotFailedAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>繪製時看到、還沒載入的截圖；留到 WindowUpdate 載入（每幀最多一張，不在 OnGUI 裡解碼）。</summary>
        private string _pendingScreenshot;
        /// <summary>背景讀檔中的截圖；讀完才在 WindowUpdate 解碼，主線程不等磁碟。</summary>
        private string _readingScreenshot;
        private Task<byte[]> _screenshotRead;
        // 每個 IMGUI 事件都會重畫；帶參數的翻譯字串只在值變了才重組，免得每幀配置一堆字串。
        private readonly TextCache<long?> _tokenLabel = new TextCache<long?>();
        private readonly TextCache<AgentMode> _modeLabel = new TextCache<AgentMode>();
        private readonly TextCache<ReasoningLevel> _reasoningLabel = new TextCache<ReasoningLevel>();
        private TextCache<string> _modelLabel = new TextCache<string>();
        private float _modelLabelWidth = -1f;
        private readonly TextCache<int> _autoUsageLabel = new TextCache<int>();
        private readonly TextCache<int> _workingLabel = new TextCache<int>();
        private readonly TextCache<bool> _jumpLabel = new TextCache<bool>();
        private string _toolToggleTip;
        private float _workingHeight;
        private float _workingHeightWidth = -1f;
        private float _settlingWidth = -1f;
        private int _settlingSince;
        private float _linesTotal;
        private float _linesWidth = -1f;
        private int _linesEntryCount = -1;
        private int _linesTranscriptVersion = -1;
        private int _linesTranscriptEpoch = -1;
        private AgentTranscript _linesTranscript;
        private readonly HashSet<int> _combatNoted = new HashSet<int>();
        private string _capabilityWarning;
        private CancellationTokenSource _cts;
        private int _slashIndex;
        private bool _posInitialized;
        // 發射後不管的請求：async 方法的 throw 變成 faulted Task，同步 try/catch 抓不到，
        // 這裡在主線程逐幀收割，faulted 才補忙碌提示（Translate 只能在主線程呼叫）。
        private Task _observedTask;
        private ColonyAgentSession _observedSession;

        protected override float Margin => 0f;

        public override Vector2 InitialSize => RimAgentMod.Settings != null
            ? RimAgentMod.Settings.WindowSize
            : new Vector2(RimAgentSettings.DefaultWindowWidth, RimAgentSettings.DefaultWindowHeight);

        public Window_Agent()
        {
            layer = WindowLayer.GameUI;
            draggable = true;
            resizeable = true;
            doWindowBackground = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            closeOnAccept = false;
            closeOnCancel = false;
            preventCameraMotion = false;
            soundAppear = null;
            soundClose = SoundDefOf.TabClose;
        }

        public static bool IsOpenNow => Find.WindowStack != null && Find.WindowStack.IsOpen<Window_Agent>();

        public static void Toggle()
        {
            Window_Agent existing = Find.WindowStack.WindowOfType<Window_Agent>();
            if (existing != null)
            {
                existing.Close();
            }
            else
            {
                Find.WindowStack.Add(new Window_Agent());
            }
        }

        public override void PreOpen()
        {
            base.PreOpen();
            ProbeCapabilities();
        }

        protected override void SetInitialSizeAndPosition()
        {
            RimAgentSettings settings = RimAgentMod.Settings;
            windowRect = settings != null
                ? settings.GetWindowBounds(Verse.UI.screenWidth, Verse.UI.screenHeight)
                : new Rect(10f, 40f, RimAgentSettings.DefaultWindowWidth, RimAgentSettings.DefaultWindowHeight);
            _posInitialized = true;
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            DrainObservedTask();
            LoadPendingScreenshot();
            if (!_posInitialized) return;

            // 小於可讀尺寸時保留玩家正在調整的大小，不跳回預設寬高。
            windowRect.width = Mathf.Max(RimAgentSettings.MinWindowWidth, windowRect.width);
            windowRect.height = Mathf.Max(RimAgentSettings.MinWindowHeight, windowRect.height);

            // 確保視窗至少有標題列在螢幕可見區域內
            float minX = -(windowRect.width - 60f);
            float maxX = Verse.UI.screenWidth - 40f;
            float minY = 0f;
            float maxY = Verse.UI.screenHeight - 32f;

            float clampedX = Mathf.Clamp(windowRect.x, minX, maxX);
            float clampedY = Mathf.Clamp(windowRect.y, minY, maxY);

            if (Mathf.Abs(windowRect.x - clampedX) > 0.5f || Mathf.Abs(windowRect.y - clampedY) > 0.5f)
            {
                windowRect.x = clampedX;
                windowRect.y = clampedY;
            }

            RimAgentMod.Settings?.UpdateBounds(windowRect);
        }

        public override void PostClose()
        {
            base.PostClose();
            foreach (Texture2D texture in _screenshotTextures.Values) UnityEngine.Object.Destroy(texture);
            _screenshotTextures.Clear();
            _screenshotOrder.Clear();
            _screenshotFailedAt.Clear();
            _pendingScreenshot = null;
            _readingScreenshot = null;
            _screenshotRead = null;
            if (RimAgentMod.Settings != null)
            {
                RimAgentMod.Settings.UpdateBounds(windowRect);
                RimAgentMod.Settings.Write();
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            DrawCustomWindowFrame(inRect);

            RimAgentGameComponent component = RimAgentGameComponent.Instance;
            if (component == null || Find.CurrentMap == null)
            {
                var emptyRect = inRect.ContractedBy(12f);
                GUI.color = TitleColor;
                Text.Font = GameFont.Small;
                Widgets.Label(emptyRect, "RimAgent.NoGame".Translate());
                GUI.color = Color.white;
                return;
            }

            ColonyAgentSession session = component.Session;

            // 標題列（RimAgent）與關閉鍵
            var titleRect = new Rect(inRect.x, inRect.y, inRect.width, TitleBarHeight);
            DrawTitleBar(titleRect, session);

            // 內部內容區（縮進 10px 呼吸空間）
            var contentRect = new Rect(inRect.x + 10f, inRect.y + TitleBarHeight + 2f, inRect.width - 20f, inRect.height - TitleBarHeight - 12f);

            float top = contentRect.y;
            if (_capabilityWarning != null)
            {
                float warnHeight = Text.CalcHeight(_capabilityWarning, contentRect.width - 8f) + 8f;
                var banner = new Rect(contentRect.x, top, contentRect.width, warnHeight);
                Widgets.DrawBoxSolid(banner, new Color(0.45f, 0.12f, 0.12f, 0.75f));
                DrawThinBox(banner, EmergencyColor);
                GUI.color = EmergencyColor;
                Widgets.Label(banner.ContractedBy(4f), _capabilityWarning);
                GUI.color = Color.white;
                top += warnHeight + EntryGap;
            }

            var bottom = new Rect(contentRect.x, contentRect.yMax - BottomBarHeight, contentRect.width, BottomBarHeight);
            var autoStrip = new Rect(contentRect.x, bottom.y - AutoStripHeight - EntryGap, contentRect.width, AutoStripHeight);
            var transcriptRect = new Rect(contentRect.x, top, contentRect.width, autoStrip.y - top - EntryGap);

            // 繪製日誌、狀態條、底部輸入列與斜線選單
            DrawTranscript(transcriptRect, session);
            DrawStatusStrip(autoStrip, component, session);
            DrawBottomBar(bottom, session);
            DrawSlashPopup(bottom, session);
            DrawResizeHint(inRect);
        }

        private static void DrawResizeHint(Rect inRect)
        {
            var grip = new Rect(inRect.xMax - 17f, inRect.yMax - 18f, 14f, 14f);
            GUI.color = ControlBorderColor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(grip, "◢");
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            TooltipHandler.TipRegion(grip, "RimAgent.Window.ResizeTip".Translate());
        }

        private static void DrawCustomWindowFrame(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, BgColor);
            DrawThinBox(inRect, BorderColor);
        }

        private static void DrawThinBox(Rect rect, Color color)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            Widgets.DrawBox(rect, 1);
            GUI.color = oldColor;
        }

        private void DrawTitleBar(Rect titleRect, ColonyAgentSession session)
        {
            var bgRect = new Rect(titleRect.x + 1f, titleRect.y + 1f, titleRect.width - 2f, titleRect.height - 1f);
            Widgets.DrawBoxSolid(bgRect, new Color(0.08f, 0.11f, 0.14f, 0.6f));

            var labelRect = new Rect(titleRect.x + 12f, titleRect.y + 4f, titleRect.width - 250f, titleRect.height - 4f);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = TitleColor;
            Widgets.Label(labelRect, "RimAgent.WindowTitle".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            // 只顯示供應商實際回報的最近一次主模型輸入 token；
            // 訊息數與折疊門檻放在提示中，避免把訊息數誤標成 token 或視窗容量。
            ColonyAgentSession.ContextStatus status = session.GetContextStatus();
            var contextRect = new Rect(titleRect.xMax - 228f, titleRect.y + 4f, 196f, titleRect.height - 4f);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = status.LastInputTokens.HasValue ? ToolTextColor : ControlBorderColor;
            string tokenLabel = _tokenLabel.Get(status.LastInputTokens, tokens => tokens.HasValue
                ? "RimAgent.Context.InputTokens".Translate(tokens.Value.ToString("N0")).ToString()
                : "RimAgent.Context.Unavailable".Translate().ToString());
            Widgets.Label(contextRect, tokenLabel);
            // 提示字串只在滑鼠停在上面時才組。
            if (Mouse.IsOver(contextRect))
            {
                string compact = status.AutoCompactEnabled
                    ? "RimAgent.Context.CompactOn".Translate(status.Messages, status.Threshold)
                    : "RimAgent.Context.CompactOff".Translate(status.Messages);
                string window = status.ContextWindow.HasValue
                    ? "RimAgent.Context.WindowKnown".Translate(status.ContextWindow.Value.ToString("N0"))
                    : "RimAgent.Context.WindowUnknown".Translate();
                TooltipHandler.TipRegion(contextRect, "RimAgent.Context.Tooltip".Translate(window, compact));
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;

            var closeRect = new Rect(titleRect.xMax - 26f, titleRect.y + 4f, 20f, 20f);
            if (Mouse.IsOver(closeRect))
            {
                Widgets.DrawBoxSolid(closeRect, CloseButHoverColor);
            }
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Mouse.IsOver(closeRect) ? EmergencyColor : BorderColor;
            Widgets.Label(closeRect, "✕");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(closeRect))
            {
                Close();
            }
        }

        private void DrawTranscript(Rect outRect, ColonyAgentSession session)
        {
            float width = outRect.width - 16f;
            AgentTranscript transcript = session.Transcript;
            bool sameTranscript = ReferenceEquals(transcript, _linesTranscript);
            bool widthChanged = Mathf.Abs(width - _linesWidth) > 0.001f;
            // 拖曳縮放時寬度每幀都在變，全量重排（每行 CalcHeight＋markdown）會一路卡到放開；
            // 寬度穩定 ReflowDelayMs 之後才重排，期間照舊寬度排版（行高才對得上）。有新內容時照常重排，不擋新回覆。
            bool deferReflow = widthChanged && sameTranscript && transcript.Version == _linesTranscriptVersion
                               && _linesWidth > 0f && !WidthSettled(width);
            if (!deferReflow && (!sameTranscript || transcript.Version != _linesTranscriptVersion || widthChanged))
            {
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

            float lineWidth = deferReflow ? _linesWidth : width;
            float total = _linesTotal;
            string working = null;
            float workingHeight = 0f;
            if (session.IsBusy)
            {
                // 執行中的省略號做 . → .. → ... 循環動畫（每 0.5 秒一格）；
                // 翻譯字尾的 … 先去掉再補動畫點，中英語言檔都不必改。
                // 用 Environment.TickCount 而不用 Unity Time：BCL 在測試與遊戲內都能 JIT。
                working = _workingLabel.Get(WorkingPhase(), phase => "<color=#" + PauseHex + ">"
                    + "RimAgent.Working".Translate().ToString().TrimEnd('…', '.', ' ') + WorkingDots(phase) + "</color>");
                // 一行字，高度只隨寬度變。
                if (Mathf.Abs(width - _workingHeightWidth) > 0.001f)
                {
                    _workingHeight = Text.CalcHeight(working, width);
                    _workingHeightWidth = width;
                }
                workingHeight = _workingHeight;
                total += workingHeight + EntryGap;
            }

            // 只有原本就停在底部（或剛送出指令）才跟著新內容捲到底；往上翻舊紀錄時不被拉回去。
            // 設成確切的最大值而非 float.MaxValue：捲軸狀態會記住這個值，太大時滾輪往上的位移會被吃掉。
            var viewRect = new Rect(0f, 0f, width, Math.Max(total, outRect.height));
            float maxScroll = viewRect.height - outRect.height;
            if (_scrollToBottom || _scroll.y >= _lastMaxScroll - StickToBottomSlack)
            {
                _scroll.y = maxScroll;
                _scrollToBottom = false;
                _seenVersion = _linesTranscriptVersion;
            }
            _lastMaxScroll = maxScroll;
            float visibleTop = Mathf.Clamp(_scroll.y, 0f, maxScroll);
            float visibleBottom = visibleTop + outRect.height;

            bool showJump = _scroll.y < maxScroll - StickToBottomSlack;
            var jumpRect = new Rect(outRect.center.x - JumpButtonWidth / 2f, outRect.yMax - JumpButtonHeight - 6f, JumpButtonWidth, JumpButtonHeight);
            // 捲動區內的座標系不同，先在外面判斷滑鼠是否壓在跳底按鈕上，免得同一下點擊也展開底下的工具列。
            bool mouseOnJump = showJump && Mouse.IsOver(jumpRect);

            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float y = 0f;
            TranscriptEntry clickedTool = null;
            foreach (TranscriptLine line in _lines)
            {
                if (line.TurnStart)
                {
                    if (y + TurnGap >= visibleTop && y <= visibleBottom)
                    {
                        Widgets.DrawBoxSolid(new Rect(0f, y + TurnGap / 2f, lineWidth, 1f), DividerColor);
                    }
                    y += TurnGap;
                }
                if (y + line.Height >= visibleTop && y <= visibleBottom)
                {
                    var lineRect = new Rect(0f, y, lineWidth, line.Height);
                    switch (line.Kind)
                    {
                        case LineKind.Screenshot:
                            DrawScreenshotPreview(lineRect, line.ImagePath);
                            break;
                        case LineKind.PlayerBubble:
                            // 縮窄視窗、延後重排期間 lineWidth 仍是舊寬度；氣泡貼齊目前可見的右緣，不被推出畫面。
                            float bubbleRight = Mathf.Min(lineRect.xMax, width);
                            var bubble = new Rect(Mathf.Max(0f, bubbleRight - line.BubbleWidth), y, line.BubbleWidth, line.Height);
                            // Unity 內建圓角：borderWidth 0 是實心填滿，1 是外框。
                            GUI.DrawTexture(bubble, BaseContent.WhiteTex, ScaleMode.StretchToFill, true, 0f, BubbleColor, 0f, BubbleRadius);
                            GUI.DrawTexture(bubble, BaseContent.WhiteTex, ScaleMode.StretchToFill, true, 0f, BubbleBorderColor, 1f, BubbleRadius);
                            DrawSelectableLine(bubble.ContractedBy(BubblePadX, BubblePadY), line.Text, line.PlainText);
                            break;
                        case LineKind.ToolHeader:
                            if (!mouseOnJump)
                            {
                                CopyToolHeaderOnRightClick(lineRect, line.PlainText);
                                if (Mouse.IsOver(lineRect))
                                {
                                    Widgets.DrawHighlight(lineRect);
                                    if (_toolToggleTip == null) _toolToggleTip = "RimAgent.Tool.ToggleTip".Translate();
                                    TooltipHandler.TipRegion(lineRect, _toolToggleTip);
                                }
                                if (Widgets.ButtonInvisible(lineRect)) clickedTool = line.Entry;
                            }
                            Widgets.Label(lineRect, line.Text);
                            break;
                        default:
                            DrawSelectableLine(lineRect, line.Text, line.PlainText);
                            break;
                    }
                }
                y += line.Height + EntryGap;
            }
            if (working != null)
            {
                Widgets.Label(new Rect(0f, y, width, workingHeight), working);
            }
            GUI.color = Color.white;
            Widgets.EndScrollView();

            if (clickedTool != null)
            {
                _toolExpanded[clickedTool] = !IsToolExpanded(clickedTool);
                RelayoutEntry(clickedTool);
            }

            if (showJump)
            {
                bool unseen = _linesTranscriptVersion != _seenVersion;
                string jumpLabel = _jumpLabel.Get(unseen, u => (u ? "RimAgent.Scroll.NewMessages" : "RimAgent.Scroll.Latest").Translate().ToString());
                Color jumpColor = unseen ? NoticeColor : TitleColor;
                if (DrawUnifiedButton(jumpRect, jumpLabel, jumpColor, jumpColor, true, GameFont.Tiny)) _scrollToBottom = true;
            }
        }

        /// <summary>寬度是否已經停在同一個值超過 <see cref="ReflowDelayMs"/>（用 TickCount：BCL 在測試與遊戲內都能 JIT）。</summary>
        private bool WidthSettled(float width)
        {
            int now = Environment.TickCount;
            if (Mathf.Abs(width - _settlingWidth) > 0.001f)
            {
                _settlingWidth = width;
                _settlingSince = now;
                return false;
            }
            return unchecked(now - _settlingSince) >= ReflowDelayMs;
        }

        /// <summary>框選層樣式快取（按字體）：平時文字透明、只看到底下的彩色 Label；取得焦點才顯示純文字，複製不到顏色標籤。</summary>
        private static readonly Dictionary<GameFont, GUIStyle> SelectableStyles = new Dictionary<GameFont, GUIStyle>();

        /// <summary>
        /// Labels 不能框選，TextArea 會把顏色標籤一起複製；所以彩色 Label 照常顯示，
        /// 上面再疊一層同字體同排版、文字平時透明的 TextArea 專門給滑鼠拖選＋Ctrl+C。
        /// 底色沿用 Label（透明），捲動、行高、換行都與量測時一致。
        /// Unity 原生呼叫集中在這一個方法（RuntimeCompatibilityTests 逐一登記這類方法）。
        /// </summary>
        private static void DrawSelectableLine(Rect rect, string richText, string plainText)
        {
            Widgets.Label(rect, richText);
            // 唯讀框選層不需要文字游標：焦點在它身上時 Unity 會畫一條閃爍豎線（如氣泡尾端的游標），
            // 把全域游標色暫時設透明即可隱藏；選取反白不受影響，畫完立刻還原，後面畫的輸入框游標正常。
            Color oldCursor = GUI.skin.settings.cursorColor;
            GUI.skin.settings.cursorColor = Color.clear;
            GUI.TextArea(rect, plainText ?? string.Empty, GetSelectableStyle());
            GUI.skin.settings.cursorColor = oldCursor;
        }

        private static GUIStyle GetSelectableStyle()
        {
            GameFont font = Text.Font;
            if (!SelectableStyles.TryGetValue(font, out GUIStyle style) || style == null)
            {
                style = new GUIStyle(Text.CurFontStyle)
                {
                    richText = false,
                    wordWrap = true
                };
                Color plain = new Color(0.90f, 0.93f, 0.96f);
                // GUIStyleState 是參考型別，收進陣列迴圈賦值仍會寫回樣式本體。
                foreach (GUIStyleState state in new[] { style.normal, style.hover, style.onNormal, style.onHover })
                {
                    state.textColor = Color.clear;
                }
                foreach (GUIStyleState state in new[] { style.active, style.focused, style.onActive, style.onFocused })
                {
                    state.textColor = plain;
                }
                SelectableStyles[font] = style;
            }
            return style;
        }

        /// <summary>
        /// 工具摘要列沒有框選層（左鍵要留給展開／收合）：右鍵直接複製該列純文字。
        /// Unity 原生呼叫集中在這一個方法（RuntimeCompatibilityTests 逐一登記這類方法）。
        /// </summary>
        private static void CopyToolHeaderOnRightClick(Rect rect, string plainText)
        {
            if (Event.current.type != EventType.MouseDown || Event.current.button != 1) return;
            if (!Mouse.IsOver(rect)) return;
            GUIUtility.systemCopyBuffer = plainText ?? string.Empty;
            Event.current.Use();
        }

        private void DrawScreenshotPreview(Rect rect, string path)
        {
            Widgets.DrawBoxSolid(rect, ControlBgColor);
            DrawThinBox(rect, ControlBorderColor);
            Texture2D texture = GetScreenshotTexture(path);
            if (texture != null)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(rect.ContractedBy(4f), texture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.color = ToolTextColor;
                Widgets.Label(rect.ContractedBy(8f), "RimAgent.Screenshot.Pending".Translate());
            }
            GUI.color = Color.white;
        }

        /// <summary>繪製時只查快取；還沒載入的記下來，交給 <see cref="LoadPendingScreenshot"/> 在 WindowUpdate 載入。</summary>
        private Texture2D GetScreenshotTexture(string path)
        {
            if (_screenshotTextures.TryGetValue(path, out Texture2D cached))
            {
                // 用到的移到最後：看得到的截圖永遠是最近用過的，淘汰不會輪到它們。
                // 比較方式要與字典一致（不分大小寫），否則同一張圖會在清單裡重複。
                int last = _screenshotOrder.Count - 1;
                if (!string.Equals(_screenshotOrder[last], path, StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < last; i++)
                    {
                        if (!string.Equals(_screenshotOrder[i], path, StringComparison.OrdinalIgnoreCase)) continue;
                        string key = _screenshotOrder[i];
                        _screenshotOrder.RemoveAt(i);
                        _screenshotOrder.Add(key);
                        break;
                    }
                }
                return cached;
            }
            if (!_screenshotFailedAt.TryGetValue(path, out int failedAt)
                || unchecked(Environment.TickCount - failedAt) >= ScreenshotRetryMs)
            {
                _pendingScreenshot = path;
            }
            return null;
        }

        /// <summary>
        /// 每幀最多載入一張截圖（在 WindowUpdate，不在 OnGUI 裡解碼）：先在背景讀檔，讀完才在主線程解碼，
        /// 再用 GPU 逐次減半縮到 <see cref="MaxScreenshotWidth"/>（一次大倍率縮會嚴重鋸齒），
        /// 只留縮圖且丟掉 CPU 端副本，原圖立即釋放。
        /// 檔案還不在或讀不了就隔 <see cref="ScreenshotRetryMs"/> 再試，不每幀打磁碟。
        /// Unity 原生呼叫集中在這一個方法（RuntimeCompatibilityTests 逐一登記這類方法）。
        /// </summary>
        private void LoadPendingScreenshot()
        {
            if (_screenshotRead == null)
            {
                string pending = _pendingScreenshot;
                _pendingScreenshot = null;
                if (pending == null || _screenshotTextures.ContainsKey(pending)) return;
                _readingScreenshot = pending;
                _screenshotRead = Task.Run(() => File.Exists(pending) ? File.ReadAllBytes(pending) : null);
                return;
            }
            if (!_screenshotRead.IsCompleted) return;
            string path = _readingScreenshot;
            // 讀檔失敗（IO、權限）時 Task 為 faulted，當作讀不到。
            byte[] bytes = _screenshotRead.Status == TaskStatus.RanToCompletion ? _screenshotRead.Result : null;
            _screenshotRead = null;
            _readingScreenshot = null;

            Texture2D preview = null;
            Texture2D full = null;
            RenderTexture previous = RenderTexture.active;
            RenderTexture step = null;
            try
            {
                full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (bytes != null && full.LoadImage(bytes))
                {
                    if (full.width <= MaxScreenshotWidth)
                    {
                        // 原圖已經夠小：直接用，只丟掉 CPU 端副本。
                        preview = full;
                        full = null;
                        preview.Apply(false, true);
                    }
                    else
                    {
                        Texture source = full;
                        int width = full.width;
                        int height;
                        do
                        {
                            // 每次最多縮一半：雙線性取樣剛好涵蓋 2×2 來源像素，不會跳過細節。
                            width = Mathf.Max(MaxScreenshotWidth, width / 2);
                            height = Mathf.Max(1, Mathf.RoundToInt(full.height * (width / (float)full.width)));
                            RenderTexture next = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                            Graphics.Blit(source, next);
                            if (step != null) RenderTexture.ReleaseTemporary(step);
                            step = next;
                            source = next;
                        }
                        while (width > MaxScreenshotWidth);
                        RenderTexture.active = step;
                        preview = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        preview.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                        preview.Apply(false, true);
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is UnityException)
            {
                if (preview != null) UnityEngine.Object.Destroy(preview);
                preview = null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (step != null) RenderTexture.ReleaseTemporary(step);
                if (full != null) UnityEngine.Object.Destroy(full);
            }

            if (preview == null)
            {
                _screenshotFailedAt[path] = Environment.TickCount;
                return;
            }
            _screenshotFailedAt.Remove(path);
            _screenshotTextures[path] = preview;
            _screenshotOrder.Add(path);
            while (_screenshotOrder.Count > MaxCachedScreenshots)
            {
                string oldest = _screenshotOrder[0];
                _screenshotOrder.RemoveAt(0);
                UnityEngine.Object.Destroy(_screenshotTextures[oldest]);
                _screenshotTextures.Remove(oldest);
            }
        }

        private static int WorkingPhase()
        {
            // TickCount 會正負迴繞，先取正數再算格子。
            return ((System.Environment.TickCount & int.MaxValue) / 500) % 3;
        }

        /// <summary>執行中動畫：. → .. → ... 循環，每 0.5 秒換一格。</summary>
        private static string WorkingDots(int phase)
        {
            switch (phase)
            {
                case 1: return "..";
                case 2: return "...";
                default: return ".";
            }
        }

        private void RebuildLines(IReadOnlyList<TranscriptEntry> entries, float width)
        {
            _lines.Clear();
            _linesTotal = 0f;
            _combatNoted.Clear();
            PruneToolExpanded(entries);
            AppendLines(entries, 0, width);
        }

        /// <summary>丟掉已經不在逐字稿裡（/clear、裁掉舊紀錄、換存檔）的展開狀態，免得一直抓著舊紀錄。</summary>
        private void PruneToolExpanded(IReadOnlyList<TranscriptEntry> entries)
        {
            if (_toolExpanded.Count == 0) return;
            var live = new HashSet<TranscriptEntry>(entries);
            var stale = new List<TranscriptEntry>();
            foreach (TranscriptEntry entry in _toolExpanded.Keys)
            {
                if (!live.Contains(entry)) stale.Add(entry);
            }
            foreach (TranscriptEntry entry in stale) _toolExpanded.Remove(entry);
        }

        private void AppendLines(IReadOnlyList<TranscriptEntry> entries, int from, float width)
        {
            for (int i = Math.Max(0, from); i < entries.Count; i++)
            {
                TranscriptEntry entry = entries[i];
                // 每句 User 紀錄開啟新回合；第一行上面不畫分隔線。
                bool turnStart = entry.Kind == TranscriptKind.User && _lines.Count > 0;
                _linesTotal += BuildEntryLines(entry, width, turnStart, _lines);
            }
            _linesEntryCount = entries.Count;
        }

        /// <summary>把一筆紀錄排成若干行加到 <paramref name="into"/>，回傳佔用的總高度（含間距）。</summary>
        private float BuildEntryLines(TranscriptEntry entry, float width, bool turnStart, List<TranscriptLine> into)
        {
            float total = 0f;
            foreach ((string text, LineKind kind) in Render(entry, IsToolExpanded(entry), _combatNoted))
            {
                var line = new TranscriptLine { Kind = kind, Text = text, Entry = entry, TurnStart = turnStart };
                line.PlainText = text.StripTags();
                if (kind == LineKind.PlayerBubble)
                {
                    // 短句貼齊字寬，長句到上限就換行。CalcSize 不換行，量到的是最長一行的寬度。
                    float maxInner = width * BubbleMaxRatio - BubblePadX * 2f;
                    float inner = Mathf.Min(Mathf.Ceil(Text.CalcSize(text).x) + 1f, maxInner);
                    line.BubbleWidth = inner + BubblePadX * 2f;
                    line.Height = Text.CalcHeight(text, inner) + BubblePadY * 2f;
                }
                else
                {
                    line.Height = Text.CalcHeight(text, width);
                }
                into.Add(line);
                total += line.Height + EntryGap + (turnStart ? TurnGap : 0f);
                turnStart = false;
            }
            string screenshotPath = TranscriptScreenshot.TryGetPath(entry, GenFilePaths.ScreenshotFolderPath);
            if (screenshotPath != null)
            {
                float imageHeight = Mathf.Clamp(width * 0.5625f, 120f, 320f);
                into.Add(new TranscriptLine { Kind = LineKind.Screenshot, Height = imageHeight, ImagePath = screenshotPath, Entry = entry });
                total += imageHeight + EntryGap;
            }
            return total;
        }

        /// <summary>只重排一筆紀錄的行（展開／收合工具時），不必整份逐字稿重算 CalcHeight 與 markdown。</summary>
        private void RelayoutEntry(TranscriptEntry entry)
        {
            int start = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (ReferenceEquals(_lines[i].Entry, entry)) { start = i; break; }
            }
            if (start < 0) return;

            int end = start;
            float oldHeight = 0f;
            bool hadCombatNote = false;
            while (end < _lines.Count && ReferenceEquals(_lines[end].Entry, entry))
            {
                TranscriptLine old = _lines[end];
                oldHeight += old.Height + EntryGap + (old.TurnStart ? TurnGap : 0f);
                hadCombatNote |= old.Kind == LineKind.CombatNote;
                end++;
            }
            // 這一筆原本就是該句指令顯示戰鬥提醒的那筆：先讓出去，重排時才會再出現。
            if (hadCombatNote) _combatNoted.Remove(entry.InstructionId);

            var fresh = new List<TranscriptLine>();
            float newHeight = BuildEntryLines(entry, _linesWidth, _lines[start].TurnStart, fresh);
            _lines.RemoveRange(start, end - start);
            _lines.InsertRange(start, fresh);
            _linesTotal += newHeight - oldHeight;
        }

        /// <summary>工具紀錄預設收合，只有等待確認的預覽預設展開（玩家要看得到確認的是什麼）；玩家親手設定過的照玩家的。</summary>
        private bool IsToolExpanded(TranscriptEntry entry)
        {
            if (entry.Kind != TranscriptKind.Tool) return false;
            if (_toolExpanded.TryGetValue(entry, out bool expanded)) return expanded;
            return entry.Result.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// 雙色分離渲染條目：採用 Unity Rich Text 標籤，達成「高亮彩色前綴標籤 + 柔和淺白內文」的圖片設計層次。
        /// </summary>
        private static IEnumerable<(string Text, LineKind Kind)> Render(TranscriptEntry entry, bool toolExpanded, HashSet<int> combatNoted)
        {
            switch (entry.Kind)
            {
                case TranscriptKind.User:
                    if (entry.GoalRun)
                    {
                        string goalTag = "<color=#" + NoticeHex + ">" + "RimAgent.Tag.Goal".Translate() + "</color> ";
                        yield return (goalTag + "<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>", LineKind.Text);
                    }
                    else if (entry.Autonomous)
                    {
                        string autoTag = "<color=#" + AutoActionHex + ">" + "RimAgent.Tag.AutoAction".Translate() + "</color> ";
                        string body = "<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>";
                        yield return (autoTag + body, LineKind.Text);
                    }
                    else
                    {
                        // 靠右的氣泡本身就標明是玩家說的，不再加標籤。
                        yield return ("<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>", LineKind.PlayerBubble);
                    }
                    break;

                case TranscriptKind.Assistant:
                    foreach (var line in RenderAssistant(entry)) yield return (line, LineKind.Text);
                    break;

                case TranscriptKind.Tool:
                    foreach (var line in RenderTool(entry, toolExpanded, combatNoted)) yield return line;
                    break;

                case TranscriptKind.Error:
                    string errTag = "<color=#" + EmergencyHex + ">" + "RimAgent.Tag.EmergencyStop".Translate() + "</color> ";
                    string errBody = "<color=#" + EmergencyHex + ">" + EscapeRichText(DescribeError(entry)) + "</color>";
                    yield return (errTag + errBody, LineKind.Text);
                    break;

                default:
                    // Notice 條目：檢驗是否為暫停、告警或提醒
                    bool isPauseOrAlert = ContainsAny(entry.Text, "暫停", "暂停", "pause", "告警", "警告", "warn", "alert");
                    string noticeTag = isPauseOrAlert
                        ? "<color=#" + PauseHex + ">" + "RimAgent.Tag.AutoPause".Translate().ToString() + "</color> "
                        : "<color=#" + NoticeHex + ">" + "RimAgent.Tag.Notice".Translate().ToString() + "</color> ";
                    string noticeBody = "<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>";
                    yield return (noticeTag + noticeBody, LineKind.Text);
                    break;
            }
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (text.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static IEnumerable<string> RenderAssistant(TranscriptEntry entry)
        {
            string reply = RenderReply(EscapeRichText(entry.Text));
            string replyText = reply.Length == 0 ? "RimAgent.EmptyReply".Translate().ToString() : reply;
            string agentTag = "<color=#" + AutoActionHex + ">" + "RimAgent.Tag.Agent".Translate() + "</color> ";
            yield return agentTag + "<color=#" + TextHex + ">" + replyText + "</color>";
            if (entry.ToolsStripped)
            {
                yield return "<color=#" + NoticeHex + ">" + "RimAgent.ToolsStripped".Translate() + "</color>";
            }
        }

        /// <summary>摘要列（▸／▾、工具名、風險判定；失敗時工具名轉紅）一定顯示，參數與結果只在展開時顯示。</summary>
        private static IEnumerable<(string Text, LineKind Kind)> RenderTool(TranscriptEntry entry, bool expanded, HashSet<int> combatNoted)
        {
            string arrow = "<color=#" + SubTextHex + ">" + (expanded ? "▾" : "▸") + "</color> ";
            string tag = "<color=#" + AutoActionHex + ">" + "RimAgent.Tag.Tool".Translate() + "</color> ";
            string call = arrow + tag + "<color=#" + (entry.Failed ? EmergencyHex : TextHex) + ">" + EscapeRichText(entry.ToolName) + "</color>";
            if (entry.RiskCheck.Length > 0)
            {
                call += " <color=#" + (entry.Failed ? EmergencyHex : NoticeHex) + ">["
                    + RiskLabel(entry.RiskCheck) + "]</color>";
            }
            yield return (call, LineKind.ToolHeader);
            if (expanded)
            {
                if (!string.IsNullOrEmpty(entry.Arguments))
                {
                    yield return ("<color=#" + SubTextHex + ">" + "RimAgent.Tool.Arguments".Translate()
                        + " " + EscapeRichText(entry.Arguments) + "</color>", LineKind.Text);
                }

                string resultStr = entry.Result.Length > MaxResultChars ? entry.Result.Substring(0, MaxResultChars) + "…" : entry.Result;
                string resColorHex = entry.Failed ? EmergencyHex : SubTextHex;
                yield return ("<color=#" + resColorHex + ">" + "RimAgent.Tool.Result".Translate()
                    + " " + EscapeRichText(resultStr) + "</color>", LineKind.Text);
            }

            if (entry.ToolCategory == ToolCategory.Combat && combatNoted.Add(entry.InstructionId))
            {
                string combatNote = "<color=#" + NoticeHex + ">" + "RimAgent.CombatLatencyNote".Translate() + "</color>";
                yield return (combatNote, LineKind.CombatNote);
            }
        }

        private static string RiskLabel(string risk)
        {
            switch (risk)
            {
                case "low": return "RimAgent.Risk.Low".Translate();
                case "high": return "RimAgent.Risk.High".Translate();
                case "unrequested": return "RimAgent.Risk.Unrequested".Translate();
                case "unavailable": return "RimAgent.Risk.Unavailable".Translate();
                default: return EscapeRichText(risk);
            }
        }

        private static string EscapeRichText(string value)
        {
            return (value ?? string.Empty).Replace('<', '＜').Replace('>', '＞');
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

        private void DrawStatusStrip(Rect rect, RimAgentGameComponent component, ColonyAgentSession session)
        {
            RimAgentSettings settings = RimAgentMod.Settings;
            var modeRect = new Rect(rect.x, rect.y, DropdownWidth, rect.height);
            var reasoningRect = new Rect(modeRect.xMax + 6f, rect.y, DropdownWidth, rect.height);

            bool enabled = !session.IsBusy && settings != null;
            string modeStatus = _modeLabel.Get(session.Mode, mode => "RimAgent.Mode.Status".Translate(ModeLabel(mode)).ToString());
            if (DrawUnifiedButton(modeRect, modeStatus, ControlBorderColor, TitleColor, enabled, GameFont.Tiny))
            {
                var options = new List<FloatMenuOption>();
                foreach (AgentMode mode in (AgentMode[])Enum.GetValues(typeof(AgentMode)))
                {
                    options.Add(new FloatMenuOption(ModeLabel(mode), () => SetMode(settings, mode))
                    {
                        tooltip = new TipSignal(ModeDescription(mode))
                    });
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            // 提示字串只在滑鼠停在上面時才組。
            if (Mouse.IsOver(modeRect)) TooltipHandler.TipRegion(modeRect, ModeDescription(session.Mode));

            ReasoningLevel current = settings != null ? settings.reasoning : ReasoningLevel.Auto;
            string reasoningStatus = _reasoningLabel.Get(current, level => "RimAgent.Reasoning.Status".Translate(ReasoningLabel(level)).ToString());
            if (DrawUnifiedButton(reasoningRect, reasoningStatus, ControlBorderColor, TitleColor, enabled, GameFont.Tiny))
            {
                var options = new List<FloatMenuOption>();
                foreach (ReasoningLevel level in (ReasoningLevel[])Enum.GetValues(typeof(ReasoningLevel)))
                {
                    options.Add(new FloatMenuOption(ReasoningLabel(level), () => SetReasoning(settings, level)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            var modelRect = new Rect(reasoningRect.xMax + 6f, rect.y, Mathf.Clamp(rect.xMax - reasoningRect.xMax - 6f, 0f, ModelDropdownWidth), rect.height);
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
                    new FloatMenuOption("RimAgent.Model.Default".Translate(), () => SetModel(settings, string.Empty))
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
            if (Mouse.IsOver(modelRect))
            {
                TooltipHandler.TipRegion(modelRect, string.IsNullOrWhiteSpace(model)
                    ? "RimAgent.Model.DefaultTip".Translate()
                    : "RimAgent.Model.Tip".Translate(model.Trim()));
            }

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

        /// <summary>
        /// 統一的扁平科技風按鈕繪製，消除重複代碼。
        /// </summary>
        private static bool DrawUnifiedButton(Rect rect, string label, Color borderColor, Color textColor, bool enabled, GameFont font = GameFont.Small)
        {
            Color bg = enabled ? ControlBgColor : new Color(0.05f, 0.07f, 0.09f, 0.5f);
            Widgets.DrawBoxSolid(rect, bg);
            DrawThinBox(rect, enabled ? borderColor : ControlBorderColor);

            if (Mouse.IsOver(rect) && enabled)
            {
                Widgets.DrawHighlight(rect);
            }

            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = font;
            GUI.color = enabled ? textColor : new Color(0.45f, 0.45f, 0.45f);
            Widgets.Label(rect, label);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            return enabled && Widgets.ButtonInvisible(rect);
        }

        private static string ModeLabel(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask: return "RimAgent.Settings.Mode.Ask".Translate();
                case AgentMode.Confirm: return "RimAgent.Settings.Mode.Confirm".Translate();
                case AgentMode.Yolo: return "RimAgent.Settings.Mode.Yolo".Translate();
                case AgentMode.Auto: return "RimAgent.Settings.Mode.Auto".Translate();
                default: return "RimAgent.Settings.Mode.Autonomous".Translate();
            }
        }

        /// <summary>模式說明（含 YOLO／自主的風險提醒）；設定頁不再列模式，改在下拉選單與按鈕的提示顯示。</summary>
        private static string ModeDescription(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask: return "RimAgent.Settings.Mode.AskDesc".Translate();
                case AgentMode.Confirm: return "RimAgent.Settings.Mode.ConfirmDesc".Translate();
                case AgentMode.Yolo: return "RimAgent.Settings.Mode.YoloDesc".Translate();
                case AgentMode.Auto: return "RimAgent.Settings.Mode.AutoDesc".Translate();
                default: return "RimAgent.Settings.Mode.AutonomousDesc".Translate();
            }
        }

        private static string ReasoningLabel(ReasoningLevel level)
        {
            return ("RimAgent.Reasoning." + level).Translate();
        }

        private static void SetMode(RimAgentSettings settings, AgentMode mode)
        {
            if (settings == null || settings.mode == mode) return;
            settings.mode = mode;
            settings.Write();
        }

        private static void SetReasoning(RimAgentSettings settings, ReasoningLevel level)
        {
            if (settings == null || settings.reasoning == level) return;
            settings.reasoning = level;
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

        private void DrawBottomBar(Rect rect, ColonyAgentSession session)
        {
            if (session.HasPendingConfirmation && !session.IsBusy)
            {
                DrawConfirmBar(rect, session);
                return;
            }

            bool canSend = !session.IsBusy && _capabilityWarning == null;
            var inputRect = new Rect(rect.x, rect.y, rect.width - ButtonWidth - 6f, rect.height);
            var buttonRect = new Rect(rect.xMax - ButtonWidth, rect.y, ButtonWidth, rect.height);

            bool enterPressed = !HandleHistoryKeys() && !HandleSlashKeys(session) &&
                                Event.current.type == EventType.KeyDown &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            DrawInputField(inputRect);
            if (DrawSendOrStop(session, buttonRect, canSend, enterPressed) && enterPressed)
            {
                Event.current.Use();
            }
        }

        private bool DrawSendOrStop(ColonyAgentSession session, Rect buttonRect, bool canSend, bool enterPressed)
        {
            bool isSlash = _input.Length > 0 && _input[0] == '/';
            if (session.IsBusy)
            {
                if (DrawUnifiedButton(buttonRect, "RimAgent.Stop".Translate(), EmergencyColor, EmergencyColor, true))
                {
                    StopRun();
                }
                if (!enterPressed || !isSlash) return false;
                Send(session);
                return true;
            }

            bool enabled = canSend || isSlash;
            bool clicked = DrawUnifiedButton(buttonRect, "RimAgent.Send".Translate(), ToolActionColor, ToolActionColor, enabled);
            if (!(clicked || enterPressed) || !enabled) return false;
            Send(session);
            return true;
        }

        private void StopRun()
        {
            _cts?.Cancel();
            RimAgentGameComponent component = RimAgentGameComponent.Instance;
            component?.StopAutonomousRun();
            if (component?.IsGoalRunning == true) component.PauseGoal();
        }

        private void DrawInputField(Rect inputRect)
        {
            Widgets.DrawBoxSolid(inputRect, ControlBgColor);
            DrawThinBox(inputRect, ControlBorderColor);

            GUI.SetNextControlName(InputControlName);
            var innerInputRect = inputRect.ContractedBy(4f, 2f);
            string before = _input;
            _input = Widgets.TextField(innerInputRect, _input);
            // 玩家自己改了內容就不算在瀏覽歷史：↓ 不會拿舊草稿蓋掉新字，打 / 也讓方向鍵回到斜線選單。
            if (_input != before) _historyIndex = -1;

            if (_input.Length == 0 && GUI.GetNameOfFocusedControl() != InputControlName)
            {
                GUI.color = new Color(0.5f, 0.55f, 0.6f);
                Text.WordWrap = false;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(innerInputRect, "RimAgent.Placeholder".Translate().ToString().Truncate(innerInputRect.width));
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// 輸入框有焦點時 ↑／↓ 瀏覽送出過的輸入，↓ 走到底還原原本的草稿。
        /// 斜線選單開著時讓給選單；但已經在瀏覽歷史時（叫回的是斜線指令也一樣）繼續瀏覽。
        /// </summary>
        private bool HandleHistoryKeys()
        {
            if (Event.current.type != EventType.KeyDown || InputHistory.Count == 0) return false;
            KeyCode key = Event.current.keyCode;
            if (key != KeyCode.UpArrow && key != KeyCode.DownArrow) return false;
            if (GUI.GetNameOfFocusedControl() != InputControlName) return false;
            if (_historyIndex < 0 && SlashCommands.Match(_input).Count > 0) return false;

            if (key == KeyCode.UpArrow)
            {
                if (_historyIndex < 0)
                {
                    _historyDraft = _input;
                    _historyIndex = InputHistory.Count - 1;
                }
                else if (_historyIndex > 0)
                {
                    _historyIndex--;
                }
                SetInput(InputHistory[_historyIndex]);
            }
            else if (_historyIndex >= 0)
            {
                _historyIndex++;
                if (_historyIndex >= InputHistory.Count)
                {
                    _historyIndex = -1;
                    SetInput(_historyDraft);
                }
                else
                {
                    SetInput(InputHistory[_historyIndex]);
                }
            }
            Event.current.Use();
            return true;
        }

        private static void RememberInput(string text)
        {
            if (InputHistory.Count > 0 && InputHistory[InputHistory.Count - 1] == text) return;
            InputHistory.Add(text);
            if (InputHistory.Count > MaxInputHistory) InputHistory.RemoveAt(0);
        }

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

        private void SetInput(string text)
        {
            _input = text;
            if (GUI.GetNameOfFocusedControl() != InputControlName) return;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            editor.text = text;
            editor.MoveTextEnd();
        }

        private void DrawSlashPopup(Rect bottom, ColonyAgentSession session)
        {
            List<SlashCommand> matches = SlashCommands.Match(_input);
            if (matches.Count == 0) return;
            _slashIndex = Mathf.Clamp(_slashIndex, 0, matches.Count - 1);

            float height = matches.Count * SlashRowHeight + 8f;
            var popup = new Rect(bottom.x, bottom.y - height - 4f, Mathf.Min(SlashPopupWidth, bottom.width), height);

            Widgets.DrawBoxSolid(popup, BgColor);
            DrawThinBox(popup, BorderColor);

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            for (int i = 0; i < matches.Count; i++)
            {
                SlashCommand command = matches[i];
                var row = new Rect(popup.x + 4f, popup.y + 4f + i * SlashRowHeight, popup.width - 8f, SlashRowHeight);
                Widgets.DrawHighlightIfMouseover(row);
                if (i == _slashIndex) Widgets.DrawHighlightSelected(row);

                string name = "/" + command.Name;
                float nameWidth = Text.CalcSize(name).x;
                GUI.color = ToolActionColor;
                Widgets.Label(new Rect(row.x + 6f, row.y, nameWidth, row.height), name);

                var descRect = new Rect(row.x + 6f + nameWidth + 10f, row.y, row.width - nameWidth - 22f, row.height);
                GUI.color = ToolTextColor;
                Widgets.Label(descRect, command.DescriptionKey.Translate().ToString().Truncate(descRect.width));
                GUI.color = Color.white;

                if (Widgets.ButtonInvisible(row)) AcceptSlash(session, command);
            }
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void RunSlash(ColonyAgentSession session, SlashCommand command, string args)
        {
            if (command == SlashCommands.Stop)
            {
                StopRun();
                return;
            }
            if (command == SlashCommands.Goal)
            {
                RunGoal(session, args);
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
                _scrollToBottom = true;
            }
            else if (command == SlashCommands.Compact)
            {
                Compact(session);
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
                    int newIndex = goals.Count + 1;
                    if (text.Length > RimAgentGameComponent.MaxGoalChars)
                        Notice(session, "RimAgent.Goal.TooLong".Translate(RimAgentGameComponent.MaxGoalChars));
                    else if (component.AddGoal(text)) Notice(session, "RimAgent.Goal.Added".Translate(newIndex, text));
                    else Notice(session, "RimAgent.Goal.Full".Translate());
                    break;
                case GoalAction.Pause:
                    if (goals.Count == 0) { Notice(session, "RimAgent.Goal.Empty".Translate()); break; }
                    component.PauseGoal();
                    if (session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
                    Notice(session, "RimAgent.Goal.Paused".Translate());
                    break;
                case GoalAction.Resume:
                    if (goals.Count == 0) { Notice(session, "RimAgent.Goal.Empty".Translate()); break; }
                    component.ResumeGoal();
                    Notice(session, "RimAgent.Goal.Resumed".Translate());
                    break;
                case GoalAction.Edit:
                    if (index < 1 || index > goals.Count || text.Length == 0)
                        Notice(session, "RimAgent.Goal.EditUsage".Translate());
                    else if (text.Length > RimAgentGameComponent.MaxGoalChars)
                        Notice(session, "RimAgent.Goal.TooLong".Translate(RimAgentGameComponent.MaxGoalChars));
                    else if (component.EditGoalAt(index - 1, text))
                    {
                        if (index == 1 && session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
                        Notice(session, "RimAgent.Goal.Edited".Translate(index, text));
                    }
                    break;
                case GoalAction.Clear:
                    if (goals.Count == 0 && string.IsNullOrEmpty(component.LastCompletedGoal))
                    { Notice(session, "RimAgent.Goal.Empty".Translate()); break; }
                    component.ClearGoals();
                    if (session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
                    Notice(session, "RimAgent.Goal.Cleared".Translate());
                    break;
                case GoalAction.Show:
                    Notice(session, index >= 1 && index <= goals.Count
                        ? "RimAgent.Goal.Show".Translate(index, goals[index - 1]).ToString()
                        : "RimAgent.Goal.ShowUsage".Translate().ToString());
                    break;
                case GoalAction.Remove:
                    if (index >= 1 && index <= goals.Count)
                    {
                        string removed = goals[index - 1];
                        component.RemoveGoalAt(index - 1);
                        if (index == 1 && session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
                        Notice(session, "RimAgent.Goal.Removed".Translate(index, removed));
                    }
                    else Notice(session, "RimAgent.Goal.NoSuch".Translate(index));
                    break;
                default:
                    Notice(session, GoalListText(component));
                    break;
            }
        }

        private static string GoalListText(RimAgentGameComponent component)
        {
            IReadOnlyList<string> goals = component.Goals;
            if (goals.Count == 0)
            {
                string empty = "RimAgent.Goal.Empty".Translate();
                return string.IsNullOrEmpty(component.LastCompletedGoal) ? empty
                    : empty + "\n" + "RimAgent.Goal.LastCompleted".Translate(component.LastCompletedGoal).ToString();
            }
            var sb = new System.Text.StringBuilder("RimAgent.Goal.ListHeader".Translate());
            for (int i = 0; i < goals.Count; i++)
            {
                string title = goals[i].Replace('\r', ' ').Replace('\n', ' ');
                if (title.Length > 180) title = title.Substring(0, 180) + "…";
                sb.Append('\n').Append(i + 1).Append(". ").Append(title);
                if (i == 0)
                {
                    string state = component.CurrentGoalPauseReason == GoalPauseReason.Blocked ? "RimAgent.Goal.StateBlocked"
                        : component.CurrentGoalPauseReason == GoalPauseReason.TurnLimit ? "RimAgent.Goal.StateTurnLimit"
                        : component.CurrentGoalPauseReason == GoalPauseReason.NoProgress ? "RimAgent.Goal.StateNoProgress"
                        : component.CurrentGoalPauseReason == GoalPauseReason.Error ? "RimAgent.Goal.StateError"
                        : component.GoalPaused ? "RimAgent.Goal.StatePaused" : "RimAgent.Goal.StateActive";
                    sb.Append(" (").Append(state.Translate()).Append(')');
                }
            }
            sb.Append('\n').Append("RimAgent.Goal.Turns".Translate(component.GoalTurns, GoalProgress.MaxTurns,
                component.GoalBlockedTurns, component.GoalIdleTurns));
            if (!string.IsNullOrWhiteSpace(component.GoalLastProgress))
            {
                string progress = component.GoalLastProgress.Replace('\r', ' ').Replace('\n', ' ');
                if (progress.Length > 240) progress = progress.Substring(0, 240) + "…";
                sb.Append('\n').Append("RimAgent.Goal.Progress".Translate(progress));
            }
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
            const float WideButtonWidth = 100f;
            // 「不再問」只在這批確實記得住時出現（MCP 工具永遠要問），免得按了沒作用。
            bool canRemember = session.ConfirmGate.CanRememberPending;
            var confirmRect = new Rect(rect.x, rect.y, WideButtonWidth, rect.height);
            var alwaysRect = new Rect(confirmRect.xMax + 6f, rect.y, canRemember ? WideButtonWidth : 0f, rect.height);
            var discardRect = new Rect((canRemember ? alwaysRect.xMax : confirmRect.xMax) + 6f, rect.y, WideButtonWidth, rect.height);
            var noteRect = new Rect(discardRect.xMax + 8f, rect.y, Mathf.Max(0f, rect.xMax - discardRect.xMax - 8f), rect.height);

            if (DrawUnifiedButton(confirmRect, "RimAgent.Confirm".Translate(), PauseColor, PauseColor, true))
            {
                Confirm(session, false);
            }
            if (canRemember && DrawUnifiedButton(alwaysRect, "RimAgent.ConfirmAlways".Translate(), PauseColor, PauseColor, true))
            {
                Confirm(session, true);
            }
            if (DrawUnifiedButton(discardRect, "RimAgent.ConfirmDiscard".Translate(), EmergencyColor, EmergencyColor, true))
            {
                session.DiscardPendingConfirmation();
            }

            Text.Font = GameFont.Tiny;
            GUI.color = NoticeColor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(noteRect, "RimAgent.ConfirmNote".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void Confirm(ColonyAgentSession session, bool rememberForSession)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Observe(session, session.ConfirmAsync(_cts.Token, rememberForSession));
        }

        private void Compact(ColonyAgentSession session)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Observe(session, session.CompactAsync(_cts.Token));
        }

        /// <summary>
        /// 記下發射後不管的請求；它 faulted（通常是同幀被自主任務搶先導致的忙碌衝突）
        /// 才在主線程補一句忙碌提示。成功完成就靜靜丟掉。
        /// </summary>
        private void Observe(ColonyAgentSession session, Task task)
        {
            _observedSession = session;
            _observedTask = task;
        }

        private void DrainObservedTask()
        {
            Task task = _observedTask;
            if (task == null || !task.IsCompleted) return;
            _observedTask = null;
            ColonyAgentSession session = _observedSession;
            _observedSession = null;
            if (task.IsFaulted && session != null)
            {
                Notice(session, "RimAgent.Slash.Busy".Translate());
            }
        }

        private void Send(ColonyAgentSession session)
        {
            string text = _input.Trim();
            if (text.Length == 0) return;
            _input = string.Empty;
            RememberInput(text);
            _historyIndex = -1;
            _scrollToBottom = true;

            if (SlashCommands.TryParse(text, out SlashCommand command, out string args))
            {
                if (command != null) RunSlash(session, command, args);
                else Notice(session, "RimAgent.Slash.Unknown".Translate(text));
                return;
            }
            if (_capabilityWarning != null) return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Observe(session, session.SendAsync(text, _cts.Token));
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
                _capabilityWarning = "RimAgent.FrameworkNotInitialized".Translate();
            }
            catch (Exception ex) when (ex is MissingMethodException || ex is TypeLoadException)
            {
                _capabilityWarning = "RimAgent.FrameworkTooOld".Translate();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CheckFunctionCalling()
        {
            string model = RimAgentMod.Settings?.agentModel;
            return RimLLMProvider.GetEffectiveCapabilities(string.IsNullOrWhiteSpace(model) ? null : model.Trim()).SupportsFunctionCalling;
        }

        private static bool? _markdownAvailable;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string RenderReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (_markdownAvailable == false) return text;
            try
            {
                // 先把 $...$／$$...$$ 數學轉成易讀純文字：框架的 Markdig 管線沒有數學擴充。
                string rich = RimLLM_Framework.Mod.RimLLMMarkdown.ToRichText(LatexMath.ConvertMath(text));
                _markdownAvailable = true;
                return rich ?? text;
            }
            catch (Exception ex) when (ex is MissingMethodException || ex is TypeLoadException)
            {
                _markdownAvailable = false;
                return text;
            }
            catch (Exception)
            {
                return text;
            }
        }

        /// <summary>記住上一次的輸入值與組好的字串；值沒變就直接回傳，不重新翻譯與格式化。</summary>
        private sealed class TextCache<TKey>
        {
            private bool _hasValue;
            private TKey _key;
            private string _text;

            public string Get(TKey key, Func<TKey, string> build)
            {
                if (!_hasValue || !EqualityComparer<TKey>.Default.Equals(_key, key))
                {
                    _key = key;
                    _text = build(key);
                    _hasValue = true;
                }
                return _text;
            }
        }
    }
}
