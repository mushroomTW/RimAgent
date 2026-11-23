using NUnit.Framework;
using RimAgent.InGameTests;

namespace RimAgent.Tests;

/// <summary>運行紀錄統計（開發模式按鈕「Run log stats」用的報表）。</summary>
[TestFixture]
public class RunLogStatsTests
{
    private const string RunA =
        """{"t":"end","ts":"2026-11-20T07:30:46Z","file":"a","ver":"2026.11.20.11","outcome":"finished","models":["m1"],"in":1000,"calls":3,"fail":1,"cancel":1}""";

    private const string RunB =
        """{"t":"end","ts":"2026-11-21T00:00:00Z","file":"b","ver":"2026.11.21.1","outcome":"cancelled","models":["m2"],"in":500,"calls":1,"fail":1,"cancel":0}""";

    private const string Note =
        """{"t":"note","ts":"2026-11-21T00:00:01Z","src":"system_one","ver":"2026.11.21.1","outcome":"no action"}""";

    private static string[] ReadRun(string file) => file == "a"
        ?
        [
            """{"t":"run","text":"x"}""",
            """{"t":"tool","call":1,"tool":"place_blueprint","ok":false,"result":"Error: cell (12,34) is blocked","ms":20}""",
            """{"t":"tool","call":2,"tool":"place_blueprint","ok":true,"ms":40}""",
            """{"t":"tool","call":3,"tool":"wait_for_time","ok":false,"cancelled":true,"result":"TaskCanceledException: A task was canceled."}""",
            """{"t":"tool","trunc"""
        ]
        : ["""{"t":"tool","call":1,"tool":"place_blueprint","ok":false,"result":"Error: cell (1,2) is blocked"}"""];

    [Test]
    public void ReportCountsToolsAndExcludesCancellations()
    {
        string report = RunLogStats.Format([RunA, RunB, Note], ReadRun);

        Assert.That(report, Does.StartWith("2 runs, 2026-11-20T07:30 ~ 2026-11-21T00:00"), "note 不是一次執行");
        Assert.That(report, Does.Contain("tool calls 4, failed 2 (50.0%), cancelled 1"));
        Assert.That(report, Does.Contain("place_blueprint (2/3)"), "取消的 wait_for_time 不算進工具明細");
        Assert.That(report, Does.Contain("2  Error: cell (N,N) is blocked"), "數字換成 N 才能歸在同一類");
        Assert.That(report, Does.Not.Contain("wait_for_time ("));
        Assert.That(report, Does.Contain("== by model").And.Contain("m1").And.Contain("m2"));
        Assert.That(report, Does.Contain("== by mod version").And.Contain("2026.11.21.1"));
    }

    [Test]
    public void VersionFilterAndBrokenLinesAreHandled()
    {
        string report = RunLogStats.Format([RunA, "{\"truncated", string.Empty, RunB], ReadRun, "2026.11.20");

        Assert.That(report, Does.StartWith("1 runs,"));
        Assert.That(RunLogStats.Format([RunB], ReadRun, "nope"), Is.EqualTo("No matching runs."));
    }
}
