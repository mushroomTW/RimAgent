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
        [Test]
        public void PromptsMentionTheTriggerInAllLanguages()
        {
            Assert.That(AutonomousPrompts.Build(AutonomousTrigger.Raid, null, AutoLanguage.English), Does.Contain("Hostiles"));
            Assert.That(AutonomousPrompts.Build(AutonomousTrigger.Raid, null, AutoLanguage.Traditional), Does.Contain("敵對"));
            Assert.That(AutonomousPrompts.Build(AutonomousTrigger.Raid, null, AutoLanguage.Simplified), Does.Contain("敌对"));
            Assert.That(AutonomousPrompts.Build(AutonomousTrigger.FoodShortage, null, AutoLanguage.Traditional), Does.Contain("糧食"));
            Assert.That(AutonomousPrompts.Build(AutonomousTrigger.MentalBreak, null, AutoLanguage.Simplified), Does.Contain("精神崩溃"));
            Assert.That(AutonomousPrompts.Build(AutonomousTrigger.Interval, null, AutoLanguage.English), Does.Contain("Routine inspection"));
        }

        [Test]
        public void GoalsAreAppendedAndBlankOnesSkipped()
        {
            var goals = new List<string> { "research electricity", "  ", null };
            string text = AutonomousPrompts.Build(AutonomousTrigger.Interval, goals, AutoLanguage.Traditional);

            Assert.That(text, Does.Contain("長期目標").And.Contain("research electricity"));
            Assert.That(text, Does.Not.Contain("- \n"));
        }

        [Test]
        public void NoGoalsMeansTaskOnly()
        {
            string text = AutonomousPrompts.Build(AutonomousTrigger.Interval, new List<string>(), AutoLanguage.English);
            Assert.That(text, Does.Not.Contain("goals:"));
        }
    }

    [TestFixture]
    public class AutonomousForbiddenToolsTests
    {
        [Test]
        public void AutonomousModeExcludesSurgeryAndPrisonerInteraction()
        {
            var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());
            var tools = registry.Select(ToolPermissions.All, AgentMode.Autonomous).OfType<AIFunction>().Select(f => f.Name).ToList();

            Assert.That(tools, Does.Not.Contain("schedule_surgery"));
            Assert.That(tools, Does.Not.Contain("set_prisoner_interaction"));
            Assert.That(tools, Does.Not.Contain("execute_trade"));
            Assert.That(tools, Does.Not.Contain("form_caravan"));
            Assert.That(tools, Does.Not.Contain("release_prisoner"));
            Assert.That(tools, Does.Contain("set_work_priority"));
        }

        [Test]
        public void IsAutonomousForbiddenIdentifiesHighRiskTools()
        {
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("schedule_surgery"), Is.True);
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("set_prisoner_interaction"), Is.True);
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("execute_trade"), Is.True);
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("form_caravan"), Is.True);
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("release_prisoner"), Is.True);
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("place_blueprint"), Is.False);
            Assert.That(AgentToolRegistry.IsAutonomousForbidden("get_colony_overview"), Is.False);
        }
    }
}

