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
        private const float EntryGap = 6f;
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
        private const string UserHex = "A8DCFF";       // 淡天藍：玩家指令
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

        private string _input = string.Empty;
        private Vector2 _scroll;
        private int _lastEntryCount = -1;

        // 渲染快取
        private readonly List<(string Text, Color Color, float Height, string ImagePath)> _lines = new List<(string, Color, float, string)>();
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
        private readonly TextCache<int> _autoUsageLabel = new TextCache<int>();
        private readonly TextCache<int> _workingLabel = new TextCache<int>();
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
                TooltipHandler.TipRegion(contextRect, "RimAgent.Context.Tooltip".Translate(compact));
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

            if (_linesEntryCount != _lastEntryCount)
            {
                _lastEntryCount = _linesEntryCount;
                _scroll.y = float.MaxValue;
            }

            var viewRect = new Rect(0f, 0f, width, Math.Max(total, outRect.height));
            float visibleTop = Mathf.Clamp(_scroll.y, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
            float visibleBottom = visibleTop + outRect.height;

            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            float y = 0f;
            foreach ((string text, Color color, float height, string imagePath) in _lines)
            {
                if (y + height >= visibleTop && y <= visibleBottom)
                {
                    var lineRect = new Rect(0f, y, lineWidth, height);
                    if (imagePath != null) DrawScreenshotPreview(lineRect, imagePath);
                    else
                    {
                        GUI.color = color;
                        Widgets.Label(lineRect, text);
                    }
                }
                y += height + EntryGap;
            }
            if (working != null)
            {
                GUI.color = Color.white;
                Widgets.Label(new Rect(0f, y, width, workingHeight), working);
            }
            GUI.color = Color.white;
            Widgets.EndScrollView();
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
            AppendLines(entries, 0, width);
        }

        private void AppendLines(IReadOnlyList<TranscriptEntry> entries, int from, float width)
        {
            for (int i = Math.Max(0, from); i < entries.Count; i++)
            {
                foreach ((string text, Color color) in Render(entries[i], _combatNoted))
                {
                    float height = Text.CalcHeight(text, width);
                    _lines.Add((text, color, height, null));
                    _linesTotal += height + EntryGap;
                }
                string screenshotPath = TranscriptScreenshot.TryGetPath(entries[i], GenFilePaths.ScreenshotFolderPath);
                if (screenshotPath != null)
                {
                    float imageHeight = Mathf.Clamp(width * 0.5625f, 120f, 320f);
                    _lines.Add((null, Color.white, imageHeight, screenshotPath));
                    _linesTotal += imageHeight + EntryGap;
                }
            }
            _linesEntryCount = entries.Count;
        }

        /// <summary>
        /// 雙色分離渲染條目：採用 Unity Rich Text 標籤，達成「高亮彩色前綴標籤 + 柔和淺白內文」的圖片設計層次。
        /// </summary>
        private static IEnumerable<(string Text, Color Color)> Render(TranscriptEntry entry, HashSet<int> combatNoted)
        {
            switch (entry.Kind)
            {
                case TranscriptKind.User:
                    if (entry.GoalRun)
                    {
                        string goalTag = "<color=#" + NoticeHex + ">" + "RimAgent.Tag.Goal".Translate() + "</color> ";
                        yield return (goalTag + "<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>", Color.white);
                    }
                    else if (entry.Autonomous)
                    {
                        string autoTag = "<color=#" + AutoActionHex + ">" + "RimAgent.Tag.AutoAction".Translate() + "</color> ";
                        string body = "<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>";
                        yield return (autoTag + body, Color.white);
                    }
                    else
                    {
                        string userText = "<color=#" + UserHex + ">" + "RimAgent.Tag.Player".Translate()
                            + "</color> <color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>";
                        yield return (userText, Color.white);
                    }
                    break;

                case TranscriptKind.Assistant:
                    foreach (var line in RenderAssistant(entry)) yield return line;
                    break;

                case TranscriptKind.Tool:
                    foreach (var line in RenderTool(entry, combatNoted)) yield return line;
                    break;

                case TranscriptKind.Error:
                    string errTag = "<color=#" + EmergencyHex + ">" + "RimAgent.Tag.EmergencyStop".Translate() + "</color> ";
                    string errBody = "<color=#" + EmergencyHex + ">" + EscapeRichText(DescribeError(entry)) + "</color>";
                    yield return (errTag + errBody, Color.white);
                    break;

                default:
                    // Notice 條目：檢驗是否為暫停、告警或提醒
                    bool isPauseOrAlert = ContainsAny(entry.Text, "暫停", "暂停", "pause", "告警", "警告", "warn", "alert");
                    string noticeTag = isPauseOrAlert
                        ? "<color=#" + PauseHex + ">" + "RimAgent.Tag.AutoPause".Translate().ToString() + "</color> "
                        : "<color=#" + NoticeHex + ">[提示]</color> ";
                    string noticeBody = "<color=#" + TextHex + ">" + EscapeRichText(entry.Text) + "</color>";
                    yield return (noticeTag + noticeBody, Color.white);
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

        private static IEnumerable<(string Text, Color Color)> RenderAssistant(TranscriptEntry entry)
        {
            string reply = RenderReply(EscapeRichText(entry.Text));
            string replyText = reply.Length == 0 ? "RimAgent.EmptyReply".Translate().ToString() : reply;
            string agentTag = "<color=#" + AutoActionHex + ">" + "RimAgent.Tag.Agent".Translate() + "</color> ";
            yield return (agentTag + "<color=#" + TextHex + ">" + replyText + "</color>", Color.white);
            if (entry.ToolsStripped)
            {
                string stripText = "<color=#" + NoticeHex + ">" + "RimAgent.ToolsStripped".Translate() + "</color>";
                yield return (stripText, Color.white);
            }
        }

        private static IEnumerable<(string Text, Color Color)> RenderTool(TranscriptEntry entry, HashSet<int> combatNoted)
        {
            string tag = "<color=#" + AutoActionHex + ">" + "RimAgent.Tag.Tool".Translate() + "</color> ";
            string call = tag + "<color=#" + TextHex + ">" + EscapeRichText(entry.ToolName) + "</color>";
            if (entry.RiskCheck.Length > 0)
            {
                call += " <color=#" + (entry.Failed ? EmergencyHex : NoticeHex) + ">["
                    + RiskLabel(entry.RiskCheck) + "]</color>";
            }
            yield return (call, Color.white);
            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                yield return ("<color=#" + SubTextHex + ">" + "RimAgent.Tool.Arguments".Translate()
                    + " " + EscapeRichText(entry.Arguments) + "</color>", Color.white);
            }

            string resultStr = entry.Result.Length > MaxResultChars ? entry.Result.Substring(0, MaxResultChars) + "…" : entry.Result;
            string resColorHex = entry.Failed ? EmergencyHex : SubTextHex;
            yield return ("<color=#" + resColorHex + ">" + "RimAgent.Tool.Result".Translate()
                + " " + EscapeRichText(resultStr) + "</color>", Color.white);

            if (entry.ToolCategory == ToolCategory.Combat && combatNoted.Add(entry.InstructionId))
            {
                string combatNote = "<color=#" + NoticeHex + ">" + "RimAgent.CombatLatencyNote".Translate() + "</color>";
                yield return (combatNote, Color.white);
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
                    options.Add(new FloatMenuOption(ModeLabel(mode), () => SetMode(settings, mode)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

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

            if (RimAgentGameComponent.AutonomousEnabled)
            {
                var usageRect = new Rect(reasoningRect.xMax + 8f, rect.y, rect.xMax - reasoningRect.xMax - 8f, rect.height);
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

            bool enterPressed = !HandleSlashKeys(session) &&
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
            _input = Widgets.TextField(innerInputRect, _input);

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
                _lastEntryCount = -1;
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
                    if (component.AddGoal(text)) Notice(session, "RimAgent.Goal.Added".Translate(newIndex, text));
                    else Notice(session, "RimAgent.Goal.Full".Translate());
                    break;
                case GoalAction.Pause:
                    component.PauseGoal();
                    if (session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
                    Notice(session, "RimAgent.Goal.Paused".Translate());
                    break;
                case GoalAction.Resume:
                    component.ResumeGoal();
                    Notice(session, "RimAgent.Goal.Resumed".Translate());
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
                    Notice(session, goals.Count == 0 ? "RimAgent.Goal.Empty".Translate().ToString()
                        : GoalListText(goals, component.GoalPaused));
                    break;
            }
        }

        private static string GoalListText(IReadOnlyList<string> goals, bool paused)
        {
            var sb = new System.Text.StringBuilder("RimAgent.Goal.ListHeader".Translate());
            for (int i = 0; i < goals.Count; i++)
            {
                sb.Append('\n').Append(i + 1).Append(". ").Append(goals[i]);
                if (i == 0) sb.Append(" (").Append((paused ? "RimAgent.Goal.StatePaused" : "RimAgent.Goal.StateActive").Translate()).Append(')');
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
            return RimLLMProvider.GetEffectiveCapabilities().SupportsFunctionCalling;
        }

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
