using Fleans.Assistant.Contracts;
using Fleans.Web.Services;

namespace Fleans.Web.Tests;

[TestClass]
public class AssistantChangeHistoryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static AssistantChangeHistory WithChanges(params string[] states)
    {
        var history = new AssistantChangeHistory();
        history.CaptureIfChanged(states[0], At);
        foreach (var state in states.Skip(1))
            history.RecordAssistant(state, "request", "reply", AssistantChangeHistory.HashOf("proposal " + state), At);
        return history;
    }

    [TestMethod]
    public void FirstCapture_StartsTheHistoryWithTheOriginalDiagram()
    {
        // Arrange
        var history = new AssistantChangeHistory();

        // Act
        var captured = history.CaptureIfChanged("blank", At);

        // Assert
        Assert.AreEqual(AssistantRevisionKind.Original, captured!.Kind);
        Assert.AreEqual(1, captured.Number);
        Assert.AreSame(captured, history.Current);
        Assert.IsNull(history.UndoTarget, "there is nothing before the original");
    }

    [TestMethod]
    public void Capture_OfTheKnownState_AddsNothing()
    {
        // Arrange
        var history = WithChanges("blank", "a");

        // Act
        var captured = history.CaptureIfChanged("a", At);

        // Assert
        Assert.IsNull(captured);
        Assert.AreEqual(2, history.Revisions.Count);
    }

    [TestMethod]
    public void Capture_OfADifferentDiagram_SavesItAsManualEdits()
    {
        // Arrange
        var history = WithChanges("blank", "a");

        // Act
        var captured = history.CaptureIfChanged("a + hand edit", At);

        // Assert
        Assert.AreEqual(AssistantRevisionKind.Manual, captured!.Kind);
        Assert.AreEqual("a + hand edit", history.Current!.Xml);
        Assert.AreEqual("a", history.UndoTarget!.Xml, "undo goes back to the state before the hand edits");
    }

    [TestMethod]
    public void RecordAssistant_KeepsRequestSummaryAndHash()
    {
        // Arrange
        var history = WithChanges("blank");
        var hash = AssistantChangeHistory.HashOf("<proposal/>");

        // Act
        var revision = history.RecordAssistant("a", "add a timer", "Added a timer.", hash, At);

        // Assert
        Assert.AreEqual(AssistantRevisionKind.Assistant, revision!.Kind);
        Assert.AreEqual(2, revision.Number);
        Assert.AreEqual("add a timer", revision.Request);
        Assert.AreEqual("Added a timer.", revision.Summary);
        Assert.AreEqual(At, revision.At);
        Assert.IsTrue(history.IsApplied(hash));
    }

    [TestMethod]
    public void RecordAssistant_ThatChangesNothing_AddsNoRevision_ButMarksTheProposalApplied()
    {
        // Arrange
        var history = WithChanges("blank", "a");
        var hash = AssistantChangeHistory.HashOf("same diagram, different text");

        // Act
        var revision = history.RecordAssistant("a", "again", "Same.", hash, At);

        // Assert
        Assert.IsNull(revision);
        Assert.AreEqual(2, history.Revisions.Count);
        Assert.IsTrue(history.IsApplied(hash));
    }

    [TestMethod]
    public void Summary_IsTruncated()
    {
        // Act
        var summary = AssistantChangeHistory.Summarize(new string('x', 500));

        // Assert
        Assert.AreEqual(AssistantChangeHistory.MaxSummaryLength + 1, summary!.Length);
        Assert.IsTrue(summary.EndsWith('…'));
        Assert.IsNull(AssistantChangeHistory.Summarize("   "));
    }

    [TestMethod]
    public void Restore_AppendsACopy_AndDeletesNothing()
    {
        // Arrange
        var history = WithChanges("blank", "a", "b");
        var source = history.Find(2)!;

        // Act
        var restored = history.RecordRestored(source, "a", At);

        // Assert
        Assert.AreEqual(4, history.Revisions.Count);
        CollectionAssert.AreEqual(new[] { "blank", "a", "b", "a" }, history.Revisions.Select(r => r.Xml).ToArray());
        Assert.AreEqual(AssistantRevisionKind.Restored, restored.Kind);
        Assert.AreEqual(2, restored.RestoredFrom);
        Assert.AreSame(restored, history.Current);
        Assert.IsTrue(history.IsApplied(source.ProposalHash), "the restored proposal is applied again");
    }

    [TestMethod]
    public void IsApplied_IsOnlyTheCurrentState_SoAnUndoneProposalCanBeAppliedAgain()
    {
        // Arrange
        var history = WithChanges("blank", "a");
        var source = history.Find(2)!;
        Assert.IsTrue(history.IsApplied(source.ProposalHash));

        // Act
        history.RecordRestored(history.Find(1)!, "blank", At);

        // Assert
        Assert.IsFalse(history.IsApplied(source.ProposalHash));
        Assert.IsFalse(history.IsApplied(null));
    }

    [TestMethod]
    public void UndoTarget_WalksBackThroughTheChanges_InsteadOfSwappingTheLastTwo()
    {
        // Arrange
        var history = WithChanges("blank", "a", "b");

        // Act / Assert: b → a → blank, then nothing left to undo
        var first = history.UndoTarget!;
        Assert.AreEqual("a", first.Xml);
        history.RecordRestored(first, first.Xml, At);

        var second = history.UndoTarget!;
        Assert.AreEqual("blank", second.Xml);
        history.RecordRestored(second, second.Xml, At);

        Assert.IsNull(history.UndoTarget);
        Assert.AreEqual(5, history.Revisions.Count, "every state is still in the list");
    }

    [TestMethod]
    public void RevisionCap_DropsTheOldestFirst_AndNumbersStayStable()
    {
        // Arrange
        var history = new AssistantChangeHistory(maxRevisions: 3);

        // Act
        for (var i = 0; i < 5; i++)
            history.CaptureIfChanged("state " + i, At);

        // Assert
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, history.Revisions.Select(r => r.Number).ToArray());
        Assert.AreEqual(2, history.Dropped);
    }

    [TestMethod]
    public void ByteCap_DropsTheOldestFirst_ButKeepsTheCurrentState()
    {
        // Arrange
        var history = new AssistantChangeHistory(maxChars: 100);
        history.CaptureIfChanged(new string('a', 60), At);

        // Act: the second doesn't fit beside the first; the third alone is over the cap
        history.CaptureIfChanged(new string('b', 60), At);
        history.CaptureIfChanged(new string('c', 150), At);

        // Assert
        Assert.AreEqual(1, history.Revisions.Count);
        Assert.AreEqual(new string('c', 150), history.Current!.Xml);
        Assert.AreEqual(2, history.Dropped);
    }

    [TestMethod]
    public void UndoTarget_WhenTheRestoredRevisionWasDropped_IsNone()
    {
        // Arrange
        var history = new AssistantChangeHistory(maxRevisions: 2);
        history.CaptureIfChanged("blank", At);
        var first = history.Current!;
        history.CaptureIfChanged("edit", At);

        // Act: restoring revision 1 evicts it
        history.RecordRestored(first, "blank", At);

        // Assert
        Assert.IsNull(history.Find(first.Number));
        Assert.IsNull(history.UndoTarget);
    }

    [TestMethod]
    public void Serialization_RoundTripsEverything()
    {
        // Arrange
        var history = new AssistantChangeHistory(maxRevisions: 3);
        history.CaptureIfChanged("<definitions>'first' & \"quoted\"</definitions>", At);
        history.RecordAssistant("a", "ру́сский запрос", "reply", "hash-a", At);
        history.CaptureIfChanged("hand", At);
        history.RecordRestored(history.Find(2)!, "a", At);

        // Act
        var json = history.Serialize();
        var copy = AssistantChangeHistory.Deserialize(json, maxRevisions: 3)!;

        // Assert
        CollectionAssert.AreEqual(history.Revisions.ToArray(), copy.Revisions.ToArray());
        Assert.AreEqual(1, copy.Dropped);
        Assert.IsTrue(copy.IsApplied("hash-a"));
        Assert.AreEqual(5, copy.CaptureIfChanged("after reload", At)!.Number, "numbering continues");
    }

    [TestMethod]
    public void Serialization_DoesNotEscapeXml_SoItFitsTheQuota()
    {
        // Act
        var history = WithChanges("<a><b/></a>");

        // Assert
        StringAssert.Contains(history.Serialize(), "<a><b/></a>");
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("{\"Revisions\":3}")]
    public void Deserialize_OfAnythingElse_IsNull(string json)
    {
        Assert.IsNull(AssistantChangeHistory.Deserialize(json));
    }

    [TestMethod]
    public void HashOf_IsStableAndDistinguishes()
    {
        Assert.AreEqual(AssistantChangeHistory.HashOf("<a/>"), AssistantChangeHistory.HashOf("<a/>"));
        Assert.AreNotEqual(AssistantChangeHistory.HashOf("<a/>"), AssistantChangeHistory.HashOf("<b/>"));
    }

    [TestMethod]
    public void Entry_ExposesItsProposalHash_ForMatchingAfterAReload()
    {
        // Act
        var entry = new AssistantChat.Entry(AssistantRoles.Assistant, "Done.", "<a/>", true);

        // Assert
        Assert.AreEqual(AssistantChangeHistory.HashOf("<a/>"), entry.ProposalHash);
        Assert.IsNull(new AssistantChat.Entry(AssistantRoles.User, "hi").ProposalHash);
    }

    [TestMethod]
    public void AutoApply_TakesAConvertingProposal_WhenOnAndReady()
    {
        // Arrange
        var chat = new AssistantChat();
        chat.Add(new AssistantChat.Entry(AssistantRoles.User, "add a timer"));
        var reply = new AssistantChat.Entry(AssistantRoles.Assistant, "Done.", "<a/>", true);
        chat.Add(reply);

        // Act / Assert
        Assert.AreSame(reply, AssistantChangeHistory.AutoApplyCandidate(chat, autoApply: true, ready: true));
        Assert.IsNull(AssistantChangeHistory.AutoApplyCandidate(chat, autoApply: false, ready: true), "the switch is off");
        Assert.IsNull(AssistantChangeHistory.AutoApplyCandidate(chat, autoApply: true, ready: false), "XML, simulation or read-only: it waits");
    }

    [TestMethod]
    public void AutoApply_NeverTakesAProposalThatDoesNotConvert()
    {
        // Arrange
        var chat = new AssistantChat();
        chat.Add(new AssistantChat.Entry(AssistantRoles.User, "add a timer"));
        chat.Add(new AssistantChat.Entry(AssistantRoles.Assistant, "Done.", "<broken", false));

        // Act / Assert
        Assert.IsNull(AssistantChangeHistory.AutoApplyCandidate(chat, autoApply: true, ready: true));
    }

    [TestMethod]
    public void AutoApply_IgnoresAnEarlierProposal_WhenTheTurnEndedWithoutOne()
    {
        // Arrange
        var chat = new AssistantChat();
        chat.Add(new AssistantChat.Entry(AssistantRoles.Assistant, "Done.", "<a/>", true));
        chat.Add(new AssistantChat.Entry(AssistantRoles.User, "what does it do?"));
        chat.Add(new AssistantChat.Entry(AssistantRoles.Assistant, "It approves things."));

        // Act / Assert
        Assert.IsNull(AssistantChangeHistory.AutoApplyCandidate(chat, autoApply: true, ready: true));
    }

    [TestMethod]
    public void RequestFor_IsTheUsersRequest_SkippingAutomaticFixRequests()
    {
        // Arrange
        var chat = new AssistantChat();
        chat.Add(new AssistantChat.Entry(AssistantRoles.User, "add a timer"));
        chat.Add(new AssistantChat.Entry(AssistantRoles.Assistant, "", "<broken", false));
        chat.Add(new AssistantChat.Entry(AssistantRoles.User, EditorAssistant.FixPrefix + "bad", Automatic: true));
        var reply = new AssistantChat.Entry(AssistantRoles.Assistant, "Done.", "<a/>", true);
        chat.Add(reply);

        // Act / Assert
        Assert.AreEqual("add a timer", chat.RequestFor(reply));
    }
}
