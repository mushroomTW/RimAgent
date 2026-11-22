using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.UI;

namespace RimAgent.Tests;

/// <summary>對話中連續工具的群組判斷。</summary>
[TestFixture]
public class ToolGroupTests
{
    /// <summary>思考與工具連在一起收成一段過程：推理模型每步都有思考，不能把摘要切成一步一行。</summary>
    [Test]
    public void ReasoningAndToolsFormOneProcess()
    {
        var entries = new List<TranscriptEntry>
        {
            new() { Kind = TranscriptKind.User },
            new() { Kind = TranscriptKind.Reasoning },
            new() { Kind = TranscriptKind.Tool },
            new() { Kind = TranscriptKind.Reasoning },
            new() { Kind = TranscriptKind.Tool },
            new() { Kind = TranscriptKind.Assistant },
            new() { Kind = TranscriptKind.Reasoning },
        };

        Assert.That(Window_Agent.ContinuesProcess(entries, 1), Is.False, "玩家的話之後的第一則思考開新的一段");
        Assert.That(Window_Agent.ContinuesProcess(entries, 2), Is.True, "思考後的工具屬於同一段");
        Assert.That(Window_Agent.ContinuesProcess(entries, 4), Is.True, "中間的思考不打斷");
        Assert.That(Window_Agent.ContinuesProcess(entries, 6), Is.False, "回覆之後重新開始");
    }

    [Test]
    public void ShieldMarksOnlyToolCallsTheDecisionModelReviewed()
    {
        Assert.That(Window_Agent.ReviewedByDecisionModel(new TranscriptEntry { Kind = TranscriptKind.Tool, RiskCheckSource = "decision" }), Is.True);
        Assert.That(Window_Agent.ReviewedByDecisionModel(new TranscriptEntry { Kind = TranscriptKind.Tool, RiskCheckSource = "fallback" }), Is.False, "決策模型失敗、改用規則分類器");
        Assert.That(Window_Agent.ReviewedByDecisionModel(new TranscriptEntry { Kind = TranscriptKind.Tool }), Is.False, "確認模式或沒經過審查");
    }

    [Test]
    public void PendingToolLaterInTheProcessIsSeenFromItsStart()
    {
        var entries = new List<TranscriptEntry>
        {
            new() { Kind = TranscriptKind.Reasoning },
            new() { Kind = TranscriptKind.Tool, Result = RimAgent.Tools.ToolText.ConfirmationPrefix + "build" },
            new() { Kind = TranscriptKind.Assistant },
            new() { Kind = TranscriptKind.Reasoning },
            new() { Kind = TranscriptKind.Tool, Result = "ok" },
        };

        Assert.That(Window_Agent.ProcessHasPending(entries, 0), Is.True, "思考排在待確認工具前面，建思考列時就要知道整段要展開");
        Assert.That(Window_Agent.ProcessHasPending(entries, 3), Is.False, "回覆之後是另一段");
    }

    [Test]
    public void ShieldStaysInsideTheLineAndClearOfWrappedText()
    {
        var line = new UnityEngine.Rect(10f, 0f, 200f, 44f);

        UnityEngine.Rect shortText = Window_Agent.ShieldRect(line, 50f, 22f);
        UnityEngine.Rect wrapped = Window_Agent.ShieldRect(line, 500f, 22f);

        Assert.That(shortText.x, Is.EqualTo(10f + 50f + 6f), "單行時緊跟文字");
        Assert.That(wrapped.xMax, Is.LessThanOrEqualTo(line.xMax), "不超出列寬");
        Assert.That(wrapped.x, Is.GreaterThanOrEqualTo(line.xMax - Window_Agent.ShieldReserve), "換行時在預留給盾牌的空白裡，不壓字");
    }

    [Test]
    public void ShieldTipIsCachedPerWindowSoLanguageChangesApply()
    {
        System.Reflection.FieldInfo field = typeof(Window_Agent).GetField("_shieldTip",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "靜態快取會讓換語言後的提示停在舊語言，直到重開遊戲");
    }
}
