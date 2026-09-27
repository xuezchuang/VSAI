using System.Linq;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class VsDebugToolsTests
{
    [Fact]
    public void ThreadStartMergesHostToolsWithoutMutatingInputAndDeduplicatesOwnedDefinitions()
    {
        var original = new JObject
        {
            ["cwd"] = "C:/work",
            ["dynamicTools"] = new JArray
            {
                new JObject { ["type"] = "function", ["name"] = "other_tool", ["description"] = "keep" },
                new JObject { ["type"] = "function", ["name"] = VsDebugTools.Snapshot, ["description"] = "old" },
                new JObject { ["type"] = "function", ["name"] = VsDebugTools.FrameVariables, ["description"] = "old" },
                new JObject { ["type"] = "function", ["namespace"] = "remote", ["name"] = VsDebugTools.Threads }
            }
        };
        var before = (JObject)original.DeepClone();

        var prepared = (JObject)VsDebugTools.PrepareRequest("thread/start", original)!;
        var tools = (JArray)prepared["dynamicTools"]!;

        Assert.True(JToken.DeepEquals(before, original));
        Assert.Equal("C:/work", prepared["cwd"]!.Value<string>());
        Assert.Equal(1, tools.Count(t => t["name"]?.Value<string>() == VsDebugTools.Snapshot
            && (t["namespace"] is null || t["namespace"]!.Type == JTokenType.Null)));
        Assert.Equal(1, tools.Count(t => t["name"]?.Value<string>() == VsDebugTools.FrameVariables
            && (t["namespace"] is null || t["namespace"]!.Type == JTokenType.Null)));
        Assert.Contains(tools, t => t["name"]?.Value<string>() == "other_tool");
        Assert.Contains(tools, t => t["namespace"]?.Value<string>() == "remote"
            && t["name"]?.Value<string>() == VsDebugTools.Threads);
        Assert.Equal(3, VsDebugTools.CreateDefinitions().Count);
    }

    [Theory]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public void ResumeAndForkDoNotReceiveDynamicToolDefinitions(string method)
    {
        var original = new JObject { ["threadId"] = "existing", ["other"] = true };

        var prepared = VsDebugTools.PrepareRequest(method, original);

        Assert.Same(original, prepared);
        Assert.Null(prepared!["dynamicTools"]);
    }

    [Fact]
    public void DefinitionsExposeBoundedIntegerSchemasAndZeroBasedFrameIndex()
    {
        var definitions = VsDebugTools.CreateDefinitions().OfType<JObject>()
            .ToDictionary(d => d["name"]!.Value<string>()!);
        Assert.Equal(3, definitions.Count);
        foreach (var definition in definitions.Values)
        {
            Assert.Equal("function", definition["type"]!.Value<string>());
            Assert.Equal("object", definition["inputSchema"]!["type"]!.Value<string>());
            Assert.False(definition["inputSchema"]!["additionalProperties"]!.Value<bool>());
        }

        AssertInteger(definitions[VsDebugTools.Snapshot], "maxFrames", 1, 128, 32);
        AssertInteger(definitions[VsDebugTools.FrameVariables], "processId", 1, int.MaxValue, null);
        AssertInteger(definitions[VsDebugTools.FrameVariables], "threadId", 1, int.MaxValue, null);
        AssertInteger(definitions[VsDebugTools.FrameVariables], "frameIndex", 0, 127, null);
        AssertInteger(definitions[VsDebugTools.FrameVariables], "programIndex", 0, 15, null);
        AssertInteger(definitions[VsDebugTools.FrameVariables], "maxVariables", 1, 128, 64);
        AssertInteger(definitions[VsDebugTools.Threads], "maxThreads", 1, 64, 16);
        AssertInteger(definitions[VsDebugTools.Threads], "maxFrames", 1, 32, 8);
        Assert.Equal(new[] { "snapshotId", "processId", "threadId", "frameIndex" },
            definitions[VsDebugTools.FrameVariables]["inputSchema"]!["required"]!.Values<string>());
    }

    [Theory]
    [InlineData("\"frameIndex\":0")]
    [InlineData("\"frameIndex\":-1")]
    [InlineData("\"frameIndex\":128")]
    [InlineData("\"frameIndex\":\"0\"")]
    [InlineData("\"frameIndex\":0.5")]
    public void FrameVariableValidationRejectsBadFrameTokensOrRanges(string frameProperty)
    {
        var json = "{\"snapshotId\":\"snap\",\"processId\":1,\"threadId\":2," + frameProperty + "}";
        var supplied = JObject.Parse(json);

        var valid = VsDebugTools.TryValidateArguments(VsDebugTools.FrameVariables, supplied, out _, out _);

        Assert.Equal(frameProperty == "\"frameIndex\":0", valid);
    }

    [Fact]
    public void ValidationAppliesDefaultsAndRejectsMissingIdentifiersAndWrongTokenKinds()
    {
        Assert.True(VsDebugTools.TryValidateArguments(VsDebugTools.Snapshot, new JObject(), out var snapshot, out _));
        Assert.Equal(32, snapshot["maxFrames"]!.Value<int>());
        Assert.True(VsDebugTools.TryValidateArguments(VsDebugTools.Threads,
            new JObject { ["snapshotId"] = "pause-1" }, out var threads, out _));
        Assert.Equal(16, threads["maxThreads"]!.Value<int>());
        Assert.Equal(8, threads["maxFrames"]!.Value<int>());

        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.Threads, new JObject(), out _, out _));
        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.Threads,
            new JObject { ["snapshotId"] = 7 }, out _, out _));
        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.Threads,
            new JObject { ["snapshotId"] = "  " }, out _, out _));
        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.FrameVariables,
            new JObject { ["snapshotId"] = "pause-1", ["threadId"] = 2, ["frameIndex"] = 0 }, out _, out _));
        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.FrameVariables,
            new JObject { ["snapshotId"] = "pause-1", ["processId"] = "1", ["threadId"] = 2, ["frameIndex"] = 0 }, out _, out _));
        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.Snapshot,
            new JObject { ["maxFrames"] = "32" }, out _, out _));
        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.Snapshot,
            new JObject { ["extra"] = true }, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void OptionalProgramIndexIsAcceptedAndPreserved(int programIndex)
    {
        var supplied = new JObject
        {
            ["snapshotId"] = "pause-1", ["processId"] = 1, ["threadId"] = 2,
            ["frameIndex"] = 0, ["programIndex"] = programIndex
        };

        Assert.True(VsDebugTools.TryValidateArguments(VsDebugTools.FrameVariables, supplied, out var normalized, out _));
        Assert.Equal(programIndex, normalized["programIndex"]!.Value<int>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void OptionalProgramIndexRejectsValuesOutsideAllowedRange(int programIndex)
    {
        var supplied = new JObject
        {
            ["snapshotId"] = "pause-1", ["processId"] = 1, ["threadId"] = 2,
            ["frameIndex"] = 0, ["programIndex"] = programIndex
        };

        Assert.False(VsDebugTools.TryValidateArguments(VsDebugTools.FrameVariables, supplied, out _, out _));
    }

    [Fact]
    public void HostOwnershipRequiresExactUnnamespacedStringToolName()
    {
        Assert.True(VsDebugTools.Owns(new JObject { ["tool"] = VsDebugTools.Snapshot }));
        Assert.False(VsDebugTools.Owns(new JObject { ["namespace"] = "remote", ["tool"] = VsDebugTools.Snapshot }));
        Assert.False(VsDebugTools.Owns(new JObject { ["namespace"] = "", ["tool"] = VsDebugTools.Snapshot }));
        Assert.False(VsDebugTools.Owns(new JObject { ["tool"] = 4 }));
        Assert.False(VsDebugTools.Owns(new JObject { ["tool"] = "vs_debug_snapshot_extra" }));
    }

    private static void AssertInteger(JObject definition, string propertyName, int minimum, int maximum, int? defaultValue)
    {
        var schema = definition["inputSchema"]!["properties"]![propertyName]!;
        Assert.Equal("integer", schema["type"]!.Value<string>());
        Assert.Equal(minimum, schema["minimum"]!.Value<int>());
        Assert.Equal(maximum, schema["maximum"]!.Value<int>());
        Assert.Equal(defaultValue, schema["default"]?.Value<int>());
    }
}
