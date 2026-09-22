using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWebViewHistoryWindowControllerTests
{
    [Fact]
    public async Task PausesAutomaticHydrationAndReleasesOnlyAnExplicitBatch()
    {
        var statuses = new List<CodexWebViewHistoryWindowStatus>();
        using var controller = new CodexWebViewHistoryWindowController(statuses.Add);
        var parameters = new JObject
        {
            ["threadId"] = "thread-long",
            ["cursor"] = "older-24",
            ["limit"] = 5
        };

        for (var page = 0; page < 24; page++)
        {
            controller.ObserveResponse(
                "thread/turns/list",
                parameters,
                BuildPage(page * 5, 5, "older-" + (page + 25)));
        }

        var status = controller.GetStatus("thread-long");
        Assert.NotNull(status);
        Assert.Equal(CodexWebViewHistoryWindowController.InitialTurnBudget, status!.LoadedTurns);
        Assert.True(status.IsVisible);
        Assert.True(status.CanLoadMore);

        var waiting = controller.WaitForCapacityAsync(
            "thread/turns/list",
            parameters,
            CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        Assert.True(controller.LoadOlder("thread-long"));
        await waiting;

        status = controller.GetStatus("thread-long");
        Assert.NotNull(status);
        Assert.True(status!.IsLoading);
        Assert.False(status.CanLoadMore);

        for (var page = 24; page < 28; page++)
        {
            controller.ObserveResponse(
                "thread/turns/list",
                parameters,
                BuildPage(page * 5, 5, "older-" + (page + 25)));
        }

        status = controller.GetStatus("thread-long");
        Assert.NotNull(status);
        Assert.Equal(
            CodexWebViewHistoryWindowController.InitialTurnBudget
                + CodexWebViewHistoryWindowController.AdditionalTurnBudget,
            status!.LoadedTurns);
        Assert.True(status.CanLoadMore);
        Assert.Contains(statuses, item => item.IsVisible && item.LoadedTurns == 120);
    }

    [Fact]
    public void DoesNotDoubleCountTurnsWhenTheOfficialUiRehydratesTheTail()
    {
        using var controller = new CodexWebViewHistoryWindowController(_ => { });
        var parameters = new JObject { ["threadId"] = "thread-1" };
        var page = BuildPage(0, 5, "older");

        controller.ObserveResponse("thread/turns/list", parameters, page);
        controller.ObserveResponse("thread/turns/list", parameters, page);

        Assert.Equal(5, controller.GetStatus("thread-1")?.LoadedTurns);
    }

    [Fact]
    public async Task LoadOlderGrantsANewBatchEvenWhenOnePageOvershootsTheByteBudget()
    {
        using var controller = new CodexWebViewHistoryWindowController(_ => { });
        var parameters = new JObject { ["threadId"] = "large-page", ["cursor"] = "older" };
        var page = BuildPage(0, 20, "older");
        foreach (var turn in (JArray)page["data"]!)
        {
            var items = (JArray)turn["items"]!;
            for (var index = 0; index < 3; index++)
            {
                items.Add(new JObject
                {
                    ["type"] = "agentMessage",
                    ["text"] = new string('x', CodexWebViewPayloadLimiter.MaxMessageTextLength)
                });
            }
        }
        controller.ObserveResponse("thread/turns/list", parameters, page);
        var waiting = controller.WaitForCapacityAsync("thread/turns/list", parameters, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        Assert.True(controller.LoadOlder("large-page"));
        await waiting;

        // The released request must have room to fetch data, rather than asking
        // the user to click repeatedly just to catch up with an oversized page.
        Assert.True(controller.WaitForCapacityAsync("thread/turns/list", parameters, CancellationToken.None).IsCompleted);
        Assert.False(controller.GetStatus("large-page")!.CanLoadMore);
        Assert.False(controller.LoadOlder("large-page"));
    }

    [Fact]
    public async Task ARequestForAnotherThreadCannotReleaseTheWaitingThread()
    {
        using var controller = new CodexWebViewHistoryWindowController(_ => { });
        var parameters = new JObject { ["threadId"] = "waiting-thread", ["cursor"] = "older" };
        controller.ObserveResponse("thread/turns/list", parameters,
            BuildPage(0, CodexWebViewHistoryWindowController.InitialTurnBudget, "older"));
        var waiting = controller.WaitForCapacityAsync("thread/turns/list", parameters, CancellationToken.None);

        Assert.False(controller.LoadOlder("different-thread"));
        Assert.False(waiting.IsCompleted);
        Assert.True(controller.GetStatus("waiting-thread")!.CanLoadMore);

        Assert.True(controller.LoadOlder("waiting-thread"));
        await waiting;
    }

    [Fact]
    public void ExhaustedHistoryCannotEnterAFalseLoadingState()
    {
        using var controller = new CodexWebViewHistoryWindowController(_ => { });
        controller.ObserveResponse("thread/turns/list", new JObject { ["threadId"] = "finished" },
            BuildPage(0, 1, null));

        Assert.False(controller.LoadOlder("finished"));
        Assert.False(controller.GetStatus("finished")!.IsLoading);
        Assert.False(controller.GetStatus("finished")!.IsVisible);
    }

    private static JObject BuildPage(int start, int count, string? nextCursor)
    {
        var turns = new JArray();
        for (var index = 0; index < count; index++)
        {
            turns.Add(new JObject
            {
                ["id"] = "turn-" + (start + index),
                ["items"] = new JArray(
                    new JObject
                    {
                        ["id"] = "message-" + (start + index),
                        ["type"] = "agentMessage",
                        ["text"] = "response"
                    })
            });
        }

        return new JObject
        {
            ["data"] = turns,
            ["nextCursor"] = nextCursor is null ? JValue.CreateNull() : nextCursor
        };
    }
}
