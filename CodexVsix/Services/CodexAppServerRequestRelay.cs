using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>
/// Relays JSON-RPC server requests into the official Codex webview and matches
/// the response produced by its native approval and interactive-input UI.
/// </summary>
internal sealed class CodexAppServerRequestRelay : IDisposable
{
    internal const int MaxNotificationMappings = 512;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, LinkedListNode<RequestIdentity>> _notificationIds = new(StringComparer.Ordinal);
    private readonly LinkedList<RequestIdentity> _notificationOrder = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly Action<JObject> _postMessage;
    private bool _disposed;

    public CodexAppServerRequestRelay(Action<JObject> postMessage)
    {
        _postMessage = postMessage ?? throw new ArgumentNullException(nameof(postMessage));
    }

    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The relay completion source uses RunContinuationsAsynchronously; response and cancellation handlers complete it directly without scheduling a Visual Studio UI-thread continuation.")]
    public Task<JObject?> ForwardAsync(JObject request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var id = request["id"];
        if (id is null)
        {
            return Task.FromResult<JObject?>(null);
        }

        // The app-server can reuse ids after a restart while an old approval card is
        // still displayed. Give each UI request its own identity, then restore the
        // server's id only when its matching response is delivered.
        var relayId = new JValue(Guid.NewGuid().ToString("N"));
        var key = CreateKey(relayId);
        var notificationKey = CreateNotificationKey((request["params"] as JObject)?["threadId"], id);
        var pending = new PendingRequest(id);
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return Task.FromResult<JObject?>(null);
            }

            _pending.Add(key, pending);
            RemoveNotificationMapping(notificationKey);
            var identity = _notificationOrder.AddLast(new RequestIdentity(notificationKey, relayId));
            _notificationIds.Add(notificationKey, identity);
            while (_notificationIds.Count > MaxNotificationMappings)
            {
                RemoveNotificationMapping(_notificationOrder.First!.Value.Key);
            }
        }

        try
        {
            var forwardedRequest = (JObject)request.DeepClone();
            forwardedRequest["id"] = relayId;
            _postMessage(new JObject
            {
                ["type"] = "mcp-request",
                ["hostId"] = "local",
                ["request"] = forwardedRequest
            });
        }
        catch
        {
            lock (_syncRoot)
            {
                _pending.Remove(key);
                if (_notificationIds.TryGetValue(notificationKey, out var identity)
                    && JToken.DeepEquals(identity.Value.RelayId, relayId))
                {
                    RemoveNotificationMapping(notificationKey);
                }
            }

            throw;
        }

        return pending.Completion.Task;
    }

    public bool TryHandleResponse(JObject message)
    {
        var response = message["response"] as JObject ?? message["message"] as JObject;
        var id = response?["id"];
        if (response is null || id is null)
        {
            return false;
        }

        PendingRequest? pending;
        lock (_syncRoot)
        {
            var key = CreateKey(id);
            if (!_pending.TryGetValue(key, out pending))
            {
                return false;
            }

            _pending.Remove(key);
        }

        var serverResponse = (JObject)response.DeepClone();
        serverResponse["id"] = pending.OriginalId.DeepClone();
        pending.Completion.TrySetResult(serverResponse);
        return true;
    }

    public JToken? TransformNotificationParameters(string method, JToken? parameters)
    {
        if (!string.Equals(method, "serverRequest/resolved", StringComparison.Ordinal)
            || parameters is not JObject resolved
            || resolved["requestId"] is not JToken requestId)
        {
            return parameters;
        }

        PendingRequest? pending;
        JObject transformed;
        lock (_syncRoot)
        {
            var key = CreateNotificationKey(resolved["threadId"], requestId);
            if (!_notificationIds.TryGetValue(key, out var identity))
            {
                return parameters;
            }

            transformed = (JObject)resolved.DeepClone();
            transformed["requestId"] = identity.Value.RelayId.DeepClone();
            var relayKey = CreateKey(identity.Value.RelayId);
            _pending.TryGetValue(relayKey, out pending);
            _pending.Remove(relayKey);
            RemoveNotificationMapping(key);
        }

        // The server has resolved this request elsewhere; null would incorrectly
        // reopen it in the classic UI, and no JSON-RPC response is needed.
        pending?.Completion.TrySetException(new CodexServerRequestResolvedException());
        return transformed;
    }

    private void RemoveNotificationMapping(string key)
    {
        if (_notificationIds.TryGetValue(key, out var identity))
        {
            _notificationIds.Remove(key);
            _notificationOrder.Remove(identity);
        }
    }

    private static string CreateNotificationKey(JToken? threadId, JToken requestId)
    {
        return new JArray(threadId?.DeepClone() ?? JValue.CreateNull(), requestId.DeepClone())
            .ToString(Formatting.None);
    }

    public void CancelPending()
    {
        PendingRequest[] pending;
        lock (_syncRoot)
        {
            pending = new PendingRequest[_pending.Count];
            _pending.Values.CopyTo(pending, 0);
            _pending.Clear();
            _notificationIds.Clear();
            _notificationOrder.Clear();
        }

        foreach (var request in pending)
        {
            request.Completion.TrySetResult(null);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelPending();
    }

    private static string CreateKey(JToken id)
    {
        return id.ToString(Formatting.None);
    }

    private sealed class RequestIdentity
    {
        public RequestIdentity(string key, JToken relayId)
        {
            Key = key;
            RelayId = relayId.DeepClone();
        }

        public string Key { get; }
        public JToken RelayId { get; }
    }

    private sealed class PendingRequest
    {
        public PendingRequest(JToken originalId)
        {
            OriginalId = originalId.DeepClone();
        }

        public JToken OriginalId { get; }
        public TaskCompletionSource<JObject?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class CodexServerRequestResolvedException : OperationCanceledException
{
    public CodexServerRequestResolvedException()
        : base("The app-server request was already resolved.")
    {
    }
}
