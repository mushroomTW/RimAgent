using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>
/// 自主任務的執行生命週期：同時只跑一輪、新局或讀檔後讓舊任務作廢、每輪換新的取消語彙基元，
/// 以及任務結束後回寫排程器（冷卻、重試退避、工具配額）與設定錯誤後的暫停。
/// </summary>
/// <remarks>
/// 從 <see cref="RimAgentGameComponent"/> 抽出：原本直接讀 <c>Find.TickManager</c> 與 <c>Current.Game</c>，
/// 無法單元測試。遊戲時間改由建構時傳入的委派提供。
/// </remarks>
internal sealed class AutonomousRunner
{
    /// <summary>設定錯誤（金鑰、額度、供應商離線）後暫停自主任務的長度：一個遊戲日。</summary>
    public const int ConfigurationErrorHoldTicks = 60000;

    private readonly AutonomousScheduler _scheduler;
    private readonly Func<int?> _currentTick;
    private readonly Action<string> _warn;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 自主任務從起跑到回寫排程器為止。session 的忙碌旗標在背景執行緒就放開，早於冷卻記錄；
    /// 只看忙碌旗標的話，中間落下的訊號檢查會拿舊冷卻再起一輪。
    /// </summary>
    private volatile bool _running;

    /// <summary>新局、讀檔時遞增；舊的自主任務藉此確認自己已不屬於目前這份遊戲。</summary>
    private int _generation;

    /// <summary>建立自主任務執行器。</summary>
    /// <param name="scheduler">任務結束後要回寫的排程器。</param>
    /// <param name="currentTick">目前的遊戲 tick；沒有遊戲（例如已回到主選單）時回傳 null。</param>
    /// <param name="warn">寫入除錯記錄的警告。</param>
    public AutonomousRunner(AutonomousScheduler scheduler, Func<int?> currentTick, Action<string> warn)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _currentTick = currentTick ?? throw new ArgumentNullException(nameof(currentTick));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>是否有自主任務正在進行（含 System One 篩選與代答）。</summary>
    public bool IsRunning => _running;

    /// <summary>目前這份遊戲的世代編號。</summary>
    public int Generation => _generation;

    /// <summary>設定錯誤後暫停到這個 tick 為止；不存檔，讀檔即恢復。</summary>
    public int HoldUntilTick { get; private set; }

    /// <summary><paramref name="generation"/> 是否仍屬於目前這份遊戲。</summary>
    public bool IsCurrent(int generation) => generation == _generation;

    /// <summary>取消進行中的自主任務；收尾時會壓住該條件的重試。</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// 新局、讀檔或排入讀檔時呼叫：取消進行中的任務，並讓它的收尾與排程器寫入失效。
    /// </summary>
    public void Invalidate()
    {
        _cts?.Cancel();
        _generation++;
        _running = false;
    }

    /// <summary>
    /// 起一輪自主任務：同步標記為執行中並換一個新的取消來源，<paramref name="body"/> 結束後只清掉自己這一代的旗標。
    /// </summary>
    /// <param name="body">任務內容，收到這一輪的世代編號與取消語彙基元；例外由它自己處理。</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Bug", "S2930:\"IDisposables\" should be disposed",
        Justification = "還在收尾的上一輪可能仍在使用舊的取消來源；CTS 沒有計時器或等待把手，交給 GC 即可。")]
    public async Task RunGuardedAsync(Func<int, CancellationToken, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _running = true;
        int generation = _generation;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        try
        {
            // 不加 ConfigureAwait(false)：收尾要回主線程，免得與主線程的 Evaluate 同時改排程器狀態。
            await body(generation, token);
        }
        finally
        {
            // 讀檔後新的一輪可能已經開始，舊的 finally 不能把它的旗標關掉。
            if (generation == _generation) _running = false;
        }
    }

    /// <summary>
    /// 送出一輪主 LLM 的自主任務，結束後回寫排程器。
    /// </summary>
    /// <param name="request">這一輪的內容。</param>
    /// <param name="generation">由 <see cref="RunGuardedAsync"/> 取得的世代編號。</param>
    /// <param name="cancellationToken">由 <see cref="RunGuardedAsync"/> 取得的取消語彙基元。</param>
    /// <returns>
    /// 模型正常回覆時 <c>Completed</c> 為 true（<c>Reply</c> 仍可能為 null）；
    /// session 被占用、取消、失敗或已作廢時為 false。
    /// </returns>
    public async Task<(bool Completed, string? Reply)> RunTaskAsync(AutonomousRunRequest request, int generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 起跑前遊戲已被換掉（例如排程器在同一幀觸發後玩家立刻讀檔）：不碰任何狀態。
        if (generation != _generation) return (false, null);
        ColonyAgentSession session = request.Session;
        // 送出前先算好這句會拿到的編號：busy 旗標保證成功送出的那句取的是下一個號；
        // 送完才讀 CurrentInstructionId 的話，玩家在旗標釋放的瞬間送指令會讓我們數到別句。
        int instructionId = session.Transcript.CurrentInstructionId + 1;
        (bool Completed, string? Reply) outcome = (false, null);
        try
        {
            string reply = await session.SendAsync(
                request.Instruction,
                autonomous: true,
                maxToolSteps: request.MaxToolSteps,
                reasoningOverride: ReasoningLevel.Low,
                autonomousInfo: request.RunInfo,
                cancellationToken: cancellationToken);
            outcome = (true, reply);
        }
        catch (AgentBusyException)
        {
            // 玩家在同一幀搶先送了指令（session 被占用）：這次沒跑，不算一次自主任務、不吃冷卻。
            return (false, null);
        }
        catch (OperationCanceledException)
        {
            // 玩家主動停止自主任務：收尾統一壓住重試。
        }
        catch (Exception ex)
        {
            // 背景任務沒有呼叫端可以接例外：照一般失敗記錄並套用冷卻。
            _warn("RimAgent autonomous run failed: " + ex.Message);
        }
        // 讀檔／新局已把這個任務作廢：不要用舊遊戲的結果改新遊戲的排程器或冷卻。
        if (generation != _generation) return (false, null);
        RecordRun(request, cancellationToken.IsCancellationRequested, instructionId);
        return outcome;
    }

    /// <summary>任務結束後的紀錄：冷卻、重試退避、工具配額計數，以及設定錯誤後的暫停。</summary>
    private void RecordRun(AutonomousRunRequest request, bool stopped, int instructionId)
    {
        // 冷卻、定期巡視與重試退避都從任務結束起算：一輪在高速下常跑掉十幾個遊戲小時，
        // 從起跑算的話一結束就全部到期。任務期間玩家退回主選單時沒有遊戲可讀，沿用起跑 tick。
        int finishTick = _currentTick() ?? request.Tick;
        // 玩家按了停止就壓住該條件的重試（解除前不再跑）：憑 token 判斷，
        // SendAsync 吃掉取消沒擲 OCE 時同樣生效。
        if (stopped) _scheduler.Dismiss(request.Trigger, finishTick);
        // 只數這一句的工具紀錄：transcript 會裁掉最舊的紀錄，前後相減會失準。
        AgentTranscript transcript = request.Session.Transcript;
        int used = transcript.Snapshot().Count(e =>
            e.Kind == TranscriptKind.Tool && e.InstructionId == instructionId && e.BudgetConsumed);
        // 停止且沒消耗工具配額就不吃冷卻（其他條件不受影響）。
        if (used > 0 || !stopped) _scheduler.RecordRun(finishTick, used, request.Config);
        _scheduler.RecordRunFinished(request.Trigger, finishTick);

        if (transcript.ErrorKindOf(instructionId) != AgentErrorKind.Configuration) return;
        // 這類錯誤幾秒內不會自己好，照常重觸發只會連續失敗。
        HoldUntilTick = request.Tick + ConfigurationErrorHoldTicks;
        _warn("RimAgent autonomous tasks paused for one in-game day after a provider configuration error.");
        transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = "Autonomous tasks are paused for one in-game day: the model provider reported a key, balance or availability problem. " +
                   "Fix it in the RimLLM Framework settings; player instructions still work."
        });
    }
}

/// <summary><see cref="AutonomousRunner.RunTaskAsync"/> 一輪自主任務的內容。</summary>
internal sealed class AutonomousRunRequest
{
    /// <summary>送出任務的殖民地 Agent 工作階段。</summary>
    public required ColonyAgentSession Session { get; init; }

    /// <summary>觸發這一輪的條件。</summary>
    public required AutonomousTrigger Trigger { get; init; }

    /// <summary>觸發時的遊戲 tick。</summary>
    public required int Tick { get; init; }

    /// <summary>自主任務設定；回寫冷卻時使用。</summary>
    public required AutonomousConfig Config { get; init; }

    /// <summary>送給模型的任務指令。</summary>
    public required string Instruction { get; init; }

    /// <summary>本輪工具步數上限；null 代表不額外限制。</summary>
    public int? MaxToolSteps { get; init; }

    /// <summary>執行紀錄用的起跑脈絡；沒開執行紀錄時為 null。</summary>
    public AutonomousRunInfo? RunInfo { get; init; }
}
