extern alias ste;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;

namespace RimAgent.Tools
{
    /// <summary>
    /// 包住每個工具：被 FunctionInvokingChatClient 叫用時即時寫入行動紀錄（玩家不必等整個迴圈
    /// 結束才看到 agent 在做什麼），在呼叫前後把工具的靜態風險告知閘門，
    /// 並在自動模式下做兩段式執行：先強制預覽拿到「將做的事」，交給分類器判定，
    /// 低風險再強制執行一次，高風險就把預覽當「需確認」回給模型。
    /// 這裡刻意不用 ConfigureAwait(false)：續行要留在主線程，第二段執行才能碰遊戲狀態。
    /// </summary>
    internal sealed class LoggingAIFunction : DelegatingAIFunction
    {
        // 稽核與風險分類必須看見完整引數，不能截斷交易清單或銀額。
        private const int MaxArgumentChars = int.MaxValue;

        private readonly AgentTranscript _transcript;
        private readonly ToolCategory _category;
        private readonly ToolRisk _risk;
        private readonly ConfirmationGate _gate;
        private readonly RiskClassifier _classifier;

        private bool RequiresExplicitConfirmation =>
            string.Equals(Name, "mcp_call_tool", StringComparison.Ordinal);

        public LoggingAIFunction(AIFunction inner, AgentTranscript transcript, ToolCategory category, ToolRisk risk, ConfirmationGate gate, RiskClassifier classifier = null)
            : base(inner)
        {
            _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
            _category = category;
            _risk = risk;
            _gate = gate;
            _classifier = classifier;
        }

        protected override async ste::System.Threading.Tasks.ValueTask<object> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var entry = new TranscriptEntry
            {
                Kind = TranscriptKind.Tool,
                ToolName = Name,
                ToolCategory = _category,
                Arguments = FormatArguments(arguments),
                InstructionId = _transcript.CurrentInstructionId
            };

            bool budgetConsumed = false;
            if (_gate != null && !_gate.TryConsumeToolBudget(out budgetConsumed))
            {
                entry.Result = ToolText.Error("autonomous tool budget exhausted.");
                entry.Failed = true;
                _transcript.Add(entry);
                return entry.Result;
            }
            entry.BudgetConsumed = budgetConsumed;

            _gate?.BeginInvocation(_risk);
            try
            {
                object result;
                if (_gate != null && RequiresExplicitConfirmation)
                {
                    result = await InvokeExplicitConfirmationAsync(arguments, cancellationToken);
                }
                else if (_gate != null && _classifier != null && _gate.NeedsClassification(_risk))
                {
                    result = await InvokeClassifiedAsync(arguments, entry, cancellationToken);
                }
                else
                {
                    result = await base.InvokeCoreAsync(arguments, cancellationToken);
                }
                string text = result?.ToString() ?? string.Empty;
                entry.Result = text;
                entry.Failed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
                if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
                {
                    // 此閉包保留本次工具呼叫的引數，確認時不會再讓模型選工具或改參數。
                    _gate?.RegisterPendingApproval(token => ExecuteApprovedAsync(arguments, entry, token));
                }
                return result;
            }
            catch (Exception ex)
            {
                entry.Result = ex.GetType().Name + ": " + ex.Message;
                entry.Failed = true;
                throw;
            }
            finally
            {
                _gate?.EndInvocation();
                _transcript.Add(entry);
            }
        }

        private async Task<object> ExecuteApprovedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
        {
            _gate?.ForceExecute();
            try
            {
                object result = await base.InvokeCoreAsync(arguments, cancellationToken);
                string text = result?.ToString() ?? string.Empty;
                _transcript.UpdateToolResult(entry, text, text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal));
                return result;
            }
            catch (Exception ex)
            {
                _transcript.UpdateToolResult(entry, ex.GetType().Name + ": " + ex.Message, true);
                throw;
            }
            finally
            {
                _gate?.ClearForce();
            }
        }

        /// <summary>MCP 以使用者權限執行本機程式；所有可執行模式都必須先取得玩家明確確認。</summary>
        private async Task<object> InvokeExplicitConfirmationAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            object preview;
            _gate.ForcePreview();
            try
            {
                preview = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            finally
            {
                _gate.ClearForce();
            }

            string text = preview?.ToString() ?? string.Empty;
            if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
            {
                _gate.NoteBlocked(text);
            }
            return preview;
        }

        /// <summary>自動模式：預覽 → 分類 → 執行或擋下。工具的預覽分支不碰世界，靠 CommandGateTests 鎖住。</summary>
        private async Task<object> InvokeClassifiedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
        {
            object previewResult;
            _gate.ForcePreview();
            try
            {
                previewResult = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            finally
            {
                _gate.ClearForce();
            }

            string preview = previewResult?.ToString() ?? string.Empty;
            // 沒走到閘門（參數錯、目標不存在）就沒有可判定的命令，原樣回給模型。
            if (!preview.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal)) return previewResult;

            RiskVerdict verdict = await _classifier.ClassifyAsync(new RiskQuestion
            {
                Instruction = _transcript.CurrentInstructionText,
                ToolName = Name,
                Arguments = entry.Arguments,
                Preview = preview.Substring(ToolText.ConfirmationPrefix.Length)
            }, cancellationToken);
            string riskCheck;
            if (!verdict.Risky)
            {
                riskCheck = "low";
            }
            else
            {
                riskCheck = verdict.Failed ? "unavailable" : "high";
            }
            entry.RiskCheck = riskCheck;

            if (verdict.Risky)
            {
                _gate.NoteBlocked(preview);
                return string.IsNullOrEmpty(verdict.Reason) ? preview : preview + " (risk check: " + verdict.Reason + ")";
            }

            _gate.ForceExecute();
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            finally
            {
                _gate.ClearForce();
            }
        }

        private static string FormatArguments(AIFunctionArguments arguments)
        {
            if (arguments == null || arguments.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (KeyValuePair<string, object> pair in arguments)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(pair.Key).Append('=').Append(pair.Value?.ToString() ?? "null");
            }
            if (sb.Length > MaxArgumentChars)
            {
                sb.Length = MaxArgumentChars;
                sb.Append('…');
            }
            return sb.ToString();
        }
    }
}
