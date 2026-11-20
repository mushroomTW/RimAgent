#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Mcp;
using RimAgent.Tools;
using Verse;

namespace RimAgent;

public partial class RimAgentGameComponent
{
    public bool AddGoal(string goal)
    {
        string trimmed = (goal ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxGoalChars || _goals.Count >= MaxGoals) return false;
        _goals.Add(trimmed);
        if (_goals.Count == 1)
        {
            _goalPaused = false;
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalPauseReason = GoalPauseReason.None;
            _goalNextRunAt = DateTime.MinValue;
        }
        return true;
    }

    public void RemoveGoalAt(int index)
    {
        if (index < 0 || index >= _goals.Count) return;
        if (index == 0)
        {
            _goalCts?.Cancel();
            // 讓正在等待審計的舊回合失效；新回合要等舊回合放開 session 才會開始。
            _goalGeneration++;
            _goalRunning = false;
        }
        _goals.RemoveAt(index);
        if (index == 0)
        {
            _goalPaused = false;
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalPauseReason = GoalPauseReason.None;
            _goalNextRunAt = DateTime.MinValue;
        }
    }

    public bool EditGoalAt(int index, string objective)
    {
        string trimmed = (objective ?? string.Empty).Trim();
        if (index < 0 || index >= _goals.Count || trimmed.Length == 0 || trimmed.Length > MaxGoalChars) return false;
        if (index == 0)
        {
            _goalCts?.Cancel();
            // 同一個字串也算新目標：舊審計結果不能套用到剛編輯過的目標。
            _goalGeneration++;
            _goalRunning = false;
        }
        _goals[index] = trimmed;
        if (index == 0)
        {
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalNextRunAt = DateTime.MinValue;
            if (_goalPauseReason != GoalPauseReason.User)
            {
                _goalPaused = false;
                _goalPauseReason = GoalPauseReason.None;
            }
        }
        return true;
    }

    public void ClearGoals()
    {
        _goalCts?.Cancel();
        _goalGeneration++;
        _goalRunning = false;
        _goals.Clear();
        _goalPaused = false;
        _goalPauseReason = GoalPauseReason.None;
        _goalTurns = 0;
        _goalBlockedTurns = 0;
        _goalIdleTurns = 0;
        _goalBlockerKey = null;
        _goalNeedsVerification = false;
        _goalAuditNote = null;
        _goalLastProgress = null;
        _lastCompletedGoal = null;
        _goalNextRunAt = DateTime.MinValue;
    }

    public void PauseGoal()
    {
        if (_goals.Count == 0) return;
        _goalPaused = true;
        _goalPauseReason = GoalPauseReason.User;
        _goalCts?.Cancel();
    }

    public void ResumeGoal()
    {
        if (_goals.Count == 0) return;
        _goalPaused = false;
        _goalPauseReason = GoalPauseReason.None;
        _goalBlockedTurns = 0;
        _goalIdleTurns = 0;
        _goalBlockerKey = null;
        _goalNeedsVerification = false;
        _goalAuditNote = null;
        _goalTurns = 0;
        _goalNextRunAt = DateTime.MinValue;
    }

    private void TryStartGoal()
    {
        if (_goals.Count == 0 || _goalPaused || _goalRunning || _autoRunning || Find.CurrentMap == null
            || DateTime.UtcNow < _goalNextRunAt)
        {
            return;
        }
        try
        {
            ColonyAgentSession session = Session;
            if (session.IsBusy || session.HasPendingConfirmation) return;
            _goalRunning = true;
            _ = RunGoalAsync(session, _goals[0], _goalGeneration);
        }
        catch (Exception ex)
        {
            _goalRunning = false;
            _goalNextRunAt = DateTime.UtcNow.AddSeconds(30);
            WarnOnce("RimAgent goal run could not start: " + ex.Message);
        }
    }

    private async Task RunGoalAsync(ColonyAgentSession session, string objective, int generation)
    {
        var cts = new CancellationTokenSource();
        _goalCts = cts;
        bool exclusive = false;
        try
        {
            int instructionId = session.Transcript.CurrentInstructionId + 1;
            string confirmationReport = session.PeekGoalConfirmationReport(objective);
            string reply = await session.SendAsync(
                GoalProgress.BuildPrompt(objective, confirmationReport, _goalNeedsVerification, _goalAuditNote),
                goalRun: true, displayText: objective, cancellationToken: cts.Token);
            if (reply != null && confirmationReport != null) session.TakeGoalConfirmationReport(objective);
            if (!GoalRunIsCurrent(objective, generation, cts)) return;

            // SendAsync 返回前已放開忙碌旗標；設置與審計之間不能有 await，
            // 先把 session 留在手上，避免玩家指令或自主任務插進審計。
            if (session.IsBusy || !session.TryBeginExclusiveWork()) return;
            exclusive = true;

            // 本輪已將 auditNote 送給模型，清空備忘避免跨輪殘留
            _goalAuditNote = null;

            IReadOnlyList<TranscriptEntry> entries = session.Transcript.Snapshot();
            bool hasToolEvidence = GoalProgress.HasVerificationEvidence(entries, instructionId);
            bool usedTool = HasActiveGoalToolUsage(entries, instructionId);
            GoalTurnDecision decision = GoalProgress.AfterTurn(
                _goalTurns, _goalBlockedTurns, reply, session.HasPendingConfirmation, hasToolEvidence,
                _goalBlockerKey, _goalIdleTurns, usedTool);

            GoalAuditResult audit = await EvaluateGoalAuditAsync(
                session, entries, instructionId, objective, reply, decision, cts);

            // 等待審計期間玩家可能編輯、刪除或暫停目標；即使是同一個字串也不能沿用舊結果。
            if (!GoalRunIsCurrent(objective, generation, cts)) return;

            if (audit != null)
            {
                GoalAuditVerdict verdict = GoalProgress.CombineAudit(
                    audit.AllDoneProbability, audit.RemainingProbability, audit.NeedsUserProbability);
                HandleAuditedGoalDecision(session, objective, reply, decision, audit, verdict);
            }
            else
            {
                HandleGoalDecision(session, objective, reply, decision);
            }
        }
        catch (Exception ex)
        {
            HandleGoalRunError(ex, objective, generation, cts, session);
        }
        finally
        {
            FinalizeGoalRun(exclusive, session, cts, generation);
        }
    }

    /// <summary>判斷是否要送審計，並等待結果。只有「完成」且無待確認命令時才稽核。</summary>
    private async Task<GoalAuditResult> EvaluateGoalAuditAsync(
        ColonyAgentSession session, IReadOnlyList<TranscriptEntry> entries, int instructionId,
        string objective, string reply, GoalTurnDecision decision, CancellationTokenSource cts)
    {
        // 審計只能否決完成：沒有工具證據的「完成」在 AfterTurn 就不算完成，也不送審計升級。
        // 有待確認的命令時目標還沒走完，稽核只會看到半截的結果：不稽核。
        if (reply == null || session.HasPendingConfirmation || !decision.Complete) return null;
        DecisionGoalAuditor auditor = GoalAuditor;
        if (auditor == null) return null;
        string toolEvidence = GoalProgress.BuildAuditEvidence(entries, instructionId);
        // 稽核回來後要繼續在主線程處理目標狀態；切到執行緒池會讓後續的存檔／UI 更新離開主線程。
        return await auditor.AuditAsync(objective, reply, toolEvidence, cts.Token);
    }

    /// <summary>處理目標執行失敗：記錄封鎖輪數，超過閾值則暫停目標。</summary>
    private void HandleGoalRunError(Exception ex, string objective, int generation, CancellationTokenSource cts, ColonyAgentSession session)
    {
        if (generation != _goalGeneration || cts.IsCancellationRequested) return;
        _goalBlockedTurns = _goalBlockerKey == "model-error" ? _goalBlockedTurns + 1 : 1;
        _goalBlockerKey = "model-error";
        _goalIdleTurns = 0;
        if (_goalBlockedTurns >= GoalProgress.MaxConsecutiveBlocks)
        {
            _goalPaused = true;
            _goalPauseReason = GoalPauseReason.Error;
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Goal paused after repeated errors. Use /goal resume to continue: " + objective
            });
        }
        WarnOnce("RimAgent goal run failed: " + ex.Message);
    }

    /// <summary>收尾目標執行：釋放獨占工作、清除 CTS、重設執行中旗標。</summary>
    private void FinalizeGoalRun(bool exclusive, ColonyAgentSession session, CancellationTokenSource cts, int generation)
    {
        if (exclusive) session.EndExclusiveWork();
        if (ReferenceEquals(_goalCts, cts)) _goalCts = null;
        cts.Dispose();
        if (generation == _goalGeneration)
        {
            _goalRunning = false;
            _goalNextRunAt = DateTime.UtcNow.AddSeconds(1);
        }
    }

    private bool GoalRunIsCurrent(string objective, int generation, CancellationTokenSource cts)
    {
        return generation == _goalGeneration
            && !cts.IsCancellationRequested
            && _goals.Count > 0
            && string.Equals(_goals[0], objective, StringComparison.Ordinal)
            && !_goalPaused;
    }

    private static bool HasActiveGoalToolUsage(IEnumerable<TranscriptEntry> entries, int instructionId)
    {
        // 記憶與網頁搜尋不碰殖民地，不算推進目標；否則每輪讀 preferences.md 就能躲過閒置暫停。
        // 委派本身也不算，子代理實際跑的工具另有紀錄。
        return entries.Any(e => e.InstructionId == instructionId && e.Kind == TranscriptKind.Tool
            && e.ToolCategory != ToolCategory.Memory && e.ToolCategory != ToolCategory.Web
            && e.ToolCategory != ToolCategory.Agents
            && !e.Failed && !(e.Result ?? string.Empty).StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal));
    }

    internal void HandleGoalDecision(ColonyAgentSession session, string objective, string reply, GoalTurnDecision decision)
    {
        _goalTurns = decision.Turns;
        _goalBlockedTurns = decision.BlockedTurns;
        _goalIdleTurns = decision.IdleTurns;
        _goalBlockerKey = decision.BlockerKey;
        _goalNeedsVerification = reply != null && GoalProgress.ParseStatus(reply) == GoalTurnStatus.Complete
            && !decision.Complete;
        if (reply != null && !session.HasPendingConfirmation)
        {
            _goalLastProgress = TranscriptEntryData.Truncate(GoalProgress.VisibleReply(reply));
        }
        if (decision.Complete)
        {
            _lastCompletedGoal = objective;
            _goals.RemoveAt(0);
            _goalTurns = 0;
            _goalBlockedTurns = 0;
            _goalIdleTurns = 0;
            _goalBlockerKey = null;
            _goalNeedsVerification = false;
            _goalAuditNote = null;
            _goalLastProgress = null;
            _goalPauseReason = GoalPauseReason.None;
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Goal completed and verified: " + objective
            });
        }
        else if (decision.Pause)
        {
            _goalPaused = true;
            DetermineGoalPauseDetails(decision, reply, objective, out _goalPauseReason, out string noticeText);
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = noticeText
            });
        }
    }

    internal void HandleAuditedGoalDecision(
        ColonyAgentSession session,
        string objective,
        string reply,
        GoalTurnDecision decision,
        GoalAuditResult audit,
        GoalAuditVerdict verdict)
    {
        _goalTurns = decision.Turns;
        _goalIdleTurns = decision.IdleTurns;

        if (reply != null && !session.HasPendingConfirmation)
        {
            _goalLastProgress = TranscriptEntryData.Truncate(GoalProgress.VisibleReply(reply));
        }

        // 審計只能否決完成，不能把沒有工具證據的回合升級成完成。
        if (verdict == GoalAuditVerdict.Complete && !decision.Complete)
        {
            verdict = GoalAuditVerdict.Continue;
        }

        switch (verdict)
        {
            case GoalAuditVerdict.Blocked:
                _goalPaused = true;
                _goalPauseReason = GoalPauseReason.Blocked;
                _goalBlockedTurns = 1;
                _goalBlockerKey = "audit-needs-user";
                _goalNeedsVerification = false;
                _goalAuditNote = null;
                session.Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = FormatNotice("RimAgent.Goal.AuditBlocked", "Goal paused by decision audit (player input required): {0}", objective)
                });
                break;

            case GoalAuditVerdict.Complete:
                _lastCompletedGoal = objective;
                _goals.RemoveAt(0);
                _goalTurns = 0;
                _goalBlockedTurns = 0;
                _goalIdleTurns = 0;
                _goalBlockerKey = null;
                _goalNeedsVerification = false;
                _goalAuditNote = null;
                _goalLastProgress = null;
                _goalPauseReason = GoalPauseReason.None;
                session.Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = FormatNotice("RimAgent.Goal.AuditCompleted", "Goal completed and verified by decision audit: {0}", objective)
                });
                break;

            default:
                // 稽核否決完成不等於把原本的暫停條件吃掉：回合上限、連續阻礙、連續沒有
                // 有效工具行動都要照原樣暫停，否則目標可能一直空轉。
                _goalBlockedTurns = decision.BlockedTurns;
                _goalBlockerKey = decision.BlockerKey;
                _goalNeedsVerification = true;
                _goalAuditNote = audit.Reason;
                if (decision.Pause)
                {
                    _goalPaused = true;
                    DetermineGoalPauseDetails(decision, reply, objective, out _goalPauseReason, out string pauseNotice);
                    session.Transcript.Add(new TranscriptEntry
                    {
                        Kind = TranscriptKind.Notice,
                        Text = pauseNotice
                    });
                }
                else
                {
                    _goalPaused = false;
                    _goalPauseReason = GoalPauseReason.None;
                    session.Transcript.Add(new TranscriptEntry
                    {
                        Kind = TranscriptKind.Notice,
                        Text = FormatNotice("RimAgent.Goal.AuditContinuing", "Decision audit determined the goal is not yet finished; continuing: {0}", objective)
                    });
                }
                break;
        }
    }

    private static string FormatNotice(string key, string fallback, string arg)
    {
        try
        {
            if (key.CanTranslate())
            {
                return key.Translate(arg).ToString();
            }
        }
        catch
        {
            // Translate 在非主執行緒或遊戲迴圈外可能拋出例外
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, fallback, arg);
    }

    private static void DetermineGoalPauseDetails(
        GoalTurnDecision decision,
        string reply,
        string objective,
        out GoalPauseReason reason,
        out string noticeText)
    {
        if (decision.Blocked)
        {
            if (reply == null)
            {
                reason = GoalPauseReason.Error;
                noticeText = "Goal paused after repeated errors. Use /goal resume to continue: " + objective;
            }
            else
            {
                reason = GoalPauseReason.Blocked;
                noticeText = "Goal blocked by the same condition for three turns. Use /goal resume after it changes: " + objective;
            }
        }
        else if (decision.NoProgress)
        {
            reason = GoalPauseReason.NoProgress;
            noticeText = "Goal paused after three turns without a successful tool action. Use /goal resume to continue: " + objective;
        }
        else
        {
            reason = GoalPauseReason.TurnLimit;
            noticeText = "Goal paused at the turn limit. Use /goal resume to continue: " + objective;
        }
    }
}
