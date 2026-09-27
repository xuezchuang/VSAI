using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal interface IVsDebugToolService : IDisposable
{
    Task<JObject> ExecuteAsync(string toolName, JObject arguments, CancellationToken cancellationToken);
}

/// <summary>App-server tool contracts owned by this Visual Studio instance.</summary>
internal static class VsDebugTools
{
    internal const string Snapshot = "vs_debug_snapshot";
    internal const string FrameVariables = "vs_debug_frame_variables";
    internal const string Threads = "vs_debug_threads";

    internal static bool Owns(JObject? parameters)
    {
        // A tool in another namespace must keep its original handler, even if its
        // short name happens to match one of ours.
        var ns = parameters?["namespace"];
        return (ns is null || ns.Type == JTokenType.Null)
            && parameters?["tool"]?.Type == JTokenType.String
            && IsName(parameters["tool"]!.Value<string>());
    }

    private static bool IsName(string? name) => name == Snapshot || name == FrameVariables || name == Threads;

    internal static JToken? PrepareRequest(string method, JToken? parameters)
    {
        // The installed protocol accepts definitions on thread/start only. They
        // persist with the thread and are restored on resume/fork by app-server.
        if (method != "thread/start") return parameters;
        var result = parameters is JObject source ? (JObject)source.DeepClone() : new JObject();
        var existing = result["dynamicTools"];
        if (existing is not null && existing.Type != JTokenType.Null && existing is not JArray)
            throw new ArgumentException("dynamicTools must be an array.", nameof(parameters));
        var tools = existing as JArray ?? new JArray();
        foreach (var tool in tools.OfType<JObject>().Where(IsOwnedDefinition).ToArray()) tool.Remove();
        foreach (var tool in CreateDefinitions()) tools.Add(tool);
        result["dynamicTools"] = tools;
        return result;
    }

    private static bool IsOwnedDefinition(JObject tool) =>
        tool["name"]?.Type == JTokenType.String && IsName(tool["name"]!.Value<string>())
        && (tool["namespace"] is null || tool["namespace"]!.Type == JTokenType.Null)
        && (tool["type"] is null || tool["type"]!.Value<string>() == "function");

    internal static JArray CreateDefinitions() => new(
        Definition(Snapshot,
            "Read the debugger in this Visual Studio instance without changing execution or editor focus. "
            + "Use first when asked to investigate the current breakpoint, exception, crash or debugging state. "
            + "When paused, returns a snapshotId, process/thread identities and zero-based frameIndex values with source locations. "
            + "Use that snapshotId for subsequent variable/thread reads; capture again after stepping or continuing. "
            + "No program is started, paused or resumed by this tool.",
            new JObject { ["maxFrames"] = Integer(1, 128, 32) }),
        Definition(FrameVariables,
            "Read arguments and locals of an exact frame from vs_debug_snapshot or vs_debug_threads. "
            + "Requires snapshotId, processId, threadId and zero-based frameIndex from that pause. "
            + "Also pass programIndex when returned, to distinguish native/managed programs sharing a thread ID. "
            + "Does not select a frame, evaluate arbitrary expressions or allow implicit property/function evaluation. "
            + "Object-member expansion and unavailable/optimized-out values are reported explicitly, not inferred.",
            new JObject
            {
                ["snapshotId"] = SnapshotId(), ["processId"] = Integer(1, int.MaxValue),
                ["threadId"] = Integer(1, int.MaxValue), ["frameIndex"] = Integer(0, 127),
                ["programIndex"] = Integer(0, 15),
                ["maxVariables"] = Integer(1, 128, 64)
            }, "snapshotId", "processId", "threadId", "frameIndex"),
        Definition(Threads,
            "Read bounded thread lists and call stacks for the same paused Visual Studio snapshot. "
            + "Use to investigate other threads or a hang. Requires snapshotId from vs_debug_snapshot. "
            + "Does not switch the selected process, thread or frame. Truncation and unavailable data are explicit.",
            new JObject
            {
                ["snapshotId"] = SnapshotId(), ["maxThreads"] = Integer(1, 64, 16),
                ["maxFrames"] = Integer(1, 32, 8)
            }, "snapshotId"));

    private static JObject Definition(string name, string description, JObject properties, params string[] required) => new()
    {
        // Current app-server schemas use tagged definitions. Older schemas ignore
        // this extra field and use the same name/description/inputSchema fields.
        ["type"] = "function", ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JArray(required), ["additionalProperties"] = false
        }
    };

    private static JObject SnapshotId() => new()
    {
        ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256,
        ["description"] = "Opaque snapshotId returned by vs_debug_snapshot for the current pause."
    };

    private static JObject Integer(int minimum, int maximum, int? defaultValue = null)
    {
        var result = new JObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
        if (defaultValue.HasValue) result["default"] = defaultValue.Value;
        return result;
    }

    internal static bool TryValidateArguments(string name, JToken? value, out JObject arguments, out string error)
    {
        arguments = new JObject();
        error = string.Empty;
        if (value is not JObject supplied)
        {
            error = "Tool arguments must be a JSON object.";
            return false;
        }
        var definition = CreateDefinitions().OfType<JObject>().SingleOrDefault(d => d["name"]!.Value<string>() == name);
        if (definition is null) { error = "Unknown Visual Studio debugger tool."; return false; }
        var schema = (JObject)definition["inputSchema"]!;
        var properties = (JObject)schema["properties"]!;
        foreach (var property in supplied.Properties())
        {
            if (properties[property.Name] is not JObject rule)
            {
                error = "Unsupported debugger-tool argument: " + property.Name;
                return false;
            }
            var token = property.Value;
            if (rule["type"]!.Value<string>() == "integer")
            {
                if (token.Type != JTokenType.Integer
                    || !long.TryParse(token.ToString(), out var number)
                    || number < rule["minimum"]!.Value<long>() || number > rule["maximum"]!.Value<long>())
                {
                    error = "Argument " + property.Name + " is outside the allowed integer range.";
                    return false;
                }
            }
            else if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>())
                || token.Value<string>()!.Length > rule["maxLength"]!.Value<int>())
            {
                error = "Argument " + property.Name + " must be a nonempty snapshot identifier.";
                return false;
            }
        }
        foreach (var required in schema["required"]!.Values<string>())
        {
            if (supplied[required!] is null) { error = "Missing debugger-tool argument: " + required; return false; }
        }
        arguments = (JObject)supplied.DeepClone();
        foreach (var property in properties.Properties())
            if (arguments[property.Name] is null && property.Value["default"] is JToken defaultValue)
                arguments[property.Name] = defaultValue.DeepClone();
        return true;
    }
}
