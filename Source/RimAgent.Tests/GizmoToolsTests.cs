using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class GizmoToolsTests
    {
        private FakeGameWorld _world;
        private GizmoTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Things.Add(FakeGameWorld.MakeThing(40, "Ship_ComputerCore", "ship computer core", ThingKind.Building, 10, 10, playerOwned: true));
            _world.Things.Add(FakeGameWorld.MakeThing(41, "Steel", "steel", ThingKind.Item, 12, 10));
            _world.Gizmos[40] = new List<GizmoInfo>
            {
                new GizmoInfo { Number = 1, Label = "Deconstruct", Kind = GizmoInfo.UnsupportedKind },
                new GizmoInfo { Number = 2, Label = "Initiate ship reactor startup", Description = "Wake the hibernating reactor." },
                new GizmoInfo { Number = 3, Label = "Launch ship", Disabled = true, DisabledReason = "Missing: ship engine" },
                new GizmoInfo { Number = 4, Label = "Power", Kind = GizmoInfo.ToggleKind, On = true },
                new GizmoInfo { Number = 5, Label = "Set force attack target", Kind = GizmoInfo.TargetKind, TargetHint = "a cell, a pawn, a building" },
                new GizmoInfo { Number = 6, Label = "Activate...", Description = "Choose who activates it." }
            };
            _tools = new GizmoTools(_world);
        }

        [Test]
        public void ListShowsKindStateAndDisabledReason()
        {
            string text = _tools.ListGizmos(40);

            Assert.That(text, Does.StartWith("Command buttons of [40]:"));
            Assert.That(text, Does.Contain("1) Deconstruct [unsupported]"));
            Assert.That(text, Does.Contain("2) Initiate ship reactor startup [action] — Wake the hibernating reactor."));
            Assert.That(text, Does.Contain("3) Launch ship [action] disabled: Missing: ship engine"));
            Assert.That(text, Does.Contain("4) Power [toggle, on]"));
        }

        [Test]
        public void ListShowsWhatKindOfTargetAButtonNeeds()
        {
            string text = _tools.ListGizmos(40);

            Assert.That(text, Does.Contain("5) Set force attack target [target: a cell, a pawn, a building]"));
            Assert.That(text, Does.Not.Contain("5) Set force attack target [unsupported]"));
        }

        [Test]
        public void TargetButtonTakesAThingOrACell()
        {
            Assert.That(_tools.UseGizmo(40, "5", targetId: 41), Is.EqualTo("Pressed \"Set force attack target\" on [40]: targeted [41]."));
            Assert.That(_tools.UseGizmo(40, "Set force attack target", targetX: 30, targetZ: 25), Is.EqualTo("Pressed \"Set force attack target\" on [40]: targeted cell (30,25)."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "UseGizmo(40,5,Set force attack target,[41])", "UseGizmo(40,5,Set force attack target,cell (30,25))" }));
        }

        [Test]
        public void TargetButtonWithoutATargetSaysWhatIsNeededAndDoesNothing()
        {
            string text = _tools.UseGizmo(40, "5");

            Assert.That(text, Does.StartWith("Error:").And.Contain("needs a target: a cell, a pawn, a building").And.Contain("targetId").And.Contain("targetX"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void InvalidTargetReportsTheGamesReason()
        {
            _world.RejectedTargets["cell (30,25)"] = "target is beyond the maximum range";

            string text = _tools.UseGizmo(40, "5", targetX: 30, targetZ: 25);

            Assert.That(text, Is.EqualTo("Error: target is beyond the maximum range"));
        }

        [Test]
        public void ActionThatTurnsOutToStartTargetingGetsTheTarget()
        {
            // 虛空巨石柱、互動物件的按鈕是 Command_Action，按下才開始選目標；工具照樣把目標交給遊戲。
            Assert.That(_tools.UseGizmo(40, "6", targetId: 41), Is.EqualTo("Pressed \"Activate...\" on [40]: targeted [41]."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "UseGizmo(40,6,Activate...,[41])" }));
        }

        [Test]
        public void ContradictoryOrIncompleteTargetsAreRefusedBeforeTouchingTheWorld()
        {
            Assert.That(_tools.UseGizmo(40, "5", targetId: 41, targetX: 3, targetZ: 3), Does.StartWith("Error:").And.Contain("not both"));
            Assert.That(_tools.UseGizmo(40, "5", targetX: 3), Does.StartWith("Error:").And.Contain("both targetX and targetZ"));
            Assert.That(_tools.UseGizmo(40, "4", targetId: 41), Does.StartWith("Error:").And.Contain("toggle"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void ListReportsUnknownAndButtonlessThings()
        {
            Assert.That(_tools.ListGizmos(999), Does.StartWith("Error:").And.Contain("find_things"));
            Assert.That(_tools.ListGizmos(41), Is.EqualTo("[41] has no command buttons."));
        }

        [Test]
        public void UseByNumberOrPartialLabel()
        {
            Assert.That(_tools.UseGizmo(40, "4"), Is.EqualTo("Pressed \"Power\" on [40]: now off."));
            Assert.That(_tools.UseGizmo(40, "reactor"), Is.EqualTo("Pressed \"Initiate ship reactor startup\" on [40]."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "UseGizmo(40,4,Power)", "UseGizmo(40,2,Initiate ship reactor startup)" }));
        }

        [Test]
        public void UseByNumberSelectsTheRightButtonWhenLabelsMatch()
        {
            _world.Gizmos[41] = new List<GizmoInfo>
            {
                new GizmoInfo { Number = 1, Label = "Power", Kind = GizmoInfo.ToggleKind, On = false },
                new GizmoInfo { Number = 2, Label = "Power", Kind = GizmoInfo.ToggleKind, On = true }
            };

            Assert.That(_tools.UseGizmo(41, "Power"), Does.StartWith("Error:").And.Contain("Use a button number"));
            Assert.That(_world.Calls, Is.Empty);
            Assert.That(_tools.UseGizmo(41, "2"), Is.EqualTo("Pressed \"Power\" on [41]: now off."));
            Assert.That(_world.Gizmos[41][0].On, Is.False);
            Assert.That(_world.Gizmos[41][1].On, Is.False);
            Assert.That(_world.Calls, Is.EqualTo(new[] { "UseGizmo(41,2,Power)" }));
        }

        [Test]
        public void UseReportsTheConfirmationDialogItOpens()
        {
            _world.FollowUpDialog = new PendingChoiceInfo
            {
                Id = "dialog-9",
                Text = "Starting the reactor will attract raids for 15 days.",
                Options = new[] { new ChoiceOptionInfo { Number = 1, Text = "Confirm" }, new ChoiceOptionInfo { Number = 2, Text = "Go back" } }
            };

            string text = _tools.UseGizmo(40, "2");

            Assert.That(text, Does.StartWith("Pressed \"Initiate ship reactor startup\" on [40]. A dialog is open"));
            Assert.That(text, Does.Contain("[dialog-9]").And.Contain("Options: 1) Confirm; 2) Go back"));
        }

        [Test]
        public void UseRefusesDisabledUnsupportedAndUnknownButtons()
        {
            Assert.That(_tools.UseGizmo(40, "Launch ship"), Is.EqualTo("Error: \"Launch ship\" is disabled: Missing: ship engine"));
            Assert.That(_tools.UseGizmo(40, "1"), Does.StartWith("Error:").And.Contain("menu choice or another kind of input"));
            Assert.That(_tools.UseGizmo(40, "self-destruct"), Does.StartWith("Error:").And.Contain("Buttons: 1) Deconstruct"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void BlockedUseReturnsPreviewWithoutTouchingWorld()
        {
            var gated = new GizmoTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

            Assert.That(gated.UseGizmo(40, "Power"), Does.StartWith("Needs confirmation:").And.Contain("\"Power\" on [40]"));
            Assert.That(gated.UseGizmo(40, "5", targetX: 30, targetZ: 25), Does.StartWith("Needs confirmation:").And.Contain("aimed at cell (30,25)"));
            Assert.That(_world.Calls, Is.Empty);
        }
    }
}
