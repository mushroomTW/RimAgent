using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests;

/// <summary>執行期 transcript 的上限與 Epoch：UI 靠 Epoch 判斷能不能只渲染新增的尾段。</summary>
[TestFixture]
public class AgentTranscriptTests
{
    [Test]
    public void AddOnlyBumpsVersionNotEpoch()
    {
        var transcript = new AgentTranscript();
        int epoch = transcript.Epoch;
        int version = transcript.Version;

        transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = "a" });
        transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Assistant, Text = "b" });

        Assert.That(transcript.Version, Is.EqualTo(version + 2));
        Assert.That(transcript.Epoch, Is.EqualTo(epoch));
    }

    [Test]
    public void ClearBumpsEpoch()
    {
        var transcript = new AgentTranscript();
        transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = "a" });
        int epoch = transcript.Epoch;

        transcript.Clear();

        Assert.That(transcript.Count, Is.EqualTo(0));
        Assert.That(transcript.Epoch, Is.EqualTo(epoch + 1));
    }

    [Test]
    public void OverflowDropsOldestInOneBatchAndBumpsEpoch()
    {
        var transcript = new AgentTranscript();
        for (int i = 0; i < AgentTranscript.MaxEntries; i++)
        {
            transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Notice, Text = i.ToString() });
        }
        int epoch = transcript.Epoch;
        Assert.That(transcript.Count, Is.EqualTo(AgentTranscript.MaxEntries));

        transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Notice, Text = "last" });

        Assert.That(transcript.Count, Is.EqualTo(AgentTranscript.TrimTo));
        Assert.That(transcript.Epoch, Is.EqualTo(epoch + 1));
        var entries = transcript.Snapshot();
        Assert.That(entries[entries.Count - 1].Text, Is.EqualTo("last"));
        // 最舊的被丟掉，留下的是連續的尾段。
        Assert.That(entries[0].Text, Is.EqualTo((AgentTranscript.MaxEntries + 1 - AgentTranscript.TrimTo).ToString()));
    }
}
