#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using RimAgent.Game;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

public sealed partial class ColonyAgentSession
{
    /// <summary>執行等待中的確認：用預覽當下捕捉的原工具呼叫與引數，不再重送模型。</summary>
    /// <param name="rememberForSession">
    /// 把這批工具記成「本局不再問」：之後確認模式下同名工具直接執行。
    /// 只記工具名稱，重開遊戲即失效；MCP 工具永遠不記。
    /// </param>
    /// <returns>執行結果文字；若無等待中的確認則為 null。</returns>
    public async Task<string> ConfirmAsync(bool rememberForSession = false, CancellationToken cancellationToken = default)
    {
        string text = _pendingConfirmation;
        if (text == null || !ConfirmGate.HasPendingApprovals) return null;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new AgentBusyException();
        }
        int instructionId = Transcript.CurrentInstructionId;
        RunLogRecord confirmLog = RunLoggingEnabled?.Invoke() == true ? new RunLogRecord
        {
            Instruction = RunLogRecord.Limit(text),
            Mode = Mode.ToString(),
            Source = "confirmation"
        } : null;
        string goalObjective = _pendingGoalObjective;
        string confirmOutcome = "confirmation_failed";
        try
        {
            Undo.BeginFrame();
            _world.TakeNewGameErrors();
            // 不切離主線程：ApprovePendingAsync 會逐批執行工具，工具必須在遊戲主線程碰世界。
            int executed = await ConfirmGate.ApprovePendingAsync(cancellationToken, rememberForSession);
            Undo.EndFrame(text + " (confirmed)");
            _pendingConfirmation = null;
            _pendingGoalObjective = null;
            string summary = string.Create(CultureInfo.InvariantCulture, $"Executed {executed} confirmed command(s).");
            if (rememberForSession) summary += " Those tools will not ask again this session.";
            ReportConfirmation(summary, TranscriptKind.Notice, errorKind: default, goalObjective, instructionId);
            confirmOutcome = "confirmed";
            return summary;
        }
        catch (Exception ex)
        {
            Undo.EndFrame(text + " (confirmation failed)");
            int remaining = ConfirmGate.PendingApprovalCount;
            int executed = ConfirmGate.LastApprovalExecutedCount;
            if (remaining == 0)
            {
                _pendingConfirmation = null;
                _pendingGoalObjective = null;
            }
            string failure = string.Create(CultureInfo.InvariantCulture, $"Confirmation stopped after {executed} command(s); {remaining} command(s) still await confirmation. ") +
                "The failed command will not be retried automatically. " + ex.Message;
            ReportConfirmation(failure, TranscriptKind.Error, Classify(ex), goalObjective, instructionId);
            return null;
        }
        finally
        {
            TryWriteRunLog(confirmLog, instructionId, confirmOutcome);
            Volatile.Write(ref _busy, 0);
        }
    }

    private void ReportConfirmation(string text, TranscriptKind kind, AgentErrorKind errorKind, string goalObjective, int instructionId)
    {
        if (goalObjective != null)
        {
            _confirmedGoalObjective = goalObjective;
            _goalConfirmationReport = BuildGoalConfirmationReport(text, instructionId);
        }
        Transcript.Add(new TranscriptEntry
        {
            Kind = kind,
            ErrorKind = errorKind,
            Text = text,
            InstructionId = Transcript.CurrentInstructionId
        });
    }

    public void DiscardPendingConfirmation()
    {
        if (_pendingConfirmation == null) return;
        string instruction = _pendingConfirmation;
        int instructionId = Transcript.CurrentInstructionId;
        _pendingConfirmation = null;
        _pendingGoalObjective = null;
        ConfirmGate.DiscardPendingApprovals();
        Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = "Pending confirmation discarded.",
            InstructionId = Transcript.CurrentInstructionId
        });
        RunLogRecord discardLog = RunLoggingEnabled?.Invoke() == true ? new RunLogRecord
        {
            Instruction = RunLogRecord.Limit(instruction),
            Mode = Mode.ToString(),
            Source = "confirmation"
        } : null;
        TryWriteRunLog(discardLog, instructionId, "discarded");
    }

    private string BuildGoalConfirmationReport(string summary, int instructionId)
    {
        var lines = new List<string> { summary };
        foreach (TranscriptEntry entry in Transcript.Snapshot().Where(e =>
            e.InstructionId == instructionId && e.Kind == TranscriptKind.Tool))
        {
            lines.Add(entry.ToolName + " (" + entry.Arguments + "): " +
                (entry.Failed ? "failed: " : string.Empty) + RunLogRecord.Limit(entry.Result));
        }
        string report = string.Join('\n', lines);
        return report.Length <= 4000 ? report : report[..4000] + "…";
    }
}
