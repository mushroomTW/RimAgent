#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

/// <summary>
/// 懸浮可移動的 Agent 視窗，支援隨意拖曳、自由縮放、位置記憶。
/// 包含雙色標籤決策日誌串流、模式/思考切換、輸入列與斜線指令。
/// </summary>
#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent : Window
{
    private const string ColorTagPrefix = "<color=#";
    private const string ColorTagClose = "</color>";
    private const string ColorTagCloseSpace = "</color> ";
    private const string GoalEmptyTranslationKey = "RimAgent.Goal.Empty";

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
    private const int BubbleRadius = 12;
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
    /// <summary>收合成桌面小助手時的頭像邊長。</summary>
    private const float AvatarSize = 76f;
    private const float AvatarBubbleHeight = 32f;
    private const float AvatarBubbleGap = 4f;
    private const float AvatarBubbleMaxWidth = 260f;
    /// <summary>按下到放開之間圖標移動不超過這個距離才算點擊（展開），否則是拖曳。</summary>
    private const float IconClickSlack = 4f;
    /// <summary>視窗與彈出選單的圓角半徑。</summary>
    private const float WindowRadius = 10f;
    /// <summary>按鈕、輸入框等控制項的圓角半徑。</summary>
    private const float ControlRadius = 6f;
    /// <summary>上下文用量到達幾成時標題列計量變黃預警。</summary>

    // 圖片風格配色
    private static readonly Color BgColor = new(0.04f, 0.06f, 0.08f, 0.88f);
    private static readonly Color BorderColor = new(0.62f, 0.68f, 0.76f, 0.70f);
    private static readonly Color TitleColor = new(0.80f, 0.88f, 0.96f);
    private static readonly Color CloseButHoverColor = new(1f, 0.35f, 0.35f, 0.3f);

    // Rich text 標籤色彩 Hex（用於雙色分離排版）
    private const string EmergencyHex = "FF5959"; // 亮紅橙：[危險急停]
    private const string PauseHex = "FFC846";     // 金黃：[自動暫停]
    private const string AutoActionHex = "73CCFF"; // 冷青藍：[自動處理]
    private const string TextHex = "E6EDF5";       // 柔和淺白：內文敘述
    private const string SubTextHex = "A0B0C0";    // 淺灰藍：次要細節 / 結果
    private const string NoticeHex = "FFD670";     // 提示黃
    private const string ReasoningHex = "B9A6E0";  // 淡紫：模型思考

    private static readonly Color ControlBgColor = new(0.08f, 0.11f, 0.15f, 0.75f);
    private static readonly Color ControlBorderColor = new(0.45f, 0.52f, 0.60f, 0.65f);
    private static readonly Color ToolActionColor = new(0.48f, 0.82f, 1f);
    private static readonly Color ToolTextColor = new(0.75f, 0.78f, 0.82f);
    private static readonly Color EmergencyColor = new(1f, 0.35f, 0.35f);
    private static readonly Color PauseColor = new(1f, 0.80f, 0.28f);
    private static readonly Color NoticeColor = new(0.95f, 0.85f, 0.45f);
    private static readonly Color BubbleColor = new(0.16f, 0.30f, 0.44f, 0.55f);
    private static readonly Color BubbleBorderColor = new(0.66f, 0.86f, 1f, 0.45f);
    private static readonly Color DividerColor = new(0.45f, 0.52f, 0.60f, 0.30f);

    private enum LineKind
    {
        Text,
        /// <summary>玩家親自輸入的話：靠右、包在氣泡裡。</summary>
        PlayerBubble,
        /// <summary>工具呼叫摘要列：點一下展開或收合參數與結果。</summary>
        ToolHeader,
        /// <summary>每句指令一次的戰鬥延遲提醒；畫法同 Text，重排該筆時要讓它重新出現。</summary>
        CombatNote,
        Screenshot,
        /// <summary>連續工具呼叫的摘要列（「已執行 N 個工具」）：點一下展開或收合整組。</summary>
        ToolGroupHeader,
        /// <summary>回覆裡的頂層表格：逐格繪製，Text 是表格原文。</summary>
        Table
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
        /// <summary>ToolGroupHeader 專用：這組的第一筆工具紀錄，同時是展開狀態的鍵。</summary>
        public TranscriptEntry GroupFirst;
        /// <summary>回合開頭：上方多留 <see cref="TurnGap"/> 並畫分隔線。</summary>
        public bool TurnStart;
        /// <summary>Table 專用：框架排好的表格。</summary>
        public RimLLM_Framework.Mod.RimLLMMarkdownTable Table;
    }

    /// <summary>整局共用、關掉視窗也保留；最新的在最後。</summary>
    private static readonly List<string> InputHistory = [];
    private static Texture2D _iconTex;
    private static Texture2D _discTex;
    /// <summary>小助手頭像沿用 About/ModIcon.png（由遊戲的模組中繼資料載入）。</summary>
    private static Texture2D IconTex => _iconTex ??= LoadedModManager.GetMod<RimAgentMod>()?.Content?.ModMetaData?.Icon
        ?? ContentFinder<Texture2D>.Get("UI/Buttons/MainButtons/RimAgent");

    /// <summary>收合成桌面小助手：對話照常進行，只是不佔畫面。</summary>
    private bool _collapsed;
    /// <summary>這次點圓盤的按下動作已經把面板關掉，放開時不再重開。</summary>
    private bool _gaugeClosedPanel;
    /// <summary>收合當下的逐字稿 Version；之後變了就在圖標角落亮提示點。</summary>
    private int _collapsedVersion;
    private bool _iconPressed;
    private Vector2 _iconPressPos;
    /// <summary>頭像左上角的螢幕座標；視窗會因泡泡伸縮，頭像才是位置的錨點。</summary>
    private Vector2 _avatarPos;
    /// <summary>上次排版設定的視窗位置；和現在的位置不同代表玩家拖動了。</summary>
    private Vector2 _laidOutPos;
    private bool _bubbleOnLeft = true;
    private string _bubbleText;
    private int _bubbleState = -1;
    private AgentActivity _bubbleActivity;
    private string _bubbleMeasuredText;
    private float _bubbleWidth;

    private string _input = string.Empty;

    /// <summary>一句指令最多附幾張貼上的圖片。</summary>
    private const int MaxPastedImages = 4;

    /// <summary>以 Ctrl+V 貼上、等著隨下一句指令送出的圖片。</summary>
    private sealed class PastedImage
    {
        public byte[] Jpeg;
        public Texture2D Thumbnail;
    }

    private readonly List<PastedImage> _pastedImages = new();
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
    private readonly Dictionary<TranscriptEntry, bool> _toolExpanded = [];
    /// <summary>玩家親手展開或收合過的工具群組（以群組第一筆為鍵）；沒設定的收合，除非組內有等待確認的預覽。</summary>
    private readonly Dictionary<TranscriptEntry, bool> _groupExpanded = [];
    // 逐步排版時正在累積的連續工具群組；逐字稿重排時歸零。
    private TranscriptEntry _groupFirst;
    private int _groupHeaderIndex = -1;
    private int _groupCount;
    private int _groupFailed;
    private bool _groupPending;

    // 渲染快取
    private readonly List<TranscriptLine> _lines = [];
    private readonly Dictionary<string, Texture2D> _screenshotTextures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>最近用到的排在最後；繪製時會把用到的移到最後，淘汰從最前面開始。</summary>
    private readonly List<string> _screenshotOrder = [];
    /// <summary>載入失敗的截圖與失敗時的 Environment.TickCount。</summary>
    private readonly Dictionary<string, int> _screenshotFailedAt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>繪製時看到、還沒載入的截圖；留到 WindowUpdate 載入（每幀最多一張，不在 OnGUI 裡解碼）。</summary>
    private string _pendingScreenshot;
    /// <summary>背景讀檔中的截圖；讀完才在 WindowUpdate 解碼，主線程不等磁碟。</summary>
    private string _readingScreenshot;
    private Task<byte[]> _screenshotRead;
    // 每個 IMGUI 事件都會重畫；帶參數的翻譯字串只在值變了才重組，免得每幀配置一堆字串。
    private readonly TextCache<AgentMode> _modeLabel = new();
    private readonly TextCache<int> _reasoningLabel = new();
    private float _reasoningLabelWidth = DropdownWidth;
    private TextCache<string> _modelLabel = new();
    private float _modelLabelWidth = -1f;
    private readonly TextCache<int> _autoUsageLabel = new();
    // 執行中提示的快取：活動（參考比對）與動畫格任一改變才重組字串。
    private AgentActivity _workingActivity;
    private int _workingPhase = -1;
    private string _workingText;
    private readonly TextCache<bool> _jumpLabel = new();
    private string _toolToggleTip;
    private string _reasoningToggleTip;
    private float _workingHeight;
    private float _workingHeightWidth = -1f;
    private string _workingHeightText;
    private float _settlingWidth = -1f;
    private int _settlingSince;
    private float _linesTotal;
    private float _linesWidth = -1f;
    private int _linesEntryCount = -1;
    private int _linesTranscriptVersion = -1;
    private int _linesTranscriptEpoch = -1;
    private AgentTranscript _linesTranscript;
    private readonly HashSet<int> _combatNoted = [];
    private string _capabilityWarning;
    private DateTime _nextCapabilityProbe;
    private CancellationTokenSource _cts;
    /// <summary>被新一輪取代、但舊工作可能還在跑的 CTS 與其工作；停止時一併取消，工作結束後釋放。</summary>
    private readonly List<(CancellationTokenSource Cts, Task Work)> _retiredCts = [];
    private int _slashIndex;
    private int _skillIndex;
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
        // 原生陰影是方形，會在圓角框外露出一條黑邊。
        drawShadow = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        closeOnAccept = false;
        closeOnCancel = false;
        preventCameraMotion = false;
        soundAppear = null;
        soundClose = SoundDefOf.TabClose;
        RimAgentGameComponent.GameTornDown += OnGameTornDown;
    }

    /// <summary>
    /// 遊戲被換掉或回主選單時視窗不會收到 PostClose：取消進行中的指令並釋放截圖貼圖（事件在主線程觸發）。
    /// </summary>
    private void OnGameTornDown()
    {
        RimAgentGameComponent.GameTornDown -= OnGameTornDown;
        CancelRunCts();
        ReleaseScreenshotTextures();
    }

    private void ReleaseScreenshotTextures()
    {
        foreach (Texture2D texture in _screenshotTextures.Values) UnityEngine.Object.Destroy(texture);
        _screenshotTextures.Clear();
    }

    public static bool IsOpenNow => Find.WindowStack != null && Find.WindowStack.IsOpen<Window_Agent>();

    public static void Toggle()
    {
        Window_Agent existing = Find.WindowStack.WindowOfType<Window_Agent>();
        if (existing is { _collapsed: true })
        {
            existing.Expand();
        }
        else if (existing != null)
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
        if (DateTime.UtcNow >= _nextCapabilityProbe) ProbeCapabilities();
        if (!_posInitialized) return;
        if (_collapsed)
        {
            UpdateCollapsedIcon();
            return;
        }

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
        Find.WindowStack.WindowOfType<ContextPanel>()?.Close(false);
        // 關窗後不該再讓進行中的對話繼續跑；下一次送出會建立新的 CTS。
        RimAgentGameComponent.GameTornDown -= OnGameTornDown;
        CancelRunCts();
        // 已取消的 CTS 可能仍被收尾中的工作讀取 Token，不 Dispose，交給 GC。
        _cts = null;
        ReleaseScreenshotTextures();
        _screenshotOrder.Clear();
        _screenshotFailedAt.Clear();
        _pendingScreenshot = null;
        _readingScreenshot = null;
        _screenshotRead = null;
        TakePastedImages();
        if (RimAgentMod.Settings != null)
        {
            // 收合時 windowRect 是圖標大小，設定裡留的展開尺寸才是要記住的。
            if (!_collapsed) RimAgentMod.Settings.UpdateBounds(windowRect);
            RimAgentMod.Settings.Write();
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (_collapsed)
        {
            DrawCollapsedIcon(inRect);
            return;
        }

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
            DrawRoundedBox(banner, new Color(0.45f, 0.12f, 0.12f, 0.75f), EmergencyColor);
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
        DrawSkillPopup(bottom);
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
        // 視窗最右、最下一排像素會被裁掉（截圖量得右框整條消失、下框變暗），外框右下各內縮 1px 才畫得完整。
        DrawRoundedBox(new Rect(inRect.x, inRect.y, inRect.width - 1f, inRect.height - 1f), BgColor, BorderColor, WindowRadius);
    }
}
