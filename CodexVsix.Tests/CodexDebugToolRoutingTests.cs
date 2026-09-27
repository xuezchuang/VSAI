using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexDebugToolRoutingTests
{
    [Theory]
    [InlineData("vs_debug_snapshot", null, true)]
    [InlineData("other_tool", null, false)]
    [InlineData("vs_debug_snapshot", "remote", false)]
    public async Task OwnedCallsAreHandledLocallyAndOtherNamespacesRelayUnchanged(string tool, string? ns, bool owned)
    {
        var backend = new VsDebugToolDispatcherTests.FakeDebugToolService
        {
            Execute = (_, __, ___) => Task.FromResult(new JObject { ["snapshotId"] = "snapshot-1" })
        };
        using var service = new CodexProcessService(CodexDiagnosticLogger.Shared, true, null, backend);
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        SetField(service, "_serverInput", writer);
        SetField(service, "_serverGeneration", 4L);
        JObject? relayed = null;
        service.AppServerRequestHandler = request =>
        {
            relayed = request;
            return Task.FromResult<JObject?>(new JObject { ["result"] = new JObject { ["relay"] = true } });
        };
        var parameters = new JObject
        {
            ["tool"] = tool,
            ["arguments"] = new JObject(),
            ["threadId"] = "thread-1",
            ["turnId"] = "turn-1"
        };
        if (ns is not null) parameters["namespace"] = ns;
        var message = new JObject
        {
            ["id"] = 22,
            ["method"] = "item/tool/call",
            ["params"] = parameters
        };

        await (Task)Invoke(service, "HandleServerRequestAsync", message, 4L)!;

        Assert.Equal(owned ? 1 : 0, backend.CallCount);
        Assert.Equal(owned, relayed is null);
        if (!owned)
            Assert.True(JToken.DeepEquals(message, relayed));
        var reply = JObject.Parse(Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(22, reply["id"]!.Value<int>());
        Assert.Equal(owned, reply["result"]?["success"]?.Value<bool>() ?? false);
        if (!owned) Assert.True(reply["result"]?["relay"]!.Value<bool>());
    }

    [Fact]
    public async Task PendingLocalToolReplyCannotReachReplacementServer()
    {
        var backend = new VsDebugToolDispatcherTests.FakeDebugToolService();
        using var service = new CodexProcessService(CodexDiagnosticLogger.Shared, true, null, backend);
        SetField(service, "_serverGeneration", 4L);
        var message = new JObject
        {
            ["id"] = 22,
            ["method"] = "item/tool/call",
            ["params"] = new JObject { ["tool"] = VsDebugTools.Snapshot, ["arguments"] = new JObject() }
        };
        var handling = (Task)Invoke(service, "HandleServerRequestAsync", message, 4L)!;
        await backend.WaitForStartedAsync();

        Invoke(service, "RestartServer", false);
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        SetField(service, "_serverInput", writer);
        backend.Complete(new JObject { ["snapshotId"] = "stale" });

        await handling;
        Assert.Equal(0L, output.Length);
    }

    private static object? Invoke(CodexProcessService service, string name, params object?[] arguments) =>
        typeof(CodexProcessService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments);

    private static void SetField(CodexProcessService service, string name, object value) =>
        typeof(CodexProcessService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);
}
