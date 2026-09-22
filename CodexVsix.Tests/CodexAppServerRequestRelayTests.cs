using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexAppServerRequestRelayTests
{
    [Fact]
    public async Task RelaysServerRequestAndMatchesOfficialWebviewResponse()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(message => posted.Add(message));
        var pending = relay.ForwardAsync(new JObject
        {
            ["id"] = 42,
            ["method"] = "item/commandExecution/requestApproval",
            ["params"] = new JObject { ["threadId"] = "thread-1" }
        });

        var envelope = Assert.Single(posted);
        Assert.Equal("mcp-request", envelope["type"]?.Value<string>());
        Assert.Equal("local", envelope["hostId"]?.Value<string>());
        var relayId = envelope["request"]?["id"];
        Assert.Equal(JTokenType.String, relayId?.Type);

        Assert.True(relay.TryHandleResponse(new JObject
        {
            ["type"] = "mcp-response",
            ["hostId"] = "local",
            ["response"] = new JObject
            {
                ["id"] = relayId?.DeepClone(),
                ["result"] = new JObject { ["decision"] = "accept" }
            }
        }));

        var response = await pending;
        Assert.Equal(42, response?["id"]?.Value<int>());
        Assert.Equal("accept", response?["result"]?["decision"]?.Value<string>());
    }

    [Fact]
    public async Task LateResponseAfterCancellationCannotAnswerReusedServerId()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var request = new JObject { ["id"] = 42, ["method"] = "item/commandExecution/requestApproval" };
        var oldPending = relay.ForwardAsync(request);
        var oldRelayId = posted[0]["request"]!["id"]!.DeepClone();
        relay.CancelPending();
        Assert.Null(await oldPending);

        var newPending = relay.ForwardAsync(request);
        var newRelayId = posted[1]["request"]!["id"]!.DeepClone();
        Assert.False(JToken.DeepEquals(oldRelayId, newRelayId));
        Assert.False(relay.TryHandleResponse(CreateResponse(oldRelayId, "accept")));
        Assert.False(newPending.IsCompleted);

        Assert.True(relay.TryHandleResponse(CreateResponse(newRelayId, "decline")));
        var response = await newPending;
        Assert.Equal(42, response?["id"]?.Value<int>());
        Assert.Equal("decline", response?["result"]?["decision"]?.Value<string>());
        Assert.Equal(42, request["id"]?.Value<int>());
    }

    [Fact]
    public async Task ConcurrentRequestsWithReusedServerIdKeepTheirResponsesSeparate()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var request = new JObject { ["id"] = "approval-1", ["method"] = "item/commandExecution/requestApproval" };
        var first = relay.ForwardAsync(request);
        var second = relay.ForwardAsync(request);
        var firstId = posted[0]["request"]!["id"]!.DeepClone();
        var secondId = posted[1]["request"]!["id"]!.DeepClone();

        Assert.True(relay.TryHandleResponse(CreateResponse(secondId, "decline")));
        Assert.False(first.IsCompleted);
        Assert.Equal("decline", (await second)?["result"]?["decision"]?.Value<string>());
        Assert.True(relay.TryHandleResponse(CreateResponse(firstId, "accept")));
        var firstResponse = await first;
        Assert.Equal("approval-1", firstResponse?["id"]?.Value<string>());
        Assert.Equal("accept", firstResponse?["result"]?["decision"]?.Value<string>());
    }

    [Fact]
    public async Task ErrorResponseRestoresOriginalIdWithoutChangingWebviewMessage()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var pending = relay.ForwardAsync(new JObject { ["id"] = 7, ["method"] = "item/tool/requestUserInput" });
        var relayId = posted[0]["request"]!["id"]!.DeepClone();
        var message = new JObject
        {
            ["response"] = new JObject
            {
                ["id"] = relayId.DeepClone(),
                ["error"] = new JObject { ["code"] = -32603, ["message"] = "Unavailable" }
            }
        };

        Assert.True(relay.TryHandleResponse(message));
        var response = await pending;
        Assert.Equal(7, response?["id"]?.Value<int>());
        Assert.Equal(-32603, response?["error"]?["code"]?.Value<int>());
        Assert.True(JToken.DeepEquals(relayId, message["response"]?["id"]));
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The relay completion sources use RunContinuationsAsynchronously and are completed by the test before exception assertions; no Visual Studio UI context is involved.")]
    public async Task ResolvedNotificationEndsPendingRequestAndRejectsLateResponse()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var pending = relay.ForwardAsync(CreateRequest("thread-1", new JValue(42)));
        var uiId = posted[0]["request"]!["id"]!;

        var transformed = relay.TransformNotificationParameters("serverRequest/resolved", CreateResolved("thread-1", new JValue(42)));

        Assert.True(JToken.DeepEquals(uiId, transformed?["requestId"]));
        Assert.True(pending.IsCompleted);
        await Assert.ThrowsAsync<CodexServerRequestResolvedException>(() => pending);
        Assert.False(relay.TryHandleResponse(CreateResponse(uiId, "accept")));
    }

    [Fact]
    public async Task ResolvedNotificationUsesUiIdAfterResponseAndConsumesMapping()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var pending = relay.ForwardAsync(CreateRequest("thread-1", new JValue(42)));
        var uiId = posted[0]["request"]!["id"]!;
        Assert.True(relay.TryHandleResponse(CreateResponse(uiId, "accept")));
        Assert.Equal(42, (await pending)?["id"]?.Value<int>());
        var parameters = CreateResolved("thread-1", new JValue(42));

        var transformed = relay.TransformNotificationParameters("serverRequest/resolved", parameters);

        Assert.True(JToken.DeepEquals(uiId, transformed?["requestId"]));
        Assert.Equal("thread-1", transformed?["threadId"]?.Value<string>());
        Assert.Equal(42, parameters["requestId"]!.Value<int>());
        Assert.Same(parameters, relay.TransformNotificationParameters("serverRequest/resolved", parameters));
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The relay completion sources use RunContinuationsAsynchronously and are completed by the test before exception assertions; no Visual Studio UI context is involved.")]
    public async Task ResolvedNotificationMatchesThreadAndPreservesRequestIdType()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var firstPending = relay.ForwardAsync(CreateRequest("thread-1", new JValue(42)));
        var secondPending = relay.ForwardAsync(CreateRequest("thread-2", new JValue(42)));
        var textPending = relay.ForwardAsync(CreateRequest("thread-1", new JValue("42")));

        var first = relay.TransformNotificationParameters("serverRequest/resolved", CreateResolved("thread-1", new JValue(42)));
        var second = relay.TransformNotificationParameters("serverRequest/resolved", CreateResolved("thread-2", new JValue(42)));
        var text = relay.TransformNotificationParameters("serverRequest/resolved", CreateResolved("thread-1", new JValue("42")));

        Assert.True(JToken.DeepEquals(posted[0]["request"]!["id"], first?["requestId"]));
        Assert.True(JToken.DeepEquals(posted[1]["request"]!["id"], second?["requestId"]));
        Assert.True(JToken.DeepEquals(posted[2]["request"]!["id"], text?["requestId"]));
        await Assert.ThrowsAsync<CodexServerRequestResolvedException>(() => firstPending);
        await Assert.ThrowsAsync<CodexServerRequestResolvedException>(() => secondPending);
        await Assert.ThrowsAsync<CodexServerRequestResolvedException>(() => textPending);
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The relay completion sources use RunContinuationsAsynchronously and are completed by the test before exception assertions; no Visual Studio UI context is involved.")]
    public async Task OldResponseDoesNotReplaceNewMappingForReusedServerId()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        var request = CreateRequest("thread-1", new JValue(42));
        _ = relay.ForwardAsync(request);
        var newPending = relay.ForwardAsync(request);
        Assert.True(relay.TryHandleResponse(CreateResponse(posted[0]["request"]!["id"]!, "accept")));

        var transformed = relay.TransformNotificationParameters("serverRequest/resolved", CreateResolved("thread-1", new JValue(42)));

        Assert.True(JToken.DeepEquals(posted[1]["request"]!["id"], transformed?["requestId"]));
        await Assert.ThrowsAsync<CodexServerRequestResolvedException>(() => newPending);
    }

    [Fact]
    public void CancelPendingClearsRetainedNotificationMappings()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        _ = relay.ForwardAsync(CreateRequest("thread-1", new JValue(42)));
        Assert.True(relay.TryHandleResponse(CreateResponse(posted[0]["request"]!["id"]!, "accept")));
        relay.CancelPending();
        var parameters = CreateResolved("thread-1", new JValue(42));

        Assert.Same(parameters, relay.TransformNotificationParameters("serverRequest/resolved", parameters));
    }

    [Fact]
    public async Task NotificationRetentionEvictsOldestMappingAtCapacity()
    {
        var posted = new List<JObject>();
        using var relay = new CodexAppServerRequestRelay(posted.Add);
        for (var id = 0; id <= CodexAppServerRequestRelay.MaxNotificationMappings; id++)
        {
            var pending = relay.ForwardAsync(CreateRequest("thread-1", new JValue(id)));
            Assert.True(relay.TryHandleResponse(CreateResponse(posted[id]["request"]!["id"]!, "accept")));
            Assert.NotNull(await pending);
        }

        var oldest = CreateResolved("thread-1", new JValue(0));
        Assert.Same(oldest, relay.TransformNotificationParameters("serverRequest/resolved", oldest));
        var newest = relay.TransformNotificationParameters("serverRequest/resolved",
            CreateResolved("thread-1", new JValue(CodexAppServerRequestRelay.MaxNotificationMappings)));
        Assert.True(JToken.DeepEquals(posted[posted.Count - 1]["request"]!["id"], newest?["requestId"]));
    }

    [Fact]
    public void FailedPostDoesNotLeaveNotificationMapping()
    {
        using var relay = new CodexAppServerRequestRelay(_ => throw new InvalidOperationException("view closed"));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = relay.ForwardAsync(CreateRequest("thread-1", new JValue(42)));
        });
        var parameters = CreateResolved("thread-1", new JValue(42));

        Assert.Same(parameters, relay.TransformNotificationParameters("serverRequest/resolved", parameters));
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The relay completion sources use RunContinuationsAsynchronously and are completed by the test before exception assertions; no Visual Studio UI context is involved.")]
    public async Task UnrelatedAndUnmatchedNotificationsRemainUnchanged()
    {
        using var relay = new CodexAppServerRequestRelay(_ => { });
        var pending = relay.ForwardAsync(CreateRequest("thread-1", new JValue(42)));
        var parameters = CreateResolved("thread-1", new JValue(42));
        var unknown = CreateResolved("thread-unknown", new JValue(42));
        var malformed = new JObject { ["threadId"] = "thread-1" };

        Assert.Same(parameters, relay.TransformNotificationParameters("turn/completed", parameters));
        Assert.Same(unknown, relay.TransformNotificationParameters("serverRequest/resolved", unknown));
        Assert.Same(malformed, relay.TransformNotificationParameters("serverRequest/resolved", malformed));
        Assert.Null(relay.TransformNotificationParameters("serverRequest/resolved", null));
        Assert.NotSame(parameters, relay.TransformNotificationParameters("serverRequest/resolved", parameters));
        await Assert.ThrowsAsync<CodexServerRequestResolvedException>(() => pending);
    }

    private static JObject CreateRequest(string threadId, JToken id)
    {
        return new JObject
        {
            ["id"] = id,
            ["method"] = "item/commandExecution/requestApproval",
            ["params"] = new JObject { ["threadId"] = threadId }
        };
    }

    private static JObject CreateResolved(string threadId, JToken id)
    {
        return new JObject { ["threadId"] = threadId, ["requestId"] = id };
    }

    private static JObject CreateResponse(JToken id, string decision)
    {
        return new JObject
        {
            ["type"] = "mcp-response",
            ["hostId"] = "local",
            ["response"] = new JObject
            {
                ["id"] = id.DeepClone(),
                ["result"] = new JObject { ["decision"] = decision }
            }
        };
    }

    [Fact]
    public async Task CancelPendingReturnsControlToClassicFallback()
    {
        using var relay = new CodexAppServerRequestRelay(_ => { });
        var pending = relay.ForwardAsync(new JObject
        {
            ["id"] = "request-1",
            ["method"] = "item/tool/requestUserInput",
            ["params"] = new JObject()
        });

        relay.CancelPending();

        Assert.Null(await pending);
    }
}
