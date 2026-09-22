using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProcessServiceProtocolTests
{
    [Fact]
    public void ScalarAppServerErrorDoesNotAttemptChildAccess()
    {
        var error = new JValue("company gateway rejected the request");

        var message = CodexProcessService.ExtractAppServerErrorMessage(error, "fallback");
        var code = CodexProcessService.ExtractAppServerErrorCode(error);

        Assert.Equal("company gateway rejected the request", message);
        Assert.Null(code);
    }

    [Fact]
    public void ObjectAppServerErrorPreservesMessageAndNumericStringCode()
    {
        var error = new JObject
        {
            ["message"] = "invalid provider response",
            ["code"] = "-32602"
        };

        var message = CodexProcessService.ExtractAppServerErrorMessage(error, "fallback");
        var code = CodexProcessService.ExtractAppServerErrorCode(error);

        Assert.Equal("invalid provider response", message);
        Assert.Equal(-32602, code);
    }

    [Fact]
    public void StructuredAppServerErrorCodeIsIgnoredInsteadOfThrowing()
    {
        var error = new JObject
        {
            ["message"] = "malformed code",
            ["code"] = new JObject { ["unexpected"] = true }
        };

        Assert.Null(CodexProcessService.ExtractAppServerErrorCode(error));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("error")]
    [InlineData("exception")]
    [InlineData("unhandled")]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The test controls these RunContinuationsAsynchronously completion sources directly; no Visual Studio UI context is involved.")]
    public async Task DelayedServerRequestCannotReplyToReplacementServer(string outcome)
    {
        using var service = new CodexProcessService();
        var reply = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AppServerRequestHandler = _ => reply.Task;
        var fallbackInvoked = false;
        service.UserInputRequestHandler = _ =>
        {
            fallbackInvoked = true;
            return Task.FromResult<JObject?>(new JObject());
        };
        SetField(service, "_serverGeneration", 7L);
        var request = new JObject { ["id"] = 1, ["method"] = "item/tool/requestUserInput" };
        var handling = (Task)Invoke(service, "HandleServerRequestAsync", request, 7L)!;
        Assert.False(handling.IsCompleted);

        Invoke(service, "RestartServer", false);
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        SetField(service, "_serverInput", writer);
        var generation = (long)GetField(service, "_serverGeneration")!;
        if (outcome == "exception")
        {
            reply.SetException(new InvalidOperationException("old interface request failed"));
        }
        else if (outcome == "unhandled")
        {
            reply.SetResult(null);
        }
        else
        {
            reply.SetResult(outcome == "error"
                ? new JObject { ["error"] = new JObject { ["code"] = -32603, ["message"] = "old error" } }
                : new JObject { ["result"] = new JObject { ["answer"] = "old answer" } });
        }

        await handling;
        Assert.Equal(0L, output.Length);
        Assert.False(fallbackInvoked);

        // A new server may reuse the same JSON-RPC id. Only its own response may be written.
        service.AppServerRequestHandler = _ => Task.FromResult<JObject?>(
            new JObject { ["result"] = new JObject { ["answer"] = "new answer" } });
        await (Task)Invoke(service, "HandleServerRequestAsync", request, generation)!;
        var response = JObject.Parse(Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(1, response["id"]!.Value<int>());
        Assert.Equal("new answer", response["result"]!["answer"]!.Value<string>());
    }

    [Fact]
    public async Task ResolvedServerRequestDoesNotReplyReportErrorOrOpenFallback()
    {
        using var service = new CodexProcessService();
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        SetField(service, "_serverInput", writer);
        SetField(service, "_serverGeneration", 7L);
        var reportedError = false;
        var fallbackInvoked = false;
        var turnType = typeof(CodexProcessService).GetNestedType("ActiveTurnState", BindingFlags.NonPublic)!;
        var turn = Activator.CreateInstance(turnType, new object?[]
        {
            new Action<string>(_ => { }), new Action<string>(_ => reportedError = true),
            (Action<ChatMessage>?)null, (Action<long, long?>?)null
        })!;
        SetField(service, "_activeTurn", turn);
        using var relay = new CodexAppServerRequestRelay(_ => { });
        service.AppServerRequestHandler = relay.ForwardAsync;
        service.UserInputRequestHandler = _ =>
        {
            fallbackInvoked = true;
            return Task.FromResult<JObject?>(new JObject());
        };
        var request = new JObject
        {
            ["id"] = 1,
            ["method"] = "item/tool/requestUserInput",
            ["params"] = new JObject { ["threadId"] = "thread-1" }
        };
        var handling = (Task)Invoke(service, "HandleServerRequestAsync", request, 7L)!;
        Assert.False(handling.IsCompleted);

        relay.TransformNotificationParameters("serverRequest/resolved",
            new JObject { ["threadId"] = "thread-1", ["requestId"] = 1 });
        await handling;

        Assert.Equal(0L, output.Length);
        Assert.False(reportedError);
        Assert.False(fallbackInvoked);
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The test controls these RunContinuationsAsynchronously completion sources directly; no Visual Studio UI context is involved.")]
    public async Task DelayedNativeUserInputCannotReplyToReplacementServer()
    {
        using var service = new CodexProcessService();
        var reply = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.UserInputRequestHandler = _ => reply.Task;
        SetField(service, "_serverGeneration", 7L);
        var request = new JObject { ["id"] = 1, ["method"] = "item/tool/requestUserInput" };
        var handling = (Task)Invoke(service, "HandleServerRequestAsync", request, 7L)!;
        Assert.False(handling.IsCompleted);

        Invoke(service, "RestartServer", false);
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        SetField(service, "_serverInput", writer);
        reply.SetResult(new JObject { ["answers"] = new JObject() });

        await handling;
        Assert.Equal(0L, output.Length);
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The test controls these RunContinuationsAsynchronously completion sources directly; no Visual Studio UI context is involved.")]
    public async Task RestartCompletesActiveTurnWithoutWaitingForAnExitCallback()
    {
        using var service = new CodexProcessService();
        var turnType = typeof(CodexProcessService).GetNestedType("ActiveTurnState", BindingFlags.NonPublic)!;
        var turn = Activator.CreateInstance(turnType, new object?[]
        {
            new Action<string>(_ => { }), new Action<string>(_ => { }),
            (Action<ChatMessage>?)null, (Action<long, long?>?)null
        })!;
        var completion = (TaskCompletionSource<int>)turnType.GetProperty("Completion")!.GetValue(turn)!;
        SetField(service, "_activeTurn", turn);

        Invoke(service, "RestartServer", false);

        Assert.True(completion.Task.IsCompleted);
        Assert.Equal(1, await completion.Task);
        Assert.Null(GetField(service, "_activeTurn"));
    }

    [Fact]
    public void RestartDiscardsOldNotificationsBeforeAReplacementProcessStarts()
    {
        using var service = new CodexProcessService();
        SetField(service, "_serverGeneration", 7L);
        var notifications = 0;
        service.AppServerNotificationReceived += (_, _) => notifications++;

        Invoke(service, "RestartServer", false);
        Invoke(service, "HandleServerMessage", "{\"method\":\"account/updated\",\"params\":{}}", 7L);

        Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The fixture controls these asynchronous completion sources without a Visual Studio UI context.")]
    public async Task DuplicateCancellationWaitsForOneInterruptAndRestartsOnlyOnFailure(bool rejectInterrupt)
    {
        using var fixture = new CancellationFixture();
        var first = fixture.Service.CancelActiveTurnAsync();
        var second = fixture.Service.CancelActiveTurnAsync();

        Assert.Same(first, second);
        Assert.False(second.IsCompleted);
        Assert.False(fixture.Completion.Task.IsCompleted);
        var request = await fixture.WaitForRequestAsync();
        Assert.Equal("turn/interrupt", request["method"]?.Value<string>());
        Assert.Equal("turn-1", request["params"]?["turnId"]?.Value<string>());
        fixture.ResolveInterrupt(request["id"]!, rejectInterrupt);
        await first;

        Assert.Equal(1, await fixture.Completion.Task);
        Assert.Equal(1, fixture.Writer.WriteCount);
        if (rejectInterrupt)
        {
            Assert.Null(GetField(fixture.Service, "_serverProcess"));
            Assert.True(fixture.Process.WaitForExit(1000));
        }
        else
        {
            Assert.NotNull(GetField(fixture.Service, "_serverProcess"));
            Assert.False(fixture.Process.HasExited);
        }
    }

    [Fact]
    public async Task UnansweredInterruptTimesOutAndTerminatesOnlyItsOwnedProcess()
    {
        using var fixture = new CancellationFixture();
        var cancellation = fixture.Service.CancelActiveTurnAsync();
        var finished = await Task.WhenAny(cancellation, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.Same(cancellation, finished);
        await cancellation;
        Assert.True(fixture.Process.WaitForExit(1000));
        Assert.Null(GetField(fixture.Service, "_serverProcess"));
        Assert.True(fixture.Completion.Task.IsCompleted);
        Assert.Empty((System.Collections.IDictionary)GetField(fixture.Service, "_pendingRequests")!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOldCancellationCannotRestartANewerTurnOrGeneration(bool replaceGeneration)
    {
        using var fixture = new CancellationFixture();
        var cancellation = fixture.Service.CancelActiveTurnAsync();
        var request = await fixture.WaitForRequestAsync();
        var nextTurn = CreateTurn("turn-2");
        SetField(fixture.Service, "_activeTurn", nextTurn);
        if (replaceGeneration)
        {
            SetField(fixture.Service, "_serverGeneration", 8L);
        }

        // The old request can already have been received while its continuation
        // resumes after a new turn or process generation has become current.
        fixture.ResolveInterrupt(request["id"]!, reject: true);
        await cancellation;

        Assert.Same(nextTurn, GetField(fixture.Service, "_activeTurn"));
        Assert.NotNull(GetField(fixture.Service, "_serverProcess"));
        Assert.Equal(replaceGeneration ? 8L : 7L, GetField(fixture.Service, "_serverGeneration"));
        Assert.False(fixture.Process.HasExited);
        Assert.False(GetTurnCompletion(nextTurn).Task.IsCompleted);
    }

    private static object CreateTurn(string turnId)
    {
        var turnType = typeof(CodexProcessService).GetNestedType("ActiveTurnState", BindingFlags.NonPublic)!;
        var turn = Activator.CreateInstance(turnType, new object?[]
        {
            new Action<string>(_ => { }), new Action<string>(_ => { }),
            (Action<ChatMessage>?)null, (Action<long, long?>?)null
        })!;
        turnType.GetProperty("TurnId")!.SetValue(turn, turnId);
        return turn;
    }

    private static TaskCompletionSource<int> GetTurnCompletion(object turn)
    {
        return (TaskCompletionSource<int>)turn.GetType().GetProperty("Completion")!.GetValue(turn)!;
    }

    private sealed class CancellationFixture : IDisposable
    {
        public CancellationFixture()
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Process = System.Diagnostics.Process.Start(new ProcessStartInfo(powershell,
                "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            // Keep the original owned process handle for exit assertions even when
            // the service disposes its own wrapper after killing this child.
            var serverProcess = System.Diagnostics.Process.GetProcessById(Process.Id);
            Writer = new CapturingWriter(Output);
            SetField(Service, "_serverProcess", serverProcess);
            SetField(Service, "_serverInput", Writer);
            SetField(Service, "_serverGeneration", 7L);
            SetField(Service, "_threadId", "thread-1");
            var turn = CreateTurn("turn-1");
            SetField(Service, "_activeTurn", turn);
            Completion = GetTurnCompletion(turn);
        }

        public CodexProcessService Service { get; } = new();
        public Process Process { get; }
        public MemoryStream Output { get; } = new();
        public CapturingWriter Writer { get; }
        public TaskCompletionSource<int> Completion { get; }

        [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The fixture writer completes this RunContinuationsAsynchronously source directly after writing the captured request, without a Visual Studio UI context.")]
        public async Task<JObject> WaitForRequestAsync()
        {
            var request = Writer.Request.Task;
            Assert.Same(request, await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(10))));
            return await request;
        }

        public void ResolveInterrupt(JToken requestId, bool reject)
        {
            var response = new JObject { ["id"] = requestId.DeepClone() };
            response[reject ? "error" : "result"] = reject
                ? new JObject { ["code"] = -32603, ["message"] = "interrupt rejected" }
                : new JObject();
            Invoke(Service, "ResolvePendingRequest", response);
        }

        public void Dispose()
        {
            try
            {
                Service.Dispose();
            }
            finally
            {
                try
                {
                    if (!Process.HasExited)
                    {
                        Process.Kill();
                        Process.WaitForExit(2000);
                    }
                }
                finally
                {
                    Process.Dispose();
                    Writer.Dispose();
                    Output.Dispose();
                }
            }
        }
    }

    private sealed class CapturingWriter : StreamWriter
    {
        public CapturingWriter(Stream stream)
            : base(stream, new UTF8Encoding(false), 1024, true)
        {
            AutoFlush = true;
        }

        public TaskCompletionSource<JObject> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount { get; private set; }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            WriteCount++;
            Request.TrySetResult(JObject.Parse(value!));
        }
    }

    private static object? Invoke(CodexProcessService service, string name, params object?[] arguments)
    {
        return typeof(CodexProcessService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments);
    }

    private static object? GetField(CodexProcessService service, string name)
    {
        return typeof(CodexProcessService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);
    }

    private static void SetField(CodexProcessService service, string name, object value)
    {
        typeof(CodexProcessService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);
    }
}
