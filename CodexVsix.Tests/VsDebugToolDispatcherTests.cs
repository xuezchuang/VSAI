using System;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class VsDebugToolDispatcherTests
{
    [Fact]
    public async Task ServiceFailureIsWrappedWithoutExposingExceptionMessage()
    {
        using var dispatcher = new VsDebugToolDispatcher(new FakeDebugToolService
        {
            Execute = (_, __, ___) => throw new InvalidOperationException("private debuggee value")
        });

        var response = await dispatcher.ExecuteAsync(1, Call(VsDebugTools.Snapshot), 2);

        Assert.False(response!["success"]!.Value<bool>());
        Assert.Contains("debugger_unavailable", response.ToString());
        Assert.DoesNotContain("private debuggee value", response.ToString());
    }

    [Fact]
    public async Task OversizedPayloadIsReplacedWithBoundedError()
    {
        using var dispatcher = new VsDebugToolDispatcher(new FakeDebugToolService
        {
            Execute = (_, __, ___) => Task.FromResult(new JObject { ["output"] = new string('x', VsDebugToolDispatcher.MaxResultCharacters + 1) })
        });

        var response = await dispatcher.ExecuteAsync(1, Call(VsDebugTools.Snapshot), 1);

        Assert.False(response!["success"]!.Value<bool>());
        Assert.Contains("result_too_large", response.ToString());
        Assert.DoesNotContain("xxxx", response.ToString());
    }

    [Theory]
    [InlineData("resolved")]
    [InlineData("turn-completed")]
    [InlineData("interrupted")]
    [InlineData("generation")]
    public async Task LifecycleEventsCancelPendingReadAndSuppressItsReply(string retirement)
    {
        var fake = new FakeDebugToolService();
        using var dispatcher = new VsDebugToolDispatcher(fake);
        var call = dispatcher.ExecuteAsync(17, Call(VsDebugTools.Threads, "thread-1", "turn-1"), 5);
        var token = await fake.WaitForStartedAsync();

        switch (retirement)
        {
            case "resolved":
                dispatcher.ObserveNotification("serverRequest/resolved",
                    new JObject { ["threadId"] = "thread-1", ["requestId"] = 17 });
                break;
            case "turn-completed":
                dispatcher.ObserveNotification("turn/completed",
                    new JObject { ["threadId"] = "thread-1", ["turn"] = new JObject { ["id"] = "turn-1" } });
                break;
            case "interrupted":
                dispatcher.Interrupt(new JObject { ["threadId"] = "thread-1", ["turnId"] = "turn-1" });
                break;
            default:
                dispatcher.RetireBefore(6);
                break;
        }

        Assert.True(token.IsCancellationRequested);
        fake.Complete(new JObject { ["ok"] = true });
        Assert.Null(await call);
    }

    [Fact]
    public async Task StaleGenerationIsRejectedBeforeCallingBackend()
    {
        var fake = new FakeDebugToolService();
        using var dispatcher = new VsDebugToolDispatcher(fake);
        dispatcher.RetireBefore(9);

        var response = await dispatcher.ExecuteAsync(1, Call(VsDebugTools.Snapshot), 8);

        Assert.Null(response);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task RequestResolutionMatchesBothIdAndThreadWhenProvided()
    {
        var fake = new FakeDebugToolService();
        using var dispatcher = new VsDebugToolDispatcher(fake);
        var call = dispatcher.ExecuteAsync(17, Call(VsDebugTools.Threads, "thread-1", "turn-1"), 5);
        var token = await fake.WaitForStartedAsync();

        dispatcher.ObserveNotification("serverRequest/resolved",
            new JObject { ["threadId"] = "another-thread", ["requestId"] = 17 });
        Assert.False(token.IsCancellationRequested);
        dispatcher.ObserveNotification("serverRequest/resolved",
            new JObject { ["threadId"] = "thread-1", ["requestId"] = 17 });
        Assert.True(token.IsCancellationRequested);
        fake.Complete(new JObject());
        Assert.Null(await call);
    }

    [Fact]
    public async Task InterruptFromStaleGenerationDoesNotCancelCurrentRead()
    {
        var fake = new FakeDebugToolService();
        using var dispatcher = new VsDebugToolDispatcher(fake);
        var call = dispatcher.ExecuteAsync(17, Call(VsDebugTools.Threads, "thread-1", "turn-1"), 5);
        var token = await fake.WaitForStartedAsync();

        dispatcher.Interrupt(new JObject { ["threadId"] = "thread-1", ["turnId"] = "turn-1" }, generation: 4);
        Assert.False(token.IsCancellationRequested);
        fake.Complete(new JObject { ["ok"] = true });

        var response = await call;
        Assert.True(response!["success"]!.Value<bool>());
    }

    private static JObject Call(string name, string? threadId = null, string? turnId = null)
    {
        var arguments = name == VsDebugTools.Snapshot
            ? new JObject()
            : new JObject { ["snapshotId"] = "snapshot-1" };
        var parameters = new JObject { ["tool"] = name, ["arguments"] = arguments };
        if (threadId is not null) parameters["threadId"] = threadId;
        if (turnId is not null) parameters["turnId"] = turnId;
        return parameters;
    }

    internal sealed class FakeDebugToolService : IVsDebugToolService
    {
        private readonly TaskCompletionSource<CancellationToken> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JObject> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, JObject, CancellationToken, Task<JObject>>? Execute { get; set; }
        public int CallCount { get; private set; }

        public Task<JObject> ExecuteAsync(string toolName, JObject arguments, CancellationToken cancellationToken)
        {
            CallCount++;
            _started.TrySetResult(cancellationToken);
            return Execute is null ? AwaitCompletion(cancellationToken) : Execute(toolName, arguments, cancellationToken);
        }

        public Task<CancellationToken> WaitForStartedAsync() => _started.Task;
        public void Complete(JObject value) => _completion.TrySetResult(value);
        public void Dispose() { }

        private async Task<JObject> AwaitCompletion(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() => _completion.TrySetCanceled()))
                return await _completion.Task.ConfigureAwait(false);
        }
    }
}
