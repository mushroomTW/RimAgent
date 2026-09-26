using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RimAgent.UI;

namespace RimAgent.Tests
{
    [TestFixture]
    public class SlashCommandsTests
    {
        [Test]
        public void MatchFiltersByPrefixWhileTypingName()
        {
            Assert.That(SlashCommands.Match("/").Select(c => c.Name), Is.EqualTo(new[] { "goal", "undo", "clear", "compact", "stop" }));
            Assert.That(SlashCommands.Match("/G").Select(c => c.Name), Is.EqualTo(new[] { "goal" }));
            Assert.That(SlashCommands.Match("/xyz"), Is.Empty);
            Assert.That(SlashCommands.Match("/goal "), Is.Empty, "打了空白代表名稱已定，不再彈選單");
            Assert.That(SlashCommands.Match("hello"), Is.Empty);
            Assert.That(SlashCommands.Match(string.Empty), Is.Empty);
        }

        [Test]
        public void TryParseSeparatesNameAndArgs()
        {
            Assert.That(SlashCommands.TryParse("  /goal   research electricity ", out SlashCommand command, out string args), Is.True);
            Assert.That(command, Is.SameAs(SlashCommands.Goal));
            Assert.That(args, Is.EqualTo("research electricity"));

            Assert.That(SlashCommands.TryParse("/UNDO", out command, out _), Is.True);
            Assert.That(command, Is.SameAs(SlashCommands.Undo));

            Assert.That(SlashCommands.TryParse("/compact", out command, out _), Is.True);
            Assert.That(command, Is.SameAs(SlashCommands.Compact));

            Assert.That(SlashCommands.TryParse("/nope 1", out command, out _), Is.True, "未知名稱仍算斜線指令，由 UI 提示");
            Assert.That(command, Is.Null);

            Assert.That(SlashCommands.TryParse("/", out command, out _), Is.True);
            Assert.That(command, Is.Null);

            Assert.That(SlashCommands.TryParse("set Alice doctor 1", out _, out _), Is.False);
        }

        [Test]
        public void ParseGoalDistinguishesListAddRemove()
        {
            Assert.That(SlashCommands.ParseGoal(string.Empty, out _, out _), Is.EqualTo(GoalAction.List));
            Assert.That(SlashCommands.ParseGoal("   ", out _, out _), Is.EqualTo(GoalAction.List));

            Assert.That(SlashCommands.ParseGoal("研發電力", out _, out string text), Is.EqualTo(GoalAction.Add));
            Assert.That(text, Is.EqualTo("研發電力"));

            Assert.That(SlashCommands.ParseGoal("remove 2", out int index, out _), Is.EqualTo(GoalAction.Remove));
            Assert.That(index, Is.EqualTo(2));

            Assert.That(SlashCommands.ParseGoal("remove x", out index, out _), Is.EqualTo(GoalAction.Remove));
            Assert.That(index, Is.EqualTo(0), "解析失敗給 0，UI 會回報沒有這個序號");

            Assert.That(SlashCommands.ParseGoal("remove the wall", out _, out _), Is.EqualTo(GoalAction.Remove), "以 remove 開頭一律視為刪除，避免誤加目標");

            Assert.That(SlashCommands.ParseGoal("remove", out index, out _), Is.EqualTo(GoalAction.Remove), "沒帶序號也不能加成叫 remove 的目標");
            Assert.That(index, Is.EqualTo(0));
            Assert.That(SlashCommands.ParseGoal("pause", out _, out _), Is.EqualTo(GoalAction.Pause));
            Assert.That(SlashCommands.ParseGoal("RESUME", out _, out _), Is.EqualTo(GoalAction.Resume));
            Assert.That(SlashCommands.ParseGoal("clear", out _, out _), Is.EqualTo(GoalAction.Clear));
            Assert.That(SlashCommands.ParseGoal("show 2", out index, out _), Is.EqualTo(GoalAction.Show));
            Assert.That(index, Is.EqualTo(2));
            Assert.That(SlashCommands.ParseGoal("edit 2 build a hospital", out index, out text), Is.EqualTo(GoalAction.Edit));
            Assert.That(index, Is.EqualTo(2));
            Assert.That(text, Is.EqualTo("build a hospital"));
            Assert.That(SlashCommands.ParseGoal("edit", out index, out _), Is.EqualTo(GoalAction.Edit));
            Assert.That(index, Is.Zero);
        }
    }
}
