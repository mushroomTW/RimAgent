extern alias ste;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>
    /// 包住每個工具：被 FunctionInvokingChatClient 叫用時即時寫入行動紀錄（玩家不必等整個迴圈
    /// 結束才看到 agent 在做什麼），在呼叫前後把工具的靜態風險告知閘門，
    /// 並在自動模式下做兩段式執行：先強制預覽拿到「將做的事」，交給分類器判定，
    /// 低風險再強制執行一次，高風險就把預覽當「需確認」回給模型。
    /// 這裡刻意不用 ConfigureAwait(false)：續行要留在主線程，第二段執行才能碰遊戲狀態。
    ///
    /// 另外兩道與世界有關的守衛也在這裡，工具本身不必管：
    /// 遊戲沒在進行（讀檔中、回到主選單）時不碰工具直接回錯誤；
    /// 工具回傳後把這段期間遊戲記錄的錯誤附在結果後面，模型才知道命令雖然「成功」但遊戲隨即噴錯。
    /// </summary>
    internal sealed class LoggingAIFunction : DelegatingAIFunction
    {
        // 稽核與風險分類必須看見完整引數，不能截斷交易清單或銀額。
        private const int MaxArgumentChars = int.MaxValue;
        private const int MaxGameErrorsAppended = 5;

        private readonly AgentTranscript _transcript;
        private readonly IGameWorld _world;
        private readonly ToolCategory _category;
        private readonly ToolRisk _risk;
        private readonly ConfirmationGate _gate;
        private readonly RiskClassifier _classifier;

        private bool RequiresExplicitConfirmation =>
            string.Equals(Name, "mcp_call_tool", StringComparison.Ordinal);

        public LoggingAIFunction(AIFunction inner, AgentTranscript transcript, IGameWorld world, ToolCategory category, ToolRisk risk, ConfirmationGate gate, RiskClassifier classifier = null)
            : base(inner)
        {
            _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
            _world = world ?? throw new ArgumentNullException(nameof(world));
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

            string notReady = _world.GetNotReadyReason();
            if (notReady != null)
            {
                entry.Result = ToolText.Error($"the game cannot run tools right now ({notReady}). Tell the player and stop.");
                entry.Failed = true;
                _transcript.Add(entry);
                return entry.Result;
            }

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
                string text = AppendGameErrors(result?.ToString() ?? string.Empty);
                entry.Result = text;
                entry.Failed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
                if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
                {
                    // 此閉包保留本次工具呼叫的引數，確認時不會再讓模型選工具或改參數。
                    _gate?.RegisterPendingApproval(token => ExecuteApprovedAsync(arguments, entry, token));
                }
                return text;
            }
            catch (Exception ex)
            {
                // 例外那次期間的遊戲錯誤也算這次的，不能留給下一個工具呼叫背鍋。
                entry.Result = AppendGameErrors(ex.GetType().Name + ": " + ex.Message);
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
                string text = AppendGameErrors(result?.ToString() ?? string.Empty);
                _transcript.UpdateToolResult(entry, text, text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal));
                return text;
            }
            catch (Exception ex)
            {
                _transcript.UpdateToolResult(entry, AppendGameErrors(ex.GetType().Name + ": " + ex.Message), true);
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

        /// <summary>
        /// 把這次呼叫期間遊戲記錄的錯誤附在結果後面。工具回「成功」只代表命令送進遊戲了，
        /// 遊戲隨即噴的錯（例如藍圖放下後的例外）模型否則看不到。預覽也一樣附：它同樣在主線程跑過遊戲程式碼。
        /// </summary>
        private string AppendGameErrors(string text)
        {
            IReadOnlyList<string> errors = _world.TakeNewGameErrors();
            if (errors.Count == 0) return text;
            var sb = new StringBuilder(text);
            sb.AppendLine().Append("Game errors logged during this call (").Append(errors.Count).Append("):");
            foreach (string error in errors.Take(MaxGameErrorsAppended)) sb.AppendLine().Append("- ").Append(error);
            if (errors.Count > MaxGameErrorsAppended) sb.AppendLine().Append("- ... and ").Append(errors.Count - MaxGameErrorsAppended).Append(" more");
            return sb.ToString();
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
