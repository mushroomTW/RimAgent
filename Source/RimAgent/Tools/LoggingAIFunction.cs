extern alias ste;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.AI;
using RimAgent.Agent;

namespace RimAgent.Tools
{
    /// <summary>
    /// 包住每個工具，在被 FunctionInvokingChatClient 叫用時即時寫入行動紀錄，
    /// 玩家不必等整個迴圈結束才看到 agent 在做什麼。
    /// </summary>
    internal sealed class LoggingAIFunction : DelegatingAIFunction
    {
        private const int MaxArgumentChars = 300;

        private readonly AgentTranscript _transcript;
        private readonly ToolCategory _category;

        public LoggingAIFunction(AIFunction inner, AgentTranscript transcript, ToolCategory category)
            : base(inner)
        {
            _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
            _category = category;
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

            try
            {
                object result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
                string text = result?.ToString() ?? string.Empty;
                entry.Result = text;
                entry.Failed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
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
                _transcript.Add(entry);
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
