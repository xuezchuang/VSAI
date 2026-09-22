using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWebViewAutoCompactionCoordinatorTests
{
    [Fact]
    public async Task CompactsAfterTheTurnCompletesWhenContextUsageCrossesTheThreshold()
    {
        var compacted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => true,
            (threadId, _) =>
            {
                compacted.TrySetResult(threadId);
                return Task.CompletedTask;
            },
            _ => { });

        coordinator.ObserveNotification(
            "thread/tokenUsage/updated",
            new JObject
            {
                ["threadId"] = "thread-compact",
                ["tokenUsage"] = new JObject
                {
                    ["last"] = new JObject { ["totalTokens"] = 90 },
                    ["modelContextWindow"] = 100
                }
            });
        coordinator.ObserveNotification(
            "turn/completed",
            new JObject { ["threadId"] = "thread-compact", ["turnId"] = "turn-1" });

        var completed = await Task.WhenAny(compacted.Task, Task.Delay(3000));
        Assert.Same(compacted.Task, completed);
        Assert.Equal("thread-compact", await compacted.Task);
    }

    [Fact]
    public void DisablingDuringTheScheduledDelayPreventsCompactionAndClearsPending()
    {
        var enabled = true;
        var requests = 0;
        var delays = 0;
        var logs = new List<string>();
        using var coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => enabled,
            (_, _) => { requests++; return Task.CompletedTask; },
            logs.Add,
            _ => { delays++; enabled = false; return Task.CompletedTask; });

        ReportUsage(coordinator, 90);
        CompleteTurn(coordinator);
        enabled = true;
        CompleteTurn(coordinator);

        Assert.Equal(1, delays);
        Assert.Equal(0, requests);
        Assert.Empty(logs);
    }

    [Theory]
    [InlineData(false, 20)]
    [InlineData(true, 20)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public void LowerUsageWithdrawsCompactionBeforeOrDuringTheDelay(bool duringDelay, int usage)
    {
        var requests = 0;
        var delays = 0;
        CodexWebViewAutoCompactionCoordinator? coordinator = null;
        using (coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => true,
            (_, _) => { requests++; return Task.CompletedTask; },
            _ => { },
            _ => { delays++; ReportUsage(coordinator!, usage); return Task.CompletedTask; }))
        {
            ReportUsage(coordinator, 90);
            if (!duringDelay)
            {
                ReportUsage(coordinator, usage);
            }

            CompleteTurn(coordinator);
            CompleteTurn(coordinator);

            Assert.Equal(duringDelay ? 1 : 0, delays);
            Assert.Equal(0, requests);
        }
    }

    [Theory]
    [InlineData("turn/started", "turn/completed")]
    [InlineData("codex/event/task_started", "codex/event/task_complete")]
    public void NewTurnDuringDelayDefersCompactionUntilThatTurnCompletes(string started, string completed)
    {
        var requests = 0;
        var delays = 0;
        CodexWebViewAutoCompactionCoordinator? coordinator = null;
        using (coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => true,
            (_, _) => { requests++; return Task.CompletedTask; },
            _ => { },
            _ =>
            {
                if (++delays == 1)
                {
                    coordinator!.ObserveNotification(started, new JObject { ["threadId"] = "thread-1" });
                }

                return Task.CompletedTask;
            }))
        {
            ReportUsage(coordinator, 90);
            CompleteTurn(coordinator);
            Assert.Equal(0, requests);

            coordinator.ObserveNotification(completed, new JObject { ["threadId"] = "thread-1" });
            Assert.Equal(2, delays);
            Assert.Equal(1, requests);
        }
    }

    [Fact]
    public void DisposeDuringDelayDoesNotIssueARequestOrLogAnError()
    {
        var requests = 0;
        var logs = new List<string>();
        CodexWebViewAutoCompactionCoordinator? coordinator = null;
        using (coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => true,
            (_, _) => { requests++; return Task.CompletedTask; },
            logs.Add,
            token =>
            {
                coordinator!.Dispose();
                Assert.True(token.IsCancellationRequested);
                return Task.CompletedTask;
            }))
        {
            ReportUsage(coordinator, 90);
            CompleteTurn(coordinator);

            Assert.Equal(0, requests);
            Assert.Empty(logs);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeDuringCompactionSuppressesLateSuccessAndFailureLogging(bool fail)
    {
        var requests = 0;
        var logs = new List<string>();
        CodexWebViewAutoCompactionCoordinator? coordinator = null;
        using (coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => true,
            (_, token) =>
            {
                requests++;
                coordinator!.Dispose();
                Assert.True(token.IsCancellationRequested);
                return fail ? Task.FromException(new InvalidOperationException("connection closed")) : Task.CompletedTask;
            },
            logs.Add,
            _ => Task.CompletedTask))
        {
            ReportUsage(coordinator, 90);
            CompleteTurn(coordinator);
            ReportUsage(coordinator, 90);
            CompleteTurn(coordinator);

            Assert.Equal(1, requests);
            Assert.Empty(logs);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCompactionRetriesOnlyWhileTheLatestUsageIsStillHigh(bool lowerUsage)
    {
        var requests = 0;
        CodexWebViewAutoCompactionCoordinator? coordinator = null;
        using (coordinator = new CodexWebViewAutoCompactionCoordinator(
            () => true,
            (_, _) =>
            {
                if (++requests == 1)
                {
                    if (lowerUsage)
                    {
                        ReportUsage(coordinator!, 20);
                    }

                    return Task.FromException(new InvalidOperationException("retry later"));
                }

                return Task.CompletedTask;
            },
            _ => { },
            _ => Task.CompletedTask))
        {
            ReportUsage(coordinator, 90);
            CompleteTurn(coordinator);
            CompleteTurn(coordinator);

            Assert.Equal(lowerUsage ? 1 : 2, requests);
        }
    }

    private static void ReportUsage(CodexWebViewAutoCompactionCoordinator coordinator, int usedTokens)
    {
        coordinator.ObserveNotification("thread/tokenUsage/updated", new JObject
        {
            ["threadId"] = "thread-1",
            ["tokenUsage"] = new JObject
            {
                ["last"] = new JObject { ["totalTokens"] = usedTokens },
                ["modelContextWindow"] = 100
            }
        });
    }

    private static void CompleteTurn(CodexWebViewAutoCompactionCoordinator coordinator)
    {
        coordinator.ObserveNotification("turn/completed", new JObject { ["threadId"] = "thread-1" });
    }

    [Fact]
    public void MissingTokenCountDoesNotActLikeAnExplicitZero()
    {
        Assert.False(CodexWebViewAutoCompactionCoordinator.TryReadContextUsage(
            "thread/tokenUsage/updated",
            new JObject
            {
                ["threadId"] = "thread-1",
                ["tokenUsage"] = new JObject { ["modelContextWindow"] = 100 }
            },
            out _, out _, out _));
    }

    [Fact]
    public void ReadsTheSupportedTokenUsageShape()
    {
        Assert.True(CodexWebViewAutoCompactionCoordinator.TryReadContextUsage(
            "thread/tokenUsage/updated",
            new JObject
            {
                ["threadId"] = "thread-1",
                ["tokenUsage"] = new JObject
                {
                    ["lastTokenUsage"] = new JObject { ["totalTokens"] = 850 },
                    ["modelContextWindow"] = 1000
                }
            },
            out var threadId,
            out var used,
            out var window));

        Assert.Equal("thread-1", threadId);
        Assert.Equal(850, used);
        Assert.Equal(1000, window);
    }
}
