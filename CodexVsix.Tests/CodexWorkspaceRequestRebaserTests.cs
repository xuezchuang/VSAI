using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWorkspaceRequestRebaserTests
{
    [Fact]
    public void MissingStartDirectoryUsesSelectedFolderAndLeavesOriginalUntouched()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        var original = new JObject { ["model"] = "model" };

        var prepared = rebaser.PrepareRequest("thread/start", original, @"D:\chosen");

        Assert.Equal(@"D:\chosen", Cwd(prepared));
        Assert.Null(original["cwd"]);
        Assert.Equal("model", prepared?["model"]?.Value<string>());
        Assert.Equal(@"D:\chosen", Cwd(rebaser.PrepareRequest("thread/start", null, @"D:\chosen")));
    }

    [Fact]
    public void ChildDirectoryPreservedByFrontendIsCorrectedOnFirstAndLaterTurns()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        rebaser.ChangeDirectory(@"D:\repo\sub", @"D:\repo");
        Start(rebaser, "new", @"D:\repo\sub", @"D:\repo");

        var first = Turn("new", @"D:\repo\sub");
        var prepared = rebaser.PrepareRequest("turn/start", first, @"D:\repo");
        Assert.Equal(@"D:\repo", Cwd(prepared));
        SucceedTurn(rebaser, first, prepared);

        rebaser.ChangeDirectory(@"D:\repo", @"E:\next");
        Assert.Equal(@"D:\repo", Cwd(rebaser.PrepareRequest("turn/start", first, @"E:\next")));
        Assert.Equal(@"D:\repo\sub", Cwd(first));
    }

    [Fact]
    public void PrewarmedThreadCanBeRebasedWithoutAnotherThreadStart()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        Start(rebaser, "prewarm", @"C:\old", @"C:\old");
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen");
        var original = WithRoots(Turn("prewarm", @"C:\OLD\"), @"C:\old");
        var snapshot = original.DeepClone();

        var prepared = rebaser.PrepareRequest("turn/start", original, @"D:\chosen");

        Assert.Equal(@"D:\chosen", Cwd(prepared));
        AssertRoots(prepared!, @"D:\chosen", @"C:\old");
        Assert.True(JToken.DeepEquals(snapshot, original));
    }

    [Fact]
    public void StartResponseArrivingAfterTwoFolderChangesUsesLatestChoice()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        var original = new JObject { ["cwd"] = @"C:\a" };
        var prepared = rebaser.PrepareRequest("thread/start", original, @"C:\a");
        rebaser.ChangeDirectory(@"C:\a", @"D:\b");
        rebaser.ChangeDirectory(@"D:\b", @"E:\c");
        rebaser.ObserveResponse("thread/start", original, prepared, Started("pending"));

        Assert.Equal(@"E:\c", Cwd(rebaser.PrepareRequest("turn/start", Turn("pending", @"C:\a"), @"E:\c")));
    }

    [Fact]
    public void CorrectedStartResponseInFlightRetainsBothStaleCwds()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        rebaser.ChangeDirectory(@"C:\a", @"D:\b");
        var original = new JObject { ["cwd"] = @"C:\a" };
        var prepared = rebaser.PrepareRequest("thread/start", original, @"D:\b");
        rebaser.ChangeDirectory(@"D:\b", @"E:\c");
        rebaser.ObserveResponse("thread/start", original, prepared, Started("pending"));

        Assert.Equal(@"E:\c", Cwd(rebaser.PrepareRequest("turn/start", Turn("pending", @"D:\b"), @"E:\c")));
        Assert.Equal(@"E:\c", Cwd(rebaser.PrepareRequest("turn/start", Turn("pending", @"C:\a"), @"E:\c")));
    }

    [Fact]
    public void PreparingFirstTurnFreezesItsFolderBeforeResponseOrFailure()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        Start(rebaser, "running", @"C:\old", @"C:\old");
        var turn = Turn("running", @"C:\old");
        var prepared = rebaser.PrepareRequest("turn/start", turn, @"C:\old");
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen");

        // No ObserveResponse represents failure or a response still in flight.
        Assert.Equal(@"C:\old", Cwd(rebaser.PrepareRequest("turn/start", turn, @"D:\chosen")));
        SucceedTurn(rebaser, turn, prepared);
        Assert.Equal(@"C:\old", Cwd(rebaser.PrepareRequest("turn/start", turn, @"D:\chosen")));
    }

    [Fact]
    public void FailedCorrectedTurnKeepsItsPinnedRetryMappingAcrossFolderChanges()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        Start(rebaser, "retry", @"C:\a", @"C:\a");
        rebaser.ChangeDirectory(@"C:\a", @"D:\b");
        var turn = Turn("retry", @"C:\a");
        Assert.Equal(@"D:\b", Cwd(rebaser.PrepareRequest("turn/start", turn, @"D:\b")));
        rebaser.ChangeDirectory(@"D:\b", @"E:\c");

        Assert.Equal(@"D:\b", Cwd(rebaser.PrepareRequest("turn/start", turn, @"E:\c")));
    }

    [Fact]
    public void ExistingAndResumedThreadsAreNeverRebased()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen");
        var resume = new JObject { ["threadId"] = "history", ["cwd"] = @"C:\old" };
        rebaser.ObserveResponse("thread/resume", resume, resume, Started("history"));
        var turn = WithRoots(Turn("history", @"C:\old"), @"C:\old");

        Assert.True(JToken.DeepEquals(turn, rebaser.PrepareRequest("turn/start", turn, @"D:\chosen")));
        Assert.True(JToken.DeepEquals(resume, rebaser.PrepareRequest("thread/resume", resume, @"D:\chosen")));
    }

    [Fact]
    public void ExplicitWorktreeIsNotRewrittenOrMovedOnLaterFolderChanges()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen");
        var request = WithRoots(new JObject { ["cwd"] = @"E:\worktree" }, @"C:\old");
        var prepared = rebaser.PrepareRequest("thread/start", request, @"D:\chosen");
        Assert.True(JToken.DeepEquals(request, prepared));
        rebaser.ObserveResponse("thread/start", request, prepared, Started("worktree"));
        rebaser.ChangeDirectory(@"D:\chosen", @"F:\next");

        Assert.Equal(@"E:\worktree", Cwd(rebaser.PrepareRequest("turn/start", Turn("worktree", @"E:\worktree"), @"F:\next")));
    }

    [Fact]
    public void NewStartRebasesOnlyMatchingRootsAndPreservesPermissionProfile()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen", @"C:\prefill");
        var original = WithRoots(new JObject { ["cwd"] = @"C:\prefill" }, @"C:\prefill");

        var prepared = rebaser.PrepareRequest("thread/start", original, @"D:\chosen");

        Assert.Equal(@"D:\chosen", Cwd(prepared));
        AssertRoots(prepared!, @"D:\chosen", @"C:\prefill");
        AssertRoots(original, @"C:\prefill");
        var profile = new JObject { ["cwd"] = @"C:\old", ["permissions"] = "named-profile" };
        Assert.Equal("named-profile", rebaser.PrepareRequest("thread/start", profile, @"D:\chosen")?["permissions"]?.Value<string>());
    }

    [Fact]
    public void FailedThreadStartDoesNotRegisterAHistoricalThread()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        var request = new JObject { ["cwd"] = @"C:\old" };
        var prepared = rebaser.PrepareRequest("thread/start", request, @"C:\old");
        rebaser.ObserveResponse("thread/start", request, prepared, null);
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen");

        Assert.Equal(@"C:\old", Cwd(rebaser.PrepareRequest("turn/start", Turn("history", @"C:\old"), @"D:\chosen")));
    }

    [Fact]
    public void MissingTurnCwdOnRebasedPrewarmReceivesTheSelectedFolder()
    {
        var rebaser = new CodexWorkspaceRequestRebaser();
        Start(rebaser, "prewarm", @"C:\old", @"C:\old");
        rebaser.ChangeDirectory(@"C:\old", @"D:\chosen");

        Assert.Equal(@"D:\chosen", Cwd(rebaser.PrepareRequest("turn/start", new JObject { ["threadId"] = "prewarm" }, @"D:\chosen")));
    }

    private static void Start(CodexWorkspaceRequestRebaser rebaser, string id, string requestedDirectory, string defaultDirectory)
    {
        var original = new JObject { ["cwd"] = requestedDirectory };
        var prepared = rebaser.PrepareRequest("thread/start", original, defaultDirectory);
        rebaser.ObserveResponse("thread/start", original, prepared, Started(id));
    }

    private static JObject Started(string id) => new() { ["thread"] = new JObject { ["id"] = id } };
    private static JObject Turn(string id, string directory) => new() { ["threadId"] = id, ["cwd"] = directory };
    private static string? Cwd(JToken? request) => request?["cwd"]?.Value<string>();
    private static void SucceedTurn(CodexWorkspaceRequestRebaser rebaser, JToken original, JToken? prepared)
        => rebaser.ObserveResponse("turn/start", original, prepared, new JObject { ["turn"] = new JObject { ["id"] = "turn" } });

    private static JObject WithRoots(JObject request, string directory)
    {
        request["workspaceRoots"] = new JArray(directory, @"Z:\shared", directory + @"\child");
        request["sandboxPolicy"] = new JObject { ["type"] = "workspaceWrite", ["writableRoots"] = new JArray(directory, @"Z:\shared") };
        request["permissions"] = new JObject { ["sandboxPolicy"] = new JObject { ["writableRoots"] = new JArray(directory, @"Z:\shared") } };
        request["config"] = new JObject
        {
            ["sandbox_workspace_write.writable_roots"] = new JArray(directory, @"Z:\shared"),
            ["sandbox_workspace_write"] = new JObject { ["writable_roots"] = new JArray(directory, @"Z:\shared") },
            ["unrelated"] = directory
        };
        return request;
    }

    private static void AssertRoots(JToken request, string directory, string? originalDirectory = null)
    {
        Assert.Equal(directory, request["workspaceRoots"]?[0]?.Value<string>());
        Assert.Equal(@"Z:\shared", request["workspaceRoots"]?[1]?.Value<string>());
        Assert.Equal((originalDirectory ?? directory) + @"\child", request["workspaceRoots"]?[2]?.Value<string>());
        Assert.Equal(directory, request["sandboxPolicy"]?["writableRoots"]?[0]?.Value<string>());
        Assert.Equal(@"Z:\shared", request["sandboxPolicy"]?["writableRoots"]?[1]?.Value<string>());
        Assert.Equal(directory, request["permissions"]?["sandboxPolicy"]?["writableRoots"]?[0]?.Value<string>());
        Assert.Equal(directory, request["config"]?["sandbox_workspace_write.writable_roots"]?[0]?.Value<string>());
        Assert.Equal(directory, request["config"]?["sandbox_workspace_write"]?["writable_roots"]?[0]?.Value<string>());
        Assert.Equal(originalDirectory ?? directory, request["config"]?["unrelated"]?.Value<string>());
    }
}
