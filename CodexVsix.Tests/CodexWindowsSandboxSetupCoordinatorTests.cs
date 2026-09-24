using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

[SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification =
    "The fake transport and readiness completion sources are controlled by these tests and do not use the Visual Studio UI context.")]
public sealed class CodexWindowsSandboxSetupCoordinatorTests
{
    [Fact]
    public async Task NotificationBeforeStartedResponsePublishesOnlyAfterReadinessSucceeds()
    {
        var session = new FakeSession();
        var readinessEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(
            session,
            (_, _) =>
            {
                readinessEntered.TrySetResult(true);
                return readinessResult.Task;
            },
            value => published.TrySetResult(value));

        var startTask = coordinator.StartAsync(Settings(), Request(), CancellationToken.None);
        var notification = new JObject { ["mode"] = "elevated", ["success"] = true };
        session.Raise("windowsSandbox/setupCompleted", notification);

        Assert.False(published.Task.IsCompleted);

        session.StartResult.TrySetResult(new JObject { ["started"] = true });
        var started = await startTask;
        Assert.True(started?["started"]?.Value<bool>());
        await readinessEntered.Task;
        Assert.False(published.Task.IsCompleted);

        readinessResult.TrySetResult(true);
        var completion = await published.Task;
        Assert.True(completion?["success"]?.Value<bool>());
        await session.Disposed;
    }

    [Fact]
    public async Task ReadinessFailurePublishesAnErrorCompletion()
    {
        var session = new FakeSession();
        var published = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(
            session,
            (_, _) => Task.FromResult(false),
            value => published.TrySetResult(value));

        var startTask = coordinator.StartAsync(Settings(), Request(), CancellationToken.None);
        session.StartResult.TrySetResult(new JObject { ["started"] = true });
        Assert.True((await startTask)?["started"]?.Value<bool>());

        session.Raise("windowsSandbox/setupCompleted", new JObject
        {
            ["mode"] = "elevated",
            ["success"] = true
        });

        var completion = await published.Task;
        Assert.False(completion?["success"]?.Value<bool>());
        Assert.NotNull(completion?["error"]);
        await session.Disposed;
    }

    [Fact]
    public async Task ConcurrentSetupIsRejectedAndTheActiveSessionIsDisposedAfterCompletion()
    {
        var firstSession = new FakeSession();
        var sessionsCreated = 0;
        var published = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CodexWindowsSandboxSetupCoordinator(
            () =>
            {
                sessionsCreated++;
                return firstSession;
            },
            (_, _) => Task.FromResult(true),
            value => published.TrySetResult(value));

        var firstStartTask = coordinator.StartAsync(Settings(), Request(), CancellationToken.None);
        firstSession.StartResult.TrySetResult(new JObject { ["started"] = true });
        Assert.True((await firstStartTask)?["started"]?.Value<bool>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartAsync(Settings(), Request(), CancellationToken.None));
        Assert.Equal(1, sessionsCreated);
        Assert.Equal(0, firstSession.DisposeCount);

        firstSession.Raise("windowsSandbox/setupCompleted", new JObject
        {
            ["mode"] = "elevated",
            ["success"] = true
        });

        Assert.True((await published.Task)?["success"]?.Value<bool>());
        await firstSession.Disposed;
        Assert.Equal(1, firstSession.DisposeCount);
    }

    [Fact]
    public async Task FailedStartDisposesSessionAndAllowsALaterSetup()
    {
        var failedSession = new FakeSession { StartException = new InvalidOperationException("transport failure") };
        var nextSession = new FakeSession();
        var sessions = new Queue<FakeSession>(new[] { failedSession, nextSession });
        using var coordinator = new CodexWindowsSandboxSetupCoordinator(
            () => sessions.Dequeue(),
            (_, _) => Task.FromResult(true),
            _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartAsync(Settings(), Request(), CancellationToken.None));
        await failedSession.Disposed;

        var retry = coordinator.StartAsync(Settings(), Request(), CancellationToken.None);
        nextSession.StartResult.TrySetResult(new JObject { ["started"] = true });
        Assert.True((await retry)?["started"]?.Value<bool>());
        coordinator.Dispose();
        await nextSession.Disposed;
        Assert.Equal(1, nextSession.DisposeCount);
    }

    private static CodexWindowsSandboxSetupCoordinator CreateCoordinator(
        FakeSession session,
        Func<CodexExtensionSettings, CancellationToken, Task<bool>> readiness,
        Action<JToken?> publishCompletion)
    {
        return new CodexWindowsSandboxSetupCoordinator(() => session, readiness, publishCompletion);
    }

    private static CodexExtensionSettings Settings() => new();

    private static JObject Request() => new() { ["mode"] = "elevated" };

    private sealed class FakeSession : IWindowsSandboxSetupSession
    {
        private readonly TaskCompletionSource<bool> _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string, JToken?>? NotificationReceived;

        public TaskCompletionSource<JToken?> StartResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? StartException { get; set; }

        public int DisposeCount { get; private set; }

        public Task Disposed => _disposed.Task;

        public Task<JToken?> StartAsync(
            CodexExtensionSettings settings,
            JToken? request,
            CancellationToken cancellationToken)
        {
            if (StartException is not null)
                return Task.FromException<JToken?>(StartException);

            return StartResult.Task;
        }

        public void Raise(string method, JToken? parameters)
            => NotificationReceived?.Invoke(method, parameters);

        public void Dispose()
        {
            DisposeCount++;
            _disposed.TrySetResult(true);
        }
    }
}
