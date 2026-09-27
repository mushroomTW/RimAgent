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
    /// 分類器核可才強制執行一次；未核可則拒絕該命令並回報原因。
    /// 這裡刻意不用 ConfigureAwait(false)：續行要留在主線程，第二段執行才能碰遊戲狀態。
    ///
    /// 同一輪多個查詢可由框架一起送出：每個呼叫的暫存狀態只用區域變數與 AsyncLocal，
    /// 共用狀態（閘門、快取、行動紀錄）各自上鎖，因此並行查詢安全；命令仍由閘門逐次把關。
    ///
    /// 另外兩道與世界有關的守衛也在這裡，工具本身不必管：
    /// 遊戲沒在進行（讀檔中、回到主選單）時不碰工具直接回錯誤；
    /// 工具回傳後把這段期間遊戲記錄的錯誤附在結果後面，模型才知道命令雖然「成功」但遊戲隨即噴錯。
    /// </summary>
    internal sealed class LoggingAIFunction : DelegatingAIFunction
    {
        private const int MaxGameErrorsAppended = 5;

        private readonly AgentTranscript _transcript;
        private readonly IGameWorld _world;
        private readonly ToolCategory _category;
        private readonly ToolRisk _risk;
        private readonly ConfirmationGate _gate;
        private readonly RiskClassifier _classifier;

        private bool RequiresExplicitConfirmation =>
            _gate != null && _gate.RequiresExplicitConfirmation(Name);

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
                ToolRisk = _risk,
                Arguments = FormatArguments(arguments),
                InstructionId = _transcript.CurrentInstructionId,
                Agent = _transcript.CurrentAgent
            };

            if (TryHandlePreInvocation(entry, out string earlyResult))
            {
                return earlyResult;
            }

            _gate?.BeginInvocation(Name, _risk);
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
                string text = ToolOutputLimit.Apply(AppendGameErrors(result?.ToString() ?? string.Empty, out bool hasFreshGameErrors));
                entry.Result = text;
                entry.Failed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
                bool isPreview = text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal);
                // 命令動過世界（成功、失敗或部分生效皆可能）：同句的查詢快取全部作廢；
                // 預覽不碰世界，不必失效。
                if (_risk != ToolRisk.Query && !isPreview) _gate?.InvalidateQueryCache();
                // 成功的查詢才進快取：錯誤可能隨同句後續命令改變而消失，
                // 夾帶遊戲錯誤的結果也不記，免得下一次命中回傳過期的錯誤。
                if (_risk == ToolRisk.Query && !entry.Failed && !hasFreshGameErrors && !isPreview)
                {
                    _gate?.StoreCachedQuery(Name, entry.Arguments, text);
                }
                if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
                {
                    // 此閉包保留本次工具呼叫的引數，確認時不會再讓模型選工具或改參數。
                    _gate?.RegisterPendingApproval(Name, token => ExecuteApprovedAsync(arguments, entry, token));
                }
                return text;
            }
            catch (Exception ex)
            {
                // 例外那次期間的遊戲錯誤也算這次的，不能留給下一個工具呼叫背鍋。
                // 命令可能部分生效，同句的查詢快取一併作廢。
                if (_risk != ToolRisk.Query) _gate?.InvalidateQueryCache();
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

        private bool TryHandlePreInvocation(TranscriptEntry entry, out string earlyResult)
        {
            string notReady = _world.GetNotReadyReason();
            if (notReady != null)
            {
                entry.Result = ToolText.Error($"the game cannot run tools right now ({notReady}). Tell the player and stop.");
                entry.Failed = true;
                _transcript.Add(entry);
                earlyResult = entry.Result;
                return true;
            }

            if (_risk == ToolRisk.Query && _gate != null &&
                _gate.TryGetCachedQuery(Name, entry.Arguments, out string cached))
            {
                entry.Result = AppendGameErrors(cached);
                entry.Failed = entry.Result.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
                entry.BudgetConsumed = false;
                _transcript.Add(entry);
                earlyResult = entry.Result;
                return true;
            }

            bool budgetConsumed = false;
            if (_gate != null && !_gate.TryConsumeToolBudget(out budgetConsumed))
            {
                entry.Result = ToolText.Error("autonomous tool budget exhausted.");
                entry.Failed = true;
                _transcript.Add(entry);
                earlyResult = entry.Result;
                return true;
            }
            entry.BudgetConsumed = budgetConsumed;
            earlyResult = null;
            return false;
        }

        private async Task<object> ExecuteApprovedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
        {
            _gate?.BeginInvocation(Name, _risk);
            _gate?.ForceExecute();
            try
            {
                object result = await base.InvokeCoreAsync(arguments, cancellationToken);
                string text = ToolOutputLimit.Apply(AppendGameErrors(result?.ToString() ?? string.Empty));
                // 確認執行是真正的世界寫入：同句的查詢快取作廢。
                _gate?.InvalidateQueryCache();
                _transcript.UpdateToolResult(entry, text, text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal));
                return text;
            }
            catch (Exception ex)
            {
                _gate?.InvalidateQueryCache();
                _transcript.UpdateToolResult(entry, AppendGameErrors(ex.GetType().Name + ": " + ex.Message), true);
                throw;
            }
            finally
            {
                _gate?.EndInvocation();
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

        /// <summary>自動模式：黑名單直接擋、預覽 → 分類 → 執行或擋下。工具的預覽分支不碰世界，靠 CommandGateTests 鎖住。</summary>
        private async Task<object> InvokeClassifiedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
        {
            // 高危黑名單在自動模式永遠是高風險：直接拒絕，不必再燒一次分類器模型。
            if (AgentToolRegistry.IsYoloForbidden(Name))
            {
                entry.RiskCheck = "high";
                return ToolText.Error("safety review rejected " + Name + " (high): " +
                    "this tool is on the high-risk blacklist and never runs in Auto mode. " +
                    "Do not retry it unchanged.");
            }
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
                // 分類器也是模型呼叫：預覽過長會直接撐爆它的視窗，這裡照同一個上限縮。
                Preview = ToolOutputLimit.Apply(preview.Substring(ToolText.ConfirmationPrefix.Length))
            }, cancellationToken);
            string riskCheck;
            if (verdict.Failed)
            {
                riskCheck = "unavailable";
            }
            else if (verdict.Risky)
            {
                riskCheck = "high";
            }
            else
            {
                // 低風險但玩家沒要求：擋下的理由跟「危險」不同，紀錄要分得出來。
                riskCheck = verdict.Authorized ? "low" : "unrequested";
            }
            entry.RiskCheck = riskCheck;

            if (verdict.ShouldReject)
            {
                string reason = string.IsNullOrEmpty(verdict.Reason) ? riskCheck : verdict.Reason;
                return ToolText.Error("safety review rejected " + Name + " (" + riskCheck + "): " + reason
                    + ". This action cannot be confirmed in Auto mode; do not retry it unchanged.");
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
            return AppendGameErrors(text, out _);
        }

        private string AppendGameErrors(string text, out bool hasFreshGameErrors)
        {
            IReadOnlyList<string> errors = _world.TakeNewGameErrors();
            hasFreshGameErrors = errors.Count > 0;
            return FormatWithGameErrors(text, errors);
        }

        private static string FormatWithGameErrors(string text, IReadOnlyList<string> errors)
        {
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
            return sb.ToString();
        }
    }
}
