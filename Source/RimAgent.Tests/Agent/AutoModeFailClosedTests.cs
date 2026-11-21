using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>
/// 自動模式的命令只能由分類器核可放行（ForceExecute）；沒有分類結果時（不在工具呼叫期間、
/// 或工具沒接上分類器）只放行查詢。
/// </summary>
[TestFixture]
public class AutoModeFailClosedTests
{
    [Test]
    public void AutoBlocksCommandsOutsideToolInvocation()
    {
        var gate = new ConfirmationGate(() => AgentMode.Auto);

        Assert.That(gate.AllowCommands(), Is.False);
    }

    [Test]
    public void AutoBlocksUnclassifiedCommand()
    {
        var gate = new ConfirmationGate(() => AgentMode.Auto);
        gate.BeginInvocation("place_blueprint", ToolRisk.Safe);

        Assert.That(gate.AllowCommands(), Is.False);
    }

    [Test]
    public void AutoAllowsQueryAndClassifierApprovedCommand()
    {
        var gate = new ConfirmationGate(() => AgentMode.Auto);
        gate.BeginInvocation("get_colonists", ToolRisk.Query);
        Assert.That(gate.AllowCommands(), Is.True, "查詢不需分類");

        gate.BeginInvocation("place_blueprint", ToolRisk.Safe);
        gate.ForceExecute();
        Assert.That(gate.AllowCommands(), Is.True, "分類器核可後應放行");
    }

    [Test]
    public void FullAccessStillAllowsWithoutInvocation()
    {
        var gate = new ConfirmationGate(() => AgentMode.Autonomous);

        Assert.That(gate.AllowCommands(), Is.True, "完整權限依設計放行所有已啟用工具");
    }
}
