using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class AutonomousSchedulerTests
    {
        private AutonomousScheduler _scheduler;
        private AutonomousConfig _config;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new AutonomousScheduler();
            _config = new AutonomousConfig
            {
                Enabled = true,
                IntervalTicks = 15000,
                WindowTicks = 60000,
                BudgetPerWindow = 60,
                CooldownTicks = AutonomousScheduler.CooldownTicks,
                OnRaid = true,
                OnMentalBreak = true,
                OnFoodShortage = true
            };
        }

        private static AutonomousSignals Calm(int colonists = 3)
        {
            return new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = colonists };
        }

        [Test]
        public void DisabledNeverFires()
        {
            _config.Enabled = false;
            var raid = new AutonomousSignals { HostileCount = 5, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(20000, raid, _config), Is.EqualTo(AutonomousTrigger.None));
        }

        [Test]
        public void IntervalFiresAfterIntervalElapses()
        {
            Assert.That(_scheduler.Evaluate(0, Calm(), _config), Is.EqualTo(AutonomousTrigger.None));
            Assert.That(_scheduler.Evaluate(15000, Calm(), _config), Is.EqualTo(AutonomousTrigger.Interval));
        }

        [Test]
        public void RaidFiresOnceOnRisingEdge()
        {
            var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(0, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
            // 同樣訊號再來一次：邊緣已過，不重複開火。
            Assert.That(_scheduler.Evaluate(500, raid, _config), Is.EqualTo(AutonomousTrigger.None));
        }

        [Test]
        public void CooldownSuppressesImmediateRefire()
        {
            _scheduler.RecordRun(0, 2, _config);
            var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(500, raid, _config), Is.EqualTo(AutonomousTrigger.None));
            Assert.That(_scheduler.Evaluate(2500, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
        }

        [Test]
        public void CooldownDisabledAllowsImmediateRefire()
        {
            _config.CooldownTicks = 0; // 關閉冷卻
            _scheduler.RecordRun(0, 2, _config);
            var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(10, raid, _config), Is.EqualTo(AutonomousTrigger.Raid), "冷卻設為 0 時立即反應");
        }

        [Test]
        public void UnlimitedBudgetNeverExhausts()
        {
            _config.UnlimitedBudget = true;
            _config.BudgetPerWindow = 10;
            _scheduler.RecordRun(0, 100, _config);
            Assert.That(_scheduler.RemainingBudget(_config), Is.EqualTo(int.MaxValue));
            var raid = new AutonomousSignals { HostileCount = 3, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(2500, raid, _config), Is.EqualTo(AutonomousTrigger.Raid), "自主模式不設預算上限");
        }

        [Test]
        public void MentalBreakAndFoodShortageFireOnEdge()
        {
            var mental = new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 1, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(0, mental, _config), Is.EqualTo(AutonomousTrigger.MentalBreak));

            _scheduler.Reset();
            var hungry = new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 0, FoodNutrition = 5f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(0, hungry, _config), Is.EqualTo(AutonomousTrigger.FoodShortage));
        }

        [Test]
        public void NoColonistsMeansNoFoodShortage()
        {
            var empty = new AutonomousSignals { HostileCount = 0, NearMentalBreakCount = 0, FoodNutrition = 0f, ColonistCount = 0 };
            Assert.That(_scheduler.Evaluate(20000, empty, _config), Is.EqualTo(AutonomousTrigger.Interval));
        }

        [Test]
        public void RaidBeatsIntervalInPriority()
        {
            var raid = new AutonomousSignals { HostileCount = 2, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(20000, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
        }

        [Test]
        public void BudgetExhaustedBlocksEverythingUntilWindowResets()
        {
            _scheduler.RecordRun(0, 60, _config);
            var raid = new AutonomousSignals { HostileCount = 2, NearMentalBreakCount = 0, FoodNutrition = 100f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(3000, raid, _config), Is.EqualTo(AutonomousTrigger.None));
            Assert.That(_scheduler.ToolsUsed, Is.EqualTo(60));

            // 視窗過了就重置。
            Assert.That(_scheduler.Evaluate(61000, raid, _config), Is.EqualTo(AutonomousTrigger.Raid));
            Assert.That(_scheduler.ToolsUsed, Is.EqualTo(0));
        }

        [Test]
        public void RemainingBudgetNeverDropsBelowZero()
        {
            _scheduler.RecordRun(0, 59, _config);
            Assert.That(_scheduler.RemainingBudget(_config), Is.EqualTo(1));

            _scheduler.RecordRun(3000, 8, _config);
            Assert.That(_scheduler.RemainingBudget(_config), Is.Zero);
        }

        [Test]
        public void DisabledTriggerTypesAreIgnored()
        {
            _config.OnRaid = false;
            _config.OnMentalBreak = false;
            _config.OnFoodShortage = false;
            var bad = new AutonomousSignals { HostileCount = 5, NearMentalBreakCount = 2, FoodNutrition = 0f, ColonistCount = 3 };
            Assert.That(_scheduler.Evaluate(0, bad, _config), Is.EqualTo(AutonomousTrigger.None));
        }
    }

    [TestFixture]
    public class AutonomousPromptsTests
    {
        private static AutonomousPack Pack()
        {
            return new AutonomousPack
            {
                Tasks = new Dictionary<string, string>(System.StringComparer.Ordinal)
                {
                    { "Raid", "Hostiles are on the map." },
                    { "FoodShortage", "Food reserves are low." },
                    { "MentalBreak", "A colonist is close to a mental break." },
                    { "Interval", "Routine inspection task." }
                }
            };
        }

        [Test]
        public void PromptsMentionTheTrigger()
        {
            Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.Raid), Does.Contain("Hostiles"));
            Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.FoodShortage), Does.Contain("Food reserves"));
            Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.MentalBreak), Does.Contain("mental break"));
            Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.Interval), Does.Contain("Routine inspection"));
        }

        [Test]
        public void UnknownTriggerFallsBackToInterval()
        {
            Assert.That(AutonomousPrompts.Build(Pack(), AutonomousTrigger.Fire), Does.Contain("Routine inspection"));
        }

        [Test]
        public void MissingTaskReturnsNullSoTheRunIsSkipped()
        {
            Assert.That(AutonomousPrompts.Build(null, AutonomousTrigger.Raid), Is.Null);
            Assert.That(AutonomousPrompts.Build(new AutonomousPack(), AutonomousTrigger.Raid), Is.Null);
        }

        [Test]
        public void HasTaskReportsSectionPresence()
        {
            Assert.That(AutonomousPrompts.HasTask(Pack(), AutonomousTrigger.Raid), Is.True);
            Assert.That(AutonomousPrompts.HasTask(Pack(), AutonomousTrigger.Fire), Is.False, "退回 Interval 的情況呼叫端要能察覺");
            Assert.That(AutonomousPrompts.HasTask(null, AutonomousTrigger.Raid), Is.False);
        }

        [Test]
        public void AutonomousPromptContainsOnlyItsOwnTask()
        {
            string text = AutonomousPrompts.Build(Pack(), AutonomousTrigger.Interval);
            Assert.That(text, Does.Not.Contain("goals:"));
        }
    }

    [TestFixture]
    public class AutonomousToolsTests
    {
        private static readonly string[] HighRiskTools =
        {
            "schedule_surgery",
            "set_prisoner_interaction",
            "execute_trade",
            "form_caravan",
            "release_prisoner"
        };

        [Test]
        public void AutonomousModeIncludesAllToolsWithoutBlacklist()
        {
            var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());
            var tools = registry.Select(ToolPermissions.All, AgentMode.Autonomous).OfType<AIFunction>().Select(f => f.Name).ToList();

            foreach (string name in HighRiskTools)
            {
                Assert.That(tools, Does.Contain(name), name);
            }
            Assert.That(tools, Does.Contain("set_work_priorities"));
        }
    }
}
