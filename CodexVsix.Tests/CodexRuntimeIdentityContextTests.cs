using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexRuntimeIdentityContextTests
{
    [Fact]
    public void ThreadStartPreservesDeveloperInstructionsAndReportsRequestedModel()
    {
        var request = new JObject
        {
            ["model"] = "gpt-5.6-sol",
            ["developerInstructions"] = "Keep existing project conventions."
        };

        var enriched = Assert.IsType<JObject>(CodexRuntimeIdentityContext.EnrichRequest(
            "thread/start",
            request,
            "fallback-model",
            "medium"));

        var instructions = enriched["developerInstructions"]?.Value<string>();
        Assert.Contains("Keep existing project conventions.", instructions);
        Assert.Contains("gpt-5.6-sol", instructions);
        Assert.Contains("medium", instructions);
        Assert.Equal("Keep existing project conventions.", request["developerInstructions"]?.Value<string>());
    }

    [Fact]
    public void TurnStartUsesCollaborationModelWithoutChangingVisibleInput()
    {
        var request = new JObject
        {
            ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Which model are you using?" }),
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "default",
                ["settings"] = new JObject
                {
                    ["model"] = "gpt-5.6-sol",
                    ["reasoning_effort"] = "high",
                    ["developer_instructions"] = "Existing turn rule."
                }
            }
        };

        var enriched = Assert.IsType<JObject>(CodexRuntimeIdentityContext.EnrichRequest(
            "turn/start",
            request,
            null,
            null));

        Assert.Equal("Which model are you using?", enriched["input"]?[0]?["text"]?.Value<string>());
        var instructions = enriched["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();
        Assert.Contains("Existing turn rule.", instructions);
        Assert.Contains("gpt-5.6-sol", instructions);
        Assert.Contains("high", instructions);
    }

    [Fact]
    public void EnrichmentReplacesStaleRuntimeMetadataWhenTheModelChanges()
    {
        var first = new JObject
        {
            ["model"] = "gpt-5.6-sol"
        };
        var enrichedFirst = CodexRuntimeIdentityContext.EnrichRequest(
            "thread/start",
            first,
            null,
            "medium");
        var second = Assert.IsType<JObject>(enrichedFirst);
        second["model"] = "gpt-5.5-codex";

        var enrichedSecond = Assert.IsType<JObject>(CodexRuntimeIdentityContext.EnrichRequest(
            "thread/start",
            second,
            null,
            "high"));

        var instructions = enrichedSecond["developerInstructions"]?.Value<string>();
        Assert.DoesNotContain("gpt-5.6-sol", instructions);
        Assert.Contains("gpt-5.5-codex", instructions);
        Assert.Equal(1, CountOccurrences(instructions!, "[[visual-codex-studio-runtime-metadata:v1]]"));
        Assert.Equal(1, CountOccurrences(instructions!, "[[visual-codex-studio-source-links:v1]]"));
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public void SourceLinksApplyToNewAndExistingVsixThreadsWithoutModelMetadata(string method)
    {
        var original = new JObject { ["developerInstructions"] = "Preserve project rules." };
        var enriched = CodexRuntimeIdentityContext.EnrichRequest(method, original, null, null);
        var instructions = enriched?["developerInstructions"]?.Value<string>();

        Assert.Contains("Preserve project rules.", instructions);
        Assert.Contains("inside Visual Codex Studio", instructions);
        Assert.Contains("give its clickable file-and-line link instead of pasting a large code block", instructions);
        Assert.Contains("Include code snippets only when requested or necessary", instructions);
        Assert.Contains("[File.cpp:42](<D:/project/src/File.cpp:42>)", instructions);
        Assert.Contains("[File.cpp:42:7](<D:/project/src/File.cpp:42:7>)", instructions);
        Assert.Contains("[File.cpp:42](<D:/工程 空格/100%25 #/File.cpp:42>)", instructions);
        Assert.DoesNotContain("host reports that the model", instructions);
        Assert.Equal("Preserve project rules.", original["developerInstructions"]?.Value<string>());
    }

    [Fact]
    public void SourceLinkRuleIsIdempotentAndLeavesVisibleTurnInputUntouched()
    {
        var original = new JObject
        {
            ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Locate this function." }),
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "default",
                ["settings"] = new JObject { ["developer_instructions"] = "Keep turn conventions." }
            }
        };
        var first = CodexRuntimeIdentityContext.EnrichRequest("turn/start", original, null, null);
        var second = CodexRuntimeIdentityContext.EnrichRequest("turn/start", first, null, null);
        var instructions = second?["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();

        Assert.Contains("Keep turn conventions.", instructions);
        Assert.Contains("1-based line number", instructions);
        Assert.Equal(1, CountOccurrences(instructions!, "[[visual-codex-studio-source-links:v1]]"));
        Assert.True(JToken.DeepEquals(first, second));
        Assert.True(JToken.DeepEquals(original["input"], second?["input"]));
        Assert.Equal("Keep turn conventions.", original["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>());
    }

    [Theory]
    [InlineData("thread/read")]
    [InlineData("config/read")]
    [InlineData("turn/start")]
    public void UnsupportedRequestsDoNotAcquireNewProtocolFields(string method)
    {
        var original = new JObject { ["model"] = "model", ["threadId"] = "existing" };
        var enriched = CodexRuntimeIdentityContext.EnrichRequest(method, original, "fallback", "high");

        Assert.NotSame(original, enriched);
        Assert.True(JToken.DeepEquals(original, enriched));
    }

    [Fact]
    public void CollaborationIdentityTakesPrecedenceOverTopLevelModelAndEffort()
    {
        var original = new JObject
        {
            ["model"] = "top-level-model", ["effort"] = "low", ["reasoningEffort"] = "medium",
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "default", ["settings"] = new JObject
                {
                    ["model"] = "collaboration-model", ["reasoning_effort"] = "high",
                    ["developer_instructions"] = "Keep original instruction."
                }
            }
        };

        var enriched = CodexRuntimeIdentityContext.EnrichRequest("turn/start", original, "fallback-model", "ultra");

        var instructions = enriched?["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();
        Assert.Contains("\"collaboration-model\"", instructions);
        Assert.Contains("reasoning effort is \"high\"", instructions);
        Assert.DoesNotContain("top-level-model", instructions);
        Assert.DoesNotContain("reasoning effort is \"low\"", instructions);
        Assert.Contains("Keep original instruction.", instructions);
        Assert.Equal("low", enriched?["effort"]?.Value<string>());
        Assert.Equal("Keep original instruction.", original["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollaborationDefaultEffortDoesNotUseConflictingFieldsOrFallback(bool explicitNull)
    {
        var mode = new JObject { ["model"] = "model" };
        if (explicitNull) mode["reasoning_effort"] = null;
        var request = new JObject
        {
            ["effort"] = "high", ["reasoningEffort"] = "medium",
            ["collaborationMode"] = new JObject { ["mode"] = "default", ["settings"] = mode }
        };

        var enriched = CodexRuntimeIdentityContext.EnrichRequest("turn/start", request, "model", "ultra");

        var instructions = enriched?["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();
        Assert.DoesNotContain("requested reasoning effort", instructions);
        Assert.Equal(explicitNull ? JTokenType.Null : (JTokenType?)null, enriched?["collaborationMode"]?["settings"]?["reasoning_effort"]?.Type);
    }

    [Theory]
    [InlineData("effort")]
    [InlineData("reasoningEffort")]
    public void ExplicitNullEffortRemovesStaleMetadataInsteadOfUsingFallback(string field)
    {
        var original = new JObject { ["model"] = "model", ["effort"] = "high" };
        var previous = Assert.IsType<JObject>(CodexRuntimeIdentityContext.EnrichRequest("thread/resume", original, null, null));
        previous.Remove("effort");
        previous[field] = null;

        var enriched = CodexRuntimeIdentityContext.EnrichRequest("thread/resume", previous, "model", "ultra");

        Assert.DoesNotContain("requested reasoning effort", enriched?["developerInstructions"]?.Value<string>());
        Assert.Equal(JTokenType.Null, enriched?[field]?.Type);
    }

    [Fact]
    public void ExplicitNoneEffortIsDistinctFromNullAndDefault()
    {
        var request = new JObject
        {
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "default", ["settings"] = new JObject { ["model"] = "model", ["reasoning_effort"] = "none" }
            }
        };

        var enriched = CodexRuntimeIdentityContext.EnrichRequest("turn/start", request, "model", "high");

        Assert.Contains("reasoning effort is \"none\"", enriched?["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>());
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
