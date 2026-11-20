using NUnit.Framework;
using RimAgent.InGameTests;

namespace RimAgent.Tests;

/// <summary>運行紀錄統計（開發模式按鈕「Run log stats」用的報表）。</summary>
[TestFixture]
public class RunLogStatsTests
{
    private const string NewRun =
        """{"StartedUtc":"2026-11-20T07:30:46Z","Outcome":"finished","ModVersion":"2026.11.20.11","ToolCalls":3,"ToolFailures":1,"ToolCancellations":1,"ModelSteps":[{"Model":"m1","InputTokens":1000}],"TranscriptSteps":[{"Kind":"Tool","Tool":"place_blueprint","Failed":true,"Result":"Error: cell (12,34) is blocked","DurationMs":20},{"Kind":"Tool","Tool":"place_blueprint","Failed":false,"DurationMs":40},{"Kind":"Tool","Tool":"wait_for_time","Failed":true,"Cancelled":true,"Result":"TaskCanceledException: A task was canceled."}]}""";

    /// <summary>舊紀錄沒有計數與 Cancelled 欄位：由保留的步驟推算，取消以例外名稱判斷。</summary>
    private const string OldRun =
        """{"StartedUtc":"2026-11-01T00:00:00Z","Outcome":"cancelled","ModVersion":"2026.11.01.1","ModelSteps":[{"Model":"m2","InputTokens":500}],"TranscriptSteps":[{"Kind":"Tool","Tool":"wait_for_time","Failed":true,"Result":"OperationCanceledException: x"},{"Kind":"Tool","Tool":"place_blueprint","Failed":true,"Result":"Error: cell (1,2) is blocked"}]}""";

    [Test]
    public void ReportCountsToolsAndExcludesCancellations()
    {
        string report = RunLogStats.Format([NewRun, OldRun]);

        Assert.That(report, Does.StartWith("2 runs, 2026-11-20T07:30 ~ 2026-11-01T00:00"));
        // 新紀錄 3 呼叫 1 失敗 1 取消；舊紀錄推算為 2 呼叫 1 失敗 1 取消。
        Assert.That(report, Does.Contain("tool calls 5, failed 2 (40.0%), cancelled 2"));
        Assert.That(report, Does.Contain("place_blueprint (2/3)"), "取消的 wait_for_time 不算進工具明細");
        Assert.That(report, Does.Contain("2  Error: cell (N,N) is blocked"), "數字換成 N 才能歸在同一類");
        Assert.That(report, Does.Not.Contain("wait_for_time ("));
        Assert.That(report, Does.Contain("== by model").And.Contain("m1").And.Contain("m2"));
        Assert.That(report, Does.Contain("== by mod version").And.Contain("2026.11.01.1"));
    }

    [Test]
    public void VersionFilterAndBrokenLinesAreHandled()
    {
        string report = RunLogStats.Format([NewRun, "{\"truncated", string.Empty, OldRun], "2026.11.20");

        Assert.That(report, Does.StartWith("1 runs,"));
        Assert.That(RunLogStats.Format([OldRun], "nope"), Is.EqualTo("No matching runs."));
    }
}
