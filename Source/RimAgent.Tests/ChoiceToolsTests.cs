using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class ChoiceToolsTests
    {
        private FakeGameWorld _world;
        private ChoiceTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.PendingChoices.Add(new PendingChoiceInfo
            {
                Id = "letter-12",
                Title = "Wanderer joins",
                Text = "A wanderer named Kim wants to join.",
                ExpiresInTicks = 5000,
                Options = new[]
                {
                    new ChoiceOptionInfo { Number = 1, Text = "Accept" },
                    new ChoiceOptionInfo { Number = 2, Text = "Reject" },
                    new ChoiceOptionInfo { Number = 4, Text = "Postpone", Disabled = true, DisabledReason = "about to expire" }
                }
            });
            _tools = new ChoiceTools(_world);
        }

        [Test]
        public void ListShowsIdTextOptionsAndExpiry()
        {
            string text = _tools.ListPendingChoices();

            Assert.That(text, Does.StartWith("[letter-12] Wanderer joins (expires in 2 h)"));
            Assert.That(text, Does.Contain("A wanderer named Kim wants to join."));
            Assert.That(text, Does.Contain("Options: 1) Accept; 2) Reject; 4) Postpone (disabled: about to expire)"));
        }

        [Test]
        public void ListReportsNoneWhenEmpty()
        {
            _world.PendingChoices.Clear();
            Assert.That(_tools.ListPendingChoices(), Is.EqualTo("No pending choices."));
        }

        [Test]
        public void ChooseAnswersTheLetter()
        {
            string text = _tools.ChooseOption("LETTER-12 ", 1);

            Assert.That(text, Is.EqualTo("Chose \"Accept\" on \"Wanderer joins\"."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "ChooseOption(letter-12,1)" }));
            Assert.That(_world.PendingChoices, Is.Empty);
        }

        [Test]
        public void ChooseReportsTheDialogThatFollows()
        {
            _world.FollowUpDialog = new PendingChoiceInfo
            {
                Id = "dialog-7",
                Title = "Kim",
                Text = "Kim is being chased.",
                Options = new[] { new ChoiceOptionInfo { Number = 1, Text = "OK" } }
            };

            string text = _tools.ChooseOption("letter-12", 1);

            Assert.That(text, Does.StartWith("Chose \"Accept\" on \"Wanderer joins\". A dialog is still open"));
            Assert.That(text, Does.Contain("[dialog-7] Kim").And.Contain("Options: 1) OK"));
        }

        [Test]
        public void ChooseRefusesUnknownIdMissingOrDisabledOption()
        {
            Assert.That(_tools.ChooseOption("letter-99", 1), Does.StartWith("Error:").And.Contain("list_pending_choices"));
            Assert.That(_tools.ChooseOption("letter-12", 3), Does.StartWith("Error:").And.Contain("no option 3"));
            Assert.That(_tools.ChooseOption("letter-12", 4), Does.StartWith("Error:").And.Contain("disabled: about to expire"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void BlockedChoiceReturnsPreviewWithoutTouchingWorld()
        {
            var gated = new ChoiceTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

            string text = gated.ChooseOption("letter-12", 2);

            Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("Wanderer joins").And.Contain("Reject"));
            Assert.That(_world.Calls, Is.Empty);
        }
    }
}
