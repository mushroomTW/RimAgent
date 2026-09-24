using System.Collections.Generic;
using RimAgent.Tools;
using Verse;

namespace RimAgent.Agent
{
    /// <summary>
    /// 一則對話紀錄的存檔形態。這裡保存玩家看得到的對話：指令、回覆與行動紀錄
    /// （AF session 的模型上下文則由 GameComponent 另外序列化存檔）。
    /// 讀檔後可見對話完整還原，玩家可瀏覽過去指令與操作歷程。
    /// </summary>
    public sealed class TranscriptEntryData : IExposable
    {
        /// <summary>存檔上限：對話只留最近 200 則，避免存檔膨脹。</summary>
        public const int MaxSavedEntries = 200;

        /// <summary>單一欄位上限（工具結果可能很長，存檔時截斷）。</summary>
        public const int MaxFieldChars = 2000;

#pragma warning disable S1104 // RimWorld Scribe requires public fields passed by ref
        public int kind;
        public string text = string.Empty;
        public int instructionId;
        public string toolName = string.Empty;
        public int toolCategory;
        public string arguments = string.Empty;
        public string result = string.Empty;
        public bool failed;
        public bool budgetConsumed;
        public bool toolsStripped;
        public int errorKind;
        public string riskCheck = string.Empty;
        public bool autonomous;
        public bool goalRun;
#pragma warning restore S1104

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref text, "text", string.Empty);
            Scribe_Values.Look(ref instructionId, "instruction", 0);
            Scribe_Values.Look(ref toolName, "tool", string.Empty);
            Scribe_Values.Look(ref toolCategory, "category", 0);
            Scribe_Values.Look(ref arguments, "args", string.Empty);
            Scribe_Values.Look(ref result, "result", string.Empty);
            Scribe_Values.Look(ref failed, "failed", false);
            Scribe_Values.Look(ref budgetConsumed, "budgetConsumed", false);
            Scribe_Values.Look(ref toolsStripped, "stripped", false);
            Scribe_Values.Look(ref errorKind, "error", 0);
            Scribe_Values.Look(ref riskCheck, "risk", string.Empty);
            Scribe_Values.Look(ref autonomous, "autonomous", false);
            Scribe_Values.Look(ref goalRun, "goalRun", false);
        }

        public TranscriptEntry ToEntry()
        {
            return new TranscriptEntry
            {
                Kind = (TranscriptKind)kind,
                Text = text ?? string.Empty,
                InstructionId = instructionId,
                ToolName = toolName ?? string.Empty,
                ToolCategory = (ToolCategory)toolCategory,
                Arguments = arguments ?? string.Empty,
                Result = result ?? string.Empty,
                Failed = failed,
                BudgetConsumed = budgetConsumed,
                ToolsStripped = toolsStripped,
                ErrorKind = (AgentErrorKind)errorKind,
                RiskCheck = riskCheck ?? string.Empty,
                Autonomous = autonomous,
                GoalRun = goalRun
            };
        }

        public static List<TranscriptEntryData> FromTranscript(AgentTranscript transcript)
        {
            var data = new List<TranscriptEntryData>();
            if (transcript == null) return data;
            IReadOnlyList<TranscriptEntry> entries = transcript.Snapshot();
            int start = entries.Count > MaxSavedEntries ? entries.Count - MaxSavedEntries : 0;
            for (int i = start; i < entries.Count; i++)
            {
                TranscriptEntry e = entries[i];
                data.Add(new TranscriptEntryData
                {
                    kind = (int)e.Kind,
                    text = Truncate(e.Text),
                    instructionId = e.InstructionId,
                    toolName = e.ToolName ?? string.Empty,
                    toolCategory = (int)e.ToolCategory,
                    arguments = Truncate(e.Arguments),
                    result = Truncate(e.Result),
                    failed = e.Failed,
                    budgetConsumed = e.BudgetConsumed,
                    toolsStripped = e.ToolsStripped,
                    errorKind = (int)e.ErrorKind,
                    riskCheck = e.RiskCheck ?? string.Empty,
                    autonomous = e.Autonomous,
                    goalRun = e.GoalRun
                });
            }
            return data;
        }

        internal static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length > MaxFieldChars ? value.Substring(0, MaxFieldChars) + "…" : value;
        }
    }
}
