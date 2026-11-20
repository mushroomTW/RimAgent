using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.UI;

namespace RimAgent.Tests;

/// <summary>對話中連續工具的群組判斷。</summary>
[TestFixture]
public class ToolGroupTests
{
    /// <summary>回歸：推理模型每步都有一則思考，夾在工具之間時把「已執行 N 個工具」切成一步一組。</summary>
    [Test]
    public void ReasoningBetweenToolsDoesNotBreakTheGroup()
    {
        var entries = new List<TranscriptEntry>
        {
            new() { Kind = TranscriptKind.User },
            new() { Kind = TranscriptKind.Reasoning },
            new() { Kind = TranscriptKind.Tool },
            new() { Kind = TranscriptKind.Reasoning },
            new() { Kind = TranscriptKind.Tool },
        };

        Assert.That(Window_Agent.PreviousIsTool(entries, 2), Is.False, "第一個工具前只有玩家的話與思考，開新群組");
        Assert.That(Window_Agent.PreviousIsTool(entries, 4), Is.True, "中間的思考不打斷群組");
    }
}
