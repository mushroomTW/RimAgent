using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class FloatMenuToolsTests
    {
        private FakeGameWorld _world;
        private FloatMenuTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _world.Things.Add(FakeGameWorld.MakeThing(305, "PsylinkNeuroformer", "psylink neuroformer", ThingKind.Item, 14, 9));
            _world.FloatMenuOptions.Add(new FloatMenuOptionInfo { Number = 1, Label = "Use psylink neuroformer" });
            _world.FloatMenuOptions.Add(new FloatMenuOptionInfo { Number = 2, Label = "Prioritize hauling psylink neuroformer" });
            _world.FloatMenuOptions.Add(new FloatMenuOptionInfo { Number = 3, Label = "Cannot equip (too heavy)", Disabled = true });
            _tools = new FloatMenuTools(_world);
        }

        [Test]
        public void ListShowsNumberedOptionsAndDisabledOnes()
        {
            string text = _tools.ListRightClickMenu("Ali", targetId: 305);

            Assert.That(text, Does.StartWith("Right-click menu of Ali (not drafted) on [305]:"));
            Assert.That(text, Does.Contain("1) Use psylink neuroformer\n"));
            Assert.That(text, Does.Contain("2) Prioritize hauling psylink neuroformer\n"));
            Assert.That(text, Does.Contain("3) Cannot equip (too heavy) [disabled]"));
        }

        [Test]
        public void ListSaysWhetherTheColonistIsDraftedAndHandlesAnEmptyMenu()
        {
            _world.Colonists[0].Drafted = true;
            _world.FloatMenuOptions.Clear();

            Assert.That(_tools.ListRightClickMenu("Alice", targetX: 4, targetZ: 5), Is.EqualTo("Right-click menu of Ali (drafted) on cell (4,5): no options."));
        }

        [Test]
        public void ListReportsTheGamesReasonWhenNoMenuCanBeMade()
        {
            _world.FloatMenuError = "Alice is incapacitated";

            Assert.That(_tools.ListRightClickMenu("Alice", targetId: 305), Is.EqualTo("Error: Alice is incapacitated"));
        }

        [Test]
        public void ListNeedsAColonistAndATarget()
        {
            Assert.That(_tools.ListRightClickMenu("Nobody", targetId: 305), Does.StartWith("Error:"));
            Assert.That(_tools.ListRightClickMenu("Ali"), Does.StartWith("Error:").And.Contain("give a target"));
            Assert.That(_tools.ListRightClickMenu("Ali", targetId: 305, targetX: 1, targetZ: 1), Does.StartWith("Error:").And.Contain("not both"));
        }

        [Test]
        public void UseRunsTheOptionByNumberOrLabelAndPassesNumberAndLabelToTheWorld()
        {
            _world.FloatMenuResult = "game message: Alice uses the neuroformer";

            string byNumber = _tools.UseRightClickOption("Ali", "1", targetId: 305);
            string byLabel = _tools.UseRightClickOption("Ali", "prioritize hauling", targetId: 305);

            Assert.That(byNumber, Is.EqualTo("Ordered Ali: \"Use psylink neuroformer\" on [305]: game message: Alice uses the neuroformer."));
            Assert.That(byLabel, Does.StartWith("Ordered Ali: \"Prioritize hauling psylink neuroformer\" on [305]"));
            Assert.That(_world.Calls, Is.EqualTo(new[]
            {
                "UseFloatMenuOption(101,[305],1,Use psylink neuroformer)",
                "UseFloatMenuOption(101,[305],2,Prioritize hauling psylink neuroformer)"
            }));
        }

        [Test]
        public void UseOnACellKeepsTheCellInTheCall()
        {
            Assert.That(_tools.UseRightClickOption("Bob", "1", targetX: 7, targetZ: 8), Does.StartWith("Ordered Bob: \"Use psylink neuroformer\" on cell (7,8)"));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "UseFloatMenuOption(102,cell (7,8),1,Use psylink neuroformer)" }));
        }

        [Test]
        public void UseRefusesDisabledUnknownAndAmbiguousOptionsWithoutTouchingTheWorld()
        {
            Assert.That(_tools.UseRightClickOption("Ali", "3", targetId: 305), Does.StartWith("Error:").And.Contain("disabled"));
            Assert.That(_tools.UseRightClickOption("Ali", "9", targetId: 305), Does.StartWith("Error:").And.Contain("Options: 1) Use psylink neuroformer"));
            Assert.That(_tools.UseRightClickOption("Ali", "psylink", targetId: 305), Does.StartWith("Error:").And.Contain("no option"));
            Assert.That(_tools.UseRightClickOption("Ali", "", targetId: 305), Does.StartWith("Error:"));
            _world.FloatMenuOptions.Add(new FloatMenuOptionInfo { Number = 4, Label = "Use psylink neuroformer" });
            Assert.That(_tools.UseRightClickOption("Ali", "Use psylink neuroformer", targetId: 305), Does.StartWith("Error:").And.Contain("Use an option number"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void UseReportsTheConfirmationDialogItOpens()
        {
            _world.FollowUpDialog = new PendingChoiceInfo
            {
                Id = "dialog-3",
                Text = "Using this will give Alice a psylink.",
                Options = new[] { new ChoiceOptionInfo { Number = 1, Text = "Confirm" }, new ChoiceOptionInfo { Number = 2, Text = "Cancel" } }
            };

            string text = _tools.UseRightClickOption("Ali", "1", targetId: 305);

            Assert.That(text, Does.Contain("A dialog is open").And.Contain("[dialog-3]").And.Contain("Options: 1) Confirm; 2) Cancel"));
        }

        [Test]
        public void UseSurfacesTheGamesError()
        {
            _world.FloatMenuError = "Alice is incapacitated";

            Assert.That(_tools.UseRightClickOption("Ali", "1", targetId: 305), Is.EqualTo("Error: Alice is incapacitated"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void BlockedUseReturnsPreviewWithoutTouchingWorld()
        {
            var gated = new FloatMenuTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

            string text = gated.UseRightClickOption("Ali", "1", targetId: 305);

            Assert.That(text, Does.StartWith("Needs confirmation: would order Ali to \"Use psylink neuroformer\" on [305]"));
            Assert.That(_world.Calls, Is.Empty);
        }
    }
}
