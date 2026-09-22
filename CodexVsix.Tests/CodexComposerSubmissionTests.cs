using System;
using System.Collections.Generic;
using CodexVsix.ViewModels;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexComposerSubmissionTests
{
    [Fact]
    public void SwitchingConversationsDiscardsOldFollowUpsAndRejectsLateSteerFallback()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        buffer.Reset(1);
        var queued = Submission("old queued prompt", "old.png");
        var steering = Submission("old steering prompt", "steering.png");
        Assert.Empty(buffer.Enqueue(queued, 1));
        Assert.True(buffer.RetainsImage("old.png"));

        Assert.Same(queued, Assert.Single(buffer.Reset(2)));
        Assert.Same(steering, Assert.Single(buffer.Enqueue(steering, 1)));
        Assert.Null(buffer.Dequeue(1));
        Assert.Null(buffer.Dequeue(2));
        Assert.False(buffer.RetainsImage("old.png"));
        Assert.False(buffer.RetainsImage("steering.png"));

        var current = Submission("new conversation prompt", "new.png");
        Assert.Empty(buffer.Enqueue(current, 2));
        Assert.Null(buffer.Dequeue(1));
        Assert.Same(current, buffer.Dequeue(2));
    }

    [Fact]
    public void FailedSubmissionKeepsItsExactTextAndImagesForExplicitHistoryRecovery()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        buffer.Reset(7);
        var failed = Submission("full original prompt including text beyond the history preview", "clipboard.png");
        var nextDraft = Submission("new draft typed while the request was running", "new-draft.png");

        Assert.Empty(buffer.RememberFailure(failed, "truncated history preview", 7));
        Assert.True(buffer.RetainsImage("CLIPBOARD.PNG"));
        Assert.Null(buffer.Dequeue(7)); // Recovery must never become an automatic retry.
        Assert.Equal("new draft typed while the request was running", nextDraft.Prompt);
        Assert.Equal(new[] { "new-draft.png" }, nextDraft.ImagePaths);
        Assert.Null(buffer.TakeRecovery("different history prompt", 7));

        var recovered = buffer.TakeRecovery("truncated history preview", 7);
        Assert.Same(failed, recovered);
        Assert.Equal(new[] { "clipboard.png" }, recovered!.ImagePaths);
        Assert.Equal(new[] { "clipboard.png" }, recovered.OwnedTempImagePaths);
        Assert.False(buffer.RetainsImage("clipboard.png"));
        Assert.Null(buffer.TakeRecovery("truncated history preview", 7));
    }

    [Fact]
    public void FailureAfterAConversationSwitchCannotRestoreIntoTheNewComposer()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        buffer.Reset(1);
        var failed = Submission("old request", "old.png");
        buffer.Reset(2);

        Assert.Same(failed, Assert.Single(buffer.RememberFailure(failed, failed.Prompt, 1)));
        Assert.Null(buffer.TakeRecovery(failed.Prompt, 2));
        Assert.False(buffer.RetainsImage("old.png"));
    }

    [Fact]
    public void ResetReleasesQueuedAndFailedImageOwnershipTogether()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        buffer.Reset(1);
        var queued = Submission("queued", "queued.png");
        var failed = Submission("failed", "failed.png");
        buffer.Enqueue(queued, 1);
        buffer.RememberFailure(failed, failed.Prompt, 1);

        var discarded = buffer.Reset(2);

        Assert.Contains(queued, discarded);
        Assert.Contains(failed, discarded);
        Assert.False(buffer.RetainsImage("queued.png"));
        Assert.False(buffer.RetainsImage("failed.png"));
        Assert.Null(buffer.TakeRecovery(failed.Prompt, 2));
    }

    [Fact]
    public void FailureStorageIsBoundedAndReleasesOnlyUnreferencedSnapshots()
    {
        var buffer = new ComposerSubmissionBuffer(2);
        var first = Submission("first", "shared.png");
        var second = Submission("second", "shared.png");
        var third = Submission("third", "third.png");
        buffer.RememberFailure(first, first.Prompt, 0);
        buffer.RememberFailure(second, second.Prompt, 0);

        Assert.Same(first, Assert.Single(buffer.RememberFailure(third, third.Prompt, 0)));
        Assert.True(buffer.RetainsImage("shared.png"));
        Assert.Null(buffer.TakeRecovery(first.Prompt, 0));
        Assert.Same(second, buffer.TakeRecovery(second.Prompt, 0));
        Assert.False(buffer.RetainsImage("shared.png"));
    }

    [Fact]
    public void RepeatedFailureReplacesItsHistorySnapshotWithoutDuplicatingRecovery()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        var first = Submission("same prompt", "first.png");
        var retry = Submission("same prompt", "retry.png");
        buffer.RememberFailure(first, first.Prompt, 0);

        Assert.Same(first, Assert.Single(buffer.RememberFailure(retry, retry.Prompt, 0)));
        Assert.False(buffer.RetainsImage("first.png"));
        Assert.True(buffer.RetainsImage("retry.png"));
        Assert.Same(retry, buffer.TakeRecovery(retry.Prompt, 0));
        Assert.Null(buffer.TakeRecovery(retry.Prompt, 0));
    }

    [Fact]
    public void QueuedOverflowReturnsTheDiscardedSubmissionForCleanup()
    {
        var buffer = new ComposerSubmissionBuffer(1);
        var first = Submission("first", "first.png");
        var second = Submission("second", "second.png");
        buffer.Enqueue(first, 0);

        Assert.Same(first, Assert.Single(buffer.Enqueue(second, 0)));
        Assert.False(buffer.RetainsImage("first.png"));
        Assert.Same(second, buffer.Dequeue(0));
    }

    [Fact]
    public void CapturedAttachmentsDoNotChangeWhenTheComposerCollectionChanges()
    {
        var images = new List<string> { "clipboard.png" };
        var owned = new List<string> { "clipboard.png" };
        var submission = new ComposerSubmission("prompt", images, owned);
        images.Clear();
        owned.Clear();

        Assert.Equal(new[] { "clipboard.png" }, submission.ImagePaths);
        Assert.Equal(new[] { "clipboard.png" }, submission.OwnedTempImagePaths);
    }

    [Fact]
    public void ClearingHistoryReleasesFailedSnapshotsWithoutDiscardingQueuedMessages()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        var queued = Submission("queued", "queued.png");
        var failed = Submission("failed", "failed.png");
        buffer.Enqueue(queued, 0);
        buffer.RememberFailure(failed, failed.Prompt, 0);

        Assert.Same(failed, Assert.Single(buffer.ClearFailures()));
        Assert.False(buffer.RetainsImage("failed.png"));
        Assert.True(buffer.RetainsImage("queued.png"));
        Assert.Same(queued, buffer.Dequeue(0));
    }

    [Fact]
    public void OldBusyOwnerCompletionCanDrainOnlyTheNewConversationsAuthorizedQueue()
    {
        var buffer = new ComposerSubmissionBuffer(20);
        buffer.Reset(1);
        var oldQueued = Submission("old follow-up", "old.png");
        buffer.Enqueue(oldQueued, 1);
        Assert.Same(oldQueued, Assert.Single(buffer.Reset(2)));
        var first = Submission("new first follow-up", "first.png");
        var second = Submission("new second follow-up", "second.png");
        buffer.Enqueue(first, 2);
        buffer.Enqueue(second, 2);

        // The old execution releases its busy ownership, then drains the current version.
        Assert.Null(buffer.Dequeue(1));
        Assert.Same(first, buffer.Dequeue(2));
        Assert.Same(second, buffer.Dequeue(2));
        Assert.Null(buffer.Dequeue(2));
        Assert.False(buffer.RetainsImage("old.png"));
    }

    private static ComposerSubmission Submission(string prompt, string image)
    {
        return new ComposerSubmission(prompt, new[] { image }, new[] { image });
    }
}
