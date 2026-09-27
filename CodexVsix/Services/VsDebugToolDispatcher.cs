using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Routes host tools before the WebView relay and owns in-flight reads.</summary>
internal sealed class VsDebugToolDispatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<IVsDebugToolService> _serviceFactory;
    private IVsDebugToolService? _service;
    private readonly HashSet<PendingCall> _pending = new();
    private long _minimumGeneration;
    private bool _disposed;
    internal const int MaxResultCharacters = 128 * 1024;

    internal VsDebugToolDispatcher(IVsDebugToolService service) : this(() => service) => _service = service;

    internal VsDebugToolDispatcher(Func<IVsDebugToolService> serviceFactory)
        => _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));

    // Null means that the original request was retired; it must not receive a reply.
    internal async Task<JObject?> ExecuteAsync(JToken? id, JObject parameters, long generation)
    {
        var name = parameters["tool"]!.Value<string>()!;
        if (!VsDebugTools.TryValidateArguments(name, parameters["arguments"], out var arguments, out var error))
            return Failure("invalid_arguments", error);
        var pending = new PendingCall(id, parameters, generation);
        lock (_sync)
        {
            if (_disposed || generation < _minimumGeneration) { pending.Dispose(); return null; }
            _pending.Add(pending);
        }
        JObject response;
        try
        {
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            IVsDebugToolService service;
            lock (_sync)
            {
                if (_disposed || pending.Retired) return null;
                service = _service ??= _serviceFactory();
            }
            var payload = await service.ExecuteAsync(name, arguments, pending.Cancellation.Token).ConfigureAwait(false);
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            var text = NewtonsoftJsonCompatibility.Serialize(payload, Formatting.None);
            response = text.Length > MaxResultCharacters
                ? Failure("result_too_large", "Debugger output exceeded its limit. Retry with smaller maxFrames/maxThreads/maxVariables.")
                : new JObject
                {
                    ["success"] = payload["error"] is null,
                    ["contentItems"] = new JArray(new JObject { ["type"] = "inputText", ["text"] = text })
                };
        }
        catch (OperationCanceledException)
        {
            response = Failure("debugger_read_cancelled", "Debugger read was cancelled or exceeded its time limit. Capture a new snapshot before retrying.");
        }
        catch (Exception)
        {
            // Exception messages can contain values or expressions from the debuggee.
            response = Failure("debugger_unavailable", "Visual Studio could not read the requested debugger data. Capture a new snapshot and retry while paused.");
        }
        finally
        {
            lock (_sync) _pending.Remove(pending);
            pending.Dispose();
        }
        lock (_sync)
            return pending.Retired || _disposed || generation < _minimumGeneration ? null : response;
    }

    internal void ObserveNotification(string method, JObject? parameters)
    {
        if (parameters is null) return;
        if (method == "serverRequest/resolved")
        {
            var id = parameters["requestId"];
            var thread = parameters["threadId"]?.Value<string>();
            if (id is not null)
                Retire(p => JToken.DeepEquals(p.Id, id) && (thread is null || p.ThreadId == thread));
        }
        else if (method == "turn/completed" || method == "thread/closed")
        {
            var thread = parameters["threadId"]?.Value<string>();
            var turn = parameters["turn"]?["id"]?.Value<string>() ?? parameters["turnId"]?.Value<string>();
            if (thread is not null)
                Retire(p => p.ThreadId == thread && (turn is null || p.TurnId == turn));
        }
    }

    internal void Interrupt(JToken? parameters, long? generation = null)
    {
        if (parameters is not JObject values) return;
        var thread = values["threadId"]?.Value<string>();
        var turn = values["turnId"]?.Value<string>();
        if (thread is not null) Retire(p => p.ThreadId == thread && (turn is null || p.TurnId == turn)
            && (!generation.HasValue || p.Generation == generation.Value));
    }

    internal void RetireBefore(long generation)
    {
        lock (_sync) _minimumGeneration = Math.Max(_minimumGeneration, generation);
        Retire(p => p.Generation < generation);
    }

    private void Retire(Func<PendingCall, bool> predicate)
    {
        PendingCall[] calls;
        lock (_sync)
        {
            calls = _pending.Where(predicate).ToArray();
            foreach (var call in calls) call.Retired = true;
        }
        foreach (var call in calls)
        {
            try { call.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    internal static JObject Failure(string code, string message) => new()
    {
        ["success"] = false,
        ["contentItems"] = new JArray(new JObject
        {
            ["type"] = "inputText",
            ["text"] = NewtonsoftJsonCompatibility.Serialize(new JObject
            {
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            }, Formatting.None)
        })
    };

    public void Dispose()
    {
        IVsDebugToolService? service;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            service = _service;
        }
        Retire(_ => true);
        service?.Dispose();
    }

    private sealed class PendingCall : IDisposable
    {
        internal PendingCall(JToken? id, JObject parameters, long generation)
        {
            Id = id?.DeepClone();
            ThreadId = parameters["threadId"]?.Value<string>();
            TurnId = parameters["turnId"]?.Value<string>();
            Generation = generation;
            Cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        }
        internal JToken? Id { get; }
        internal string? ThreadId { get; }
        internal string? TurnId { get; }
        internal long Generation { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal bool Retired { get; set; }
        public void Dispose() => Cancellation.Dispose();
    }
}
